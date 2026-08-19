using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DeskBox.Views;

/// <summary>
/// Lightweight full-monitor selector. Hovering a normal top-level window
/// selects that window; hovering Desktop or Taskbar selects the whole monitor.
/// </summary>
public sealed partial class ScreenshotCaptureWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly Win32Helper.RECT _monitorRect;
    private IntPtr _selectedWindow;
    private Win32Helper.RECT _selectedRect;
    private bool _isCapturing;

    public ScreenshotCaptureWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) ||
            !Win32Helper.TryGetMonitorWorkArea(cursor.X, cursor.Y, out Win32Helper.RECT monitor, out _))
        {
            monitor = new Win32Helper.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }

        _monitorRect = monitor;
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        _appWindow.IsShownInSwitchers = false;
        _appWindow.MoveAndResize(new RectInt32(
            monitor.Left,
            monitor.Top,
            Math.Max(1, monitor.Right - monitor.Left),
            Math.Max(1, monitor.Bottom - monitor.Top)));
        Win32Helper.SetWindowPos(
            _hwnd,
            Win32Helper.HWND_TOPMOST,
            monitor.Left,
            monitor.Top,
            monitor.Right - monitor.Left,
            monitor.Bottom - monitor.Top,
            Win32Helper.SWP_SHOWWINDOW);

        RootGrid.PointerMoved += RootGrid_PointerMoved;
        RootGrid.KeyDown += RootGrid_KeyDown;
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        _selectedWindow = FindWindowAtPoint(cursor, out _selectedRect);
        if (_selectedWindow == IntPtr.Zero)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SelectionHintText.Text = "Toàn màn hình";
            return;
        }

        SelectionHintText.Text = GetWindowTitle(_selectedWindow);
        int left = Math.Max(_selectedRect.Left, _monitorRect.Left) - _monitorRect.Left;
        int top = Math.Max(_selectedRect.Top, _monitorRect.Top) - _monitorRect.Top;
        int right = Math.Min(_selectedRect.Right, _monitorRect.Right) - _monitorRect.Left;
        int bottom = Math.Min(_selectedRect.Bottom, _monitorRect.Bottom) - _monitorRect.Top;
        SelectionBorder.Width = Math.Max(1, right - left);
        SelectionBorder.Height = Math.Max(1, bottom - top);
        SelectionBorder.Margin = new Thickness(left, top, 0, 0);
        SelectionBorder.HorizontalAlignment = HorizontalAlignment.Left;
        SelectionBorder.VerticalAlignment = VerticalAlignment.Top;
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
        }
    }

    private async Task CaptureAsync()
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        CaptureButton.IsEnabled = false;
        Win32Helper.RECT captureRect = _selectedWindow == IntPtr.Zero
            ? _monitorRect
            : _selectedRect;

        try
        {
            _appWindow.Hide();
            await Task.Delay(80);
            string imagePath = await Task.Run(() => CaptureScreenToPng(captureRect));
            StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(
                Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            App.Log($"[Screenshot] Captured mode={(_selectedWindow == IntPtr.Zero ? "monitor" : "window")} path={imagePath}");
            Close();
        }
        catch (Exception ex)
        {
            App.Log($"[Screenshot] Capture failed: {ex}");
            _appWindow.Show();
            CaptureButton.IsEnabled = true;
            _isCapturing = false;
        }
    }

    private static string CaptureScreenToPng(Win32Helper.RECT rect)
    {
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        string directory = Path.Combine(DeskBoxDataPathService.Current.RootPath, "cache", "screenshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                rect.Left,
                rect.Top,
                0,
                0,
                new DrawingSize(width, height),
                CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(path, ImageFormat.Png);
        CleanupOldScreenshots(directory);
        return path;
    }

    private static void CleanupOldScreenshots(string directory)
    {
        DateTime cutoff = DateTime.Now - TimeSpan.FromDays(2);
        foreach (string path in Directory.EnumerateFiles(directory, "screenshot-*.png"))
        {
            try
            {
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    private IntPtr FindWindowAtPoint(Win32Helper.POINT point, out Win32Helper.RECT rect)
    {
        Win32Helper.RECT selectedRect = default;
        IntPtr result = IntPtr.Zero;
        uint currentProcessId = (uint)Environment.ProcessId;
        Win32Helper.EnumWindows((window, _) =>
        {
            if (window == _hwnd || !Win32Helper.IsWindowVisible(window) ||
                Win32Helper.GetWindowThreadProcessId(window, out uint processId) == 0 ||
                processId == currentProcessId ||
                !Win32Helper.GetWindowRect(window, out Win32Helper.RECT candidate) ||
                candidate.Right <= candidate.Left || candidate.Bottom <= candidate.Top ||
                point.X < candidate.Left || point.X >= candidate.Right ||
                point.Y < candidate.Top || point.Y >= candidate.Bottom)
            {
                return true;
            }

            string className = GetWindowClassName(window);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "DV2ControlHost")
            {
                return true;
            }

            result = window;
            selectedRect = candidate;
            return false;
        }, IntPtr.Zero);
        rect = selectedRect;
        return result;
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var text = new StringBuilder(256);
        Win32Helper.GetWindowText(window, text, text.Capacity);
        return string.IsNullOrWhiteSpace(text.ToString()) ? "Cửa sổ" : text.ToString();
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var text = new StringBuilder(128);
        Win32Helper.GetClassName(window, text, text.Capacity);
        return text.ToString();
    }
}
