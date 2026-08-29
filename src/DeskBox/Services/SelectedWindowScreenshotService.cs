using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Foundation;
using WinRT;

namespace DeskBox.Services;

/// <summary>
/// Xuất nội dung riêng của một HWND bằng Windows Graphics Capture. Dịch vụ này
/// chỉ được tạo đúng lúc người dùng lưu/sao chép một cửa sổ đã chọn, vì vậy không
/// giữ D3D, frame pool hoặc bộ nhớ ảnh trong nền.
/// </summary>
internal static partial class SelectedWindowScreenshotService
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    public static async Task<string> CaptureAsync(IntPtr window)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(window, IntPtr.Zero);

        try
        {
            string path = await CaptureWithWindowsGraphicsCaptureAsync(window);
            App.Log($"[Screenshot] window-isolated engine=wgc hwnd=0x{window.ToInt64():X}");
            return path;
        }
        catch (Exception ex) when (IsRecoverableCaptureFailure(ex))
        {
            App.Log($"[Screenshot] window-wgc-fallback hwnd=0x{window.ToInt64():X} reason={ex.GetType().Name}: {ex.Message}");
            string path = await Task.Run(() => CaptureWithPrintWindow(window));
            App.Log($"[Screenshot] window-isolated engine=printwindow hwnd=0x{window.ToInt64():X}");
            return path;
        }
    }

    private static async Task<string> CaptureWithWindowsGraphicsCaptureAsync(IntPtr window)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows Graphics Capture is not supported on this device.");
        }

        GraphicsCaptureItem item = CreateItemForWindow(window);
        if (item.Size.Width <= 0 || item.Size.Height <= 0)
        {
            throw new InvalidOperationException("The selected window is no longer available for capture.");
        }

        using (ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport))
        using (IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>())
        using (IDirect3DDevice direct3DDevice = CreateDirect3DDevice(dxgiDevice))
        using (Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                   direct3DDevice,
                   DirectXPixelFormat.B8G8R8A8UIntNormalized,
                   numberOfBuffers: 1,
                   item.Size))
        using (GraphicsCaptureSession session = framePool.CreateCaptureSession(item))
        {
            var frameSource = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<Direct3D11CaptureFramePool, object>? handler = null;
            handler = (sender, _) =>
            {
                try
                {
                    Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
                    if (!frameSource.TrySetResult(frame))
                    {
                        frame.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    frameSource.TrySetException(ex);
                }
            };

            framePool.FrameArrived += handler;
            try
            {
                session.IsCursorCaptureEnabled = false;
                session.StartCapture();

                using var timeout = new CancellationTokenSource(FrameTimeout);
                using CancellationTokenRegistration registration = timeout.Token.Register(
                    () => frameSource.TrySetException(new TimeoutException("Timed out waiting for an isolated window frame.")));
                using Direct3D11CaptureFrame frame = await frameSource.Task;
                return await SaveFrameAsync(frame.Surface);
            }
            finally
            {
                framePool.FrameArrived -= handler;
            }
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        IntPtr hstring = IntPtr.Zero;
        IntPtr factoryPointer = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(
                "Windows.Graphics.Capture.GraphicsCaptureItem",
                "Windows.Graphics.Capture.GraphicsCaptureItem".Length,
                out hstring));
            Guid interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref interopGuid, out factoryPointer));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            return CreateCaptureItem(interop, window);
        }
        finally
        {
            if (factoryPointer != IntPtr.Zero)
            {
                Marshal.Release(factoryPointer);
            }
            if (hstring != IntPtr.Zero)
            {
                WindowsDeleteString(hstring);
            }
        }
    }

    private static GraphicsCaptureItem CreateCaptureItem(IGraphicsCaptureItemInterop interop, IntPtr window)
    {
        IntPtr abiItem = interop.CreateForWindow(window, GraphicsCaptureItemGuid);
        if (abiItem == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows Graphics Capture did not create an item for the selected window.");
        }

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(abiItem)
                ?? throw new InvalidOperationException("Could not marshal the selected capture item.");
        }
        finally
        {
            Marshal.Release(abiItem);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
    {
        int hresult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr abiDevice);
        Marshal.ThrowExceptionForHR(hresult);
        if (abiDevice == IntPtr.Zero)
        {
            throw new InvalidOperationException("Direct3D interop returned an empty WinRT device.");
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abiDevice)
                ?? throw new InvalidOperationException("Could not create the WinRT Direct3D device.");
        }
        finally
        {
            Marshal.Release(abiDevice);
        }
    }

    private static async Task<string> SaveFrameAsync(IDirect3DSurface surface)
    {
        using SoftwareBitmap source = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
        using SoftwareBitmap bitmap = SoftwareBitmap.Convert(
            source,
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(GetScreenshotCacheDirectory());
        StorageFile file = await folder.CreateFileAsync(
            $"window-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png",
            CreationCollisionOption.FailIfExists);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return file.Path;
    }

    private static string CaptureWithPrintWindow(IntPtr window)
    {
        if (!Win32Helper.GetWindowRect(window, out Win32Helper.RECT bounds) ||
            bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new InvalidOperationException("Could not get the selected window bounds for fallback capture.");
        }

        using var bitmap = new Bitmap(
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(window, hdc, PwRenderFullContent))
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"PrintWindow failed (Win32 error {error}).");
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        string directory = GetScreenshotCacheDirectory();
        string path = Path.Combine(directory, $"window-fallback-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static string GetScreenshotCacheDirectory()
    {
        string directory = Path.Combine(DeskBoxDataPathService.Current.RootPath, "cache", "screenshots");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsRecoverableCaptureFailure(Exception ex) => ex is
        COMException or
        InvalidOperationException or
        NotSupportedException or
        TimeoutException or
        TypeLoadException or
        MissingMethodException;

    private const uint PwRenderFullContent = 0x00000002;
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr hstring, ref Guid iid, out IntPtr factory);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
}
