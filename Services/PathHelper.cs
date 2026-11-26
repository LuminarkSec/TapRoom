using System.IO;
using System.Text.RegularExpressions;

namespace TapRoom.Services;

public static partial class PathHelper
{
    public static string CasesRoot => Path.Combine(SettingsStore.Current.EffectiveStorageRoot, "Cases");
    public static string ProfilesRoot => Path.Combine(SettingsStore.Current.EffectiveStorageRoot, "Profiles");
    public static string ToolsRoot => Path.Combine(AppContext.BaseDirectory, "Tools");
    public static string LocalAppDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapRoom");
    public static string LocalToolsRoot => Path.Combine(LocalAppDataRoot, "Tools");

    public static string BuildCaseName(string? inputCaseName, string url)
    {
        var baseName = string.IsNullOrWhiteSpace(inputCaseName)
            ? GetDomainForCaseName(url)
            : inputCaseName.Trim();

        var safe = SafeFileName(baseName);
        if (safe.Length > 100) safe = safe[..100];
        return $"{safe}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
    }

    public static string GetDomainForCaseName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return "capture";
    }

    public static string SafeFileName(string value)
    {
        var cleaned = UnsafeChars().Replace(value, "_");
        return string.IsNullOrWhiteSpace(cleaned) ? "capture" : cleaned;
    }

    [GeneratedRegex("[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeChars();
}
