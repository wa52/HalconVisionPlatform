using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HalconStartupAudit;

// x64 only. Observes newly created check children; never attaches to an existing app,
// changes exception handlers/registers, patches modules, or modifies installation files.
internal static class NativeCapture
{
    private const uint Continue = 0x00010002, NotHandled = 0x80010001;
    internal static TrialResult Run(string executable, string[] arguments, string workingDirectory, string output, int seconds)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("原生采集只支持 x64。");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        // DEBUG_ONLY_THIS_PROCESS + CREATE_NO_WINDOW. Debugger termination kills only this debug child.
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000002, IntPtr.Zero, workingDirectory, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建原生采集子进程失败。");
        CloseHandle(process.Thread);
        var timer = Stopwatch.StartNew();
        var result = new TrialResult { ProcessId = (int)process.ProcessId, Output = output, Capture = true };
        var loaderBreakpointHandled = false;
        var exited = false;
        try
        {
            Program.Save(Path.Combine(output, "started.json"), new { result.ProcessId, executable, arguments, capture = true, startedUtc = DateTimeOffset.UtcNow });
            while (!exited)
            {
                if (!result.TimedOut && timer.Elapsed.TotalSeconds > seconds)
                {
                    result.TimedOut = true;
                    if (!TerminateProcess(process.Process, 1460)) throw new Win32Exception(Marshal.GetLastWin32Error(), "结束超时检查失败。");
                }
                if (timer.Elapsed.TotalSeconds > seconds + 10) throw new TimeoutException("采集子进程未及时报告退出。");
                if (!WaitForDebugEvent(out var debug, 100))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 121) continue; // ERROR_SEM_TIMEOUT
                    throw new Win32Exception(error, "等待原生调试事件失败。");
                }
                var continuation = Continue;
                try
                {
                    switch (debug.EventCode)
                    {
                        case 3: // CREATE_PROCESS: the OS owns debug event process/thread handles.
                            try { RecordModule(result, debug.FileHandle, debug.ProcessBase); }
                            finally { if (debug.FileHandle != IntPtr.Zero) CloseHandle(debug.FileHandle); }
                            break;
                        case 6: // LOAD_DLL
                            try { RecordModule(result, debug.FileHandle, debug.DllBase); }
                            finally { if (debug.FileHandle != IntPtr.Zero) CloseHandle(debug.FileHandle); }
                            break;
                        case 1:
                            continuation = NotHandled;
                            if (debug.ExceptionCode == 0x80000003 && debug.FirstChance != 0 && !loaderBreakpointHandled)
                            {
                                loaderBreakpointHandled = true; continuation = Continue;
                                break;
                            }
                            // First-chance CLR/AV events can be handled normally by the child.
                            // Do not turn them into successful handling or suppress a fatal event.
                            if (debug.ExceptionCode == 0xc0000005 || debug.FirstChance == 0)
                            {
                                SnapshotModules(result);
                                var address = unchecked((ulong)debug.ExceptionAddress.ToInt64());
                                var module = result.Modules.FirstOrDefault(m => m.Size > 0 && address >= Convert.ToUInt64(m.BaseAddress[2..], 16) && address - Convert.ToUInt64(m.BaseAddress[2..], 16) < (ulong)m.Size);
                                if (result.Exceptions.Count < 200)
                                    result.Exceptions.Add(new($"0x{debug.ExceptionCode:X8}", $"0x{address:X16}", debug.FirstChance != 0, debug.ThreadId,
                                        module?.Path, module is null ? null : $"0x{address - Convert.ToUInt64(module.BaseAddress[2..], 16):X}",
                                        debug.ExceptionCode == 0xc0000005 && debug.NumberParameters >= 2 ? debug.Information0 switch { 0 => "read", 1 => "write", 8 => "execute", _ => "unknown" } : null,
                                        debug.ExceptionCode == 0xc0000005 && debug.NumberParameters >= 2 ? $"0x{debug.Information1:X16}" : null));
                                else if (!result.ValidationErrors.Contains("异常记录达到200项上限。")) result.ValidationErrors.Add("异常记录达到200项上限。");
                                // CoreCLR can terminate on a native AV without a second-chance event.
                                // Capture the first AV too; exit status, not this event alone, decides failure.
                                if (result.DumpAttempts < 32 || debug.FirstChance == 0) WriteDump(process.Process, debug, result);
                                else if (!result.ValidationErrors.Contains("首次异常转储达到32份上限，后续首次异常仅保留事件记录。"))
                                    result.ValidationErrors.Add("首次异常转储达到32份上限，后续首次异常仅保留事件记录。");
                                Program.Save(Path.Combine(output, "native-events.json"), result);
                            }
                            break;
                        case 5:
                            result.ExitCode = debug.ExitCode; exited = true;
                            break;
                    }
                }
                finally
                {
                    if (!ContinueDebugEvent(debug.ProcessId, debug.ThreadId, continuation))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "继续原生调试事件失败。");
                }
            }
            result.CleanupConfirmed = WaitForSingleObject(process.Process, 5000) == 0;
            return result;
        }
        finally
        {
            // Never enumerate or terminate other processes. The retained handle belongs to this CreateProcess call.
            if (!exited)
            {
                TerminateProcess(process.Process, 1460);
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (!WaitForDebugEvent(out var pending, 100)) continue;
                    if (pending.EventCode is 3 or 6 && pending.FileHandle != IntPtr.Zero) CloseHandle(pending.FileHandle);
                    ContinueDebugEvent(pending.ProcessId, pending.ThreadId, pending.EventCode == 1 ? NotHandled : Continue);
                    if (pending.EventCode == 5) { result.ExitCode = pending.ExitCode; break; }
                }
                result.CleanupConfirmed = WaitForSingleObject(process.Process, 5000) == 0;
            }
            CloseHandle(process.Process);
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            Program.Save(Path.Combine(output, "result.json"), result);
        }
    }

    private static void RecordModule(TrialResult result, IntPtr file, IntPtr address)
    {
        if (file == IntPtr.Zero) return;
        var buffer = new StringBuilder(32768);
        if (GetFinalPathNameByHandle(file, buffer, buffer.Capacity, 0) == 0) return;
        var path = buffer.ToString(); if (path.StartsWith(@"\\?\")) path = path[4..];
        string? version = null;
        try { version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch (Exception) { /* The module may have been loaded from a temporary/deleted file. */ }
        result.Modules.Add(new(path, $"0x{unchecked((ulong)address.ToInt64()):X16}", 0, version));
    }

    private static void SnapshotModules(TrialResult result)
    {
        try
        {
            using var child = Process.GetProcessById(result.ProcessId);
            foreach (ProcessModule module in child.Modules)
            {
                var address = $"0x{unchecked((ulong)module.BaseAddress.ToInt64()):X16}";
                result.Modules.RemoveAll(item => item.BaseAddress == address);
                result.Modules.Add(new(module.FileName, address, module.ModuleMemorySize, module.FileVersionInfo.FileVersion));
            }
        }
        catch (Exception error) { result.ValidationErrors.Add("故障时模块枚举失败：" + error.Message); }
    }

    private static void WriteDump(IntPtr process, DebugEvent debug, TrialResult result)
    {
        var thread = OpenThread(0x0008, false, debug.ThreadId); // THREAD_GET_CONTEXT, only the faulting child thread
        var allocation = Marshal.AllocHGlobal(1248);
        var context = new IntPtr((allocation.ToInt64() + 15) & ~15L);
        var record = Marshal.AllocHGlobal(152); var pointers = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new byte[1232], 0, context, 1232); Marshal.WriteInt32(context, 48, 0x100003);
            if (thread == IntPtr.Zero || !GetThreadContext(thread, context)) throw new Win32Exception(Marshal.GetLastWin32Error(), "获取故障线程寄存器失败。");
            // Copy the OS-reported EXCEPTION_RECORD; no child memory/registers are written.
            var eventPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DebugEvent>());
            try
            {
                Marshal.StructureToPtr(debug, eventPointer, false); var bytes = new byte[152];
                Marshal.Copy(eventPointer + 16, bytes, 0, bytes.Length); Marshal.Copy(bytes, 0, record, bytes.Length);
            }
            finally { Marshal.FreeHGlobal(eventPointer); }
            Marshal.WriteIntPtr(pointers, record); Marshal.WriteIntPtr(pointers, 8, context);
            var info = new DumpException { ThreadId = debug.ThreadId, Pointers = pointers, ClientPointers = false };
            var path = Path.Combine(result.Output, $"native-event-{++result.DumpAttempts:000}.dmp");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!MiniDumpWriteDump(process, (uint)result.ProcessId, file.SafeFileHandle.DangerousGetHandle(), 0x1000, ref info, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "写入原生转储失败。");
            result.DumpPath = path; result.DumpFiles.Add(path);
        }
        catch (Exception error) { result.DumpError = error.ToString(); }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            Marshal.FreeHGlobal(allocation); Marshal.FreeHGlobal(record); Marshal.FreeHGlobal(pointers);
        }
    }

    // Windows command-line quoting, including literal quotes and trailing backslashes.
    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; text.Append(c);
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CountX, CountY, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    // minidumpapiset.h includes pshpack4.h; this differs from the regular Win32 x64 ABI.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DumpException { public uint ThreadId; public IntPtr Pointers; [MarshalAs(UnmanagedType.Bool)] public bool ClientPointers; }
    [StructLayout(LayoutKind.Explicit, Size = 176)]
    private struct DebugEvent
    {
        [FieldOffset(0)] public uint EventCode;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public IntPtr FileHandle;
        [FieldOffset(24)] public IntPtr DllBase;
        [FieldOffset(40)] public IntPtr ProcessBase;
        [FieldOffset(16)] public uint ExitCode;
        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(32)] public IntPtr ExceptionAddress;
        [FieldOffset(40)] public uint NumberParameters;
        [FieldOffset(48)] public ulong Information0;
        [FieldOffset(56)] public ulong Information1;
        [FieldOffset(168)] public uint FirstChance;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WaitForDebugEvent(out DebugEvent debug, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ContinueDebugEvent(uint processId, uint threadId, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("Dbghelp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, uint type, ref DumpException exception, IntPtr streams, IntPtr callback);
    [DllImport("kernel32.dll")] internal static extern void RaiseException(uint code, uint flags, uint numberOfArguments, IntPtr arguments);
}
