using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using TapRoom.Models;
using TapRoom.Services;

namespace TapRoom;

public partial class MainWindow : Window
{
    private readonly CaptureManager _captureManager = new();
    private readonly StartupOptions? _startupOptions;
    private bool _startupCaptureStarted;
    private bool _captureStartInProgress;
    private bool _startupCheckInProgress;
    private bool _shutdownInProgress;
    private bool _allowClose;
    private readonly HashSet<Guid> _openingWeb = new();
    private readonly List<string> _metadataWarnings = new();
    private bool _loadingMetadata;
    private ICollectionView? _capturesView;
    private long _iocLoadVersion;
    private bool _settingsOpen;
    private List<IocEntry> _currentIocRows = new();
    private IReadOnlyList<HttpActivity> _httpRows = Array.Empty<HttpActivity>();
    private List<IocEntry> _visibleIocRows = new();
    public ObservableCollection<CaptureSession> Captures { get; } = new();

    public MainWindow(StartupOptions? startupOptions = null)
    {
        _startupOptions = startupOptions;
        InitializeComponent();
        WindowPlacementStore.Restore(this);
        IocNoiseFilter.EnsureUserFilterFiles();
        DataContext = this;
        var settings = SettingsStore.Current;
        BrowserComboBox.SelectedIndex = (int)settings.DefaultBrowser;
        AutoLaunchCheckBox.IsChecked = settings.AutoLaunchBrowser;
        StopParseOnBrowserCloseCheckBox.IsChecked = settings.StopOnBrowserClose;
        AutoParseCheckBox.IsChecked = settings.AutoParseAfterStop;
        ScreenshotCheckBox.IsChecked = settings.ScreenshotEnabled;
        HideBrowserNoiseCheckBox.IsChecked = settings.HideBrowserNoise;
        CaseStatusBox.ItemsSource = new[] { "All" }.Concat(Enum.GetNames<CaptureStatus>()).ToArray();
        CaseStatusBox.SelectedIndex = 0;
        _capturesView = CollectionViewSource.GetDefaultView(Captures);
        _capturesView.Filter = MatchesCaptureFilter;
        _capturesView.SortDescriptions.Add(new SortDescription(nameof(CaptureSession.StartedUtc), ListSortDirection.Descending));
        if (SettingsStore.LoadWarning is { } warning) _metadataWarnings.Add(warning);
        _captureManager.IocsParsed += CaptureManager_IocsParsed;
        _captureManager.AutoStopParseFailed += CaptureManager_AutoStopParseFailed;
        _captureManager.StatusMessage += CaptureManager_StatusMessage;

        _loadingMetadata = true;
        try
        {
            foreach (var session in _captureManager.LoadExistingSessions().OrderByDescending(s => s.StartedUtc))
                AddSession(session);
        }
        catch (Exception ex) { _metadataWarnings.Add(ex.Message); }
        finally { _loadingMetadata = false; }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        UpdateActionButtons();
        RefreshCaptureCount();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _startupCheckInProgress = true;
        try
        {
            if (_metadataWarnings.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, _metadataWarnings) +
                    "\n\nSee %LOCALAPPDATA%\\TapRoom\\Logs\\taproom.log for details.", "Metadata recovery / load warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RunStartupSetupCheckAsync();
        }
        finally { _startupCheckInProgress = false; }
        if (!_shutdownInProgress) await RunStartupCaptureAsync();
    }

