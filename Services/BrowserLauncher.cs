using System.IO;
using System.Diagnostics;
using System.Text;
using TapRoom.Models;

namespace TapRoom.Services;

public sealed class BrowserLauncher
{
    public string? EdgePath { get; set; }
    public bool EdgeHideCopilot { get; set; } = true;
    public bool EdgeHideSidebar { get; set; } = true;
    public bool EdgeSuppressFirstRun { get; set; } = true;
    public bool EdgeDisableStartupBoost { get; set; } = true;
    public bool EdgeDisableBackgroundMode { get; set; } = true;
    public string? FirefoxPath { get; set; }
    public string? FirefoxPortablePath { get; set; }

    public BrowserLaunchResult Launch(BrowserKind browser, string caseName, string url, int proxyPort, string profileDirectory)
    {
        Directory.CreateDirectory(profileDirectory);

        return browser switch
        {
            BrowserKind.Edge => LaunchEdge(caseName, url, proxyPort, profileDirectory),
            BrowserKind.Firefox => LaunchFirefox(url, proxyPort, profileDirectory, portable: false),
            BrowserKind.FirefoxPortable => LaunchFirefox(url, proxyPort, profileDirectory, portable: true),
            _ => throw new NotSupportedException($"Unsupported browser: {browser}")
        };
    }

    private BrowserLaunchResult LaunchEdge(string caseName, string url, int proxyPort, string profileDirectory)
    {
        var exe = ResolveEdgePath();
        // Only seed preferences in a brand-new, empty investigation profile.
        // Never edit Preferences for an existing or potentially running browser.
        SeedEdgePreferences(profileDirectory);
        return new BrowserLaunchResult(BrowserKind.Edge, Start(exe, BuildEdgeArguments(url, proxyPort, profileDirectory)), profileDirectory, exe);
    }

    internal IReadOnlyList<string> BuildEdgeArguments(string url, int proxyPort, string profileDirectory)
    {
        var args = new List<string>
        {
            $"--user-data-dir={profileDirectory}",
            $"--proxy-server=http=127.0.0.1:{proxyPort};https=127.0.0.1:{proxyPort}",
            "--new-window", "--no-default-browser-check"
        };
        if (EdgeSuppressFirstRun) args.Add("--no-first-run");
        if (EdgeDisableBackgroundMode) args.Add("--disable-background-mode");
        // Edge's internal feature names are version-dependent, not stable policies.
        // Build a single switch so one group does not overwrite another.
        var disabledFeatures = new List<string>();
        if (EdgeDisableStartupBoost) disabledFeatures.Add("msEdgeStartupBoost");
        if (EdgeHideCopilot) disabledFeatures.AddRange(new[] { "msCopilot", "msUndersideButton" });
        if (EdgeHideSidebar) disabledFeatures.AddRange(new[] { "msEdgeSidebarV2", "msGenericIconSidebarButton" });
        if (disabledFeatures.Count != 0) args.Add("--disable-features=" + string.Join(",", disabledFeatures));
        args.Add(url);
        return args;
    }

    internal void SeedEdgePreferences(string profileDirectory)
    {
        if (!EdgeHideSidebar || Directory.EnumerateFileSystemEntries(profileDirectory).Any()) return;
        // These known profile preference keys are best-effort; newer versions may
        // ignore them. Do not copy another profile, Secure Preferences or Local State.
        var defaultDirectory = Path.Combine(profileDirectory, "Default");
        Directory.CreateDirectory(defaultDirectory);
        var path = Path.Combine(defaultDirectory, "Preferences");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(stream, new
                {
                    browser = new { show_hub_apps_tower = false, show_hub_apps_tower_pinned = false }
                });
            }
            // Do not overwrite an existing profile file even if it appeared concurrently.
            File.Move(temp, path);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private BrowserLaunchResult LaunchFirefox(string url, int proxyPort, string profileDirectory, bool portable)
    {
        WriteFirefoxProfilePreferences(profileDirectory, proxyPort);

        var exe = portable ? ResolveFirefoxPortablePath() : ResolveFirefoxPath();

        // -no-remote and -new-instance stop Firefox from opening the URL as a tab
        // in an existing Firefox window. The explicit -profile path keeps each
        // capture session isolated and lets TapRoom write per-session proxy prefs.
        var args = new[] { "-no-remote", "-new-instance", "-profile", profileDirectory, url };
        return new BrowserLaunchResult(portable ? BrowserKind.FirefoxPortable : BrowserKind.Firefox, Start(exe, args), profileDirectory, exe);
    }

