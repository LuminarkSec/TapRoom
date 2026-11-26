using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TapRoom.Services;

namespace TapRoom;

// Read-only viewer. No browser navigation or shell opening of image content.
internal sealed class ScreenshotViewer : Window
{
    private readonly string _folder;
    private readonly ScreenshotRecord[] _records;
    private int _index;
    private long _version;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Button _previous = new() { Content = "Previous", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4) };
    private readonly Button _next = new() { Content = "Next", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4) };
    private readonly CheckBox _actualSize = new() { Content = "Actual size", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };

    internal ScreenshotViewer(string folder, ScreenshotRecord[] records, int index, string displayName)
    {
        _folder = folder; _records = records; _index = index;
        Title = "TapRoom screenshots — " + displayName;
        Width = 1120; Height = 800; MinWidth = 480; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(6) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(_previous); toolbar.Children.Add(_next); toolbar.Children.Add(_actualSize);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(_caption, Dock.Bottom); root.Children.Add(_caption);
        _scroll.Content = _image; root.Children.Add(_scroll); Content = root;
        _previous.Click += (_, _) => Move(-1); _next.Click += (_, _) => Move(1);
        _actualSize.Checked += (_, _) => UpdateScale(); _actualSize.Unchecked += (_, _) => UpdateScale();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Left) Move(-1); else if (e.Key == Key.Right) Move(1); else if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { ++_version; _image.Source = null; };
        _ = LoadAsync();
    }
    private void UpdateScale()
    {
        bool actual = _actualSize.IsChecked == true;
        _image.Stretch = actual ? Stretch.None : Stretch.Uniform;
        _scroll.HorizontalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
    private void Move(int delta)
    {
        int next = _index + delta;
        if (next < 0 || next >= _records.Length) return;
        _index = next; _ = LoadAsync();
    }
    private async Task LoadAsync()
    {
        var version = ++_version;
        var record = _records[_index];
        _previous.IsEnabled = _index > 0; _next.IsEnabled = _index + 1 < _records.Length;
        _image.Source = null;
        _caption.Text = $"{_index + 1} / {_records.Length}   |   {record.CapturedUtc:yyyy-MM-dd HH:mm:ss.fff} UTC   |   {record.Width}×{record.Height}   |   {record.Reason}\n{record.WindowTitle}";
        try
        {
            var image = await Task.Run(() => ScreenshotStore.ReadImage(_folder, record.FileName));
            if (version == _version) _image.Source = image;
        }
        catch (Exception ex) { if (version == _version) _caption.Text += "\nCannot open image: " + ex.Message; }
    }
}