    private async Task RunStartupSetupCheckAsync()
    {
        try
        {
            SetStatus("Checking TapRoom setup...");
            var setupManager = new SetupManager();
            var result = await setupManager.CheckPrerequisitesAsync(CancellationToken.None);

            if (result.IsReady)
            {
                SetStatus("Ready. mitmproxy tools and CA certificate found.");
                return;
            }

            SetStatus("Setup attention required.");

            var issueText = string.Join(Environment.NewLine, result.Issues.Select(i => "- " + i));
            var answer = MessageBox.Show(
                this,
                "TapRoom setup looks incomplete or broken:" + Environment.NewLine + Environment.NewLine +
                issueText + Environment.NewLine + Environment.NewLine +
                "Open Setup / Verify now?",
                "TapRoom setup check",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
                new SetupWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus("Setup check failed.");
            var answer = MessageBox.Show(
                this,
                "TapRoom could not complete its startup setup check:" + Environment.NewLine + Environment.NewLine +
                ex.Message + Environment.NewLine + Environment.NewLine +
                "Open Setup / Verify now?",
                "TapRoom setup check",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
                new SetupWindow { Owner = this }.ShowDialog();
        }
    }

    private async Task RunStartupCaptureAsync()
    {
        if (_startupCaptureStarted || string.IsNullOrWhiteSpace(_startupOptions?.Url))
            return;

        _startupCaptureStarted = true;
        await StartCaptureFromOptionsAsync(_startupOptions);
    }

    public async Task HandleExternalStartupArgsAsync(string[] args)
    {
        if (_shutdownInProgress || _allowClose) return;
        BringToFront();

        var options = StartupOptions.Parse(args);
        if (options.PromptForUrl)
        {
            var prompt = new UrlPromptWindow { Owner = this };
            if (prompt.ShowDialog() != true)
            {
                SetStatus("TapRoom is already running. URL prompt cancelled.");
                return;
            }

            options = options with { Url = prompt.Url };
        }

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            SetStatus("TapRoom is already running.");
            return;
        }

        SetStatus("Received command-line URL from another TapRoom launch.");
        await StartCaptureFromOptionsAsync(options);
    }

    private async Task StartCaptureFromOptionsAsync(StartupOptions options)
    {
        if (_captureStartInProgress || _settingsOpen || _shutdownInProgress)
        {
            SetStatus("Another operation is in progress. Submit the URL again when it finishes.");
            return;
        }
        CaseNameTextBox.Text = options.CaseName ?? string.Empty;
        UrlTextBox.Text = options.Url ?? string.Empty;
        BrowserComboBox.SelectedIndex = options.Browser switch
        {
            BrowserKind.Firefox => 1,
            BrowserKind.FirefoxPortable => 2,
            _ => 0
        };
        AutoLaunchCheckBox.IsChecked = options.AutoLaunchBrowser;
        StopParseOnBrowserCloseCheckBox.IsChecked = options.StopAndParseOnBrowserClose;
        AutoParseCheckBox.IsChecked = options.AutoParseAfterStop;

        await StartCaptureFromInputAsync();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();

        // Toggle Topmost briefly to make Windows bring the existing instance forward.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void CaptureManager_IocsParsed(CaptureSession session, ParsedIocResult parsed)
    {
        if (CapturesGrid.SelectedItem is CaptureSession selected && selected.Id == session.Id)
            await ShowParsedIocsAsync(session, parsed);

        SetStatus($"Stopped and parsed {session.CaseName}: {parsed.Domains.Count} domains, {parsed.Urls.Count} URLs, {parsed.PostUrls.Count} POST URLs.");
    }

    private void CaptureManager_AutoStopParseFailed(CaptureSession session, Exception exception)
    {
        var title = session.Status == CaptureStatus.Stopped ? "Automatic IOC parsing failed" : "Capture shutdown warning";
        SetStatus($"{session.CaseName}: {session.Status}. {title}.");
        if (!_shutdownInProgress)
            MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CaptureManager_StatusMessage(string message)
    {
        if (_loadingMetadata) _metadataWarnings.Add(message);
        if (Dispatcher.CheckAccess()) SetStatus(message);
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => SetStatus(message)));
    }

    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        await StartCaptureFromInputAsync();
    }

