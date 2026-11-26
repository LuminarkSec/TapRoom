using System.Text.Json;
using System.Text.Json.Serialization;
using TapRoom.Models;

namespace TapRoom.Services;

public sealed record AppSettings
{
    public string StorageRoot { get; init; } = "";
    public BrowserKind DefaultBrowser { get; init; } = BrowserKind.Edge;
    public bool AutoLaunchBrowser { get; init; } = true;
    public bool StopOnBrowserClose { get; init; } = true;
    public bool AutoParseAfterStop { get; init; } = true;
    public bool HideBrowserNoise { get; init; } = true;
    public bool ScreenshotEnabled { get; init; } = false;
    public int ScreenshotIntervalSeconds { get; init; } = 2;
    public double ScreenshotChangePercent { get; init; } = 3;
    public bool ScreenshotSaveInitial { get; init; } = true;
    public int ScreenshotMaximum { get; init; } = 500;
    public int FirstProxyPort { get; init; } = 9000;
    public int LastProxyPort { get; init; } = 9999;
    public string EdgePath { get; init; } = "";
    public bool EdgeHideCopilot { get; init; } = true;
    public bool EdgeHideSidebar { get; init; } = true;
    public bool EdgeSuppressFirstRun { get; init; } = true;
    public bool EdgeDisableStartupBoost { get; init; } = true;
    public bool EdgeDisableBackgroundMode { get; init; } = true;
    public string FirefoxPath { get; init; } = "";
    public string FirefoxPortablePath { get; init; } = "";
    [JsonIgnore]
    public string EffectiveStorageRoot => string.IsNullOrWhiteSpace(StorageRoot) ? AppContext.BaseDirectory : Path.GetFullPath(StorageRoot);

    public AppSettings Validate()
    {
        if (ScreenshotIntervalSeconds is < 1 or > 60 || ScreenshotMaximum is < 1 or > 5000 ||
            !double.IsFinite(ScreenshotChangePercent) || ScreenshotChangePercent is < 0.1 or > 100)
            throw new ArgumentException("Screenshots: interval must be 1–60 seconds, change 0.1–100%, and limit 1–5000.");
        if (!Enum.IsDefined(DefaultBrowser)) throw new ArgumentException("Select a supported browser.");
        if (FirstProxyPort < 1024 || LastProxyPort > 11999 || LastProxyPort < FirstProxyPort)
            throw new ArgumentException("Proxy ports must be an ordered range within 1024–11999 (12000–12999 are reserved for mitmweb).");
        if (!string.IsNullOrWhiteSpace(StorageRoot) && !Path.IsPathFullyQualified(StorageRoot))
            throw new ArgumentException("Choose an absolute investigation storage directory.");
        _ = EffectiveStorageRoot;
        CheckExecutable(EdgePath, "msedge.exe");
        CheckExecutable(FirefoxPath, "firefox.exe");
        CheckExecutable(FirefoxPortablePath, "FirefoxPortable.exe");
        return this;
    }

    private static void CheckExecutable(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Enter a full path to {expected}, or leave it blank for automatic detection.");
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string FilePath => Path.Combine(PathHelper.LocalAppDataRoot, "Config", "settings.json");
    private static readonly Lazy<AppSettings> Loaded = new(() => Load(FilePath, out _loadWarning));
    private static string? _loadWarning;
    public static AppSettings Current => Loaded.Value;
    public static string? LoadWarning { get { _ = Current; return _loadWarning; } }

    internal static AppSettings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new AppSettings();
        try { return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Empty settings.")).Validate(); }
        catch (Exception ex)
        {
            warning = $"Could not load settings at {path}: {ex.Message}. Defaults are in use. The original file has not been changed.";
            return new AppSettings();
        }
    }

    internal static void Save(string path, AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    public static void SaveForRestart(AppSettings settings)
    {
        settings.Validate();
        // Check both data directories before saving. Do not move existing cases.
        foreach (var name in new[] { "Cases", "Profiles" })
        {
            var dir = Path.Combine(settings.EffectiveStorageRoot, name);
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".taproom-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        foreach (var path in new[] { settings.EdgePath, settings.FirefoxPath, settings.FirefoxPortablePath })
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path)) throw new FileNotFoundException("Browser executable not found.", path);
        Save(FilePath, settings);
    }
}
