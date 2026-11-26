using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TapRoom.Models;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TapRoom.Services;

// Windows Graphics Capture, not a crop of the desktop. No cursor, monitor,
// PrintWindow or screen-copy fallback; unsupported capture fails closed.
internal static class WindowScreenshotCapture
{
    internal sealed record Target(nint Handle, int Pid, DateTime ProcessStartedUtc, string Title);
    internal sealed record Frame(BitmapSource Image, byte[] Sample, DateTime CapturedUtc);

    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && GraphicsCaptureSession.IsSupported();

    internal static IReadOnlyList<Target> FindWindows(CaptureSession session)
    {
        var scan = BrowserProfileMonitor.Scan(session.Browser, session.BrowserProfileDirectory);
        if (scan.ProcessIds.Count == 0 && !scan.Reliable)
            throw new InvalidOperationException(scan.Error ?? "Browser profile could not be verified.");
        var result = new List<Target>();
        var pids = scan.ProcessIds.ToHashSet();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            var expected = session.Browser == BrowserKind.Edge ? "Chrome_WidgetWin_1" : "MozillaWindowClass";
            if (!string.Equals(cls.ToString(), expected, StringComparison.Ordinal)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) != 0 || cloaked != 0) return true;
            if (!GetWindowRect(hwnd, out var rect) || rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 60) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var started = process.StartTime.ToUniversalTime();
                var title = new StringBuilder(1024);
                GetWindowText(hwnd, title, title.Capacity);
                result.Add(new(hwnd, (int)pid, started, title.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        var foreground = GetForegroundWindow();
        return result.OrderByDescending(w => w.Handle == foreground).ToArray();
    }

    // Keep a window-specific capture session alive between checks. FrameArrived
    // runs on the free-threaded pool worker; retain only the newest frame, and
    // transfer ownership to the sampler before copying its GPU surface.
    internal sealed class Source : IDisposable
    {
        private const int Buffers = 3;
        private readonly object _sync = new();
        private readonly Target _target;
        private readonly Process _process;
        private readonly IDirect3DDevice _device;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _capture;
        private Direct3D11CaptureFrame? _latest;
        private DateTime _latestUtc;
        private SizeInt32 _size;
        private bool _disposed, _resizing, _received;
        private string? _callbackError;
        private Frame? _lastUsable;
        internal string Driver { get; }

        internal Source(CaptureSession session, Target target)
        {
            _target = target;
            _process = Process.GetProcessById(target.Pid);
            IDirect3DDevice? device = null;
            Direct3D11CaptureFramePool? pool = null;
            GraphicsCaptureSession? capture = null;
            try
            {
                _ = _process.Handle;
                if (!Verified(session)) throw new InvalidOperationException("Window identity or browser profile changed before capture.");
                if (!IsSupported) throw new PlatformNotSupportedException("Cursor-free Windows Graphics Capture is unavailable.");
                var item = CreateItem(target.Handle);
                _size = item.Size;
                if (!ValidSize(_size)) throw new InvalidOperationException($"Invalid initial window size {_size.Width}x{_size.Height}.");
                device = CreateDevice(out var driver);
                Driver = driver;
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, _size);
                capture = pool.CreateCaptureSession(item);
                capture.IsCursorCaptureEnabled = false;
                _device = device; _pool = pool; _capture = capture;
                _pool.FrameArrived += FrameArrived;
                _capture.StartCapture();
            }
            catch
            {
                if (pool is not null) pool.FrameArrived -= FrameArrived;
                lock (_sync) { _disposed = true; _latest?.Dispose(); _latest = null; }
                try { capture?.Dispose(); } catch { }
                try { pool?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
                _process.Dispose();
                throw;
            }
        }

        private static bool ValidSize(SizeInt32 size) => size.Width >= 80 && size.Height >= 60 && (long)size.Width * size.Height <= 40_000_000;
        private bool Verified(CaptureSession session) => StillMatches(_target, _process) &&
            BrowserProfileMonitor.Scan(session.Browser, session.BrowserProfileDirectory).ProcessIds.Contains(_target.Pid) && StillMatches(_target, _process);

        private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_sync)
            {
                if (_disposed || _resizing) return;
                try
                {
                    var frame = sender.TryGetNextFrame();
                    if (frame is null) return;
                    _latest?.Dispose();
                    _latest = frame;
                    _latestUtc = DateTime.UtcNow;
                    _callbackError = null;
                }
                catch (Exception ex) { _callbackError = $"FrameArrived failed: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}"; }
            }
        }

        private (Direct3D11CaptureFrame? Frame, DateTime Utc, string? Error) TakeLatest()
        {
            lock (_sync)
            {
                var frame = _latest;
                _latest = null;
                return (frame, _latestUtc, _callbackError);
            }
        }