    private async void UrlTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (e.IsRepeat || _startupCheckInProgress || !StartCaptureButton.IsEnabled ||
            string.IsNullOrWhiteSpace(UrlTextBox.Text)) return;
        await StartCaptureFromInputAsync();
    }

    private async Task StartCaptureFromInputAsync()
    {
        if (_captureStartInProgress || _settingsOpen || _shutdownInProgress || _allowClose)
            return;

        var caseName = CaseNameTextBox.Text;
        var url = UrlTextBox.Text;
        var browser = SelectedBrowser();
        var launch = AutoLaunchCheckBox.IsChecked == true;
        var stopOnClose = StopParseOnBrowserCloseCheckBox.IsChecked == true;
        var autoParse = AutoParseCheckBox.IsChecked == true;
        var screenshots = ScreenshotCheckBox.IsChecked == true;
        _captureStartInProgress = true;
        SetStartCaptureBusy(true);

        try
        {
            SetStatus("Starting capture...");

            // Let the UI repaint the disabled button/Starting text before mitmdump's
            // standalone executable does any slower first-run startup work.
            await Task.Delay(50);

            var session = await _captureManager.StartCaptureAsync(caseName, url, browser, launch, stopOnClose, autoParse, string.Empty);

            AddSession(session);
            if (session.Status == CaptureStatus.Running) StartScreenshotJob(session, screenshots);
            if (!MatchesCaptureFilter(session)) ClearCaptureFilters_Click(this, new RoutedEventArgs());
            CapturesGrid.SelectedItem = session;
            SetStatus(session.Status == CaptureStatus.Running
                ? $"Started {session.CaseName} on 127.0.0.1:{session.ProxyPort} using {session.Browser}."
                : $"{session.CaseName}: {session.Status}. {session.LastError}");
            if (session.Status is CaptureStatus.Failed or CaptureStatus.StopFailed)
                MessageBox.Show(this, session.LastError ?? "Inspect the case logs.", "Capture startup warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to start capture", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Failed to start capture.");
        }
        finally
        {
            _captureStartInProgress = false;
            SetStartCaptureBusy(false);
        }
    }

    private void SetStartCaptureBusy(bool busy)
    {
        if (StartCaptureButton is null)
            return;

        StartCaptureButton.IsEnabled = !busy && !_shutdownInProgress;
        StartCaptureButton.Content = busy ? "Starting..." : "Start Capture";
        if (SettingsButton is not null) SettingsButton.IsEnabled = !busy && !_shutdownInProgress;
    }


    private void SetupVerify_Click(object sender, RoutedEventArgs e)
    {
        var setupWindow = new SetupWindow { Owner = this };
        setupWindow.ShowDialog();
    }

    private async void CapturesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var loadVersion = ++_iocLoadVersion;
        SelectScreenshotCase(CapturesGrid?.SelectedItem as CaptureSession);
        UpdateActionButtons();
        if (CapturesGrid?.SelectedItem is not CaptureSession session)
        {
            ClearParsedIocs("Select a capture to view parsed IOCs.");
            return;
        }
        ClearParsedIocs($"Loading IOCs for {session.CaseName}...");
        try
        {
            var parsed = await Task.Run(() => _captureManager.LoadParsedIocs(session));
            if (loadVersion != _iocLoadVersion || _shutdownInProgress || _allowClose ||
                CapturesGrid.SelectedItem is not CaptureSession selected || selected.Id != session.Id) return;
            if (parsed is null) ClearParsedIocs($"No parsed IOCs found for {session.CaseName}. Stop the capture and click Parse IOCs.");
            else await ShowParsedIocsAsync(session, parsed);
        }
        catch (Exception ex)
        {
            if (loadVersion == _iocLoadVersion && !_allowClose)
                ClearParsedIocs($"Cannot load IOCs for {session.CaseName}: {ex.Message}");
        }
    }

    private async void StopCapture_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null) return;

        try
        {
            SetStatus($"Stopping {session.CaseName}...");
            await _captureManager.StopAndMaybeParseAsync(session);
            if (!session.AutoParseAfterStop || session.Status != CaptureStatus.Stopped)
                SetStatus($"{session.CaseName}: {session.Status}. {session.LastError}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to stop capture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ParseIocs_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null) return;

        try
        {
            SetStatus($"Parsing IOCs for {session.CaseName}...");
            var parsed = await _captureManager.ParseIocsAsync(session);
            if (CapturesGrid.SelectedItem is CaptureSession selected && selected.Id == session.Id)
                await ShowParsedIocsAsync(session, parsed);
            SetStatus($"Parsed {parsed.Domains.Count} domains, {parsed.Urls.Count} URLs, and {parsed.PostUrls.Count} POST URLs for {session.CaseName}.");
        }
        catch (Exception ex)
        {
            if (!_shutdownInProgress) MessageBox.Show(this, ex.Message, "Failed to parse IOCs", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Failed to parse IOCs.");
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null) return;

        try
        {
            _captureManager.LaunchBrowser(session);
            SetStatus($"Opened {session.Browser} for {session.CaseName}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to open browser", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenMitmWeb_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null) return;

        if (!_openingWeb.Add(session.Id)) return;
        UpdateActionButtons();
        try
        {
            await _captureManager.LaunchMitmWebAsync(session);
            SetStatus(session.MitmWebPort.HasValue
                ? $"Opening mitmweb for {session.CaseName} on web port {session.MitmWebPort}."
                : $"Opening mitmweb for {session.CaseName}.");
        }
        catch (Exception ex)
        {
            if (!_shutdownInProgress) MessageBox.Show(this, ex.Message, "Failed to open mitmweb", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _openingWeb.Remove(session.Id); UpdateActionButtons(); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null) return;
        try { _captureManager.OpenLogsFolder(session); SetStatus($"Opened logs for {session.CaseName}."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open logs", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null) return;
        try { _captureManager.OpenFolder(session); SetStatus($"Opened case folder for {session.CaseName}."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open folder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AddSession(CaptureSession session)
    {
        session.PropertyChanged += Session_PropertyChanged;
        Captures.Add(session);
        RefreshCaptureCount();
    }

    private void UpdateActionButtons()
    {
        if (StopCaptureButton is null || ParseIocsButton is null || OpenBrowserButton is null || OpenMitmWebButton is null || CapturesGrid is null) return;
        var session = CapturesGrid.SelectedItem as CaptureSession;
        var available = !_shutdownInProgress && !_allowClose;
        StopCaptureButton.IsEnabled = available && (session?.Status is CaptureStatus.Running or CaptureStatus.Starting or CaptureStatus.StopFailed);
        ParseIocsButton.IsEnabled = available && session?.CanAnalyse == true;
        OpenBrowserButton.IsEnabled = available && session?.Status == CaptureStatus.Running;
        OpenMitmWebButton.IsEnabled = available && session?.CanAnalyse == true && session.MitmWebProcess is null && !_openingWeb.Contains(session.Id);
        if (session is not null && _captureManager.IsFinishingOrParsing(session)) ParseIocsButton.IsEnabled = false;
        if (EditCaseButton is not null) EditCaseButton.IsEnabled = available && session is not null;
        if (ArchiveCaseButton is not null) ArchiveCaseButton.IsEnabled = available && session is not null && !_openingWeb.Contains(session.Id) && _captureManager.CanArchive(session) && !_screenshotJobs.ContainsKey(session.Id);
        if (OpenFolderButton is not null) OpenFolderButton.IsEnabled = available && session is not null;
        if (OpenLogsButton is not null) OpenLogsButton.IsEnabled = available && session is not null;
        if (SettingsButton is not null) SettingsButton.IsEnabled = available && !_captureStartInProgress;
        UpdateScreenshotButtons();
    }

    private bool MatchesCaptureFilter(object item)
    {
        if (item is not CaptureSession session) return false;
        var status = CaseStatusBox?.SelectedItem as string ?? "All";
        var search = CaseSearchBox?.Text.Trim() ?? "";
        return (status == "All" || status == session.Status.ToString()) &&
            (search.Length == 0 || new[] { session.CaseName, session.CaseReference, session.Domain, session.Url, session.Notes, session.Browser.ToString() }
                .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void RefreshCaptureCount()
    {
        if (CaptureCountText is not null && _capturesView is not null)
            CaptureCountText.Text = $"{_capturesView.Cast<object>().Count()} / {Captures.Count} cases • {Captures.Count(s => s.NeedsStop)} active / unconfirmed";
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptureSession.Status) && sender is CaptureSession changed && changed.Status != CaptureStatus.Running)
            _ = StopScreenshotJobAsync(changed.Id);
        UpdateActionButtons();
        if (e.PropertyName is nameof(CaptureSession.Status) or nameof(CaptureSession.CaseReference) or nameof(CaptureSession.Notes))
        {
            // Only refresh if the relevant filter is active, avoiding unnecessary selection changes.
            if ((CaseStatusBox.SelectedItem as string) != "All" || !string.IsNullOrWhiteSpace(CaseSearchBox.Text))
                _capturesView?.Refresh();
            RefreshCaptureCount();
        }
    }
    private void CaptureFilter_Changed(object sender, RoutedEventArgs e)
    {
        _capturesView?.Refresh();
        RefreshCaptureCount();
        UpdateActionButtons();
    }
    private void ClearCaptureFilters_Click(object sender, RoutedEventArgs e)
    {
        CaseSearchBox.Clear();
        CaseStatusBox.SelectedIndex = 0;
    }
    private void CapturesGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(CapturesGrid, source) is DataGridRow row)
            CapturesGrid.SelectedItem = row.Item;
        else CapturesGrid.SelectedItem = null;
    }

    private void CaptureMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateActionButtons();
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsEnabled = (item.Tag as string) switch
            {
                "stop" => StopCaptureButton.IsEnabled,
                "browser" => OpenBrowserButton.IsEnabled,
                "web" => OpenMitmWebButton.IsEnabled,
                "parse" => ParseIocsButton.IsEnabled,
                "folder" => OpenFolderButton.IsEnabled,
                "logs" => OpenLogsButton.IsEnabled,
                "edit" => EditCaseButton.IsEnabled,
                "archive" => ArchiveCaseButton.IsEnabled,
                _ => false
            };
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen || _shutdownInProgress) return;
        _settingsOpen = true;
        try
        {
            if (new SettingsWindow { Owner = this }.ShowDialog() == true)
                SetStatus("Settings saved. Close TapRoom safely and restart to apply them.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Settings error", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _settingsOpen = false; }
    }
    private void EditCase_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null || _shutdownInProgress) return;
        var dialog = new CaseDetailsWindow(session) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _captureManager.UpdateCaseDetails(session, dialog.CaseReference, dialog.Notes);
            SetStatus($"Saved details for {session.CaseName}.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Details not saved", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void ArchiveCase_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession();
        if (session is null || _shutdownInProgress || _openingWeb.Contains(session.Id) || _screenshotJobs.ContainsKey(session.Id) || !_captureManager.CanArchive(session)) return;
        if (MessageBox.Show(this, $"Remove {session.DisplayName} from the investigation list?\n\nThe entire case folder, including capture, metadata, logs and parsed IOCs, will be moved into ArchivedCases. Nothing will be permanently deleted.\n\n{session.CaseDirectory}",
            "Archive investigation", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            if (_openingWeb.Contains(session.Id)) throw new InvalidOperationException("mitmweb is opening for this case.");
            var destination = _captureManager.ArchiveCase(session);
            session.PropertyChanged -= Session_PropertyChanged;
            Captures.Remove(session);
            RefreshCaptureCount();
            UpdateActionButtons();
            SetStatus($"Archived {session.CaseName} to {destination}.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Case not archived", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_shutdownInProgress) return;
        if (_captureStartInProgress || _startupCheckInProgress || _openingWeb.Count > 0)
        {
            SetStatus("A startup or setup operation is in progress. Close TapRoom after it finishes.");
            return;
        }
        var active = Captures.Count(s => s.NeedsStop || s.MitmWebProcess is not null ||
            (s.BrowserProcess is not null && !s.BrowserStatus.StartsWith("Closed", StringComparison.Ordinal)));
        if (active > 0 && MessageBox.Show(this,
                $"{active} case(s) have active or unverified processes. Stop capture engines, close associated browsers and mitmweb, then exit?",
                "Exit TapRoom", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _shutdownInProgress = true;
        IsEnabled = false;
        UpdateActionButtons();
        SetStatus("Stopping active processes and finishing pending work...");
        await Task.Yield();
        try
        {
            var screenshotDrain = StopAllScreenshotJobsAsync();
            var errors = await _captureManager.ShutdownAsync(Captures);
            await screenshotDrain;
            IsEnabled = true;
            if (errors.Count > 0 && MessageBox.Show(this,
                string.Join(Environment.NewLine, errors) + "\n\nExit anyway? Unconfirmed processes may remain running. Choose No to stay in TapRoom and retry.",
                "Shutdown incomplete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                _captureManager.ResumeAfterCancelledShutdown(Captures);
                SetStatus("Shutdown cancelled. Review incomplete stops before retrying.");
                return;
            }
            WindowPlacementStore.Save(this);
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            _captureManager.ResumeAfterCancelledShutdown(Captures);
            MessageBox.Show(this, ex.Message, "Shutdown failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _shutdownInProgress = false;
            if (!_allowClose) { IsEnabled = true; ResumeScreenshotJobs(); UpdateActionButtons(); }
        }
    }

    private async Task ShowParsedIocsAsync(CaptureSession session, ParsedIocResult parsed)
    {
        var version = ++_iocLoadVersion;
        ClearParsedIocs($"Preparing IOCs for {session.DisplayName}...");
        try
        {
            var rows = await Task.Run(() => parsed.ToEntries().ToList());
            if (version != _iocLoadVersion || _shutdownInProgress || _allowClose ||
                CapturesGrid.SelectedItem is not CaptureSession selected || selected.Id != session.Id) return;
            _currentIocRows = rows;
            ApplyIocFilter();
            ShowAnalysis(parsed);
        }
        catch (Exception ex)
        {
            if (version == _iocLoadVersion && !_allowClose) ClearParsedIocs($"Cannot display IOCs: {ex.Message}");
        }
    }

    private void ClearParsedIocs(string message)
    {
        ClearAnalysis(message);
        _currentIocRows = new List<IocEntry>();
        _visibleIocRows = new List<IocEntry>();
        if (ParsedIocGrid is not null)
            ParsedIocGrid.ItemsSource = null;
        if (IocHashSourceColumn is not null)
            IocHashSourceColumn.Visibility = Visibility.Collapsed;

        if (ParsedSummaryTextBlock is not null)
            ParsedSummaryTextBlock.Text = message;
    }

    private void IocFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyIocFilter();
    }

    private void ApplyIocFilter()
    {
        if (ParsedIocGrid is null)
            return;

        var selectedType = "All";
        if (IocTypeComboBox?.SelectedItem is ComboBoxItem item && item.Content is string content)
            selectedType = content;

        var filter = IocFilterTextBox?.Text?.Trim() ?? string.Empty;
        var hideBrowserNoise = HideBrowserNoiseCheckBox?.IsChecked == true;
        SetNoiseColumnVisibility(showNoiseColumns: !hideBrowserNoise);
        IEnumerable<IocEntry> rows = _currentIocRows;

        if (hideBrowserNoise)
            rows = rows.Where(row => !row.IsNoise);

        if (!string.Equals(selectedType, "All", StringComparison.OrdinalIgnoreCase))
            rows = rows.Where(row => string.Equals(row.Type, selectedType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(filter))
            rows = rows.Where(row => row.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.HashSources.Any(source => source.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    source.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)));

        var filtered = rows.ToList();
        _visibleIocRows = filtered;
        ParsedIocGrid.ItemsSource = filtered;
        if (IocHashSourceColumn is not null)
            IocHashSourceColumn.Visibility = filtered.Any(row => row.HashSources.Count > 0)
                ? Visibility.Visible : Visibility.Collapsed;

        if (_currentIocRows.Count > 0)
        {
            var noiseCount = _currentIocRows.Count(row => row.IsNoise);
            var hiddenText = hideBrowserNoise ? $", {noiseCount} browser-noise items hidden" : string.Empty;
            ParsedSummaryTextBlock.Text = $"Showing {filtered.Count} of {_currentIocRows.Count} parsed items{hiddenText}.";
        }
    }



    private void CopyVisibleIocs_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleIocRows.Count == 0)
        {
            SetStatus("No visible IOCs to copy.");
            return;
        }

        try
        {
            Clipboard.SetText(IocExportService.Build(_visibleIocRows, "tsv", IncludeNoiseColumnsForVisibleExport(), DefangExportsCheckBox.IsChecked == true));
            SetStatus($"Copied {_visibleIocRows.Count} visible IOC rows to the clipboard.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportVisibleIocs_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleIocRows.Count == 0)
        {
            SetStatus("No visible IOCs to export.");
            return;
        }

        ExportIocRows(_visibleIocRows, IncludeNoiseColumnsForVisibleExport(), "visible-iocs.tsv");
    }

    private void ExportAllIocs_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIocRows.Count == 0)
        {
            SetStatus("No parsed IOCs to export.");
            return;
        }

        ExportIocRows(_currentIocRows, includeNoiseColumns: true, "all-iocs.tsv");
    }


    private void OpenNoiseFilters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IocNoiseFilter.EnsureUserFilterFiles();
            Process.Start(new ProcessStartInfo
            {
                FileName = IocNoiseFilter.UserNoiseDirectory,
                UseShellExecute = true
            });
            SetStatus($"Opened user noise filter folder: {IocNoiseFilter.UserNoiseDirectory}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to open noise filters", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadNoiseFilters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IocNoiseFilter.ReloadUserRules();
            RecomputeCurrentIocNoiseFlags();
            ApplyIocFilter();
            SetStatus("Reloaded user noise filters.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to reload noise filters", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RecomputeCurrentIocNoiseFlags()
    {
        if (_currentIocRows.Count == 0)
            return;

        var classify = IocNoiseFilter.CreateClassifier();
        _currentIocRows = _currentIocRows.Select(row =>
        {
            var isNoise = classify(row.Type, row.Value, out var reason);
            return new IocEntry
            {
                Type = row.Type,
                Value = row.Value,
                HashSources = row.HashSources,
                IsNoise = isNoise,
                NoiseReason = reason
            };
        }).ToList();
    }

    private void ExportIocRows(IReadOnlyList<IocEntry> rows, bool includeNoiseColumns, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export IOCs",
            FileName = Path.GetFileNameWithoutExtension(defaultFileName),
            DefaultExt = ".tsv",
            Filter = "Tab-separated values (*.tsv)|*.tsv|CSV (*.csv)|*.csv|JSON (*.json)|*.json|Text files (*.txt)|*.txt",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var format = Path.GetExtension(dialog.FileName).TrimStart('.').ToLowerInvariant();
            if (format is not ("tsv" or "csv" or "json" or "txt"))
                throw new InvalidOperationException("Choose a TSV, CSV, JSON or TXT extension.");
            File.WriteAllText(dialog.FileName, IocExportService.Build(rows, format, includeNoiseColumns, DefangExportsCheckBox.IsChecked == true), Encoding.UTF8);
            SetStatus($"Exported {rows.Count} IOC rows to {dialog.FileName}.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool IncludeNoiseColumnsForVisibleExport()
    {
        // Match the visible grid: when browser noise is hidden, the diagnostic noise
        // columns are hidden too, so copying/exporting visible rows stays clean.
        return HideBrowserNoiseCheckBox?.IsChecked != true;
    }

    private void IocGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ParsedIocGrid, source) is DataGridRow row)
        {
            if (!row.IsSelected) ParsedIocGrid.SelectedItem = row.Item;
        }
        else ParsedIocGrid.UnselectAll();
    }

    private void CopySelectedIocRaw_Click(object sender, RoutedEventArgs e) => CopySelectedIocs(false);
    private void CopySelectedIocDefanged_Click(object sender, RoutedEventArgs e) => CopySelectedIocs(true);

    private void CopySelectedIocs(bool defang)
    {
        var rows = ParsedIocGrid.SelectedItems.OfType<IocEntry>().ToArray();
        if (rows.Length == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => defang ? IocExportService.Defang(r.Type, r.Value) : r.Value)));
            SetStatus($"Copied {rows.Length} selected indicators.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LookupIoc_Click(object sender, RoutedEventArgs e)
    {
        if (ParsedIocGrid.SelectedItems.Count != 1 || ParsedIocGrid.SelectedItem is not IocEntry row)
        { SetStatus("Select exactly one indicator for external lookup."); return; }
        if (!(row.Type.Contains("domain", StringComparison.OrdinalIgnoreCase) || row.Type is "IP address" or "SHA-256" or "SHA-1" or "MD5"))
        {
            SetStatus("External lookup is limited to domains, IPs and hashes. Full captured URLs are not sent.");
            return;
        }
        if (MessageBox.Show(this, $"Send this indicator to VirusTotal using your normal browser?\n\n{row.Value}\n\nThis discloses the indicator to an external service. No capture or file is uploaded.",
            "Confirm external lookup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo { FileName = "https://www.virustotal.com/gui/search/" + Uri.EscapeDataString(row.Value), UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Lookup failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ClearAnalysis(string message)
    {
        _httpRows = Array.Empty<HttpActivity>();
        if (AnalysisSummary is not null) AnalysisSummary.Text = message;
        if (AnalysisWarning is not null) AnalysisWarning.Text = "";
        if (DomainActivityGrid is not null) DomainActivityGrid.ItemsSource = null;
        if (RedirectGrid is not null) RedirectGrid.ItemsSource = null;
        if (FilesGrid is not null) FilesGrid.ItemsSource = null;
        ApplyHttpFilter();
    }

    private void ShowAnalysis(ParsedIocResult parsed)
    {
        var analysis = parsed.Analysis;
        if (analysis?.Available != true)
        {
            ClearAnalysis($"{parsed.Domains.Count} domains · {parsed.Urls.Count} unique URLs · {parsed.PostUrls.Count} POST URLs");
            AnalysisWarning.Text = analysis?.Warning ?? "Advanced analysis unavailable. Select Parse IOCs to regenerate it.";
            return;
        }
        _httpRows = analysis.Requests;
        var ipCount = analysis.Requests.Select(r => r.PeerIp).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var posts = analysis.Requests.Count(r => r.Method.Equals("POST", StringComparison.OrdinalIgnoreCase));
        var hashed = analysis.Files.Count(f => !string.IsNullOrEmpty(f.SHA256));
        var bytes = analysis.Requests.Sum(r => r.ResponseBytes ?? 0);
        var noResponse = analysis.Requests.Count(r => r.StatusCode is null);
        AnalysisSummary.Text = $"{analysis.Requests.Count:N0} HTTP requests · {parsed.Domains.Count:N0} domains · {parsed.Urls.Count:N0} unique URLs · {posts:N0} POST requests\n" +
            $"{analysis.Redirects.Count:N0} redirects · {analysis.Files.Count:N0} file candidates ({hashed:N0} hashed) · {ipCount:N0} peer IPs · {noResponse:N0} without response · {bytes:N0} retained response bytes";
        AnalysisWarning.Text = "Counts include browser noise. Peer IPs are recorded connection endpoints, not necessarily origin servers.";
        DomainActivityGrid.ItemsSource = analysis.Requests.GroupBy(r => r.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DomainActivity(g.Key, g.Count())).OrderByDescending(r => r.Requests).ThenBy(r => r.Domain).ToArray();
        RedirectGrid.ItemsSource = analysis.Redirects;
        FilesGrid.ItemsSource = analysis.Files;
        ApplyHttpFilter();
    }

    private void HttpFilter_Changed(object sender, TextChangedEventArgs e) => ApplyHttpFilter();

    private void ApplyHttpFilter()
    {
        if (HttpGrid is null || HttpCountText is null) return;
        var filter = HttpSearchBox?.Text.Trim() ?? "";
        IReadOnlyList<HttpActivity> combined = _screenshotRows.Count == 0 ? _httpRows : _httpRows.Concat(ScreenshotTimelineRows())
            .OrderBy(r => DateTimeOffset.TryParse(r.Utc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : DateTimeOffset.MaxValue).ToArray();
        IReadOnlyList<HttpActivity> visible = filter.Length == 0 ? combined : combined.Where(r => new[] {r.Url, r.Domain, r.Method, r.StatusCode?.ToString() ?? "", r.PeerIp, r.Event, r.ContentType, r.Error, r.Utc, r.Detail}
            .Any(v => v.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        HttpGrid.ItemsSource = visible;
        HttpCountText.Text = $"{visible.Count:N0} / {combined.Count:N0} events ({_httpRows.Count:N0} HTTP, {_screenshotRows.Count:N0} screenshots)";
    }

    private void SetNoiseColumnVisibility(bool showNoiseColumns)
    {
        var visibility = showNoiseColumns ? Visibility.Visible : Visibility.Collapsed;

        if (IocNoiseColumn is not null)
            IocNoiseColumn.Visibility = visibility;

        if (IocNoiseReasonColumn is not null)
            IocNoiseReasonColumn.Visibility = visibility;
    }

    private CaptureSession? SelectedSession()
    {
        if (CapturesGrid.SelectedItem is CaptureSession session) return session;
        MessageBox.Show(this, "Select a capture first.", "No capture selected", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private BrowserKind SelectedBrowser()
    {
        return BrowserComboBox.SelectedIndex switch
        {
            1 => BrowserKind.Firefox,
            2 => BrowserKind.FirefoxPortable,
            _ => BrowserKind.Edge
        };
    }

    private void SetStatus(string message) => StatusTextBlock.Text = message;
}
