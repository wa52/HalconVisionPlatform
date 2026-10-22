using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HalconStartupAudit;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static int Main(string[] args)
    {
        if (args is ["--child", var scenario]) return ControlChild(scenario);
        try
        {
            if (args is ["--self-check", var selfOutput]) return SelfCheck(FreshDirectory(selfOutput));
            if (args.Length < 4) throw new ArgumentException("用法：<fixture|layout|lifecycle> <检查exe> <全新输出目录> <次数1..200> [--capture]");
            var target = args[0];
            var expectedName = target switch { "fixture" or "lifecycle" => "HalconVision.LifecycleChecks.exe", "layout" => "HalconLayoutCheck.exe", _ => throw new ArgumentException("不支持的检查目标。") };
            var executable = Path.GetFullPath(args[1]);
            if (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), expectedName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("只能启动指定名称的本项目检查程序。");
            var root = FindProjectRoot(executable);
            if (!int.TryParse(args[3], out var count) || count is < 1 or > 200) throw new ArgumentException("次数必须为1..200。");
            var capture = args.Length == 5 && args[4] == "--capture";
            if (args.Length > 5 || args.Length == 5 && !capture) throw new ArgumentException("无效选项。");
            var output = FreshDirectory(args[2]);
            var rows = new List<TrialResult>();
            Save(Path.Combine(output, "environment.json"), new
            {
                target, executable, sha256 = FileHash(executable), capture,
                parentRuntime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                halconRoot = Environment.GetEnvironmentVariable("HALCONROOT"), halconArch = Environment.GetEnvironmentVariable("HALCONARCH"),
                dependencies = DependencyIdentity(executable),
                note = "只读环境快照；采集模式影响时序，不等同于普通启动。dump仅留本地artifacts，不上传。"
            });
            for (var round = 1; round <= count; round++)
            {
                var trial = FreshDirectory(Path.Combine(output, $"run-{round:000}"));
                var fixture = Path.Combine(trial, "fixture");
                string[] arguments = target switch { "fixture" => ["--fixture-only", fixture], "lifecycle" => [fixture], _ => [] };
                var row = Run(executable, arguments, root, trial, capture, target == "lifecycle" ? 90 : 20);
                if (target == "fixture") ValidateFixture(row, fixture);
                else if (target == "lifecycle" && !File.Exists(Path.Combine(fixture, "result.txt"))) row.ValidationErrors.Add("生命周期检查没有写出 result.txt。");
                else if (target == "layout" && !capture && !row.StandardOutput.Contains("PASS:")) row.ValidationErrors.Add("布局检查没有输出 PASS。");
                rows.Add(row); Save(Path.Combine(output, "summary.json"), rows);
                Console.WriteLine($"{target} {round}/{count}: {row.ExitHex}; success={row.Success}; pid={row.ProcessId}");
                // Each start is a separate sample. A failure is never retried or hidden.
                if (!row.Success) return 1;
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
    }

    private static void ValidateFixture(TrialResult row, string fixture)
    {
        var stages = Path.Combine(fixture, "startup-stages.txt");
        row.Stages = File.Exists(stages) ? File.ReadAllLines(stages) : [];
        if (!row.Stages.SequenceEqual(new[] { "before-gen-image-const", "after-gen-image-const", "after-write-first-png", "after-write-second-png" }))
            row.ValidationErrors.Add("原生图像生成/保存阶段没有完整到达。");
        foreach (var image in new[] { "01.png", "02.png" })
        {
            var path = Path.Combine(fixture, "images", image);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) row.ValidationErrors.Add("缺少测试图：" + image);
        }
        Save(Path.Combine(row.Output, "result.json"), row);
    }

    internal static TrialResult Run(string executable, string[] arguments, string workingDirectory, string output, bool capture, int seconds)
    {
        if (capture) return NativeCapture.Run(executable, arguments, workingDirectory, output, seconds);
        var timer = Stopwatch.StartNew();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("检查进程启动失败。");
        var result = new TrialResult { ProcessId = process.Id, Output = output, Capture = false };
        try
        {
            // Preserve original bytes: a Windows check process may emit OEM Chinese instead of UTF-8.
            using var stdoutFile = new FileStream(Path.Combine(output, "stdout.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var stderrFile = new FileStream(Path.Combine(output, "stderr.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(stdoutFile); var stderr = process.StandardError.BaseStream.CopyToAsync(stderrFile);
            Save(Path.Combine(output, "started.json"), new { result.ProcessId, executable, arguments, startedUtc = DateTimeOffset.UtcNow });
            if (!process.WaitForExit(seconds * 1000)) { result.TimedOut = true; process.Kill(entireProcessTree: true); }
            if (!process.WaitForExit(5000)) throw new TimeoutException("本检查子进程未在结束请求后退出。");
            result.ExitCode = unchecked((uint)process.ExitCode); result.CleanupConfirmed = process.HasExited;
            if (!Task.WaitAll([stdout, stderr], 5000)) throw new TimeoutException("检查输出管道未关闭。");
            stdoutFile.Dispose(); stderrFile.Dispose();
            result.StandardOutput = DecodeConsole(Path.Combine(output, "stdout.bin")); result.StandardError = DecodeConsole(Path.Combine(output, "stderr.bin"));
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); result.CleanupConfirmed = process.WaitForExit(5000); }
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            Save(Path.Combine(output, "result.json"), result);
        }
    }

    private static int SelfCheck(string output)
    {
        var executable = Environment.ProcessPath!;
        if (!Path.GetFileName(executable).Equals("HalconStartupAudit.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("自检查请通过生成的 HalconStartupAudit.exe 启动。");
        var results = new List<object>();
        foreach (var capture in new[] { false, true })
        foreach (var scenario in new[] { "ok", "handled-exception", "handled-null", "exit-42", "hang", "access-violation", "null-then-av" })
        {
            // Plain native fault would open a system crash dialog; debug mode captures it without suppressing it.
            if (!capture && scenario is "access-violation" or "null-then-av") continue;
            var row = Run(executable, ["--child", scenario], Environment.CurrentDirectory,
                FreshDirectory(Path.Combine(output, (capture ? "capture-" : "plain-") + scenario)), capture, scenario == "hang" ? 2 : 15);
            var expected = scenario switch
            {
                "ok" or "handled-exception" or "handled-null" => row.Success,
                "exit-42" => row.ExitCode == 42 && !row.Success,
                "hang" => row.TimedOut && !row.Success,
                _ => row.ExitCode == 0xc0000005 && !row.Success && row.Exceptions.Any(x => x.Code == "0xC0000005") && row.DumpPath is not null
                    && ValidateDump(row.DumpPath, 0xc0000005, row.Exceptions.Last().Address) && (scenario != "null-then-av" || row.DumpFiles.Count >= 2)
            };
            if (!row.CleanupConfirmed || !expected) throw new InvalidOperationException("采集器自检查失败：" + scenario + "; " + JsonSerializer.Serialize(row, Json));
            results.Add(new { capture, scenario, passed = true, row }); Save(Path.Combine(output, "self-check.json"), results);
            Console.WriteLine($"PASS collector control: {capture}/{scenario}; {row.ExitHex}");
        }
        return 0;
    }

    private static int ControlChild(string scenario)
    {
        switch (scenario)
        {
            case "ok": return 0;
            case "handled-exception": try { throw new InvalidOperationException("采集器受控异常，必须正常交给子进程处理。"); } catch (InvalidOperationException) { return 0; }
            case "handled-null": try { return ReadLength(null!); } catch (NullReferenceException) { return 0; }
            case "exit-42": return 42;
            case "hang": Thread.Sleep(Timeout.Infinite); return 0;
            case "access-violation": NativeCapture.RaiseException(0xc0000005, 1, 0, IntPtr.Zero); return 99;
            case "null-then-av":
                try { ReadLength(null!); } catch (NullReferenceException) { }
                NativeCapture.RaiseException(0xc0000005, 1, 0, IntPtr.Zero); return 99;
            default: return 2;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadLength(string value) => value.Length;

    private static object[] DependencyIdentity(string executable)
    {
        var folder = Path.GetDirectoryName(executable)!;
        var files = new[] { "HalconVision.Halcon.dll", "HalconVision.App.dll", "MVTec.HalconDotNet.dll", Path.GetFileNameWithoutExtension(executable) + ".runtimeconfig.json" }
            .Select(file => Path.Combine(folder, file)).ToList();
        var root = Environment.GetEnvironmentVariable("HALCONROOT"); var arch = Environment.GetEnvironmentVariable("HALCONARCH");
        if (!string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(arch)) files.Add(Path.Combine(root, "bin", arch, "halcon.dll"));
        return files.Where(File.Exists).Select(file => (object)new { path = file, sha256 = FileHash(file), version = FileVersionInfo.GetVersionInfo(file).FileVersion }).ToArray();
    }

    private static bool ValidateDump(string path, uint expectedCode, string expectedAddress)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x504d444d) return false; // MDMP
        stream.Position = 8; var count = reader.ReadUInt32(); var directory = reader.ReadUInt32();
        for (var i = 0; i < count; i++)
        {
            stream.Position = directory + i * 12; var type = reader.ReadUInt32(); reader.ReadUInt32(); var rva = reader.ReadUInt32();
            if (type != 6) continue; // ExceptionStream
            stream.Position = rva + 8; if (reader.ReadUInt32() != expectedCode) return false;
            stream.Position = rva + 24; if (reader.ReadUInt64() != Convert.ToUInt64(expectedAddress[2..], 16)) return false;
            stream.Position = rva + 160; var contextSize = reader.ReadUInt32(); var contextRva = reader.ReadUInt32();
            return contextSize >= 256 && contextRva + (long)contextSize <= stream.Length;
        }
        return false;
    }

    private static string DecodeConsole(string path)
    {
        var data = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP()).GetString(data);
        }
    }
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    private static string FindProjectRoot(string file)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HalconVisionPlatform.sln")) && Directory.Exists(Path.Combine(directory.FullName, "src", "HalconVision.Halcon"))) return directory.FullName;
        throw new ArgumentException("检查程序必须位于 HalconVisionPlatform 工程内。");
    }
    private static string FreshDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("拒绝覆盖已有输出：" + full);
        Directory.CreateDirectory(full); return full;
    }
    private static string FileHash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}

internal sealed class TrialResult
{
    public int ProcessId { get; set; }
    public string Output { get; set; } = "";
    public bool Capture { get; set; }
    public uint? ExitCode { get; set; }
    public string? ExitHex => ExitCode is { } value ? $"0x{value:X8}" : null;
    public bool TimedOut { get; set; }
    public bool CleanupConfirmed { get; set; }
    public bool Success => ExitCode == 0 && !TimedOut && CleanupConfirmed && ValidationErrors.Count == 0 && DumpError is null;
    public double Milliseconds { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public string[] Stages { get; set; } = [];
    public List<string> ValidationErrors { get; set; } = [];
    public List<NativeException> Exceptions { get; set; } = [];
    public List<ModuleRecord> Modules { get; set; } = [];
    public string? DumpPath { get; set; }
    public int DumpAttempts { get; set; }
    public List<string> DumpFiles { get; set; } = [];
    public string? DumpError { get; set; }
}

internal sealed record NativeException(string Code, string Address, bool FirstChance, uint ThreadId, string? Module, string? ModuleOffset, string? AccessType, string? AccessAddress);
internal sealed record ModuleRecord(string Path, string BaseAddress, int Size, string? Version);
