using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using TapRoom.Models;

namespace TapRoom.Services;

public sealed class CaptureManager
{
    private readonly PortAllocator _ports = new(SettingsStore.Current.FirstProxyPort, SettingsStore.Current.LastProxyPort);
    private readonly PortAllocator _mitmWebPorts = new(12000, 12999);
    private readonly BrowserLauncher _browserLauncher = new()
    {
        EdgePath = SettingsStore.Current.EdgePath,
        EdgeHideCopilot = SettingsStore.Current.EdgeHideCopilot,
        EdgeHideSidebar = SettingsStore.Current.EdgeHideSidebar,
        EdgeSuppressFirstRun = SettingsStore.Current.EdgeSuppressFirstRun,
        EdgeDisableStartupBoost = SettingsStore.Current.EdgeDisableStartupBoost,
        EdgeDisableBackgroundMode = SettingsStore.Current.EdgeDisableBackgroundMode,
        FirefoxPath = SettingsStore.Current.FirefoxPath,
        FirefoxPortablePath = SettingsStore.Current.FirefoxPortablePath
    };
    // Public lifecycle methods are called on the WPF dispatcher. Only blocking
    // process operations and WMI queries run on workers.
    private readonly Dictionary<Guid, Task> _stops = new();
    private readonly Dictionary<Guid, Task> _finishes = new();
    private readonly Dictionary<Guid, Task<ParsedIocResult>> _parses = new();
    private readonly Dictionary<Guid, BrowserWatch> _watches = new();
    private readonly HashSet<Task> _monitorTasks = new();
    private bool _shuttingDown;
    private sealed class BrowserWatch
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool SeenProfile { get; set; }

    }
    private static readonly JsonSerializerOptions MetadataOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public event Action<CaptureSession, ParsedIocResult>? IocsParsed;
    public event Action<CaptureSession, Exception>? AutoStopParseFailed;
    public event Action<string>? StatusMessage;

    public string MitmDumpPath { get; set; } = ToolResolver.FindExecutable("mitmdump.exe") ?? "mitmdump.exe";
    public string MitmWebPath { get; set; } = ToolResolver.FindExecutable("mitmweb.exe") ?? "mitmweb.exe";

    private void NotifyStatus(string message) => StatusMessage?.Invoke(message);

    public IEnumerable<CaptureSession> LoadExistingSessions()
    {
        if (!Directory.Exists(PathHelper.CasesRoot))
            yield break;

        foreach (var caseDir in Directory.EnumerateDirectories(PathHelper.CasesRoot))
        {
            var metadataFile = Path.Combine(caseDir, "metadata.json");
            if (!File.Exists(metadataFile) && !File.Exists(metadataFile + ".bak")) continue;
            CaptureSession? session = null;
            try
            {
                var record = ReadMetadata(metadataFile);

                session = new CaptureSession
                {
                    Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
                    CaseName = record.CaseName ?? Path.GetFileName(Path.GetDirectoryName(metadataFile) ?? "capture"),
                    Url = record.Url ?? string.Empty,
                    CaseReference = record.CaseReference ?? "",
                    Notes = record.Notes ?? "",
                    AutoParseAfterStop = record.AutoParseAfterStop,
                    ProxyPort = record.ProxyPort,
                    Browser = record.Browser,
                    StopAndParseOnBrowserClose = record.StopAndParseOnBrowserClose,
                    BrowserProfileDirectory = record.BrowserProfileDirectory ?? string.Empty,
                    MitmDumpPath = record.MitmDumpPath ?? string.Empty,
                    MitmWebPath = record.MitmWebPath ?? string.Empty,
                    CaseDirectory = caseDir,
                    CaptureFile = Path.Combine(caseDir, "capture.mitm"),
                    MetadataFile = metadataFile,
                    StartedUtc = record.StartedUtc == default ? Directory.GetCreationTimeUtc(caseDir) : record.StartedUtc,
                    EndedUtc = record.EndedUtc,
                    Status = record.Status is CaptureStatus.Starting or CaptureStatus.Running or CaptureStatus.Stopping or CaptureStatus.StopFailed
                        ? CaptureStatus.Interrupted : record.Status,
                    CaptureExitCode = record.CaptureExitCode,
                    LastError = record.Status is CaptureStatus.Starting or CaptureStatus.Running or CaptureStatus.Stopping or CaptureStatus.StopFailed or CaptureStatus.Interrupted
                        ? "Previous shutdown not confirmed. Inspect processes and capture file manually." : record.LastError,
                    BrowserStatus = "Not tracked (previous run)",
                    MitmWebPort = null,
                    MitmWebStatus = "Not running"
                };
            }
            catch (Exception ex)
            {
                Report($"Cannot load metadata {metadataFile}: {ex.Message}");
            }

            if (session is not null) yield return session;
        }
    }

    public async Task<CaptureSession> StartCaptureAsync(string? caseNameInput, string url, BrowserKind browser, bool autoLaunchBrowser, bool stopAndParseOnBrowserClose, bool autoParseAfterStop = true, string? notes = null)
    {
        if (_shuttingDown) throw new InvalidOperationException("TapRoom is closing.");
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));

        url = UrlNormaliser.Refang(url);

        // Bare hosts (including host:port) are accepted; explicit non-web schemes are not.
        if (!url.Contains("://", StringComparison.Ordinal) &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var absolute) ||
             System.Text.RegularExpressions.Regex.IsMatch(url, @"^[^/:?#]+:[0-9]+(?:[/\?#]|$)")))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Enter a valid HTTP or HTTPS URL.", nameof(url));

        // Resolve and verify the mitmproxy capture component before creating a case folder
        // or launching a browser. This gives the user a clear Setup / Verify error instead
        // of a later process-start or port-timeout failure.
        NotifyStatus("Checking mitmdump...");
        MitmDumpPath = ResolveRequiredTool("mitmdump.exe", "mitmdump.exe is required to start captures. Run Setup / Verify first, or put mitmdump.exe on PATH.");
        if (!await Task.Run(() => ToolRuns(MitmDumpPath, "--version", TimeSpan.FromSeconds(10))))
            throw new InvalidOperationException($"mitmdump.exe was found but did not run successfully: {MitmDumpPath}. Run Setup / Verify to repair the mitmproxy installation.");

        NotifyStatus("Preparing capture case folder...");
        var baseCaseName = PathHelper.BuildCaseName(caseNameInput, url);
        var caseName = MakeUniqueCaseName(baseCaseName);
        var caseDir = Path.Combine(PathHelper.CasesRoot, caseName);
        var parsedDir = Path.Combine(caseDir, "parsed");
        var logsDir = Path.Combine(caseDir, "logs");
        var profileDir = Path.Combine(PathHelper.ProfilesRoot, caseName, browser.ToString() + "-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(caseDir);
        Directory.CreateDirectory(parsedDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(profileDir);

        NotifyStatus("Reserving local proxy port...");
        var port = _ports.ReserveNext();
        var captureFile = Path.Combine(caseDir, "capture.mitm");
        var metadataFile = Path.Combine(caseDir, "metadata.json");

        var session = new CaptureSession
        {
            CaseName = caseName,
            CaseReference = caseNameInput?.Trim() ?? "",
            Notes = notes?.Trim() ?? "",
            AutoParseAfterStop = autoParseAfterStop,
            Url = url,
            CaseDirectory = caseDir,
            CaptureFile = captureFile,
            MetadataFile = metadataFile,
            BrowserProfileDirectory = profileDir,
            MitmDumpPath = MitmDumpPath,
            ProxyPort = port,
            Browser = browser,
            StopAndParseOnBrowserClose = stopAndParseOnBrowserClose,
            Status = CaptureStatus.Starting,
            StartedUtc = DateTime.UtcNow
        };

        try
        {
            WriteMetadata(session, required: true);
            NotifyStatus($"Starting mitmdump for {caseName}...");
            session.MitmProcess = StartMitmDump(port, captureFile, logsDir);
            AttachMitmLifecycle(session);
            WriteMetadata(session);
            NotifyStatus($"Waiting for proxy on 127.0.0.1:{port}...");
            await WaitForListeningPortAsync(port, session.MitmProcess,
                MitmShutdownControl.ScriptPath(logsDir), TimeSpan.FromSeconds(15));
            if (session.Status != CaptureStatus.Starting || session.MitmProcess.HasExited)
                throw new InvalidOperationException("Capture engine exited during startup. Check the case logs.");
            session.Status = CaptureStatus.Running;

            if (autoLaunchBrowser)
            {
                NotifyStatus($"Launching {browser} for {caseName}...");
                AttachBrowser(session, _browserLauncher.Launch(browser, caseName, url, port, profileDir));
            }

            WriteMetadata(session);
            Report($"Capture running for {caseName} on port {port}.", session);
            return session;
        }
        catch (Exception ex)
        {
            session.LastError = ex.Message;
            if (session.MitmProcess is not null && session.NeedsStop)
            {
                await StopCaptureAsync(session);
                if (session.Status == CaptureStatus.StopFailed) return session; // keep retryable in UI
            }
            // The exit handler might already have released the port. Do not release
            // it twice (another session could have reserved it in the meantime).
            ReleaseCapturePort(session);
            session.LastError = ex.Message;
            session.Status = CaptureStatus.Failed;
            session.EndedUtc ??= DateTime.UtcNow;
            WriteMetadata(session);
            Report($"Failed to start {caseName}: {ex.Message}", session);
            return session;
        }
    }

    public Task StopCaptureAsync(CaptureSession session)
    {
        if (_stops.TryGetValue(session.Id, out var existing)) return existing;
        if (!session.NeedsStop) return Task.CompletedTask;
        var operation = StopCaptureCoreAsync(session);
        _stops[session.Id] = operation;
        return operation;
    }

    private async Task StopCaptureCoreAsync(CaptureSession session)
    {
        // Yield once so the shared task is registered before completion is possible.
        session.Status = CaptureStatus.Stopping;
        WriteMetadata(session);
        NotifyStatus($"Stopping capture for {session.CaseName}...");
        await Task.Yield();
        try
        {
            var process = session.MitmProcess;
            var script = MitmShutdownControl.ScriptPath(Path.Combine(session.CaseDirectory, "logs"));
            var outcome = await Task.Run(() => StopProcessAndCheckPort(process, session.ProxyPort, script));
            session.LastError = outcome.Error;
            session.CaptureExitCode = outcome.ExitCode;
            session.Status = outcome.Status;
            if (outcome.Status != CaptureStatus.StopFailed)
            {
                session.EndedUtc = DateTime.UtcNow;
                ReleaseCapturePort(session);
            }
            WriteMetadata(session);
            Report($"{session.CaseName}: {session.Status}. {session.LastError}", session);
            // If the process exited just after a failed wait, settle it now.
            if (session.Status == CaptureStatus.StopFailed && process is { HasExited: true })
                HandleUnexpectedExit(session, process);
        }
        finally { _stops.Remove(session.Id); session.OnPropertyChanged(nameof(CaptureSession.Status)); }
    }

    private void ReleaseCapturePort(CaptureSession session)
    {
        if (session.PortReleased) return;
        session.PortReleased = true;
        _ports.Release(session.ProxyPort);
    }

    private sealed record StopOutcome(CaptureStatus Status, int? ExitCode, string? Error);

    private static StopOutcome StopProcessAndCheckPort(Process? process, int port, string script)
    {
        var outcome = StopProcess(process, script);
        if (outcome.Status == CaptureStatus.StopFailed) return outcome;
        try
        {
            for (var i = 0; i < 10 && IsPortListening(port); i++) Thread.Sleep(100);
            if (IsPortListening(port)) return new(CaptureStatus.StopFailed, outcome.ExitCode,
                $"A listener remains on port {port}; capture shutdown is not confirmed. Retry Stop after checking the process.");
            return outcome;
        }
        catch (Exception ex) { return new(CaptureStatus.StopFailed, outcome.ExitCode, ex.Message); }
    }

    private static StopOutcome StopProcess(Process? process, string? shutdownScript = null)
    {
        var details = new List<string>();
        void Record(string message)
        {
            details.Add(message);
            if (shutdownScript is null) return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(shutdownScript)!, "shutdown.log"),
                    $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
            }
            catch { /* Diagnostic failures must not prevent stopping. */ }
        }
        string Details(string message) => string.Join(" ", details.Append(message));
        try
        {
            if (process is null) return new(CaptureStatus.Failed, null, "No capture process was started.");
            if (process.HasExited) return new(CaptureStatus.Failed, process.ExitCode, "Engine exited before stop signal.");

            if (shutdownScript is not null)
            {
                var accepted = MitmShutdownControl.TryRequest(process, shutdownScript, TimeSpan.FromSeconds(15), out var detail);
                Record(detail);
                if (accepted && process.HasExited && process.ExitCode == 0)
                {
                    Record("Clean exit confirmed via mitmproxy addon (exit 0).");
                    return new(CaptureStatus.Stopped, 0, null);
                }
                if (process.HasExited)
                    return new(CaptureStatus.Failed, process.ExitCode, Details("Engine exited without a confirmed clean shutdown."));
            }

            var signalled = WindowsProcessTools.TrySendCtrlC(process, TimeSpan.FromSeconds(8), out var consoleDetail);
            Record(consoleDetail);
            if (signalled && process.HasExited && process.ExitCode == 0)
            {
                Record("Clean exit confirmed via CTRL+C (exit 0).");
                return new(CaptureStatus.Stopped, 0, null);
            }
            if (process.HasExited)
                return new(CaptureStatus.Failed, process.ExitCode, Details("Engine exited without a confirmed clean shutdown."));
            Record("Graceful shutdown did not complete; attempting forced termination.");
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            if (!process.WaitForExit(5000)) return new(CaptureStatus.StopFailed, null, Details("Engine exit could not be confirmed; retry Stop."));
            Record($"Forced termination confirmed (exit {process.ExitCode}).");
            return new(CaptureStatus.ForceStopped, process.ExitCode, Details("Forced termination; capture may be incomplete."));
        }
        catch (Exception ex) { return new(CaptureStatus.StopFailed, null, Details(ex.Message)); }
    }

    public Process LaunchBrowser(CaptureSession session)
    {
        if (_shuttingDown || session.Status != CaptureStatus.Running)
            throw new InvalidOperationException("The capture must be running before opening an investigation browser through its proxy.");

        NotifyStatus($"Launching {session.Browser} for {session.CaseName}...");
        var launch = _browserLauncher.Launch(
            session.Browser,
            session.CaseName,
            session.Url,
            session.ProxyPort,
            session.BrowserProfileDirectory);

        AttachBrowser(session, launch);
        NotifyStatus($"Browser launched for {session.CaseName}.");
        return session.BrowserProcess ?? launch.RootProcess;
    }

    public async Task<Process> LaunchMitmWebAsync(CaptureSession session)
    {
        if (_shuttingDown || !session.CanAnalyse)
            throw new InvalidOperationException("Stop the capture and confirm engine exit before opening mitmweb.");
        if (session.MitmWebProcess is { HasExited: false })
            throw new InvalidOperationException($"mitmweb is already running for this capture on port {session.MitmWebPort}.");

        NotifyStatus($"Checking mitmweb for {session.CaseName}...");
        var mitmWebPath = ResolveRequiredTool("mitmweb.exe", "mitmweb.exe was not found. Run Setup / Verify first, or put mitmweb.exe on PATH.");
        MitmWebPath = mitmWebPath;
        session.MitmWebPath = mitmWebPath;

        if (!File.Exists(session.CaptureFile))
            throw new FileNotFoundException("Capture file does not exist yet. Stop or wait for the capture to write data first.", session.CaptureFile);

        var captureInfo = new FileInfo(session.CaptureFile);
        if (captureInfo.Length == 0)
            throw new InvalidOperationException("Capture file exists but is empty. Stop the capture and wait for mitmdump to flush it before opening mitmweb.");

        if (!await Task.Run(() => ToolRuns(mitmWebPath, "--version", TimeSpan.FromSeconds(8))))
            throw new InvalidOperationException($"mitmweb.exe was found but did not run successfully: {mitmWebPath}");

        if (_shuttingDown || session.MitmWebProcess is { HasExited: false })
            throw new InvalidOperationException("mitmweb cannot be started now.");
        // Settle any previously exited viewer before reserving a replacement port.
        if (session.MitmWebProcess is { HasExited: true } previous && session.MitmWebPort is int oldPort)
            HandleWebExit(session, previous, oldPort);
        var webPort = _mitmWebPorts.ReserveNext();
        Process? process = null;
        try
        {
            var args = $"--set server=false --web-host 127.0.0.1 --web-port {webPort} -r \"{session.CaptureFile}\"";
            var logs = Path.Combine(session.CaseDirectory, "logs");
            Directory.CreateDirectory(logs);
            process = WindowsProcessTools.StartInPrivateConsole(mitmWebPath, args, session.CaseDirectory,
                Path.Combine(logs, "mitmweb.stdout.log"), Path.Combine(logs, "mitmweb.stderr.log"));
            session.MitmWebProcess = process;
            session.MitmWebPort = webPort;
            session.MitmWebStatus = $"Running (web {webPort})";
            var tracked = process;
            tracked.Exited += (_, _) => PostToUi(() => HandleWebExit(session, tracked, webPort));
            tracked.EnableRaisingEvents = true;
            if (tracked.HasExited) HandleWebExit(session, tracked, webPort);
            WriteMetadata(session);
            NotifyStatus($"mitmweb started for {session.CaseName} on web port {webPort}.");
            return tracked;
        }
        catch
        {
            if (process is null) _mitmWebPorts.Release(webPort);
            else if (process.HasExited) HandleWebExit(session, process, webPort);
            throw;
        }
    }

    private void HandleWebExit(CaptureSession session, Process process, int port)
    {
        if (!ReferenceEquals(session.MitmWebProcess, process)) return;
        session.MitmWebProcess = null;
        session.MitmWebPort = null;
        session.MitmWebStatus = $"Closed (exit {process.ExitCode})";
        _mitmWebPorts.Release(port);
        WriteMetadata(session);
        Report($"{session.CaseName}: mitmweb {session.MitmWebStatus}", session);
    }

    public ParsedIocResult? LoadParsedIocs(CaptureSession session)
    {
        var parsedDir = Path.Combine(session.CaseDirectory, "parsed");
        if (!Directory.Exists(parsedDir) && Directory.Exists(parsedDir + ".previous")) parsedDir += ".previous";
        var harFile = Path.Combine(parsedDir, "flows.har.json");
        var domainsFile = Path.Combine(parsedDir, "domains.txt");
        var urlsFile = Path.Combine(parsedDir, "urls.txt");
        var postUrlsFile = Path.Combine(parsedDir, "post-urls.txt");
        var postDomainsFile = Path.Combine(parsedDir, "post-domains.txt");

        if (!File.Exists(domainsFile) && !File.Exists(urlsFile) && !File.Exists(postUrlsFile) && !File.Exists(postDomainsFile))
            return null;

        static IReadOnlyList<string> ReadLinesIfExists(string path, StringComparer comparer)
        {
            return File.Exists(path)
                ? File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Distinct(comparer).OrderBy(line => line, comparer).ToArray()
                : Array.Empty<string>();
        }

        return new ParsedIocResult
        {
            ParsedDirectory = parsedDir,
            HarFile = harFile,
            DomainsFile = domainsFile,
            UrlsFile = urlsFile,
            PostUrlsFile = postUrlsFile,
            PostDomainsFile = postDomainsFile,
            Domains = ReadLinesIfExists(domainsFile, StringComparer.OrdinalIgnoreCase),
            Urls = ReadLinesIfExists(urlsFile, StringComparer.Ordinal),
            PostUrls = ReadLinesIfExists(postUrlsFile, StringComparer.Ordinal),
            PostDomains = ReadLinesIfExists(postDomainsFile, StringComparer.OrdinalIgnoreCase),
            Analysis = CaptureAnalysisService.Load(parsedDir)
        };
    }

    public Task<ParsedIocResult> ParseIocsAsync(CaptureSession session)
    {
        if (_shuttingDown) throw new InvalidOperationException("TapRoom is closing.");
        if (!session.CanAnalyse) throw new InvalidOperationException("Engine exit must be confirmed before parsing IOCs.");
        if (_parses.TryGetValue(session.Id, out var existing)) return existing;
        var operation = ParseIocsCoreAsync(session);
        _parses[session.Id] = operation;
        session.OnPropertyChanged(nameof(CaptureSession.Status));
        return operation;
    }

    private async Task<ParsedIocResult> ParseIocsCoreAsync(CaptureSession session)
    {
        await Task.Yield();
        try { return await ParseIocsWorkerAsync(session); }
        finally { _parses.Remove(session.Id); session.OnPropertyChanged(nameof(CaptureSession.Status)); }
    }

    private async Task<ParsedIocResult> ParseIocsWorkerAsync(CaptureSession session)
    {

        NotifyStatus($"Checking mitmdump before parsing {session.CaseName}...");
        var mitmDumpPath = ResolveRequiredTool("mitmdump.exe", "mitmdump.exe was not found. Run Setup / Verify first, or put mitmdump.exe on PATH.");
        MitmDumpPath = mitmDumpPath;
        session.MitmDumpPath = mitmDumpPath;

        if (!await Task.Run(() => ToolRuns(mitmDumpPath, "--version", TimeSpan.FromSeconds(10))))
            throw new InvalidOperationException($"mitmdump.exe was found but did not run successfully: {mitmDumpPath}");

        if (!File.Exists(session.CaptureFile))
            throw new FileNotFoundException("Capture file does not exist yet. Stop or wait for the capture to write data first.", session.CaptureFile);

        if (new FileInfo(session.CaptureFile).Length == 0)
            throw new InvalidOperationException("Capture file exists but is empty. Browse through the investigation browser, then stop the capture before parsing.");

        var parsedDir = Path.Combine(session.CaseDirectory, "parsed");
        var stage = Path.Combine(session.CaseDirectory, $".parsed-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stage);
        try
        {
            var harFile = Path.Combine(stage, "flows.har.json");
            var script = CaptureAnalysisService.Prepare(stage);
            var args = $"-q -n -r \"{session.CaptureFile}\" --set hardump=\"{harFile}\" -s \"{script}\"";
            var result = await RunCommandAsync(mitmDumpPath, args, TimeSpan.FromMinutes(5));
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Failed to export HAR. Exit code: {result.ExitCode}\n{result.Error}\n{result.Output}");
            if (!File.Exists(harFile) || new FileInfo(harFile).Length == 0)
                throw new InvalidOperationException("mitmdump completed, but no HAR output was written.");
            NotifyStatus($"Parsing HAR for {session.CaseName} IOCs...");
            var parsed = await Task.Run(() => ParseHarFile(harFile, stage,
                Path.Combine(stage, "domains.txt"), Path.Combine(stage, "urls.txt"),
                Path.Combine(stage, "post-urls.txt"), Path.Combine(stage, "post-domains.txt")));
            var analysis = await Task.Run(() => CaptureAnalysisService.Load(stage));
            if (analysis?.Available != true) Report(analysis?.Warning ?? "Advanced analysis unavailable.", session);
            File.Delete(script);
            PublishParsedDirectory(stage, parsedDir);
            Report($"Parsed IOCs for {session.CaseName}.", session);
            return new ParsedIocResult
            {
                ParsedDirectory = parsedDir, HarFile = Path.Combine(parsedDir, "flows.har.json"),
                DomainsFile = Path.Combine(parsedDir, "domains.txt"), UrlsFile = Path.Combine(parsedDir, "urls.txt"),
                PostUrlsFile = Path.Combine(parsedDir, "post-urls.txt"), PostDomainsFile = Path.Combine(parsedDir, "post-domains.txt"),
                Domains = parsed.Domains, Urls = parsed.Urls, PostUrls = parsed.PostUrls, PostDomains = parsed.PostDomains, Analysis = analysis
            };
        }
        finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } }
    }

    private static void PublishParsedDirectory(string stage, string destination)
    {
        var backup = destination + ".previous";
        if (Directory.Exists(backup))
        {
            if (!Directory.Exists(destination)) Directory.Move(backup, destination);
            else Directory.Delete(backup, true);
        }
        if (Directory.Exists(destination)) Directory.Move(destination, backup);
        try { Directory.Move(stage, destination); }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup)) Directory.Move(backup, destination);
            throw;
        }
        try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
    }

    internal static ParsedIocResult ParseHarFile(string harFile, string parsedDir, string domainsFile, string urlsFile, string postUrlsFile, string postDomainsFile)
    {
        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new SortedSet<string>(StringComparer.Ordinal);
        var postUrls = new SortedSet<string>(StringComparer.Ordinal);
        var postDomains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(harFile);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("HAR output did not contain log.entries.");
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("request", out var request))
                continue;

            var method = request.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;

            var url = request.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            urls.Add(url);

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var host = uri.Host.ToLowerInvariant();
                domains.Add(host);

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                    postDomains.Add(host);
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                postUrls.Add(url);
        }

        File.WriteAllLines(domainsFile, domains);
        File.WriteAllLines(urlsFile, urls);
        File.WriteAllLines(postUrlsFile, postUrls);
        File.WriteAllLines(postDomainsFile, postDomains);

        return new ParsedIocResult
        {
            ParsedDirectory = parsedDir,
            HarFile = harFile,
            DomainsFile = domainsFile,
            UrlsFile = urlsFile,
            PostUrlsFile = postUrlsFile,
            PostDomainsFile = postDomainsFile,
            Domains = domains.ToArray(),
            Urls = urls.ToArray(),
            PostUrls = postUrls.ToArray(),
            PostDomains = postDomains.ToArray()
        };
    }

    private static string ResolveRequiredTool(string exeName, string failureMessage)
    {
        var resolved = ToolResolver.FindExecutable(exeName);
        if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            return resolved;

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, exeName)))
            return Path.Combine(AppContext.BaseDirectory, exeName);

        throw new FileNotFoundException(failureMessage, exeName);
    }

    private static bool ToolRuns(string exe, string args, TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return Task.WaitAll(new Task[] { output, error }, 2000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<CommandResult> RunCommandAsync(string exe, string args, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var completion = Task.WhenAll(process.WaitForExitAsync(), stdoutTask, stderrTask);
        try { await completion.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Timed out running: {exe} {args}");
        }
        var output = await stdoutTask;
        var error = await stderrTask;
        return new CommandResult(process.ExitCode, output, error);
    }

    public Process OpenFolder(CaptureSession session)
    {
        Directory.CreateDirectory(session.CaseDirectory);
        return Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{session.CaseDirectory}\"",
            UseShellExecute = true
        })!;
    }

    public Process OpenLogsFolder(CaptureSession session)
    {
        var logsDirectory = Path.Combine(session.CaseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        return Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{logsDirectory}\"",
            UseShellExecute = true
        })!;
    }


    private void AttachMitmLifecycle(CaptureSession session)
    {
        var process = session.MitmProcess!;
        process.Exited += (_, _) => PostToUi(() => HandleUnexpectedExit(session, process));
        process.EnableRaisingEvents = true;
        if (process.HasExited) HandleUnexpectedExit(session, process);
    }

    private void HandleUnexpectedExit(CaptureSession session, Process process)
    {
        if (!ReferenceEquals(session.MitmProcess, process) ||
            session.Status is not (CaptureStatus.Starting or CaptureStatus.Running or CaptureStatus.StopFailed)) return;
        try
        {
            if (IsPortListening(session.ProxyPort))
            {
                session.Status = CaptureStatus.StopFailed;
                session.LastError = $"Engine exited but port {session.ProxyPort} is still listening. Retry Stop after checking the process.";
                WriteMetadata(session);
                Report($"{session.CaseName}: {session.LastError}", session);
                return;
            }
        }
        catch (Exception ex)
        {
            session.Status = CaptureStatus.StopFailed;
            session.LastError = ex.Message;
            WriteMetadata(session);
            return;
        }
        session.EndedUtc = DateTime.UtcNow;
        session.CaptureExitCode = process.ExitCode;
        session.LastError = $"Unexpected capture engine exit ({process.ExitCode}). Capture may be incomplete.";
        session.Status = CaptureStatus.Failed;
        ReleaseCapturePort(session);
        WriteMetadata(session);
        Report($"{session.CaseName}: {session.LastError}", session);
    }

    private void AttachBrowser(CaptureSession session, BrowserLaunchResult launch)
    {
        if (_watches.TryGetValue(session.Id, out var old)) old.Cancellation.Cancel();
        var watch = new BrowserWatch();
        _watches[session.Id] = watch;
        session.BrowserProcess = launch.RootProcess;
        session.BrowserPid = launch.RootProcess.Id;
        session.BrowserStatus = "Starting / verifying profile";
        WriteMetadata(session);
        var task = MonitorBrowserAsync(session, launch, watch);
        _monitorTasks.Add(task);
        _ = ObserveMonitorAsync(task);
    }

    private async Task ObserveMonitorAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) { Report($"Browser monitor failed: {ex.Message}"); }
        finally { _monitorTasks.Remove(task); }
    }

    private async Task MonitorBrowserAsync(CaptureSession session, BrowserLaunchResult launch, BrowserWatch watch)
    {
        var ct = watch.Cancellation.Token;
        var missing = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var snapshot = await Task.Run(() => BrowserProfileMonitor.Scan(launch.Browser, launch.ProfileDirectory));
                if (ct.IsCancellationRequested || _shuttingDown) return;
                if (snapshot.ProcessIds.Count > 0)
                {
                    watch.SeenProfile = true;
                    missing = 0;
                    var pid = snapshot.ProcessIds[0];
                    if (session.BrowserPid != pid || session.BrowserStatus != $"Running (PID {pid})")
                    {
                        session.BrowserPid = pid;
                        session.BrowserStatus = $"Running (PID {pid})";
                        WriteMetadata(session);
                    }
                }
                else if (!snapshot.Reliable || !watch.SeenProfile)
                {
                    missing = 0;
                    if (session.BrowserStatus != "Unverified - manual stop may be needed")
                    {
                        session.BrowserStatus = "Unverified - manual stop may be needed";
                        Report($"{session.CaseName}: browser profile not verified. {snapshot.Error}", session);
                        WriteMetadata(session);
                    }
                }
                else if (++missing >= 3)
                {
                    // Several reliable empty scans after a verified profile avoid
                    // treating a short launcher hand-off as browser closure.
                    await HandleBrowserClosedAsync(session);
                    return;
                }
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (_watches.TryGetValue(session.Id, out var current) && ReferenceEquals(current, watch)) _watches.Remove(session.Id);
            session.OnPropertyChanged(nameof(CaptureSession.BrowserStatus));
            watch.Cancellation.Dispose();
        }
    }

    // Shared manual/browser stop-and-parse operation. Startup recovery and
    // application shutdown still use StopCaptureAsync without automatic parsing.
    public Task StopAndMaybeParseAsync(CaptureSession session)
    {
        if (_finishes.TryGetValue(session.Id, out var pending)) return pending;
        if (!session.NeedsStop && !_stops.ContainsKey(session.Id)) return Task.CompletedTask;
        var task = FinishCaptureAsync(session);
        _finishes[session.Id] = task;
        session.OnPropertyChanged(nameof(CaptureSession.Status));
        return task;
    }

    private async Task FinishCaptureAsync(CaptureSession session)
    {
        await Task.Yield();
        try
        {
            await StopCaptureAsync(session);
            if (_shuttingDown) return;
            if (session.Status != CaptureStatus.Stopped)
            {
                var message = $"Capture status: {session.Status}. Review shutdown.log and mitmdump logs before manually parsing.\n\n{session.LastError}";
                Report($"{session.CaseName}: {message}", session);
                AutoStopParseFailed?.Invoke(session, new InvalidOperationException(message));
                return;
            }
            if (!session.AutoParseAfterStop) return;
            var parsed = await ParseIocsAsync(session);
            if (!_shuttingDown) IocsParsed?.Invoke(session, parsed);
        }
        catch (Exception ex)
        {
            Report($"{session.CaseName}: {ex.Message}", session);
            if (!_shuttingDown) AutoStopParseFailed?.Invoke(session, ex);
        }
        finally
        {
            _finishes.Remove(session.Id);
            session.OnPropertyChanged(nameof(CaptureSession.Status));
        }
    }

    private async Task HandleBrowserClosedAsync(CaptureSession session)
    {
        session.BrowserPid = null;
        session.BrowserStatus = "Closed";
        WriteMetadata(session);
        try
        {
            if (session.StopAndParseOnBrowserClose && !_shuttingDown)
                await StopAndMaybeParseAsync(session);
            if (!session.NeedsStop && !_shuttingDown)
                await Task.Run(() => CleanupBrowserProfile(session));
        }
        finally { WriteMetadata(session); }
    }

    public bool IsFinishingOrParsing(CaptureSession session) => _finishes.ContainsKey(session.Id) || _parses.ContainsKey(session.Id);

    public bool CanArchive(CaptureSession session) =>
        !_shuttingDown && session.CanAnalyse && !_stops.ContainsKey(session.Id) &&
        !_finishes.ContainsKey(session.Id) && !_parses.ContainsKey(session.Id) && !_watches.ContainsKey(session.Id) &&
        session.MitmWebProcess is null &&
        (session.BrowserProcess is null || session.BrowserStatus.StartsWith("Closed", StringComparison.Ordinal));

    public string ArchiveCase(CaptureSession session)
    {
        if (!CanArchive(session)) throw new InvalidOperationException("Close associated browsers and mitmweb and wait for pending operations first. Interrupted captures must be checked manually.");
        return CaseArchive.Move(SettingsStore.Current.EffectiveStorageRoot, session.CaseDirectory);
    }

    public void UpdateCaseDetails(CaptureSession session, string reference, string notes)
    {
        if (_shuttingDown) throw new InvalidOperationException("TapRoom is closing.");
        var oldReference = session.CaseReference;
        var oldNotes = session.Notes;
        session.CaseReference = reference.Trim();
        session.Notes = notes.Trim();
        try { WriteMetadata(session, required: true); }
        catch { session.CaseReference = oldReference; session.Notes = oldNotes; throw; }
    }

    public async Task<IReadOnlyList<string>> ShutdownAsync(IEnumerable<CaptureSession> sessions)
    {
        _shuttingDown = true;
        var all = sessions.ToArray();
        var errors = new List<string>();
        var previousWatches = _watches.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var watch in previousWatches.Values) watch.Cancellation.Cancel();
        // Stop engines before waiting for existing monitor/parse operations.
        await Task.WhenAll(all.Where(s => s.NeedsStop).Select(StopCaptureAsync));
        foreach (var session in all)
        {
            if (session.MitmProcess is null && session.MitmWebProcess is null && session.BrowserProcess is null) continue;
            if (session.NeedsStop) errors.Add($"{session.CaseName}: capture engine exit not confirmed.");
            if (session.MitmWebProcess is { } web)
            {
                var outcome = await Task.Run(() => StopProcess(web));
                if (outcome.Status == CaptureStatus.StopFailed) errors.Add($"{session.CaseName}: mitmweb exit not confirmed.");
                else if (session.MitmWebPort is int port) HandleWebExit(session, web, port);
            }
            if (session.BrowserProcess is not null && session.BrowserStatus != "Closed" && !session.BrowserStatus.StartsWith("Closed -", StringComparison.Ordinal))
            {
                var closed = await Task.Run(() => BrowserProfileMonitor.CloseAsync(session.Browser, session.BrowserProfileDirectory));
                if (!closed || (previousWatches.TryGetValue(session.Id, out var prior) && !prior.SeenProfile))
                    errors.Add($"{session.CaseName}: investigation browser closure could not be verified; close it manually.");
                else
                {
                    session.BrowserPid = null;
                    session.BrowserStatus = "Closed";
                    if (!session.NeedsStop) await Task.Run(() => CleanupBrowserProfile(session));
                }
            }
            WriteMetadata(session);
        }
        try { await Task.WhenAll(_monitorTasks.ToArray()); } catch (Exception ex) { Report(ex.Message); }
        try { await Task.WhenAll(_finishes.Values.ToArray()); } catch (Exception ex) { Report(ex.Message); }
        try { await Task.WhenAll(_parses.Values.ToArray()); } catch (Exception ex) { Report(ex.Message); }
        return errors;
    }

    public void ResumeAfterCancelledShutdown(IEnumerable<CaptureSession> sessions)
    {
        _shuttingDown = false;
        foreach (var session in sessions)
        {
            if (session.BrowserProcess is null || _watches.ContainsKey(session.Id) ||
                session.BrowserStatus.StartsWith("Closed", StringComparison.Ordinal)) continue;
            // A fresh scan must verify the profile again before any auto-stop.
            AttachBrowser(session, new BrowserLaunchResult(session.Browser, session.BrowserProcess,
                session.BrowserProfileDirectory, string.Empty));
        }
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }

    private static void CleanupBrowserProfile(CaptureSession session)
    {
        if (string.IsNullOrWhiteSpace(session.BrowserProfileDirectory) || !Directory.Exists(session.BrowserProfileDirectory))
            return;

        try
        {
            var profilesRoot = Path.GetFullPath(PathHelper.ProfilesRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var profileDir = Path.GetFullPath(session.BrowserProfileDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!profileDir.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase))
            {
                AppendProfileCleanupLog(session, $"Skipped profile cleanup because path is outside TapRoom profile root: {session.BrowserProfileDirectory}");
                return;
            }

            Directory.Delete(session.BrowserProfileDirectory, recursive: true);
            AppendProfileCleanupLog(session, $"Deleted browser profile: {session.BrowserProfileDirectory}");
        }
        catch (Exception ex)
        {
            AppendProfileCleanupLog(session, $"Failed to delete browser profile: {session.BrowserProfileDirectory}. {ex.Message}");
        }
    }

    private static void AppendProfileCleanupLog(CaptureSession session, string message)
    {
        try
        {
            var logsDir = Path.Combine(session.CaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(Path.Combine(logsDir, "profile-cleanup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Best effort only. Profile cleanup should not break capture handling.
        }
    }

    private Process StartMitmDump(int port, string captureFile, string logsDir)
    {
        var stdout = Path.Combine(logsDir, "mitmdump.stdout.log");
        var stderr = Path.Combine(logsDir, "mitmdump.stderr.log");

        File.AppendAllText(stdout, $"Starting mitmdump on 127.0.0.1:{port}{Environment.NewLine}");
        File.AppendAllText(stdout, $"Executable: {MitmDumpPath}{Environment.NewLine}");
        File.AppendAllText(stderr, string.Empty);

        if (string.IsNullOrWhiteSpace(MitmDumpPath) || !File.Exists(MitmDumpPath))
            throw new FileNotFoundException("mitmdump.exe was not found. Run Setup / Verify first, or put mitmdump.exe on PATH.", MitmDumpPath);

        if (IsPortListening(port))
            throw new InvalidOperationException($"Port {port} is already listening. Try starting the capture again so TapRoom can allocate another port.");

        EnsureCaptureDirectoryWritable(Path.GetDirectoryName(captureFile) ?? AppContext.BaseDirectory);

        var workingDirectory = Path.GetDirectoryName(captureFile) ?? AppContext.BaseDirectory;
        var script = MitmShutdownControl.Prepare(logsDir);
        var args = $"--listen-host 127.0.0.1 --listen-port {port} -w \"{captureFile}\" -s \"{script}\"";
        var process = WindowsProcessTools.StartInPrivateConsole(MitmDumpPath, args, workingDirectory, stdout, stderr);
        return process;
    }

    private static async Task WaitForListeningPortAsync(int port, Process process, string shutdownScript, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"mitmdump exited before opening 127.0.0.1:{port}. Check logs in the case folder.");

            if (IsPortListening(port) && File.Exists(MitmShutdownControl.ReadyPath(shutdownScript)))
                return;

            await Task.Delay(250);
        }

        throw new TimeoutException($"mitmdump did not confirm both proxy readiness on 127.0.0.1:{port} and its shutdown addon. Check mitmdump stdout/stderr logs.");
    }

    private static bool IsPortListening(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(e => e.Address.Equals(System.Net.IPAddress.Loopback) && e.Port == port);
    }

    private static void EnsureCaptureDirectoryWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var testFile = Path.Combine(directory, $".taproom-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(testFile, "ok");
        }
        catch (Exception ex)
        {
            throw new IOException($"Capture folder is not writable: {directory}", ex);
        }
        finally
        {
            try { if (File.Exists(testFile)) File.Delete(testFile); } catch { }
        }
    }

    private static string MakeUniqueCaseName(string baseName)
    {
        var candidate = baseName;
        var i = 2;
        while (Directory.Exists(Path.Combine(PathHelper.CasesRoot, candidate)))
            candidate = $"{baseName}_{i++}";
        return candidate;
    }

    private void WriteMetadata(CaptureSession session, bool required = false)
    {
        lock (session.MetadataLock)
        {
        var metadata = new CaptureMetadata
        {
            SchemaVersion = 2,
            CaptureExitCode = session.CaptureExitCode,
            LastError = session.LastError,
            Id = session.Id,
            CaseName = session.CaseName,
            CaseReference = session.CaseReference,
            Notes = session.Notes,
            AutoParseAfterStop = session.AutoParseAfterStop,
            Url = session.Url,
            ProxyPort = session.ProxyPort,
            Browser = session.Browser,
            StopAndParseOnBrowserClose = session.StopAndParseOnBrowserClose,
            BrowserProfileDirectory = session.BrowserProfileDirectory,
            MitmDumpPath = session.MitmDumpPath,
            MitmWebPath = session.MitmWebPath,
            CaseDirectory = session.CaseDirectory,
            CaptureFile = session.CaptureFile,
            StartedUtc = session.StartedUtc,
            EndedUtc = session.EndedUtc,
            Status = session.Status,
            BrowserStatus = session.BrowserStatus,
            BrowserPid = session.BrowserPid,
            MitmWebPort = session.MitmWebPort,
            MitmWebStatus = session.MitmWebStatus
        };

        var temp = session.MetadataFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, metadata, MetadataOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(session.MetadataFile)) File.Replace(temp, session.MetadataFile, session.MetadataFile + ".bak");
            else File.Move(temp, session.MetadataFile);
        }
        catch (Exception ex)
        {
            Report($"Metadata save failed for {session.CaseName}: {ex.Message}", session);
            if (required) throw;
        }
        finally { try { File.Delete(temp); } catch { } }
        }
    }

    private CaptureMetadata ReadMetadata(string path)
    {
        CaptureMetadata Read(string file)
        {
            var record = JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(file), MetadataOptions)
                ?? throw new InvalidDataException("Empty metadata.");
            if (record.SchemaVersion != 2 || !Enum.IsDefined(record.Status))
                throw new InvalidDataException("Unsupported metadata schema. Phase 1 uses schema 2.");
            return record;
        }
        try { return Read(path); }
        catch (Exception primary)
        {
            var recovered = Read(path + ".bak");
            // The backup may predate the latest state, so never treat its capture
            // as confirmed stopped. Preserve the damaged original for inspection.
            recovered.Status = CaptureStatus.Interrupted;
            Report($"Recovered metadata from backup for {path}: {primary.Message}");
            return recovered;
        }
    }

    private static readonly object LogGate = new();
    private void Report(string message, CaptureSession? session = null)
    {
        lock (LogGate)
        {
            try
            {
                var log = Path.Combine(PathHelper.LocalAppDataRoot, "Logs");
                Directory.CreateDirectory(log);
                var line = $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(log, "taproom.log"), line);
                if (session is not null)
                {
                    var caseLogs = Path.Combine(session.CaseDirectory, "logs");
                    Directory.CreateDirectory(caseLogs);
                    File.AppendAllText(Path.Combine(caseLogs, "session.log"), line);
                }
            }
            catch { /* Reporting failures must not interrupt process cleanup. */ }
        }
        NotifyStatus(message);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class CaptureMetadata
    {
        public CaptureMetadata() { }
        public int SchemaVersion { get; set; }
        public int? CaptureExitCode { get; set; }
        public string? LastError { get; set; }
        public Guid Id { get; set; }
        public string? CaseName { get; set; }
        public string? CaseReference { get; set; }
        public string? Notes { get; set; }
        public bool AutoParseAfterStop { get; set; } = true;
        public string? Url { get; set; }
        public int ProxyPort { get; set; }
        public BrowserKind Browser { get; set; }
        public bool StopAndParseOnBrowserClose { get; set; } = true;
        public string? BrowserProfileDirectory { get; set; }
        public string? MitmDumpPath { get; set; }
        public string? MitmWebPath { get; set; }
        public string? CaseDirectory { get; set; }
        public string? CaptureFile { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public CaptureStatus Status { get; set; }
        public string? BrowserStatus { get; set; }
        public int? BrowserPid { get; set; }
        public int? MitmWebPort { get; set; }
        public string? MitmWebStatus { get; set; }
    }
}
