using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TapRoom.Services;

public static class WindowsProcessTools
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CtrlCEvent = 0;
    private static readonly object ConsoleGate = new();

    // Each engine owns a private console. Restrict inherited handles to its three
    // standard streams; stdout/stderr go directly to files without pipe deadlocks.
    public static Process StartInPrivateConsole(string fileName, string arguments, string workingDirectory,
        string? stdout = null, string? stderr = null)
    {
        lock (ConsoleGate)
        {
            using var input = OpenStream("NUL", false);
            using var output = OpenStream(stdout ?? "NUL", true);
            using var error = OpenStream(stderr ?? "NUL", true);
            var handles = new[] { input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle() };
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var list = Marshal.AllocHGlobal(size);
            var values = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            var initialised = false;
            try
            {
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                initialised = true;
                Marshal.Copy(handles, 0, values, handles.Length);
                if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020002, values,
                        (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfoEx>(),
                        dwFlags = StartfUseShowWindow | StartfUseStdHandles,
                        wShowWindow = 0,
                        hStdInput = handles[0], hStdOutput = handles[1], hStdError = handles[2]
                    },
                    AttributeList = list
                };
                var command = new StringBuilder($"\"{fileName}\" {arguments}");
                if (!CreateProcess(fileName, command, IntPtr.Zero, IntPtr.Zero, true,
                        CreateNewConsole | ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory,
                        ref startup, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to start {fileName}.");
                try
                {
                    var process = Process.GetProcessById((int)pi.dwProcessId);
                    _ = process.Handle; // retain an open handle before closing the native one
                    return process;
                }
                catch
                {
                    TerminateProcess(pi.hProcess, 1);
                    WaitForSingleObject(pi.hProcess, 5000);
                    throw;
                }
                finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
            }
            finally
            {
                if (initialised) DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(values);
                Marshal.FreeHGlobal(list);
            }
        }
    }

    // CTRL+C is SIGINT for mitmdump. Group zero targets only its private console.
    // The GUI temporarily joins that console and ignores CTRL+C itself.
    public static bool TrySendCtrlC(Process process, TimeSpan waitForExit, out string detail)
    {
        try
        {
            lock (ConsoleGate)
            {
                if (process.HasExited) { detail = "CTRL+C not sent: process already exited."; return false; }
                if (!AttachConsole((uint)process.Id))
                {
                    detail = $"AttachConsole failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return false;
                }
                try
                {
                    if (!SetConsoleCtrlHandler(IntPtr.Zero, true))
                    {
                        detail = $"SetConsoleCtrlHandler failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                        return false;
                    }
                    if (!GenerateConsoleCtrlEvent(CtrlCEvent, 0))
                    {
                        detail = $"GenerateConsoleCtrlEvent failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                        return false;
                    }
                    var exited = process.WaitForExit((int)waitForExit.TotalMilliseconds);
                    detail = $"CTRL+C sent; process exited={exited}.";
                    return exited;
                }
                finally { FreeConsole(); }
            }
        }
        catch (Exception ex) { detail = $"CTRL+C shutdown failed: {ex.Message}"; return false; }
    }

    private static SafeFileHandle OpenStream(string path, bool write)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        // FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
        var handle = CreateFile(path, write ? (path == "NUL" ? 0x40000000u : 4u) : 0x80000000u, 7, ref security,
            path == "NUL" ? 3u : 4u, 0x80, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"Cannot open process stream: {path}");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
}
