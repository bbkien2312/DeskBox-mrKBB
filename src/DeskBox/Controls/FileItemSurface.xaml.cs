using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public enum FileItemSurfaceMode
{
    Icon,
    List
}

public sealed class FileItemSurfaceVisualStateChangedEventArgs(
    FileItemSurfaceVisualState state) : EventArgs
{
    public FileItemSurfaceVisualState State { get; } = state;
}

public sealed partial class FileItemSurface : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(FileItemSurfaceMode),
            typeof(FileItemSurface),
            new PropertyMetadata(FileItemSurfaceMode.Icon, OnPresentationPropertyChanged));

    public static readonly DependencyProperty LayoutContextProperty =
        DependencyProperty.Register(
            nameof(LayoutContext),
            typeof(WidgetViewModel),
            typeof(FileItemSurface),
            new PropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty UseStackChildIndentProperty =
        DependencyProperty.Register(
            nameof(UseStackChildIndent),
            typeof(bool),
            typeof(FileItemSurface),
            new PropertyMetadata(false, OnPresentationPropertyChanged));

    public static readonly DependencyProperty ListItemTextMaxWidthProperty =
        DependencyProperty.Register(
            nameof(ListItemTextMaxWidth),
            typeof(double),
            typeof(FileItemSurface),
            new PropertyMetadata(double.PositiveInfinity));

    private FileItemSurfaceVisualState _visualState = FileItemSurfaceVisualState.Normal;
    private WidgetViewModel? _subscribedLayoutContext;
    private WidgetItem? _realizedItem;
    private bool _isSurfaceLoaded;

    public FileItemSurface()
    {
        InitializeComponent();
        DataContextChanged += FileItemSurface_DataContextChanged;
    }

    public event EventHandler<FileItemSurfaceVisualStateChangedEventArgs>? VisualStateChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileItemSurfaceMode Mode
    {
        get => (FileItemSurfaceMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public WidgetViewModel? LayoutContext
    {
        get => (WidgetViewModel?)GetValue(LayoutContextProperty);
        set => SetValue(LayoutContextProperty, value);
    }

    public bool UseStackChildIndent
    {
        get => (bool)GetValue(UseStackChildIndentProperty);
        set => SetValue(UseStackChildIndentProperty, value);
    }

    public double ListItemTextMaxWidth
    {
        get => (double)GetValue(ListItemTextMaxWidthProperty);
        set => SetValue(ListItemTextMaxWidthProperty, value);
    }

    public Visibility IconLayoutVisibility =>
        Mode == FileItemSurfaceMode.Icon
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ListLayoutVisibility =>
        Mode == FileItemSurfaceMode.List
            ? Visibility.Visible
            : Visibility.Collapsed;

    public HorizontalAlignment SurfaceHorizontalAlignment =>
        Mode == FileItemSurfaceMode.List
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Center;

    public double SurfaceMaxWidth =>
        Mode == FileItemSurfaceMode.Icon && LayoutContext is not null
            ? Math.Max(LayoutContext.IconImageSize + 18, LayoutContext.IconLabelMaxWidth + 12)
            : double.PositiveInfinity;

    public Thickness SurfaceMargin
    {
        get
        {
            if (Mode != FileItemSurfaceMode.List || LayoutContext is null)
            {
                return new Thickness(0);
            }

            Thickness margin = LayoutContext.ListItemMargin;
            return UseStackChildIndent &&
                DataContext is WidgetItem { IsStackChild: true }
                ? new Thickness(
                    margin.Left + 18,
                    margin.Top,
                    margin.Right,
                    margin.Bottom)
                : margin;
        }
    }

    public Thickness SurfacePadding =>
        LayoutContext is null
            ? new Thickness(0)
            : Mode == FileItemSurfaceMode.List
                ? LayoutContext.ListItemPadding
                : LayoutContext.IconTilePadding;

    public FileItemSurfaceVisualState VisualState => _visualState;

    public Border InteractiveBorder => SurfaceBorder;

    public TextBlock ItemNameText =>
        Mode == FileItemSurfaceMode.List
            ? ListItemNameText
            : IconItemNameText;

    public static Border? TryGetInteractiveBorder(object? source)
    {
        return source switch
        {
            FileItemSurface surface => surface.InteractiveBorder,
            Border border => border,
            _ => null
        };
    }

    public static FileItemSurface? FindOwner(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FileItemSurface surface)
            {
                return surface;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is FileItemSurface surface)
        {
            if (args.Property == LayoutContextProperty)
            {
                surface.RefreshLayoutContextSubscription();
            }

            surface.NotifyPresentationChanged();
            surface.RefreshRealizedItem();
        }
    }

    private void RefreshLayoutContextSubscription()
    {
        if (!_isSurfaceLoaded || ReferenceEquals(_subscribedLayoutContext, LayoutContext))
        {
            return;
        }

        if (_subscribedLayoutContext is not null)
        {
            _subscribedLayoutContext.PropertyChanged -= LayoutContext_PropertyChanged;
        }

        _subscribedLayoutContext = LayoutContext;
        if (_subscribedLayoutContext is not null)
        {
            _subscribedLayoutContext.PropertyChanged += LayoutContext_PropertyChanged;
        }
    }

    private void DetachLayoutContextSubscription()
    {
        if (_subscribedLayoutContext is not null)
        {
            _subscribedLayoutContext.PropertyChanged -= LayoutContext_PropertyChanged;
            _subscribedLayoutContext = null;
        }
    }

    private void LayoutContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyPresentationChanged();
    }

    private void FileItemSurface_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        // ListView virtualization can reuse a loaded surface for a different
        // item without raising Loaded again. Reset pointer state and ask the
        // host to reapply all item-dependent styling, especially cut opacity.
        _visualState = FileItemSurfaceVisualState.Normal;
        VisualStateChanged?.Invoke(
            this,
            new FileItemSurfaceVisualStateChangedEventArgs(_visualState));
        OnPropertyChanged(nameof(VisualState));
        NotifyPresentationChanged();
        RefreshRealizedItem();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(IconLayoutVisibility));
        OnPropertyChanged(nameof(ListLayoutVisibility));
        OnPropertyChanged(nameof(SurfaceHorizontalAlignment));
        OnPropertyChanged(nameof(SurfaceMaxWidth));
        OnPropertyChanged(nameof(SurfaceMargin));
        OnPropertyChanged(nameof(SurfacePadding));
    }

    private void SurfaceBorder_Loaded(object sender, RoutedEventArgs e)
    {
        _isSurfaceLoaded = true;
        RefreshLayoutContextSubscription();
        SetVisualState(FileItemSurfaceVisualState.Normal);
        NotifyPresentationChanged();
        RefreshRealizedItem();
    }

    private void SurfaceBorder_Unloaded(object sender, RoutedEventArgs e)
    {
        _isSurfaceLoaded = false;
        if (_realizedItem is not null)
        {
            LayoutContext?.MarkItemSurfaceNotVisible(_realizedItem);
            _realizedItem = null;
        }
        DetachLayoutContextSubscription();
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Hover);
    }

    private void SurfaceBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Pressed);
    }

    private void SurfaceBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point point = e.GetCurrentPoint(SurfaceBorder).Position;
        bool inside =
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X <= SurfaceBorder.ActualWidth &&
            point.Y <= SurfaceBorder.ActualHeight;
        SetVisualState(
            inside
                ? FileItemSurfaceVisualState.Hover
                : FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SetVisualState(FileItemSurfaceVisualState state)
    {
        if (_visualState == state)
        {
            return;
        }

        _visualState = state;
        VisualStateChanged?.Invoke(
            this,
            new FileItemSurfaceVisualStateChangedEventArgs(state));
        OnPropertyChanged(nameof(VisualState));
    }

    private void RefreshRealizedItem()
    {
        WidgetItem? next = _isSurfaceLoaded ? DataContext as WidgetItem : null;
        if (ReferenceEquals(_realizedItem, next))
        {
            return;
        }

        if (_realizedItem is not null)
        {
            LayoutContext?.MarkItemSurfaceNotVisible(_realizedItem);
        }

        _realizedItem = next;
        if (_realizedItem is not null)
        {
            LayoutContext?.MarkItemSurfaceVisible(_realizedItem);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
