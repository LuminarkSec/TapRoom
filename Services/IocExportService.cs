using System.Text;
using System.Text.Json;
using TapRoom.Models;

namespace TapRoom.Services;

internal static class IocExportService
{
    internal static string Defang(string type, string value)
    {
        if (type.Contains("URL", StringComparison.OrdinalIgnoreCase))
        {
            int separator = value.IndexOf("://", StringComparison.Ordinal);
            if (separator < 0) return value;
            var scheme = value[..separator];
            if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return value;
            var authorityStart = separator + 3;
            var authorityEnd = value.IndexOfAny(new[] {'/', '?', '#'}, authorityStart);
            if (authorityEnd < 0) authorityEnd = value.Length;
            var authority = value[authorityStart..authorityEnd];
            var hostStart = authority.LastIndexOf('@') + 1;
            var hostAndPort = authority[hostStart..];
            // The URL path/query is retained exactly, including its case and dots.
            var defangedHost = hostAndPort.Replace(".", "[.]");
            if (hostAndPort.StartsWith("[", StringComparison.Ordinal))
            {
                var close = hostAndPort.IndexOf(']');
                if (close > 0) defangedHost = hostAndPort[..(close + 1)].Replace(":", "[:]").Replace(".", "[.]") + hostAndPort[(close + 1)..];
            }
            return (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "hxxps://" : "hxxp://") + authority[..hostStart] + defangedHost + value[authorityEnd..];
        }
        if (type.Contains("domain", StringComparison.OrdinalIgnoreCase) || type == "IP address")
            return value.Replace(".", "[.]").Replace(":", "[:]");
        return value;
    }

    internal static string Build(IEnumerable<IocEntry> source, string format, bool includeNoise, bool defang)
    {
        var rows = source.Select(row => new
        {
            row.Type,
            Value = defang ? Defang(row.Type, row.Value) : row.Value,
            row.IsNoise,
            row.NoiseReason,
            SourceContext = string.Join(Environment.NewLine, row.HashSources.Select(s =>
                new IocHashSource(s.Name, defang ? Defang("URL", s.Url) : s.Url).Display))
        }).ToList();
        var includeContext = rows.Any(row => row.SourceContext.Length > 0);
        if (format == "json")
        {
            var json = new JsonSerializerOptions { WriteIndented = true };
            if (includeContext)
                return includeNoise ? JsonSerializer.Serialize(rows, json) : JsonSerializer.Serialize(rows.Select(r => new { r.Type, r.Value, r.SourceContext }), json);
            return includeNoise ? JsonSerializer.Serialize(rows.Select(r => new { r.Type, r.Value, r.IsNoise, r.NoiseReason }), json) : JsonSerializer.Serialize(rows.Select(r => new { r.Type, r.Value }), json);
        }
        var result = new StringBuilder();
        if (format == "txt")
        {
            foreach (var row in rows)
                result.AppendLine($"{Clean(row.Type)}: {Clean(row.Value)}" +
                    (row.SourceContext.Length > 0 ? $" [Captured file / source: {Clean(row.SourceContext)}]" : "") +
                    (includeNoise && row.IsNoise ? $" [Noise: {Clean(row.NoiseReason)}]" : ""));
            return result.ToString();
        }
        var delimiter = format == "csv" ? ',' : '\t';
        string Cell(string value)
        {
            // Flatten control characters and guard spreadsheet formula injection.
            var text = Clean(value);
            var trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) text = "'" + text;
            return delimiter == ',' ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }
        var headers = new List<string> { "Type", "Value" };
        if (includeContext) headers.Add("Captured file / source URL");
        if (includeNoise) headers.AddRange(new[] { "Noise", "Noise reason" });
        result.AppendLine(string.Join(delimiter.ToString(), headers));
        foreach (var row in rows)
        {
            var cells = new List<string> { row.Type, row.Value };
            if (includeContext) cells.Add(row.SourceContext.Replace(Environment.NewLine, " | "));
            if (includeNoise) cells.AddRange(new[] { row.IsNoise ? "true" : "false", row.NoiseReason });
            result.AppendLine(string.Join(delimiter.ToString(), cells.Select(Cell)));
        }
        return result.ToString();
    }

    private static string Clean(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
}
