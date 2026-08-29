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
/// người dùng có thể thấy rõ, bấm chọn cả cửa sổ hoặc kéo vùng tự do để lưu/sao chép.
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
    private Windows.Foundation.Point? _dragStart;
    private bool _isDraggingRegion;
    private bool _isManualRegion;
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
        if (_selectionLocked)
        {
            return;
        }

        Windows.Foundation.Point point = e.GetCurrentPoint(FrozenScreenImage).Position;
        if (_dragStart is { } start)
        {
            if (!_isDraggingRegion &&
                (Math.Abs(point.X - start.X) >= 4 || Math.Abs(point.Y - start.Y) >= 4))
            {
                _isDraggingRegion = true;
                _isManualRegion = true;
                _selectedWindow = IntPtr.Zero;
            }

            if (_isDraggingRegion)
            {
                UpdateManualRegion(start, point);
                return;
            }
        }

        UpdateSelectionFromCursor();
    }

    private void FrozenScreenImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionLocked || !e.GetCurrentPoint(FrozenScreenImage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragStart = e.GetCurrentPoint(FrozenScreenImage).Position;
        _isDraggingRegion = false;
        _isManualRegion = false;
        FrozenScreenImage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void FrozenScreenImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionLocked || _dragStart is null)
        {
            return;
        }

        FrozenScreenImage.ReleasePointerCapture(e.Pointer);
        if (_isDraggingRegion)
        {
            LockSelection($"Đã chọn vùng: {_selectedRect.Right - _selectedRect.Left} × {_selectedRect.Bottom - _selectedRect.Top}");
        }
        else
        {
            UpdateSelectionFromCursor();
            LockSelection(
                _selectedWindow == IntPtr.Zero ? "Toàn màn hình đã được chọn" : $"Đã chọn: {GetWindowTitle(_selectedWindow)}");
        }

        _dragStart = null;
        e.Handled = true;
    }

    private void LockSelection(string hint)
    {
        _selectionLocked = true;
        CopyButton.Visibility = Visibility.Visible;
        SaveButton.Visibility = Visibility.Visible;
        ResetButton.Visibility = Visibility.Visible;
        SelectionHintText.Text = hint;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _selectionLocked = false;
        _dragStart = null;
        _isDraggingRegion = false;
        _isManualRegion = false;
        CopyButton.Visibility = Visibility.Collapsed;
        SaveButton.Visibility = Visibility.Collapsed;
        ResetButton.Visibility = Visibility.Collapsed;
        UpdateSelectionFromCursor();
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e) => await ExportSelectionAsync(copyToClipboard: true);

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await ExportSelectionAsync(copyToClipboard: false);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            return;
        }

        // Chỉ chiếm Ctrl+C trong overlay sau khi người dùng đã khóa vùng chụp.
        // Luồng xuất ảnh dùng chung với nút Sao chép để Clipboard Windows, log,
        // dọn file tạm và xử lý lỗi hoàn toàn nhất quán.
        if (e.Key == Windows.System.VirtualKey.C &&
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            _selectionLocked &&
            !_isCapturing)
        {
            e.Handled = true;
            await ExportSelectionAsync(copyToClipboard: true);
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
        ShowSelectionBorder(_selectedRect);
    }

    private void UpdateManualRegion(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        double left = Math.Min(start.X, end.X);
        double top = Math.Min(start.Y, end.Y);
        double right = Math.Max(start.X, end.X);
        double bottom = Math.Max(start.Y, end.Y);
        _selectedRect = new Win32Helper.RECT
        {
            Left = ToCaptureX(left),
            Top = ToCaptureY(top),
            Right = ToCaptureX(right),
            Bottom = ToCaptureY(bottom)
        };
        if (_selectedRect.Right <= _selectedRect.Left)
        {
            _selectedRect.Right = Math.Min(_monitorRect.Right, _selectedRect.Left + 1);
        }
        if (_selectedRect.Bottom <= _selectedRect.Top)
        {
            _selectedRect.Bottom = Math.Min(_monitorRect.Bottom, _selectedRect.Top + 1);
        }

        ShowSelectionBorder(_selectedRect);
        SelectionHintText.Text = $"Vùng tự do: {_selectedRect.Right - _selectedRect.Left} × {_selectedRect.Bottom - _selectedRect.Top}";
    }

    private void ShowSelectionBorder(Win32Helper.RECT rect)
    {
        double left = ToVisualX(Math.Max(rect.Left, _monitorRect.Left));
        double top = ToVisualY(Math.Max(rect.Top, _monitorRect.Top));
        double right = ToVisualX(Math.Min(rect.Right, _monitorRect.Right));
        double bottom = ToVisualY(Math.Min(rect.Bottom, _monitorRect.Bottom));
        SelectionBorder.Width = Math.Max(1, right - left);
        SelectionBorder.Height = Math.Max(1, bottom - top);
        SelectionBorder.Margin = new Thickness(left, top, 0, 0);
        SelectionBorder.HorizontalAlignment = HorizontalAlignment.Left;
        SelectionBorder.VerticalAlignment = VerticalAlignment.Top;
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private int ToCaptureX(double x) => _monitorRect.Left + (int)Math.Round(
        Math.Clamp(x, 0, Math.Max(1, FrozenScreenImage.ActualWidth)) *
        Math.Max(1, _monitorRect.Right - _monitorRect.Left) / Math.Max(1, FrozenScreenImage.ActualWidth));

    private int ToCaptureY(double y) => _monitorRect.Top + (int)Math.Round(
        Math.Clamp(y, 0, Math.Max(1, FrozenScreenImage.ActualHeight)) *
        Math.Max(1, _monitorRect.Bottom - _monitorRect.Top) / Math.Max(1, FrozenScreenImage.ActualHeight));

    private double ToVisualX(int x) => (x - _monitorRect.Left) * FrozenScreenImage.ActualWidth /
        Math.Max(1, _monitorRect.Right - _monitorRect.Left);

    private double ToVisualY(int y) => (y - _monitorRect.Top) * FrozenScreenImage.ActualHeight /
        Math.Max(1, _monitorRect.Bottom - _monitorRect.Top);

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
            Win32Helper.RECT captureRect = _isManualRegion || _selectedWindow != IntPtr.Zero
                ? _selectedRect
                : _monitorRect;
            // Chỉ cửa sổ đã chọn dùng Windows Graphics Capture để lấy nội dung
            // riêng, không còn là một mảnh cắt từ Desktop bị các cửa sổ khác che.
            // Vùng kéo tự do và toàn màn hình vẫn xuất đúng ảnh nền đóng băng cũ.
            string imagePath = _selectedWindow != IntPtr.Zero && !_isManualRegion
                ? await SelectedWindowScreenshotService.CaptureAsync(_selectedWindow)
                : await Task.Run(() => CropFrozenSnapshot(captureRect));
            if (copyToClipboard)
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                var dataPackage = new DataPackage();
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                TryDelete(imagePath);
                App.Log($"[Screenshot] Copied mode={GetCaptureMode()}");
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
            App.Log($"[Screenshot] Saved mode={GetCaptureMode()} path={destination.Path}");
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

    private string GetCaptureMode() => _isManualRegion ? "region" : _selectedWindow == IntPtr.Zero ? "monitor" : "window";

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
