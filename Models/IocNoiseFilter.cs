using TapRoom.Services;

namespace TapRoom.Models;

public static class IocNoiseFilter
{
    private static readonly string[] BrowserNoiseHosts =
    {
        // Edge / Chromium background services
        "edge.microsoft.com",
        "config.edge.skype.com",
        "edgeassetservice.azureedge.net",
        "edge-consumer-static.azureedge.net",
        "data-edge.smartscreen.microsoft.com",
        "msedgeextensions.sf.tlu.dl.delivery.mp.microsoft.com",
        "msedge.f.tlu.dl.delivery.mp.microsoft.com",
        "msedge.b.tlu.dl.delivery.mp.microsoft.com",
        "msedgedevtools.microsoft.com",
        "www.bing.com",
        "clients2.google.com",
        "clients2.googleusercontent.com",
        "www.googleapis.com",
        "update.googleapis.com",
        "edgedl.me.gvt1.com",

        // Firefox / Mozilla background services
        "detectportal.firefox.com",
        "ads.mozilla.org",
        "ads-img.mozilla.org",
        "archive.mozilla.org",
        "aus5.mozilla.org",
        "ciscobinary.openh264.org",
        "classify-client.services.mozilla.com",
        "content-signature-2.cdn.mozilla.net",
        "firefox-settings-attachments.cdn.mozilla.net",
        "firefox.settings.services.mozilla.com",
        "img-getpocket.cdn.mozilla.net",
        "incoming.telemetry.mozilla.org",
        "location.services.mozilla.com",
        "merino.services.mozilla.com",
        "mozilla-ohttp.fastly-edge.com",
        "normandy.cdn.mozilla.net",
        "prod-images.merino.prod.webservices.mozgcp.net",
        "prod.ohttp-gateway.prod.webservices.mozgcp.net",
        "push.services.mozilla.com",
        "services.addons.mozilla.org"
    };

    private static readonly string[] BrowserNoiseUrlHostsAlways =
    {
        "clients2.googleusercontent.com",
        "data-edge.smartscreen.microsoft.com",
        "msedgeextensions.sf.tlu.dl.delivery.mp.microsoft.com",
        "msedge.f.tlu.dl.delivery.mp.microsoft.com",
        "msedge.b.tlu.dl.delivery.mp.microsoft.com",
        "msedgedevtools.microsoft.com",
        "detectportal.firefox.com",
        "ads.mozilla.org",
        "ads-img.mozilla.org",
        "archive.mozilla.org",
        "aus5.mozilla.org",
        "ciscobinary.openh264.org",
        "classify-client.services.mozilla.com",
        "content-signature-2.cdn.mozilla.net",
        "firefox-settings-attachments.cdn.mozilla.net",
        "firefox.settings.services.mozilla.com",
        "img-getpocket.cdn.mozilla.net",
        "incoming.telemetry.mozilla.org",
        "location.services.mozilla.com",
        "merino.services.mozilla.com",
        "mozilla-ohttp.fastly-edge.com",
        "normandy.cdn.mozilla.net",
        "prod-images.merino.prod.webservices.mozgcp.net",
        "prod.ohttp-gateway.prod.webservices.mozgcp.net",
        "push.services.mozilla.com",
        "services.addons.mozilla.org"
    };

    private static readonly string[] BrowserNoisePathFragments =
    {
        // Edge / Chromium
        "/browsernetworktime/time/",
        "/componentupdater/api/v1/update",
        "/service/update2/crx",
        "/service/update2/json",
        "/crx/blobs/",
        "/config/v1/edge/",
        "/abusiveadblocking/api/v1/blocklist",
        "/entityextractiontemplates/api/v1/assets/find-assets",
        "/assets/arbitration_priority_list/",
        "/assets/domains_config_gz/",
        "/assets/edge_hub_apps_manifest_gz/",
        "/mouse-gesture/config.json",
        "/api/browser/edge/data/settings/",
        "/api/browser/edge/data/toptraffic/",
        "/autofillservice/core/page/",
        "/extensioninstallverifier/",
        "/extensionwebstorebase/",
        "/serviceexperimentation/",
        "/filestreamingservice/files/",
        "/docs/",
        "/serve_file/",
        "/third_party/webhint/",
        "/api/shopping/v1/user/shoppingsettings",
        "/bloomfilterfiles/expandeddomainsfilterglobal.json",
        "/bloomfilterfiles/",
        "/chromewebstore/v1.1/items/verify",

        // Firefox / Mozilla
        "/canonical.html",
        "/success.txt",
        "/v1/images",
        "/v1/ads-preflight",
        "/pub/system-addons/newtab/",
        "/update/",
        "/openh264-",
        "/api/v1/classify_client/",
        "/g/chains/",
        "/bundles/",
        "/main-workspace/",
        "/security-state-staging/",
        "/v1/buckets/",
        "/submit/firefox-desktop/",
        "/api/v1/suggest",
        "/api/v1/curated-recommendations",
        "/v1/country",
        "/api/v4/addons/search/",
        "/ohttp-configs"
    };

