namespace TapRoom.Models;

public sealed class CaptureAnalysis
{
    public int SchemaVersion { get; init; }
    public string Warning { get; init; } = "";
    public List<HttpActivity> Requests { get; init; } = new();
    public List<CapturedFile> Files { get; init; } = new();
    public List<HttpRedirect> Redirects { get; init; } = new();
    public bool Available => SchemaVersion == 1 && string.IsNullOrEmpty(Warning);
}

public sealed class HttpActivity
{
    public string Utc { get; init; } = "";
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public string Domain { get; init; } = "";
    public int? StatusCode { get; init; }
    public string PeerIp { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long? ResponseBytes { get; init; }
    public string Error { get; init; } = "";
    public string Event { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class CapturedFile
{
    public string Utc { get; init; } = "";
    public string Url { get; init; } = "";
    public string Name { get; init; } = "";
    public string ContentType { get; init; } = "";
    public int StatusCode { get; init; }
    public long? DecodedBytes { get; init; }
    public string SHA256 { get; init; } = "";
    public string SHA1 { get; init; } = "";
    public string MD5 { get; init; } = "";
    public string HashStatus { get; init; } = "";
}

public sealed class HttpRedirect
{
    public string Utc { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string DestinationUrl { get; init; } = "";
    public int StatusCode { get; init; }
    public string ResponseUtc { get; init; } = "";
    public string LaterRequestUtc { get; init; } = "";
}

public sealed record DomainActivity(string Domain, int Requests);
