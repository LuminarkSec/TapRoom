using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TapRoom.Models;
using TapRoom.Services;

namespace TapRoom;

public partial class MainWindow
{
    private readonly Dictionary<Guid, ScreenshotSession> _screenshotJobs = new();
    private readonly Dictionary<Guid, bool> _screenshotChoices = new();
    private readonly Dictionary<Guid, string> _screenshotStatuses = new();
    private readonly ObservableCollection<ScreenshotRow> _screenshotRows = new();
    private Guid? _screenshotSelectedCase;
    private long _screenshotLoadVersion, _screenshotPreviewVersion;
    private readonly HashSet<string> _pendingScreenshotFiles = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ScreenshotRow(ScreenshotRecord Record, BitmapSource? Thumbnail)
    {
        public string Caption => $"{Record.CapturedUtc.ToLocalTime():HH:mm:ss.fff} • {Record.Reason}";
        public string Detail => $"+{TimeSpan.FromSeconds(Math.Max(0, Record.ElapsedSeconds)):c} | " +
            (Record.ChangePercent is double change ? $"{change:0.0}% change" : "Initial frame") +
            $" | {Record.Width}×{Record.Height}\n{Record.WindowTitle}";
    }

    private void StartScreenshotJob(CaptureSession session, bool automatic)
    {
        if (_shutdownInProgress || _allowClose || session.Status != CaptureStatus.Running || _screenshotJobs.ContainsKey(session.Id)) return;
        _screenshotChoices[session.Id] = automatic;
        _screenshotStatuses[session.Id] = automatic ? "Waiting for the investigation browser…" : "Automatic screenshots off. Capture now is available.";
        _screenshotJobs.Add(session.Id, new ScreenshotSession(session, automatic, SettingsStore.Current, ScreenshotNotification));
        UpdateScreenshotButtons();
    }

