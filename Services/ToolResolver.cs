using System.IO;
namespace TapRoom.Services;

public static class ToolResolver
{
    public static string? FindExecutable(string exeName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new List<string>
        {
            // Prefer TapRoom-managed pinned tools over PATH so setup is reproducible.
            Path.Combine(PathHelper.LocalToolsRoot, "mitmproxy", "current", exeName),
            Path.Combine(PathHelper.LocalToolsRoot, "mitmproxy", exeName),
            Path.Combine(PathHelper.LocalToolsRoot, exeName),
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(PathHelper.ToolsRoot, exeName),
            Path.Combine(PathHelper.ToolsRoot, "mitmproxy", exeName),
            Path.Combine(PathHelper.ToolsRoot, "mitmproxy", "bin", exeName),

            // Common locations used by the official Windows installer.
            Path.Combine(localAppData, "Programs", "mitmproxy", exeName),
            Path.Combine(localAppData, "Programs", "mitmproxy", "bin", exeName),
            Path.Combine(programFiles, "mitmproxy", exeName),
            Path.Combine(programFiles, "mitmproxy", "bin", exeName),
            Path.Combine(programFilesX86, "mitmproxy", exeName),
            Path.Combine(programFilesX86, "mitmproxy", "bin", exeName)
        };

        var fromPath = FindOnPath(exeName);
        if (fromPath is not null)
            candidates.Add(fromPath);

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(part.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }
}
