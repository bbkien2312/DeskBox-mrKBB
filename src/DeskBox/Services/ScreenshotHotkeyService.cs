using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Registers the optional lightweight screenshot hotkey without installing a
/// keyboard hook or a resident clipboard watcher.
/// </summary>
public sealed class ScreenshotHotkeyService : IDisposable
{
    private const int ScreenshotHotkeyId = 0x4450;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4450);

    private readonly SettingsService _settingsService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private bool _isInvoking;

    public ScreenshotHotkeyService(SettingsService settingsService, Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _invokeAsync = invokeAsync;
        _subclassProc = WindowSubclassProc;
    }

    public bool IsRegistered => _isRegistered;

    public GlobalHotkeyGesture CurrentGesture => GlobalHotkeyService.NormalizeGesture(
        _settingsService.Settings.ScreenshotHotkeyModifiers,
        _settingsService.Settings.ScreenshotHotkeyKey);

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        Detach();
        _windowHandle = windowHandle;
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _windowHandle, _subclassProc, SubclassId, UIntPtr.Zero);
        RefreshRegistration();
    }

    public void RefreshRegistration()
    {
        Unregister();
        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.ScreenshotHotkeyEnabled)
        {
            return;
        }

        GlobalHotkeyGesture gesture = CurrentGesture;
        if (!GlobalHotkeyService.IsValidGesture(gesture) ||
            !Win32Helper.RegisterHotKey(
                _windowHandle,
                ScreenshotHotkeyId,
                ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
                (uint)gesture.VirtualKey))
        {
            App.Log($"[ScreenshotHotkey] Failed to register gesture={FormatGesture(gesture)}");
            return;
        }

        _isRegistered = true;
        App.Log($"[ScreenshotHotkey] Registered gesture={FormatGesture(gesture)}");
    }

    public void Detach()
    {
        Unregister();
        if (_isSubclassInstalled && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        }

        _isSubclassInstalled = false;
        _windowHandle = IntPtr.Zero;
    }

    public void Dispose() => Detach();

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == GlobalHotkeyService.WmHotkey &&
            wParam.ToUInt32() == ScreenshotHotkeyId)
        {
            Win32Helper.ReleaseAllModifiers();
            App.UiDispatcherQueue.TryEnqueue(() => _ = InvokeAsync());
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task InvokeAsync()
    {
        if (_isInvoking)
        {
            return;
        }

        _isInvoking = true;
        try
        {
            await _invokeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ScreenshotHotkey] Invocation failed: {ex}");
        }
        finally
        {
            _isInvoking = false;
        }
    }

    private void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, ScreenshotHotkeyId);
        }

        _isRegistered = false;
    }

    private static uint ToWin32Modifiers(HotkeyModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt)) value |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifierKeys.Control)) value |= ModControl;
        if (modifiers.HasFlag(HotkeyModifierKeys.Shift)) value |= ModShift;
        return value;
    }

    private static string FormatGesture(GlobalHotkeyGesture gesture) =>
        $"{gesture.Modifiers}+VK:{gesture.VirtualKey:X2}";
}
