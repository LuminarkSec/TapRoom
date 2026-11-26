using TapRoom.Models;
using TapRoom.Services;

namespace TapRoom;

public sealed record StartupOptions
{
    public string? Url { get; init; }
    public string? CaseName { get; init; }
    public BrowserKind Browser { get; init; } = BrowserKind.Edge;
    public bool AutoLaunchBrowser { get; init; } = true;
    public bool StopAndParseOnBrowserClose { get; init; } = true;
    public bool AutoParseAfterStop { get; init; } = true;
    public bool PromptForUrl { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        var defaults = SettingsStore.Current;
        var options = new StartupOptions
        {
            Browser = defaults.DefaultBrowser,
            AutoLaunchBrowser = defaults.AutoLaunchBrowser,
            StopAndParseOnBrowserClose = defaults.StopOnBrowserClose,
            AutoParseAfterStop = defaults.AutoParseAfterStop
        };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var normalised = arg.Trim();

            if (IsSwitch(normalised, "--url", "-u") && TryReadValue(args, ref i, out var url))
                options = options with { Url = url };
            else if (IsSwitch(normalised, "--case", "--case-name", "-c") && TryReadValue(args, ref i, out var caseName))
                options = options with { CaseName = caseName };
            else if (IsSwitch(normalised, "--browser", "-b") && TryReadValue(args, ref i, out var browser))
                options = options with { Browser = ParseBrowser(browser) };
            else if (IsSwitch(normalised, "--prompt-url", "--url-prompt", "--quick-url", "--input-url"))
                options = options with { PromptForUrl = true };
            else if (IsSwitch(normalised, "--auto-browser"))
                options = options with { AutoLaunchBrowser = true };
            else if (IsSwitch(normalised, "--auto-stop"))
                options = options with { StopAndParseOnBrowserClose = true };
            else if (IsSwitch(normalised, "--auto-parse"))
                options = options with { AutoParseAfterStop = true };
            else if (IsSwitch(normalised, "--no-auto-browser"))
                options = options with { AutoLaunchBrowser = false };
            else if (IsSwitch(normalised, "--no-stop-parse"))
                options = options with { StopAndParseOnBrowserClose = false, AutoParseAfterStop = false };
            else if (IsSwitch(normalised, "--no-auto-parse"))
                options = options with { AutoParseAfterStop = false };
            else if (IsSwitch(normalised, "--no-auto-stop"))
                options = options with { StopAndParseOnBrowserClose = false };
            else if (options.Url is null && LooksLikeUrl(normalised))
                options = options with { Url = normalised };
        }

        return options;
    }

    private static bool IsSwitch(string value, params string[] names) =>
        names.Any(name => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
            return false;

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool LooksLikeUrl(string value) => UrlNormaliser.LooksLikeUrl(value);

    private static BrowserKind ParseBrowser(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "firefox" => BrowserKind.Firefox,
            "ff" => BrowserKind.Firefox,
            "portable" => BrowserKind.FirefoxPortable,
            "firefoxportable" => BrowserKind.FirefoxPortable,
            "firefox-portable" => BrowserKind.FirefoxPortable,
            _ => BrowserKind.Edge
        };
    }
}