    private void ScreenshotNotification(CaptureSession session, ScreenshotRecord? record, string status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_allowClose || _shutdownInProgress || !_screenshotJobs.ContainsKey(session.Id)) return;
            _screenshotStatuses[session.Id] = status;
            if (_screenshotSelectedCase != session.Id) return;
            ScreenshotStatusText.Text = status;
            UpdateScreenshotButtons();
            if (record is not null) _ = AddScreenshotRowAsync(session, record);
        }));
    }

    private async Task StopScreenshotJobAsync(Guid id)
    {
        if (!_screenshotJobs.TryGetValue(id, out var job)) return;
        try { await job.StopAsync(); }
        catch (Exception ex) { _screenshotStatuses[id] = "Screenshot shutdown: " + ex.Message; }
        finally
        {
            if (_screenshotJobs.TryGetValue(id, out var current) && ReferenceEquals(current, job)) _screenshotJobs.Remove(id);
            _screenshotStatuses[id] = "Screenshot capture finished. " + _screenshotStatuses.GetValueOrDefault(id, "");
            if (_screenshotSelectedCase == id && !_allowClose)
            {
                ScreenshotStatusText.Text = _screenshotStatuses[id];
                if (!_shutdownInProgress && CapturesGrid.SelectedItem is CaptureSession selected && selected.Id == id)
                    _ = LoadScreenshotsAsync(selected); // include a PNG committed immediately before cancellation
            }
            UpdateActionButtons();
        }
    }

    private async Task StopAllScreenshotJobsAsync()
    {
        await Task.WhenAll(_screenshotJobs.Keys.ToArray().Select(StopScreenshotJobAsync));
    }

    private void ResumeScreenshotJobs()
    {
        foreach (var session in Captures.Where(s => s.Status == CaptureStatus.Running))
            StartScreenshotJob(session, _screenshotChoices.GetValueOrDefault(session.Id));
    }

    private void UpdateScreenshotButtons()
    {
        if (ScreenshotNowButton is null || ScreenshotFolderButton is null || ScreenshotViewButton is null || CapturesGrid is null || ScreenshotList is null) return;
        var selected = CapturesGrid.SelectedItem as CaptureSession;
        bool available = !_shutdownInProgress && !_allowClose && selected is not null;
        ScreenshotNowButton.IsEnabled = available && selected!.Status == CaptureStatus.Running &&
            _screenshotJobs.TryGetValue(selected.Id, out var job) && !job.ManualBusy;
        ScreenshotFolderButton.IsEnabled = available && Directory.Exists(ScreenshotStore.Folder(selected!));
        ScreenshotViewButton.IsEnabled = available && ScreenshotList.SelectedItem is ScreenshotRow;
    }

    private void SelectScreenshotCase(CaptureSession? session)
    {
        if (ScreenshotList is null || ScreenshotPreview is null) return;
        _screenshotSelectedCase = session?.Id;
        _screenshotLoadVersion++;
        _screenshotPreviewVersion++;
        _pendingScreenshotFiles.Clear();
        ScreenshotList.ItemsSource = _screenshotRows;
        _screenshotRows.Clear();
        ScreenshotPreview.Source = null;
        ScreenshotStatusText.Text = session is null ? "Select an investigation." :
            _screenshotStatuses.GetValueOrDefault(session.Id, "No automatic screenshot capture is active.");
        ScreenshotCountText.Text = "";
        UpdateScreenshotButtons();
        if (session is not null) _ = LoadScreenshotsAsync(session);
    }

    private static ScreenshotRow LoadScreenshotRow(string folder, ScreenshotRecord record)
    {
        BitmapSource? thumbnail = null;
        try { thumbnail = ScreenshotStore.ReadImage(folder, record.FileName, 200); } catch { }
        return new(record, thumbnail);
    }

    private async Task LoadScreenshotsAsync(CaptureSession session)
    {
        var version = _screenshotLoadVersion;
        var folder = ScreenshotStore.Folder(session);
        try
        {
            // Only the small thumbnails are decoded, off the UI thread. The
            // original is decoded on demand when selected or opened full size.
            var rows = await Task.Run(() => ScreenshotStore.Load(folder).OrderBy(r => r.CapturedUtc)
                .Select(r => LoadScreenshotRow(folder, r)).ToList());
            if (_allowClose || version != _screenshotLoadVersion || _screenshotSelectedCase != session.Id) return;
            foreach (var row in rows)
                InsertScreenshotRow(row);
            if (ScreenshotList.SelectedIndex < 0 && _screenshotRows.Count > 0) ScreenshotList.SelectedIndex = 0;
            ScreenshotCountText.Text = $"{_screenshotRows.Count:N0} saved";
            ApplyHttpFilter();
        }
        catch (Exception ex)
        {
            if (version == _screenshotLoadVersion) ScreenshotStatusText.Text = "Cannot load screenshot index: " + ex.Message;
        }
        finally
        {
            if (version == _screenshotLoadVersion) { UpdateScreenshotButtons(); }
        }
    }

    private void InsertScreenshotRow(ScreenshotRow row)
    {
        if (_screenshotRows.Any(r => r.Record.FileName == row.Record.FileName)) return;
        int index = 0;
        while (index < _screenshotRows.Count && _screenshotRows[index].Record.CapturedUtc <= row.Record.CapturedUtc) index++;
        _screenshotRows.Insert(index, row);
    }

    private IEnumerable<HttpActivity> ScreenshotTimelineRows() => _screenshotRows.Select(row => new HttpActivity
    {
        Utc = row.Record.CapturedUtc.ToString("O"), Event = "Screenshot", Detail = row.Record.FileName + " — " + row.Record.WindowTitle
    });

    private async Task AddScreenshotRowAsync(CaptureSession session, ScreenshotRecord record)
    {
        var version = _screenshotLoadVersion;
        if (_screenshotRows.Any(r => r.Record.FileName == record.FileName) || !_pendingScreenshotFiles.Add(record.FileName)) return;
        try
        {
            var row = await Task.Run(() => LoadScreenshotRow(ScreenshotStore.Folder(session), record));
            if (_allowClose || version != _screenshotLoadVersion || _screenshotSelectedCase != session.Id ||
                _screenshotRows.Any(r => r.Record.FileName == record.FileName)) return;
            // Maintain chronological order even if background thumbnail loads finish out of order.
            InsertScreenshotRow(row);
            if (ScreenshotList.SelectedIndex < 0) ScreenshotList.SelectedIndex = 0;
            ScreenshotCountText.Text = $"{_screenshotRows.Count:N0} saved";
            ApplyHttpFilter();
            UpdateScreenshotButtons();
        }
        catch (Exception ex)
        {
            if (version == _screenshotLoadVersion) ScreenshotStatusText.Text = "Unable to load screenshot preview: " + ex.Message;
        }
        finally { if (version == _screenshotLoadVersion) _pendingScreenshotFiles.Remove(record.FileName); }
    }

    private async void ScreenshotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var version = ++_screenshotPreviewVersion;
        if (ScreenshotPreview is null) return;
        ScreenshotPreview.Source = null;
        UpdateScreenshotButtons();
        if (CapturesGrid.SelectedItem is not CaptureSession session || ScreenshotList.SelectedItem is not ScreenshotRow row) return;
        try
        {
            var image = await Task.Run(() => ScreenshotStore.ReadImage(ScreenshotStore.Folder(session), row.Record.FileName, 1400));
            if (!_allowClose && version == _screenshotPreviewVersion && _screenshotSelectedCase == session.Id) ScreenshotPreview.Source = image;
        }
        catch (Exception ex)
        {
            if (version == _screenshotPreviewVersion) ScreenshotStatusText.Text = "Cannot display screenshot: " + ex.Message;
        }
    }

    private async void ScreenshotNow_Click(object sender, RoutedEventArgs e)
    {
        if (_shutdownInProgress || CapturesGrid.SelectedItem is not CaptureSession session || session.Status != CaptureStatus.Running ||
            !_screenshotJobs.TryGetValue(session.Id, out var job) || job.ManualBusy) return;
        var pending = job.TakeNowAsync();
        ScreenshotStatusText.Text = "Capturing investigation window…";
        UpdateScreenshotButtons();
        var status = await pending;
        if (!_allowClose && _screenshotSelectedCase == session.Id) ScreenshotStatusText.Text = status;
        UpdateScreenshotButtons();
    }

    private void ScreenshotRefresh_Click(object sender, RoutedEventArgs e) => SelectScreenshotCase(CapturesGrid.SelectedItem as CaptureSession);
    private void ScreenshotFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CapturesGrid.SelectedItem is not CaptureSession session) return;
        try
        {
            var folder = ScreenshotStore.Folder(session);
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open screenshots", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ScreenshotPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ScreenshotView_Click(sender, e);
    }
    private void ScreenshotView_Click(object sender, RoutedEventArgs e)
    {
        if (CapturesGrid.SelectedItem is not CaptureSession session || ScreenshotList.SelectedItem is not ScreenshotRow row) return;
        var records = _screenshotRows.Select(r => r.Record).ToArray();
        int selected = Array.IndexOf(records, row.Record);
        if (selected < 0) return;
        var viewer = new ScreenshotViewer(ScreenshotStore.Folder(session), records, selected, session.DisplayName) { Owner = this };
        viewer.Show();
    }
}
