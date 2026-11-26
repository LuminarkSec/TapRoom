using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TapRoom.Models;
using TapRoom.Services;

namespace TapRoom;

public partial class SettingsWindow : Window
{
    private bool _saving;
    public SettingsWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        var settings = SettingsStore.Load(SettingsStore.FilePath, out _);
        BrowserBox.SelectedIndex = (int)settings.DefaultBrowser;
        LaunchBox.IsChecked = settings.AutoLaunchBrowser;
        StopBox.IsChecked = settings.StopOnBrowserClose;
        ParseBox.IsChecked = settings.AutoParseAfterStop;
        NoiseBox.IsChecked = settings.HideBrowserNoise;
        ScreenshotDefaultBox.IsChecked = settings.ScreenshotEnabled;
        ScreenshotIntervalBox.Text = settings.ScreenshotIntervalSeconds.ToString();
        ScreenshotThresholdBox.Text = settings.ScreenshotChangePercent.ToString(System.Globalization.CultureInfo.CurrentCulture);
        ScreenshotInitialBox.IsChecked = settings.ScreenshotSaveInitial;
        ScreenshotMaximumBox.Text = settings.ScreenshotMaximum.ToString();
        RootBox.Text = settings.StorageRoot;
        CurrentRootText.Text = SettingsStore.Current.EffectiveStorageRoot;
        FirstPortBox.Text = settings.FirstProxyPort.ToString();
        LastPortBox.Text = settings.LastProxyPort.ToString();
        EdgeBox.Text = settings.EdgePath;
        EdgeHideCopilotBox.IsChecked = settings.EdgeHideCopilot;
        EdgeHideSidebarBox.IsChecked = settings.EdgeHideSidebar;
        EdgeSuppressFirstRunBox.IsChecked = settings.EdgeSuppressFirstRun;
        EdgeDisableStartupBoostBox.IsChecked = settings.EdgeDisableStartupBoost;
        EdgeDisableBackgroundModeBox.IsChecked = settings.EdgeDisableBackgroundMode;
        FirefoxBox.Text = settings.FirefoxPath;
        PortableBox.Text = settings.FirefoxPortablePath;
        NoisePathText.Text = IocNoiseFilter.UserNoiseDirectory;
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose investigation storage directory" };
        if (Directory.Exists(RootBox.Text)) dialog.InitialDirectory = RootBox.Text;
        if (dialog.ShowDialog(this) == true) RootBox.Text = dialog.FolderName;
    }
    private void BrowseEdge_Click(object sender, RoutedEventArgs e) => BrowseExe(EdgeBox, "msedge.exe");
    private void BrowseFirefox_Click(object sender, RoutedEventArgs e) => BrowseExe(FirefoxBox, "firefox.exe");
    private void BrowsePortable_Click(object sender, RoutedEventArgs e) => BrowseExe(PortableBox, "FirefoxPortable.exe");
    private void BrowseExe(TextBox target, string file)
    {
        var dialog = new OpenFileDialog { Title = "Select " + file, Filter = "Executable (*.exe)|*.exe", CheckFileExists = true, FileName = file };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }
    private void OpenNoise_Click(object sender, RoutedEventArgs e)
    {
        try { IocNoiseFilter.EnsureUserFilterFiles(); Process.Start(new ProcessStartInfo(IocNoiseFilter.UserNoiseDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to open filters", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FirstPortBox.Text, out var first) || !int.TryParse(LastPortBox.Text, out var last))
        {
            MessageBox.Show(this, "Enter valid numeric port values.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(ScreenshotIntervalBox.Text, out var interval) ||
            !double.TryParse(ScreenshotThresholdBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var threshold) ||
            !int.TryParse(ScreenshotMaximumBox.Text, out var maximum))
        {
            MessageBox.Show(this, "Enter valid numeric screenshot settings.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var settings = new AppSettings
        {
            StorageRoot = RootBox.Text.Trim(), DefaultBrowser = (BrowserKind)BrowserBox.SelectedIndex,
            AutoLaunchBrowser = LaunchBox.IsChecked == true, StopOnBrowserClose = StopBox.IsChecked == true,
            AutoParseAfterStop = ParseBox.IsChecked == true, HideBrowserNoise = NoiseBox.IsChecked == true,
            ScreenshotEnabled = ScreenshotDefaultBox.IsChecked == true, ScreenshotIntervalSeconds = interval,
            ScreenshotChangePercent = threshold, ScreenshotSaveInitial = ScreenshotInitialBox.IsChecked == true, ScreenshotMaximum = maximum,
            FirstProxyPort = first, LastProxyPort = last, EdgePath = EdgeBox.Text.Trim(),
            EdgeHideCopilot = EdgeHideCopilotBox.IsChecked == true,
            EdgeHideSidebar = EdgeHideSidebarBox.IsChecked == true,
            EdgeSuppressFirstRun = EdgeSuppressFirstRunBox.IsChecked == true,
            EdgeDisableStartupBoost = EdgeDisableStartupBoostBox.IsChecked == true,
            EdgeDisableBackgroundMode = EdgeDisableBackgroundModeBox.IsChecked == true,
            FirefoxPath = FirefoxBox.Text.Trim(), FirefoxPortablePath = PortableBox.Text.Trim()
        };
        _saving = true;
        IsEnabled = false;
        try
        {
            await Task.Run(() => SettingsStore.SaveForRestart(settings));
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Settings not saved", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _saving = false; IsEnabled = true; }
    }
}
