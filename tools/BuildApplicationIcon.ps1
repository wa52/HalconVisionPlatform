param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\src\HalconVision.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assetRoot = (Resolve-Path -LiteralPath $AssetDirectory).Path
$source = Join-Path $assetRoot 'Brand.xaml'
$reader = [Xml.XmlReader]::Create($source)
try { $dictionary = [Windows.Markup.XamlReader]::Load($reader) } finally { $reader.Dispose() }
$mark = $dictionary['ApplicationMark']
if ($null -eq $mark) { throw 'Brand.xaml 缺少 ApplicationMark。' }
$mark.Freeze()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try { $drawing.DrawImage($mark, [Windows.Rect]::new(0, 0, $size, $size)) } finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); $bytes = $stream.ToArray() } finally { $stream.Dispose() }
    $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetRoot 'HalconVision.png'), $bytes) }
}

# PNG-compressed, 32-bit alpha frames supported by Windows Vista and later.
$iconPath = Join-Path $assetRoot 'HalconVision.ico'
$iconStream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($iconPath, $iconStream.ToArray())
} finally { $writer.Dispose(); $iconStream.Dispose() }

$decoder = [Windows.Media.Imaging.IconBitmapDecoder]::new([Uri]::new($iconPath), [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$actualSizes = @($decoder.Frames | ForEach-Object { $_.PixelWidth })
if (($actualSizes -join ',') -ne ($sizes -join ',')) { throw 'ICO 多尺寸解码核对失败。' }
Write-Output ('应用图标已生成并解码验证：' + ($actualSizes -join ', ') + ' px')
