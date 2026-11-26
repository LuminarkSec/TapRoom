using System.Windows;
using System.Windows.Input;

namespace TapRoom;

public partial class UrlPromptWindow : Window
{
    public string Url => UrlTextBox.Text.Trim();

    public UrlPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlTextBox.Focus();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            MessageBox.Show(this, "Enter a URL first.", "URL required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UrlTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Start_Click(sender, e);
    }
}
