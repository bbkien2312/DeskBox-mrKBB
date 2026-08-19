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
using Windows.Storage.Pickers;
using WinRT.Interop;
using DrawingSize = System.Drawing.Size;

namespace DeskBox.Views;

/// <summary>
/// Selector kiểu iTop: chụp một ảnh nền trước khi overlay xuất hiện, sau đó
/// người dùng có thể thấy rõ, rê chuột và khóa một cửa sổ để lưu hoặc sao chép.
/// Desktop/taskbar không chọn cửa sổ nào nên sẽ chụp toàn màn hình hiện tại.
/// </summary>
public sealed partial class ScreenshotCaptureWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly Win32Helper.RECT _monitorRect;
    private readonly string _snapshotPath;
    private IntPtr _selectedWindow;
    private Win32Helper.RECT _selectedRect;
    private bool _selectionLocked;
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

        // Cửa sổ chỉ được Activate sau constructor, nên overlay không lọt vào ảnh nguồn.
        _snapshotPath = CaptureMonitorSnapshot(monitor);
        FrozenScreenImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_snapshotPath));

        Win32Helper.SetWindowPos(
            _hwnd,
            Win32Helper.HWND_TOPMOST,
            monitor.Left,
            monitor.Top,
            monitor.Right - monitor.Left,
            monitor.Bottom - monitor.Top,
            Win32Helper.SWP_SHOWWINDOW);

        RootGrid.KeyDown += RootGrid_KeyDown;
        Closed += (_, _) => TryDelete(_snapshotPath);
    }

    private void FrozenScreenImage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selectionLocked)
        {
            UpdateSelectionFromCursor();
        }
    }

    private void FrozenScreenImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionLocked || !e.GetCurrentPoint(FrozenScreenImage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdateSelectionFromCursor();
        _selectionLocked = true;
        CopyButton.Visibility = Visibility.Visible;
        SaveButton.Visibility = Visibility.Visible;
        ResetButton.Visibility = Visibility.Visible;
        SelectionHintText.Text = _selectedWindow == IntPtr.Zero
            ? "Toàn màn hình đã được chọn"
            : $"Đã chọn: {GetWindowTitle(_selectedWindow)}";
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _selectionLocked = false;
        CopyButton.Visibility = Visibility.Collapsed;
        SaveButton.Visibility = Visibility.Collapsed;
        ResetButton.Visibility = Visibility.Collapsed;
        UpdateSelectionFromCursor();
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e) => await ExportSelectionAsync(copyToClipboard: true);

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await ExportSelectionAsync(copyToClipboard: false);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
        }
    }

    private void UpdateSelectionFromCursor()
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        _selectedWindow = FindWindowAtPoint(cursor, out _selectedRect);
        if (_selectedWindow == IntPtr.Zero)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SelectionHintText.Text = "Toàn màn hình (bấm để khóa vùng chụp)";
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

    private async Task ExportSelectionAsync(bool copyToClipboard)
    {
        if (_isCapturing || !_selectionLocked)
        {
            return;
        }

        _isCapturing = true;
        CopyButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        try
        {
            Win32Helper.RECT captureRect = _selectedWindow == IntPtr.Zero ? _monitorRect : _selectedRect;
            string imagePath = await Task.Run(() => CropFrozenSnapshot(captureRect));
            if (copyToClipboard)
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                var dataPackage = new DataPackage();
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                TryDelete(imagePath);
                App.Log($"[Screenshot] Copied mode={(_selectedWindow == IntPtr.Zero ? "monitor" : "window")}");
                Close();
                return;
            }

            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.SuggestedFileName = $"DeskBox-{DateTime.Now:yyyyMMdd-HHmmss}";
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            StorageFile destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                TryDelete(imagePath);
                return;
            }

            await Task.Run(() => File.Copy(imagePath, destination.Path, overwrite: true));
            TryDelete(imagePath);
            App.Log($"[Screenshot] Saved mode={(_selectedWindow == IntPtr.Zero ? "monitor" : "window")} path={destination.Path}");
            Close();
        }
        catch (Exception ex)
        {
            App.Log($"[Screenshot] Export failed: {ex}");
            SelectionHintText.Text = "Không thể xuất ảnh; hãy thử lại";
            _isCapturing = false;
            CopyButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private static string CaptureMonitorSnapshot(Win32Helper.RECT rect) => CaptureBitmapToPng(rect, "frozen");

    private string CropFrozenSnapshot(Win32Helper.RECT rect)
    {
        int sourceWidth = Math.Max(1, _monitorRect.Right - _monitorRect.Left);
        int sourceHeight = Math.Max(1, _monitorRect.Bottom - _monitorRect.Top);
        int cropLeft = Math.Clamp(rect.Left - _monitorRect.Left, 0, sourceWidth - 1);
        int cropTop = Math.Clamp(rect.Top - _monitorRect.Top, 0, sourceHeight - 1);
        int cropRight = Math.Clamp(rect.Right - _monitorRect.Left, cropLeft + 1, sourceWidth);
        int cropBottom = Math.Clamp(rect.Bottom - _monitorRect.Top, cropTop + 1, sourceHeight);

        using var source = new Bitmap(_snapshotPath);
        using var crop = source.Clone(new Rectangle(cropLeft, cropTop, cropRight - cropLeft, cropBottom - cropTop), PixelFormat.Format32bppPArgb);
        return SaveBitmap(crop, "selection");
    }

    private static string CaptureBitmapToPng(Win32Helper.RECT rect, string prefix)
    {
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new DrawingSize(width, height), CopyPixelOperation.SourceCopy);
        }

        return SaveBitmap(bitmap, prefix);
    }

    private static string SaveBitmap(Bitmap bitmap, string prefix)
    {
        string directory = Path.Combine(DeskBoxDataPathService.Current.RootPath, "cache", "screenshots");
        Directory.CreateDirectory(directory);
        CleanupOldScreenshots(directory);
        string path = Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static void CleanupOldScreenshots(string directory)
    {
        DateTime cutoff = DateTime.Now - TimeSpan.FromDays(2);
        foreach (string path in Directory.EnumerateFiles(directory, "*.png"))
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
                // Cache tạm, không để lỗi dọn dẹp làm hỏng luồng chụp.
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
                !TryGetCaptureBounds(window, out Win32Helper.RECT candidate) ||
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

    private static bool TryGetCaptureBounds(IntPtr window, out Win32Helper.RECT bounds) =>
        Win32Helper.TryGetExtendedFrameBounds(window, out bounds) || Win32Helper.GetWindowRect(window, out bounds);

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

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // File tạm sẽ được dọn ở lần chụp kế tiếp nếu hệ điều hành còn giữ handle.
        }
    }
}
