using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using TapRoom.Models;

namespace TapRoom.Services;

internal static class BrowserProfileMonitor
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(
        string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    internal sealed record Snapshot(bool Reliable, IReadOnlyList<int> ProcessIds, string? Error = null);

    // A query failure is not proof of browser closure. Match the exact profile
    // argument, not a substring or the name of an unrelated browser process.
    public static Snapshot Scan(BrowserKind browser, string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return new(false, Array.Empty<int>(), "No profile path");
        try
        {
            var name = browser == BrowserKind.Edge ? "msedge.exe" : "firefox.exe";
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='{name}'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            using var results = searcher.Get();
            var matches = new List<int>();
            var unreadable = false;
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    if (item["CommandLine"] is not string cmd || string.IsNullOrWhiteSpace(cmd))
                    { unreadable = true; continue; }
                    var args = Split(cmd);
                    if (MatchesProfile(browser, args, profile)) matches.Add(Convert.ToInt32(item["ProcessId"]));
                }
            }
            return new(!unreadable, matches, unreadable ? "Some browser command lines could not be read" : null);
        }
        catch (Exception ex) { return new(false, Array.Empty<int>(), ex.Message); }
    }

    internal static bool MatchesProfile(BrowserKind browser, IReadOnlyList<string> args, string profile)
    {
        // Renderer/helper processes must not be mistaken for the main browser.
        if (args.Any(a => a.StartsWith("--type=", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("-contentproc", StringComparison.OrdinalIgnoreCase))) return false;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;
            if (browser == BrowserKind.Edge && arg.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase))
                value = arg["--user-data-dir=".Length..];
            else if (i + 1 < args.Count && (browser == BrowserKind.Edge
                         ? arg.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase)
                         : arg.Equals("-profile", StringComparison.OrdinalIgnoreCase) || arg.Equals("--profile", StringComparison.OrdinalIgnoreCase)))
                value = args[++i];
            if (value is not null && string.Equals(value.TrimEnd('\\', '/').Replace('/', '\\'),
                profile.TrimEnd('\\', '/').Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string[] Split(string commandLine)
    {
        var ptr = CommandLineToArgvW(commandLine, out var count);
        if (ptr == IntPtr.Zero) throw new InvalidOperationException("Could not parse browser arguments.");
        try
        {
            var args = new string[count];
            for (var i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally { LocalFree(ptr); }
    }

    // Close only exact-profile browser windows. Never kill by process name.
    public static async Task<bool> CloseAsync(BrowserKind browser, string profile)
    {
        var snapshot = Scan(browser, profile);
        if (!snapshot.Reliable) return false;
        foreach (var pid in snapshot.ProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                _ = process.Handle;
                // Recheck identity after opening a handle to avoid PID reuse.
                if (Scan(browser, profile).ProcessIds.Contains(pid) && !process.HasExited)
                    process.CloseMainWindow();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            snapshot = Scan(browser, profile);
            if (snapshot.Reliable && snapshot.ProcessIds.Count == 0) return true;
        }
        return false;
    }
}
