using System.Text;

namespace TapRoom.Models;

public sealed class ParsedIocResult
{
    public string ParsedDirectory { get; init; } = string.Empty;
    public string HarFile { get; init; } = string.Empty;
    public string DomainsFile { get; init; } = string.Empty;
    public string UrlsFile { get; init; } = string.Empty;
    public string PostUrlsFile { get; init; } = string.Empty;
    public string PostDomainsFile { get; init; } = string.Empty;
    public CaptureAnalysis? Analysis { get; init; }
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PostUrls { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PostDomains { get; init; } = Array.Empty<string>();


    public IReadOnlyList<IocEntry> ToEntries()
    {
        var entries = new List<IocEntry>();
        var classify = IocNoiseFilter.CreateClassifier();
        entries.AddRange(PostDomains.Select(value => CreateEntry("POST domain", value, classify)));
        entries.AddRange(PostUrls.Select(value => CreateEntry("POST URL", value, classify)));
        entries.AddRange(Domains.Select(value => CreateEntry("Domain", value, classify)));
        entries.AddRange(Urls.Select(value => CreateEntry("URL", value, classify)));
        if (Analysis?.Available == true)
        {
            entries.AddRange(Analysis.Requests.Select(r => r.PeerIp).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).Select(v => CreateEntry("IP address", v, classify)));
            entries.AddRange(Analysis.Redirects.Select(r => r.DestinationUrl).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Select(v => CreateEntry("Redirect URL", v, classify)));
            foreach (var type in new[] { "SHA-256", "SHA-1", "MD5" })
            {
                entries.AddRange(Analysis.Files
                    .Select(f => new { Hash = type == "SHA-256" ? f.SHA256 : type == "SHA-1" ? f.SHA1 : f.MD5, File = f })
                    .Where(f => !string.IsNullOrWhiteSpace(f.Hash))
                    .GroupBy(f => f.Hash, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new IocEntry
                    {
                        Type = type,
                        Value = g.Key,
                        HashSources = g.Select(f => new IocHashSource(
                            string.IsNullOrWhiteSpace(f.File.Name) ? "(unnamed response)" : f.File.Name,
                            f.File.Url ?? string.Empty)).Distinct().ToArray()
                    }));
            }
        }
        return entries;
    }

    private static IocEntry CreateEntry(string type, string value, IocNoiseFilter.NoiseClassifier classify)
    {
        var isNoise = classify(type, value, out var reason);
        return new IocEntry
        {
            Type = type,
            Value = value,
            IsNoise = isNoise,
            NoiseReason = reason
        };
    }

    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Domains ({Domains.Count})");
        AppendList(sb, Domains);
        sb.AppendLine();

        sb.AppendLine($"POST domains ({PostDomains.Count})");
        AppendList(sb, PostDomains);
        sb.AppendLine();

        sb.AppendLine($"POST URLs ({PostUrls.Count})");
        AppendList(sb, PostUrls);
        sb.AppendLine();

        sb.AppendLine($"All URLs ({Urls.Count})");
        AppendList(sb, Urls);
        sb.AppendLine();

        sb.AppendLine("Files written:");
        sb.AppendLine(DomainsFile);
        sb.AppendLine(UrlsFile);
        sb.AppendLine(PostUrlsFile);
        sb.AppendLine(PostDomainsFile);
        sb.AppendLine(HarFile);
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, IEnumerable<string> values)
    {
        foreach (var value in values)
            sb.AppendLine("  " + value);
    }
}
