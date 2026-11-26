namespace TapRoom.Models;

public sealed class IocEntry
{
    public string Type { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    // A deduplicated hash can refer to several captured responses.
    public IReadOnlyList<IocHashSource> HashSources { get; init; } = Array.Empty<IocHashSource>();
    public string SourceContext => string.Join(Environment.NewLine, HashSources.Select(s => s.Display));
    public string SourceSummary => HashSources.Count == 0 ? string.Empty :
        HashSources.Count == 1 ? HashSources[0].Display :
        $"(+{HashSources.Count - 1} more) {HashSources[0].Display}";
    public bool IsNoise { get; init; }
    public string NoiseReason { get; init; } = string.Empty;
}

public sealed record IocHashSource(string Name, string Url)
{
    public string Display => string.IsNullOrWhiteSpace(Url) ? Name : $"{Name} — {Url}";
}
