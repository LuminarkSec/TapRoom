using System.Windows;
using TapRoom.Services;

namespace TapRoom;

public partial class SetupWindow : Window
{
    private readonly SetupManager _setupManager = new();
    private CancellationTokenSource? _cts;

    public SetupWindow()
    {
        InitializeComponent();
        _setupManager.Log += message => Dispatcher.Invoke(() => AppendLog(message));
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        AppendLog("Starting setup...");

        try
        {
            var installMode = InstallModeComboBox.SelectedIndex == 1
                ? MitmproxyInstallMode.WindowsInstaller
                : MitmproxyInstallMode.StandaloneUserProfile;

            await _setupManager.RunAsync(LocalMachineTrustCheckBox.IsChecked == true, installMode, ForceInstallCheckBox.IsChecked == true, _cts.Token);
            AppendLog("Setup completed successfully.");
            MessageBox.Show(this, "TapRoom setup completed successfully.", "Setup complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Setup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }
}