    private static readonly object UserRulesLock = new();
    private static DateTime _lastUserRulesLoadUtc = DateTime.MinValue;
    private static UserNoiseRules _userRules = UserNoiseRules.Empty;

    public static string UserNoiseDirectory => Path.Combine(PathHelper.LocalAppDataRoot, "Config", "NoiseFilters");
    public static string UserNoiseDomainsFile => Path.Combine(UserNoiseDirectory, "noise-domains.txt");
    public static string UserNoiseUrlPatternsFile => Path.Combine(UserNoiseDirectory, "noise-url-patterns.txt");

    public static void EnsureUserFilterFiles()
    {
        Directory.CreateDirectory(UserNoiseDirectory);

        if (!File.Exists(UserNoiseDomainsFile))
        {
            File.WriteAllText(UserNoiseDomainsFile,
                "# TapRoom user browser-noise domains\r\n" +
                "# One domain per line. Blank lines and lines starting with # are ignored.\r\n" +
                "# Examples:\r\n" +
                "# example.com          matches exactly example.com\r\n" +
                "# *.example.com       matches subdomains of example.com\r\n" +
                "# .example.com        matches example.com and all subdomains\r\n" +
                "\r\n");
        }

        if (!File.Exists(UserNoiseUrlPatternsFile))
        {
            File.WriteAllText(UserNoiseUrlPatternsFile,
                "# TapRoom user browser-noise URL patterns\r\n" +
                "# One pattern per line. Blank lines and lines starting with # are ignored.\r\n" +
                "# Patterns are case-insensitive substring matches against the full URL.\r\n" +
                "# Examples:\r\n" +
                "# /telemetry/\r\n" +
                "# https://updates.example.com/check\r\n" +
                "# update.example.com/api/v1/\r\n" +
                "\r\n");
        }
    }

    public static void ReloadUserRules()
    {
        lock (UserRulesLock)
        {
            EnsureUserFilterFiles();
            _userRules = LoadUserRules();
            _lastUserRulesLoadUtc = DateTime.UtcNow;
        }
    }

    internal delegate bool NoiseClassifier(string type, string value, out string reason);

    // One immutable rule snapshot per batch avoids repeated disk checks and
    // ensures that every IOC in a result uses the same rules.
    internal static NoiseClassifier CreateClassifier()
    {
        EnsureUserRulesLoaded();
        UserNoiseRules rules;
        lock (UserRulesLock) rules = _userRules;
        return (string type, string value, out string reason) => Classify(type, value, rules, out reason);
    }

    public static bool IsBrowserNoise(string type, string value, out string reason) =>
        CreateClassifier()(type, value, out reason);

    private static bool Classify(string type, string value, UserNoiseRules rules, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return type.Contains("URL", StringComparison.OrdinalIgnoreCase)
            ? IsBrowserNoiseUrl(value, rules, out reason)
            : type.Contains("domain", StringComparison.OrdinalIgnoreCase) && IsBrowserNoiseDomain(value, rules, out reason);
    }

