using System.Text.Json;
using System.Windows;

namespace TapRoom.Services;

internal static class WindowPlacementStore
{
    private static string PlacementPath => Path.Combine(PathHelper.LocalAppDataRoot, "Config", "window.json");
    internal sealed record Placement(double Left, double Top, double Width, double Height, bool Maximised);

    internal static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(PlacementPath)) return;
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath));
            if (saved is null || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) ||
                !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height) || saved.Width <= 0 || saved.Height <= 0) return;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var width = Math.Max(window.MinWidth, Math.Min(saved.Width, screen.Width));
            var height = Math.Max(window.MinHeight, Math.Min(saved.Height, screen.Height));
            var left = Math.Max(screen.Left, Math.Min(saved.Left, screen.Right - Math.Min(width, screen.Width)));
            var top = Math.Max(screen.Top, Math.Min(saved.Top, screen.Bottom - Math.Min(height, screen.Height)));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = width; window.Height = height; window.Left = left; window.Top = top;
            if (saved.Maximised) window.WindowState = WindowState.Maximized;
        }
        catch { /* Placement is optional and must never prevent startup. */ }
    }
    internal static void Save(Window window)
    {
        try
        {
            var bounds = window.RestoreBounds;
            if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized)));
        }
        catch { /* Optional preference only. */ }
    }
}
