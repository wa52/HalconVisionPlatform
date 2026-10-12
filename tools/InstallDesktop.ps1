param(
    [string]$PublishedDirectory = (Join-Path $PSScriptRoot '..\artifacts\lifecycle-verified'),
    [string]$InstallDirectory = (Join-Path $PSScriptRoot '..\artifacts\desktop')
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$source = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$destination = [IO.Path]::GetFullPath($InstallDirectory)
if (-not $destination.StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '安装目录必须位于本项目 artifacts 目录内。'
}
$executable = Join-Path $destination 'HalconVision.App.exe'
foreach ($required in @('HalconVision.App.exe', 'HalconVision.App.dll', 'HalconVision.App.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "发布目录缺少：$required" }
}
if ($source -ne $destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
}

# Explorer can retain the executable path selected previously in Open With.
# Preserve that historical entry point as an alias of the stable installation.
$legacy = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'delete-shortcut'))
if (-not $legacy.StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '旧发布目录不在本项目 artifacts 目录内。'
}
if (Test-Path -LiteralPath $legacy) {
    $item = Get-Item -LiteralPath $legacy
    if ($item.LinkType -eq 'Junction') {
        if ($item.Target -notcontains $destination) {
            foreach ($target in $item.Target) {
                if (-not [IO.Path]::GetFullPath($target).StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw '旧入口指向本项目以外目录，请先检查。'
                }
            }
            # Delete only the junction itself, never its target or its contents.
            [IO.Directory]::Delete($legacy)
            New-Item -ItemType Junction -Path $legacy -Target $destination | Out-Null
        }
    } else {
        $backup = Join-Path $artifactsRoot ('delete-shortcut-backup-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmssfff'))
        if (-not $backup.StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '备份路径越界。' }
        Move-Item -LiteralPath $legacy -Destination $backup
        New-Item -ItemType Junction -Path $legacy -Target $destination | Out-Null
    }
}

$command = '"' + $executable + '" "%1"'
$classes = 'HKCU:\Software\Classes'
foreach ($progId in @('HalconVisionPlatform.Project', 'Applications\HalconVision.App.exe')) {
    $key = Join-Path $classes ($progId + '\shell\open\command')
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $command
}
$extension = Join-Path $classes '.hvproj'
New-Item -Path $extension -Force | Out-Null
Set-Item -Path $extension -Value 'HalconVisionPlatform.Project'
$openWith = Join-Path $extension 'OpenWithProgids'
New-Item -Path $openWith -Force | Out-Null
New-ItemProperty -Path $openWith -Name 'HalconVisionPlatform.Project' -PropertyType String -Value '' -Force | Out-Null
Set-Item -Path (Join-Path $classes 'HalconVisionPlatform.Project') -Value 'HALCON 视觉工程'

$desktop = [Environment]::GetFolderPath('Desktop')
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'HalconVisionPlatform.lnk'))
$shortcut.TargetPath = $executable
$shortcut.Arguments = ''
$shortcut.WorkingDirectory = $destination
$shortcut.Description = 'HALCON 视觉检测平台'
$shortcut.IconLocation = $executable + ',0'
$shortcut.Save()

Add-Type 'using System; using System.Runtime.InteropServices; public static class HalconDesktopAssociationRefresh {
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, IntPtr a, IntPtr b);
}'
[HalconDesktopAssociationRefresh]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output "桌面快捷方式及项目关联已更新：$executable"
Write-Output '请从资源管理器双击 .hvproj，确认项目名称、节点及连线已显示。'