        private void Resize(SizeInt32 size)
        {
            lock (_sync)
            {
                _resizing = true;
                _latest?.Dispose(); _latest = null;
            }
            try
            {
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, size);
                _size = size;
                _lastUsable = null;
                _received = false;
            }
            finally { lock (_sync) _resizing = false; }
        }

        internal async Task<(Frame? Frame, string Detail)> ReadAsync(CaptureSession session, bool manual, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!Verified(session)) return (null, "Browser window is no longer verified, visible or non-minimised.");
            var timer = Stopwatch.StartNew();
            var timeout = TimeSpan.FromMilliseconds(!_received || manual ? 5000 : 500);
            string? problem = null;
            while (timer.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                var (raw, utc, callbackError) = TakeLatest();
                if (raw is null)
                {
                    if (callbackError is not null) problem = callbackError;
                    await Task.Delay(40, ct).ConfigureAwait(false);
                    continue;
                }
                SizeInt32? newSize = null;
                using (raw)
                {
                    if (!StillMatches(_target, _process)) return (null, "Browser window closed, minimised or changed during acquisition.");
                    var content = raw.ContentSize;
                    if (!ValidSize(content)) { problem = $"Frame has invalid content size {content.Width}x{content.Height}."; continue; }
                    if (content.Width != _size.Width || content.Height != _size.Height)
                    {
                        problem = $"Frame resized from {_size.Width}x{_size.Height} to {content.Width}x{content.Height}; recreating frame pool.";
                        newSize = content;
                    }
                    else
                    {
                        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(raw.Surface, BitmapAlphaMode.Ignore);
                        ct.ThrowIfCancellationRequested();
                        if (bitmap.PixelWidth != content.Width || bitmap.PixelHeight != content.Height)
                        { problem = $"Surface size {bitmap.PixelWidth}x{bitmap.PixelHeight} differs from frame {content.Width}x{content.Height}."; continue; }
                        using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                        int stride = checked(content.Width * 4), length = checked(stride * content.Height);
                        var buffer = new Windows.Storage.Streams.Buffer((uint)length) { Length = (uint)length };
                        converted.CopyToBuffer(buffer);
                        var bytes = new byte[length];
                        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
                        var sample = ScreenshotDifference.Sample(bytes, content.Width, content.Height, stride);
                        if (sample.All(b => b <= 3)) { problem = $"Received uniformly black frame ({content.Width}x{content.Height}); not saved."; _lastUsable = null; continue; }
                        if (!StillMatches(_target, _process)) return (null, "Browser window changed during surface copy.");
                        var image = BitmapSource.Create(content.Width, content.Height, 96, 96, PixelFormats.Bgr32, null, bytes, stride);
                        image.Freeze();
                        _received = true;
                        _lastUsable = new(image, sample, utc);
                        return (_lastUsable, $"Received usable frame {content.Width}x{content.Height}.");
                    }
                }
                // Release the acquired frame before resizing the pool.
                if (newSize is SizeInt32 next) Resize(next);
            }
            if (problem is not null) return (null, problem);
            if (_received)
            {
                if (manual && _lastUsable is not null && StillMatches(_target, _process))
                    return (_lastUsable, "No new frame; using last observed frame with its original timestamp.");
                return (null, "No new frame since previous check (window may be unchanged).");
            }
            return (null, $"No frame arrived within {timeout.TotalSeconds:0} seconds ({Driver}).");
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _latest?.Dispose(); _latest = null; _lastUsable = null;
            }
            _pool.FrameArrived -= FrameArrived;
            try { _capture.Dispose(); } catch { }
            try { _pool.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
            _process.Dispose();
        }
    }

    private static bool StillMatches(Target target, Process process)
    {
        GetWindowThreadProcessId(target.Handle, out var pid);
        return pid == (uint)target.Pid && !process.HasExited && process.StartTime.ToUniversalTime() == target.ProcessStartedUtc &&
            IsWindowVisible(target.Handle) && !IsIconic(target.Handle) &&
            DwmGetWindowAttribute(target.Handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked == 0;
    }

    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        // IGraphicsCaptureItemInterop is an IUnknown interface. Call its first
        // method after QueryInterface/AddRef/Release (slot 3).
        nint name = 0, factory = 0, item = 0;
        int init = RoInitialize(1); // MTA; a pre-existing STA is also usable
        if (init < 0 && init != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(init);
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out name));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref interopIid, out factory));
            var vtable = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(create(factory, hwnd, ref itemIid, out item));
            return GraphicsCaptureItem.FromAbi(item) ?? throw new InvalidOperationException("Windows returned no capture item.");
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (name != 0) WindowsDeleteString(name);
            if (init >= 0) RoUninitialize();
        }
    }

    private static IDirect3DDevice CreateDevice(out string driver)
    {
        driver = "Hardware D3D11";
        nint d3d = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            var hr = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out d3d, out _, out context);
            if (hr < 0) // WARP for VMs without a usable hardware adapter
            {
                if (context != 0) { Marshal.Release(context); context = 0; }
                if (d3d != 0) { Marshal.Release(d3d); d3d = 0; }
                driver = "WARP software D3D11";
                hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out d3d, out _, out context);
            }
            Marshal.ThrowExceptionForHR(hr);
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C"); // IDXGIDevice
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable) ?? throw new InvalidOperationException("Windows returned no graphics device.");
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (d3d != 0) Marshal.Release(d3d);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool EnumWindowsProc(nint hwnd, nint param);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CreateForWindowDelegate(nint self, nint hwnd, ref Guid iid, out nint item);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder cls, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint initType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int WindowsCreateString(string text, uint length, out nint name);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint name);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint levelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