    private static bool IsBrowserNoiseDomain(string value, UserNoiseRules rules, out string reason)
    {
        reason = string.Empty;
        var host = value.Trim().TrimEnd('.').ToLowerInvariant();
        // These exact SmartScreen hosts are noise in Domain and POST Domain views.
        // Keep this separate from BrowserNoiseHosts: the URL classifier uses that
        // list with generic path fragments, whereas these hosts have specific URL rules.
        if (host is "nav-edge.smartscreen.microsoft.com" or "telem-edge.smartscreen.microsoft.com")
        {
            reason = "Microsoft Edge SmartScreen background domain";
            return true;
        }

        if (BrowserNoiseHosts.Any(noiseHost => string.Equals(host, noiseHost, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Known browser/vendor background domain";
            return true;
        }

        if (MatchesUserNoiseDomain(host, rules, out var matchedRule))
        {
            reason = $"User noise domain: {matchedRule}";
            return true;
        }

        return false;
    }

    private static bool IsBrowserNoiseUrl(string value, UserNoiseRules rules, out string reason)
    {
        reason = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        var fullUrl = value.Trim().ToLowerInvariant();

        // Match only these known Edge SmartScreen endpoints, not arbitrary URLs
        // containing the same path or all traffic to either host. Queries may vary.
        if (uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
            ((host == "nav-edge.smartscreen.microsoft.com" && uri.AbsolutePath == "/api/browser/edge/navigate/3") ||
             (host == "telem-edge.smartscreen.microsoft.com" && uri.AbsolutePath == "/api/browser/edge/telemetry/3")))
        {
            reason = "Microsoft Edge SmartScreen navigation / telemetry";
            return true;
        }

        if (BrowserNoiseUrlHostsAlways.Any(noiseHost => string.Equals(host, noiseHost, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Known browser/vendor background URL";
            return true;
        }

        if (BrowserNoiseHosts.Any(noiseHost => string.Equals(host, noiseHost, StringComparison.OrdinalIgnoreCase)) &&
            BrowserNoisePathFragments.Any(fragment => path.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Known browser/vendor background URL";
            return true;
        }

        if (MatchesUserNoiseDomain(host, rules, out var matchedDomainRule))
        {
            reason = $"User noise domain: {matchedDomainRule}";
            return true;
        }

        var matchedUrlRule = rules.UrlPatterns.FirstOrDefault(pattern => fullUrl.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matchedUrlRule))
        {
            reason = $"User noise URL pattern: {matchedUrlRule}";
            return true;
        }

        return false;
    }

    private static void EnsureUserRulesLoaded()
    {
        lock (UserRulesLock)
        {
            try
            {
                EnsureUserFilterFiles();
                var newestWriteUtc = new[]
                {
                    File.GetLastWriteTimeUtc(UserNoiseDomainsFile),
                    File.GetLastWriteTimeUtc(UserNoiseUrlPatternsFile)
                }.Max();

                if (_lastUserRulesLoadUtc == DateTime.MinValue || newestWriteUtc > _lastUserRulesLoadUtc)
                {
                    _userRules = LoadUserRules();
                    _lastUserRulesLoadUtc = DateTime.UtcNow;
                }
            }
            catch
            {
                // Do not let a broken user-editable filter file break capture review.
                _userRules = UserNoiseRules.Empty;
                _lastUserRulesLoadUtc = DateTime.UtcNow;
            }
        }
    }

    private static UserNoiseRules LoadUserRules()
    {
        var domains = ReadUserRules(UserNoiseDomainsFile)
            .Select(NormaliseDomainRule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var urlPatterns = ReadUserRules(UserNoiseUrlPatternsFile)
            .Select(rule => rule.Trim().ToLowerInvariant())
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new UserNoiseRules(domains, urlPatterns);
    }

    private static IEnumerable<string> ReadUserRules(string path)
    {
        if (!File.Exists(path))
            yield break;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            yield return trimmed;
        }
    }

    private static string NormaliseDomainRule(string rule)
    {
        var cleaned = rule.Trim().ToLowerInvariant();

        if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
                cleaned = uri.Host;
        }

        cleaned = cleaned.TrimEnd('.');
        return cleaned;
    }

    private static bool MatchesUserNoiseDomain(string host, UserNoiseRules rules, out string matchedRule)
    {
        matchedRule = string.Empty;

        foreach (var rule in rules.Domains)
        {
            if (string.IsNullOrWhiteSpace(rule))
                continue;

            if (rule.StartsWith("*."))
            {
                var suffix = rule[1..]; // keep leading dot
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !string.Equals(host, suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                {
                    matchedRule = rule;
                    return true;
                }
            }
            else if (rule.StartsWith('.'))
            {
                var bare = rule.TrimStart('.');
                if (string.Equals(host, bare, StringComparison.OrdinalIgnoreCase) || host.EndsWith(rule, StringComparison.OrdinalIgnoreCase))
                {
                    matchedRule = rule;
                    return true;
                }
            }
            else if (string.Equals(host, rule, StringComparison.OrdinalIgnoreCase))
            {
                matchedRule = rule;
                return true;
            }
        }

        return false;
    }

    private sealed record UserNoiseRules(IReadOnlyList<string> Domains, IReadOnlyList<string> UrlPatterns)
    {
        public static UserNoiseRules Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
    }
}