    private static void WriteFirefoxProfilePreferences(string profileDirectory, int proxyPort)
    {
        Directory.CreateDirectory(profileDirectory);

        var userJs = Path.Combine(profileDirectory, "user.js");
        File.WriteAllText(userJs, $$"""
// Generated by TapRoom for this capture profile.
// These settings are intended for a short-lived investigation browser profile,
// not for a user's normal day-to-day Firefox profile.

// Proxy TapRoom traffic through the per-capture mitmproxy instance.
user_pref("network.proxy.type", 1);
user_pref("network.proxy.http", "127.0.0.1");
user_pref("network.proxy.http_port", {{proxyPort}});
user_pref("network.proxy.ssl", "127.0.0.1");
user_pref("network.proxy.ssl_port", {{proxyPort}});
user_pref("network.proxy.no_proxies_on", "");
user_pref("network.proxy.share_proxy_settings", false);

// Keep the investigation profile quiet and disposable.
user_pref("browser.shell.checkDefaultBrowser", false);
user_pref("browser.startup.page", 0);
user_pref("browser.startup.homepage", "about:blank");
user_pref("browser.startup.homepage_override.mstone", "ignore");
user_pref("startup.homepage_welcome_url", "");
user_pref("startup.homepage_welcome_url.additional", "");
user_pref("startup.homepage_override_url", "");
user_pref("browser.aboutwelcome.enabled", false);
user_pref("browser.newtabpage.enabled", false);
user_pref("browser.newtab.preload", false);

// Disable Firefox Home / Pocket / sponsored suggestion background fetches.
user_pref("browser.newtabpage.activity-stream.enabled", false);
user_pref("browser.newtabpage.activity-stream.feeds.topsites", false);
user_pref("browser.newtabpage.activity-stream.feeds.snippets", false);
user_pref("browser.newtabpage.activity-stream.feeds.section.topstories", false);
user_pref("browser.newtabpage.activity-stream.feeds.system.topstories", false);
user_pref("browser.newtabpage.activity-stream.feeds.discoverystreamfeed", false);
user_pref("browser.newtabpage.activity-stream.showSponsored", false);
user_pref("browser.newtabpage.activity-stream.showSponsoredTopSites", false);
user_pref("extensions.pocket.enabled", false);
user_pref("browser.urlbar.quicksuggest.enabled", false);
user_pref("browser.urlbar.suggest.quicksuggest", false);
user_pref("browser.urlbar.suggest.quicksuggest.nonsponsored", false);
user_pref("browser.urlbar.suggest.quicksuggest.sponsored", false);
user_pref("browser.urlbar.suggest.pocket", false);
user_pref("browser.urlbar.suggest.trending", false);

// Reduce Firefox network checks and telemetry generated by a fresh profile.
user_pref("network.captive-portal-service.enabled", false);
user_pref("network.connectivity-service.enabled", false);
user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
user_pref("toolkit.telemetry.enabled", false);
user_pref("toolkit.telemetry.unified", false);
user_pref("toolkit.telemetry.archive.enabled", false);
user_pref("toolkit.telemetry.bhrPing.enabled", false);
user_pref("toolkit.telemetry.firstShutdownPing.enabled", false);
user_pref("toolkit.telemetry.newProfilePing.enabled", false);
user_pref("toolkit.telemetry.shutdownPingSender.enabled", false);
user_pref("toolkit.telemetry.updatePing.enabled", false);
user_pref("toolkit.telemetry.server", "");
user_pref("toolkit.coverage.opt-out", true);
user_pref("toolkit.coverage.endpoint.base", "");
user_pref("browser.ping-centre.telemetry", false);
user_pref("browser.newtabpage.activity-stream.telemetry", false);
user_pref("browser.newtabpage.activity-stream.feeds.telemetry", false);
user_pref("datareporting.policy.dataSubmissionEnabled", false);
user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);
user_pref("datareporting.healthreport.uploadEnabled", false);
user_pref("datareporting.healthreport.service.enabled", false);
user_pref("app.shield.optoutstudies.enabled", false);
user_pref("app.normandy.enabled", false);
user_pref("app.normandy.api_url", "");

// Aggressively quiet Safe Browsing checks for disposable VM investigation sessions.
// This is deliberate for TapRoom captures where the operator is intentionally
// visiting suspicious sites and wants to log the traffic without browser lookups.
user_pref("browser.safebrowsing.malware.enabled", false);
user_pref("browser.safebrowsing.phishing.enabled", false);
user_pref("browser.safebrowsing.blockedURIs.enabled", false);
user_pref("browser.safebrowsing.downloads.enabled", false);
user_pref("browser.safebrowsing.downloads.remote.enabled", false);
user_pref("browser.safebrowsing.downloads.remote.url", "");
user_pref("browser.safebrowsing.downloads.remote.block_potentially_unwanted", false);
user_pref("browser.safebrowsing.downloads.remote.block_uncommon", false);
user_pref("browser.safebrowsing.provider.google.gethashURL", "");
user_pref("browser.safebrowsing.provider.google.updateURL", "");
user_pref("browser.safebrowsing.provider.google4.gethashURL", "");
user_pref("browser.safebrowsing.provider.google4.updateURL", "");
user_pref("browser.safebrowsing.provider.mozilla.gethashURL", "");
user_pref("browser.safebrowsing.provider.mozilla.updateURL", "");
user_pref("browser.safebrowsing.reportPhishURL", "");

// Reduce update/service background traffic for this temporary profile.
user_pref("app.update.auto", false);
user_pref("app.update.background.scheduling.enabled", false);
user_pref("browser.search.update", false);
user_pref("extensions.update.enabled", false);
user_pref("extensions.getAddons.cache.enabled", false);
user_pref("extensions.systemAddon.update.enabled", false);
user_pref("extensions.systemAddon.update.url", "");
user_pref("media.gmp-manager.updateEnabled", false);
user_pref("media.gmp-manager.url", "");
user_pref("media.gmp-gmpopenh264.provider.enabled", false);

// Suppress remote-settings fetches that create a lot of fresh-profile noise.
// Because TapRoom profiles are disposable, this only affects the capture session.
user_pref("services.settings.server", "");
user_pref("services.settings.server.backup", "");
user_pref("services.settings.poll_interval", 31536000);
""");
    }

    private string ResolveEdgePath()
    {
        if (!string.IsNullOrWhiteSpace(EdgePath))
        {
            if (!File.Exists(EdgePath)) throw new FileNotFoundException("Configured browser executable not found. Check TapRoom settings.", EdgePath);
            return EdgePath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "msedge.exe";
    }

    private string ResolveFirefoxPath()
    {
        if (!string.IsNullOrWhiteSpace(FirefoxPath))
        {
            if (!File.Exists(FirefoxPath)) throw new FileNotFoundException("Configured browser executable not found. Check TapRoom settings.", FirefoxPath);
            return FirefoxPath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "firefox.exe";
    }

    private string ResolveFirefoxPortablePath()
    {
        if (!string.IsNullOrWhiteSpace(FirefoxPortablePath))
        {
            if (!File.Exists(FirefoxPortablePath)) throw new FileNotFoundException("Configured browser executable not found. Check TapRoom settings.", FirefoxPortablePath);
            return FirefoxPortablePath;
        }

        var bundled = Path.Combine(PathHelper.ToolsRoot, "FirefoxPortable", "FirefoxPortable.exe");
        return File.Exists(bundled) ? bundled : "FirefoxPortable.exe";
    }

    private static Process Start(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
    }
}
