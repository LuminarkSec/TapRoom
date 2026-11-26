using System.Text.Json;
using System.Windows.Media.Imaging;
using TapRoom.Models;

namespace TapRoom.Services;

internal sealed record ScreenshotRecord(string FileName, DateTime CapturedUtc, double ElapsedSeconds,
    double? ChangePercent, int Width, int Height, string WindowTitle, string Reason);

internal static class ScreenshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string Folder(CaptureSession session) => Path.Combine(session.CaseDirectory, "screenshots");

    internal static List<ScreenshotRecord> Load(string folder)
    {
        string path = Path.Combine(folder, "index.json");
        if (!File.Exists(path)) return new();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var records = JsonSerializer.Deserialize<List<ScreenshotRecord>>(file, JsonOptions) ?? throw new InvalidDataException("Empty screenshot index.");
        if (records.Any(r => r is null || !ValidName(r.FileName)) || records.Select(r => r.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count)
            throw new InvalidDataException("Invalid screenshot index.");
        return records;
    }

    internal static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') &&
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && name == Path.GetFileName(name);

    internal static void Save(string folder, List<ScreenshotRecord> records, ScreenshotRecord record, BitmapSource image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to write screenshots through a junction or symbolic link.");
        if (!ValidName(record.FileName)) throw new IOException("Invalid screenshot filename.");
        string destination = Path.Combine(folder, record.FileName), pngTemp = destination + ".tmp";
        string index = Path.Combine(folder, "index.json"), indexTemp = index + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool pngCommitted = false, indexCommitted = false;
        try
        {
            using (var file = new FileStream(pngTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(file);
                file.Flush(flushToDisk: true);
            }
            using (var file = new FileStream(indexTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, records.Append(record).ToArray(), JsonOptions);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(pngTemp, destination);
            pngCommitted = true;
            // Complete this small transaction even if cancellation follows the PNG move.
            if (File.Exists(index)) File.Replace(indexTemp, index, null);
            else File.Move(indexTemp, index);
            indexCommitted = true;
            records.Add(record);
        }
        finally
        {
            TryDelete(pngTemp); TryDelete(indexTemp);
            if (pngCommitted && !indexCommitted) TryDelete(destination);
        }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    internal static BitmapSource ReadImage(string folder, string name, int thumbnailWidth = 0)
    {
        if (!ValidName(name)) throw new InvalidDataException("Invalid screenshot filename.");
        string path = Path.Combine(folder, name);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to load a linked screenshot.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (thumbnailWidth > 0) image.DecodePixelWidth = thumbnailWidth;
        image.StreamSource = file;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

// One worker per case. The UI owns start/manual/stop calls. Actual WMI, GPU,
// image processing, and disk writes run off the dispatcher. A single gate
// serialises periodic and manual work so indexes/baselines cannot race.
internal sealed class ScreenshotSession
{
    private readonly CaptureSession _session;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<CaptureSession, ScreenshotRecord?, string> _notify;
    private readonly Dictionary<(nint Handle, int Pid, DateTime Started), Baseline> _baselines = new();
    private readonly Dictionary<(nint Handle, int Pid, DateTime Started), WindowScreenshotCapture.Source> _sources = new();
    private DateTime _lastDiagnosticUtc;
    private bool _prepared;
    private sealed record Baseline(byte[] Sample, int Width, int Height);
    private List<ScreenshotRecord>? _records;
    private Task _automatic = Task.CompletedTask;
    private Task<string>? _manual;
    private Task? _stop;
    private string? _lastStatus;
    internal bool Automatic { get; }
    internal bool ManualBusy => _manual is { IsCompleted: false };
    internal ScreenshotSession(CaptureSession session, bool automatic, AppSettings settings, Action<CaptureSession, ScreenshotRecord?, string> notify)
    {
        _session = session; Automatic = automatic; _settings = settings; _notify = notify;
        if (automatic) _automatic = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var ct = _cancel.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var result = await CaptureOnceAsync(false, ct).ConfigureAwait(false);
                    Publish(null, result);
                    if (_records?.Count >= _settings.ScreenshotMaximum) return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (PlatformNotSupportedException) { throw; }
                catch (Exception ex) { Publish(null, "Screenshot unavailable; will retry: " + ex.Message); }
                var remaining = TimeSpan.FromSeconds(_settings.ScreenshotIntervalSeconds) - timer.Elapsed;
                await Task.Delay(remaining > TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Publish(null, "Automatic screenshots paused: " + ex.Message); }
    }

    internal Task<string> TakeNowAsync()
    {
        if (_stop is not null) return Task.FromResult("Screenshot capture has stopped.");
        if (ManualBusy) return _manual!;
        var ct = _cancel.Token;
        return _manual = Task.Run(async () =>
        {
            try { var result = await CaptureOnceAsync(true, ct).ConfigureAwait(false); Publish(null, result); return result; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "Screenshot cancelled."; }
            catch (Exception ex) { var status = "Screenshot failed: " + ex.Message; Publish(null, status); return status; }
        });
    }

    internal Task StopAsync()
    {
        if (_stop is not null) return _stop;
        _cancel.Cancel();
        return _stop = DrainAsync();
    }
    private async Task DrainAsync()
    {
        try { await Task.WhenAll(_automatic, (Task?)_manual ?? Task.CompletedTask).ConfigureAwait(false); }
        finally { CloseSources(); Log($"Screenshot worker stopped; {_records?.Count ?? 0} image(s) indexed."); _gate.Dispose(); _cancel.Dispose(); }
    }

    private async Task<string> CaptureOnceAsync(bool manual, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var folder = ScreenshotStore.Folder(_session);
            if (!_prepared)
            {
                Directory.CreateDirectory(folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to use a linked screenshot folder.");
                _prepared = true;
                Log($"Screenshot output: {folder}; automatic={Automatic}; interval={_settings.ScreenshotIntervalSeconds}s; threshold={_settings.ScreenshotChangePercent}%; save initial={_settings.ScreenshotSaveInitial}.");
            }
            if (!WindowScreenshotCapture.IsSupported) throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable (Windows 10 2004+ and a supported graphics session required).");
            _records ??= ScreenshotStore.Load(folder);
            if (_records.Count >= _settings.ScreenshotMaximum) return "Screenshot limit reached.";
            var windows = WindowScreenshotCapture.FindWindows(_session);
            var live = windows.Select(w => (w.Handle, w.Pid, w.ProcessStartedUtc)).ToHashSet();
            foreach (var key in _sources.Keys.Where(k => !live.Contains(k)).ToArray())
            {
                _sources[key].Dispose(); _sources.Remove(key);
                Log($"Released window 0x{key.Handle:X} (PID {key.Pid}); no longer eligible.");
            }
            if (windows.Count == 0) return "Waiting for a verified, non-minimised investigation browser window.";
            // Keep a window's baseline through minimisation / temporary invisibility.
            // A PID plus creation time guards against reuse across processes.
            int saved = 0, valid = 0;
            var diagnostics = new List<string>();
            // Manual: foreground associated window first; if it is unavailable,
            // try the next associated window. Automatic: include owned pop-ups.
            foreach (var window in windows)
            {
                ct.ThrowIfCancellationRequested();
                if (_records.Count >= _settings.ScreenshotMaximum) break;
                var key = (window.Handle, window.Pid, window.ProcessStartedUtc);
                WindowScreenshotCapture.Frame? frame;
                string detail;
                try
                {
                    if (!_sources.TryGetValue(key, out var source))
                    {
                        source = new WindowScreenshotCapture.Source(_session, window);
                        _sources.Add(key, source);
                        Log($"Started persistent capture for window 0x{window.Handle:X}, PID {window.Pid} ({source.Driver}); cursor disabled.");
                    }
                    (frame, detail) = await source.ReadAsync(_session, manual, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (_sources.Remove(key, out var failed)) failed.Dispose();
                    diagnostics.Add($"Window 0x{window.Handle:X}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
                    continue;
                }
                if (frame is null) { diagnostics.Add($"Window 0x{window.Handle:X}: {detail}"); continue; }
                valid++;
                var first = !_baselines.TryGetValue(key, out var previous);
                int width = frame.Image.PixelWidth, height = frame.Image.PixelHeight;
                double? change = first ? null : previous!.Width != width || previous.Height != height
                    ? 100 : ScreenshotDifference.Percent(previous.Sample, frame.Sample);
                if (!manual && first && !_settings.ScreenshotSaveInitial)
                {
                    _baselines[key] = new(frame.Sample, width, height);
                    continue;
                }
                if (!manual && !first && change < _settings.ScreenshotChangePercent) continue;
                // Compare with the last saved image. Never advance the baseline
                // when a write fails or a below-threshold frame is discarded.
                var name = $"{_records.Count + 1:D4}_{frame.CapturedUtc:yyyyMMdd_HH-mm-ss-fff}_UTC_{Guid.NewGuid():N}.png";
                var record = new ScreenshotRecord(name, frame.CapturedUtc,
                    (frame.CapturedUtc - _session.StartedUtc).TotalSeconds, change, width, height,
                    window.Title, manual ? (detail.StartsWith("No new frame", StringComparison.Ordinal) ? "Manual (last observed frame)" : "Manual") : first ? "Initial" : "Visual change");
                ScreenshotStore.Save(folder, _records, record, frame.Image, ct);
                Log($"Saved {record.FileName} ({width}x{height}, {record.Reason}, change={change?.ToString("0.0") ?? "initial"}); {detail}");
                _baselines[key] = new(frame.Sample, width, height);
                saved++;
                Publish(record, $"{_records.Count} screenshot(s) saved.");
                if (manual) break;
            }
            if (diagnostics.Count > 0 && valid > 0) Log(string.Join(" | ", diagnostics));
            if (_records.Count >= _settings.ScreenshotMaximum) return "Screenshot limit reached.";
            if (saved > 0) return $"{_records.Count} screenshot(s) saved.";
            if (valid > 0) return $"Monitoring • {_records.Count} saved (usable frame received; no significant visual change or initial saving disabled).";
            return $"{_records.Count} saved. " + (diagnostics.Count == 0 ? "No usable frame." : string.Join(" | ", diagnostics));
        }
        finally
        {
            // Manual-only capture should not leave a persistent graphics session active.
            if (!Automatic || _records?.Count >= _settings.ScreenshotMaximum) CloseSources();
            _gate.Release();
        }
    }

    private void CloseSources()
    {
        foreach (var source in _sources.Values) { try { source.Dispose(); } catch { } }
        _sources.Clear();
    }

    private void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(_session.CaseDirectory, "logs", "screenshots.log"), $"{DateTime.UtcNow:O} {message}{Environment.NewLine}"); } catch { }
    }

    private void Publish(ScreenshotRecord? record, string status)
    {
        // Log transitions and a heartbeat for repeated results (including failures).
        bool changed = !string.Equals(status, _lastStatus, StringComparison.Ordinal);
        if (record is null && (changed || DateTime.UtcNow - _lastDiagnosticUtc >= TimeSpan.FromSeconds(15)))
        {
            Log(status);
            _lastDiagnosticUtc = DateTime.UtcNow;
        }
        if (record is null && !changed) return;
        _lastStatus = status;
        try { _notify(_session, record, status); } catch { } // never affect traffic capture
    }
}
