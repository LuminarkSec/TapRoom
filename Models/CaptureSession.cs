using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TapRoom.Models;

public sealed class CaptureSession : INotifyPropertyChanged
{
    private CaptureStatus _status;
    private DateTime? _endedUtc;
    private string _browserStatus = "Not launched";
    private int? _browserPid;
    private int? _mitmWebPort;
    private string _mitmWebStatus = "Not running";

    public Guid Id { get; init; } = Guid.NewGuid();
    public string CaseName { get; init; } = string.Empty;
    private string _caseReference = "";
    private string _notes = "";
    public string CaseReference { get => _caseReference; set { _caseReference = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    public string Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }
    public string Url { get; init; } = string.Empty;
    // The host (including any subdomain), not the registrable domain or ticket number.
    public string Domain => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
        ? uri.IdnHost : "(unknown host)";
    public string DisplayName => string.IsNullOrWhiteSpace(CaseReference) ? Domain : $"{Domain} · {CaseReference}";
    public string CaseDirectory { get; init; } = string.Empty;
    public string CaptureFile { get; init; } = string.Empty;
    public string MetadataFile { get; init; } = string.Empty;
    public string BrowserProfileDirectory { get; init; } = string.Empty;
    public int ProxyPort { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public BrowserKind Browser { get; init; }
    public bool StopAndParseOnBrowserClose { get; init; } = true;
    public bool AutoParseAfterStop { get; init; } = true;
    public string MitmDumpPath { get; set; } = string.Empty;
    public string MitmWebPath { get; set; } = string.Empty;
    public Process? MitmProcess { get; set; }
    public int? CaptureExitCode { get; set; }
    public string? LastError { get; set; }
    internal object MetadataLock { get; } = new();
    internal bool PortReleased { get; set; }
    public bool CanAnalyse => Status is CaptureStatus.Stopped or CaptureStatus.ForceStopped or CaptureStatus.Failed;
    public bool NeedsStop => Status is CaptureStatus.Starting or CaptureStatus.Running or CaptureStatus.Stopping or CaptureStatus.StopFailed;
    public Process? BrowserProcess { get; set; }
    public Process? MitmWebProcess { get; set; }

    public int? MitmWebPort
    {
        get => _mitmWebPort;
        set { _mitmWebPort = value; OnPropertyChanged(); }
    }

    public string MitmWebStatus
    {
        get => _mitmWebStatus;
        set { _mitmWebStatus = value; OnPropertyChanged(); }
    }

    public CaptureStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAnalyse)); OnPropertyChanged(nameof(NeedsStop)); }
    }

    public string BrowserStatus
    {
        get => _browserStatus;
        set { _browserStatus = value; OnPropertyChanged(); }
    }

    public int? BrowserPid
    {
        get => _browserPid;
        set { _browserPid = value; OnPropertyChanged(); }
    }

    public DateTime? EndedUtc
    {
        get => _endedUtc;
        set { _endedUtc = value; OnPropertyChanged(); }
    }

    public string StartedLocal => StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
