using System.Collections.Specialized;
using System.Security.Cryptography;
using DeskBox.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Shared file-widget content used by both standalone and grouped unified hosts.
/// </summary>
public sealed partial class FileSurfaceContent :
    UserControl,
    IWidgetContent,
    ICancellableWidgetContent,
    IWidgetGroupContentCacheable,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetHostContextMenuSource,
    IWidgetTransientStateContent,
    IDisposable
{
    private const int StackDuplicateInputWindowMs = 120;
    private static readonly TimeSpan ReconciliationFreshnessWindow =
        TimeSpan.FromSeconds(1);
    private readonly LocalizationService _localizationService;
    private readonly FileService _fileService;
    private readonly SettingsService _settingsService;
    private static readonly QuickLookPreviewService s_quickLookService =
        new();
    private string[] _cutClipboardPaths = [];
    private WidgetItem? _itemRenameTarget;
    private TextBlock? _itemRenameNameText;
    private bool _isCommittingItemRename;
    private bool _isCancellingItemRename;
    private bool _isSurfaceReorderDragActive;
    private string[] _surfaceReorderPaths = [];
    private string? _surfaceReorderStackKey;
    private int _surfaceReorderInsertionIndex = -1;
    private Windows.Foundation.Point _surfaceReorderLastPosition;
    private bool _surfaceReorderHasLastPosition;
    private WidgetItem[] _pendingPointerDragItems = [];
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private bool _activeDragUsesVirtualStorageItems;
    private bool _nativeShortcutDragHandled;
    private Task<HashSet<string>?>? _activeDragDesktopSnapshotTask;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Border? _folderDropTarget;
    private Border? _stackMemberDropTarget;
    private WidgetStackItem? _pressedStack;
    private bool _stackPointerDragStarted;
    private string? _lastStackInputKey;
    private long _lastStackInputTick;
    private WidgetItem? _lastClickedItem;
    private long _lastItemClickTick;
    private string _typeAheadBuffer = string.Empty;
    private long _typeAheadLastInputTick;
    private bool _isImportBusy;
    private IntPtr _hostWindowHandle;
    private DateTimeOffset? _importBusyStartedAtUtc;
    private bool _isDisposed;
    private bool _isReadyForReuse;
    private bool _hasBeenWindowVisible;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;
    private DateTime _lastDiskReconciliationUtc = DateTime.MinValue;
    private int _diskReconciliationQueued;
    private TransitionCollection? _suspendedGridItemContainerTransitions;
    private TransitionCollection? _suspendedListItemContainerTransitions;
    private bool _itemContainerTransitionsSuspendedForHostSwitch;

    public FileSurfaceContent(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        _fileService = fileService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        ViewModel = new WidgetViewModel(
            config,
            fileService,
            organizerService,
            settingsService,
            localizationService,
            dispatcherQueue);

        InitializeComponent();
        ItemsGrid.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(ItemsView_PreviewKeyDown),
            handledEventsToo: true);
        ItemsList.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(ItemsView_PreviewKeyDown),
            handledEventsToo: true);
        RegisterScrollBarActivityTracking(ItemsGrid);
        RegisterScrollBarActivityTracking(ItemsList);
        Root.DataContext = ViewModel;
        Root.IsTabStop = true;
        EmptyAddButtonText.Text = T("Widget.AddFile");
        OpenSelectionButton.Label = T("Common.Open");
        CopySelectionButton.Label = T("Common.Copy");
        CutSelectionButton.Label = T("Common.Cut");
        DeleteSelectionButton.Label = T("Widget.MoveToRecycleBin");
        RenameSelectionButton.Label = T("Common.Rename");
        ToolTipService.SetToolTip(OpenSelectionButton, OpenSelectionButton.Label);
        ToolTipService.SetToolTip(CopySelectionButton, CopySelectionButton.Label);
        ToolTipService.SetToolTip(CutSelectionButton, CutSelectionButton.Label);
        ToolTipService.SetToolTip(DeleteSelectionButton, DeleteSelectionButton.Label);
        ToolTipService.SetToolTip(RenameSelectionButton, RenameSelectionButton.Label);
        InitializeFolderNavigationPresentation();
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ActualThemeChanged += FileSurfaceContent_ActualThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateEmptyState();
    }

    public WidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;

    internal event EventHandler? ExternalFileDragEnded;

    internal event Action<bool>? ImportBusyChanged;

    internal bool IsImportBusy => _isImportBusy;

    internal long? ImportBusyElapsedMilliseconds =>
        _isImportBusy && _importBusyStartedAtUtc is { } startedAt
            ? Math.Max(
                0,
                (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds)
            : null;

    internal void SetHostWindowHandle(IntPtr windowHandle)
    {
        _hostWindowHandle = windowHandle;
    }

    internal void SuspendItemContainerTransitionsForHostSwitch()
    {
        if (_itemContainerTransitionsSuspendedForHostSwitch)
        {
            return;
        }

        _suspendedGridItemContainerTransitions =
            ItemsGrid.ItemContainerTransitions;
        _suspendedListItemContainerTransitions =
            ItemsList.ItemContainerTransitions;
        ItemsGrid.ItemContainerTransitions = null;
        ItemsList.ItemContainerTransitions = null;
        _itemContainerTransitionsSuspendedForHostSwitch = true;
    }

    internal void ResumeItemContainerTransitionsAfterHostSwitch()
    {
        if (!_itemContainerTransitionsSuspendedForHostSwitch)
        {
            return;
        }

        ItemsGrid.ItemContainerTransitions =
            _suspendedGridItemContainerTransitions;
        ItemsList.ItemContainerTransitions =
            _suspendedListItemContainerTransitions;
        _suspendedGridItemContainerTransitions = null;
        _suspendedListItemContainerTransitions = null;
        _itemContainerTransitionsSuspendedForHostSwitch = false;
    }

    public WidgetConfig Config => ViewModel.Config;

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => WidgetKind.File;

    public FrameworkElement View => this;

    public bool IsReadyForReuse => _isReadyForReuse && !_isDisposed;

    public Task InitializeAsync()
    {
        return InitializeAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ViewModel.InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _isReadyForReuse = true;
        _lastDiskReconciliationUtc = DateTime.UtcNow;
        UpdateEmptyState();
    }

    public async Task RefreshAsync()
    {
        await ViewModel.RefreshFolderContentsAsync();
        _lastDiskReconciliationUtc = DateTime.UtcNow;
        UpdateEmptyState();
    }

    internal void RevealSavedItem(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RevealSavedItem(itemPath));
            return;
        }

        WidgetItem? item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                itemPath,
                StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(item);
        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
        ShowFeedback(new WidgetFeedbackRequest(
            T("Widget.SavedHere"),
            WidgetFeedbackSeverity.Success,
            "file-saved-here"));
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearancePreview();
        ApplyAccentVisuals();
        ApplySelectionRectangleAppearance();
        UpdateItemSurfaceVisuals();
        UpdateEmptyState();
    }

    private void FileSurfaceContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        ApplyAccentVisuals();
        ApplySelectionRectangleAppearance();
        UpdateItemSurfaceVisuals();
    }

    private void ApplyAccentVisuals()
    {
        var accent = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        ReorderInsertionAccentStop.Color = accent;
        ReorderInsertionLine.Background = new SolidColorBrush(accent);
        ImportProgressBar.Foreground = new SolidColorBrush(accent);
        if (_activeImportVisualState is not ImportCompletionState.Failed)
        {
            ImportStateIcon.Foreground = new SolidColorBrush(accent);
        }
    }

    public void OnActivated()
    {
        if (IsLoaded)
        {
            Root.Focus(FocusState.Programmatic);
        }

        if (_isWindowRevealCompleted)
        {
            QueueDiskReconciliationIfStale("activated");
        }
    }

    public void PrepareForReuse()
    {
        // A group member can stay detached while its source items or settings
        // change. Clear recycled selector state first, then rebuild the stack
        // projection before the cached surface is attached again.
        ResetSelectionForStackProjectionChange();
        ResetStackInteractionVisuals();
        PersistSurfaceReorder();
        ViewModel.StabilizeStackDisplay();
    }

    public void OnDeactivated()
    {
        // File hydration and folder watchers follow the actual window visibility,
        // rather than foreground activation. Desktop-layer groups intentionally
        // use SW_SHOWNOACTIVATE, so treating their initial inactive state as a
        // deactivation would cancel the first icon hydration pass.
    }

    public object? CaptureTransientState()
    {
        return new FileWidgetTransientState(
            GetSelectedItems()
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _cutClipboardPaths.ToArray());
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not FileWidgetTransientState fileState)
        {
            return;
        }

        RestoreSelection(ItemsGrid, fileState.SelectedPaths);
        RestoreSelection(ItemsList, fileState.SelectedPaths);
        _cutClipboardPaths = fileState.CutPaths
            .Where(path => ViewModel.Items.Any(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        ApplyCutState();
        RefreshItemSelectionVisuals();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (visible)
        {
            _hasBeenWindowVisible = true;
            UpdateEmptyState();
            return;
        }

        _isWindowRevealCompleted = false;

        // Content is attached before its host is shown, and the host reports its
        // initial hidden state during that attach. Do not cancel the initial
        // hydration in that case; only a real visible -> hidden transition
        // suspends the file surface.
        if (_hasBeenWindowVisible)
        {
            ViewModel.SuspendBackgroundActivity();
        }
    }

    private void QueueDiskReconciliationIfStale(string reason)
    {
        if (_isDisposed ||
            DateTime.UtcNow - _lastDiskReconciliationUtc <
                ReconciliationFreshnessWindow ||
            Interlocked.Exchange(ref _diskReconciliationQueued, 1) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (_isDisposed || !_isWindowVisible || !_isWindowRevealCompleted)
                    {
                        return;
                    }

                    await RefreshAsync();
                    App.LogVerbose(
                        $"[FolderRefresh] Reconciled file surface " +
                        $"widget={WidgetId} reason={reason}");
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[FolderRefresh] File surface reconciliation failed " +
                        $"widget={WidgetId} reason={reason}: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _diskReconciliationQueued, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _diskReconciliationQueued, 0);
        }
    }

    public Task AddFromTitleButtonAsync() => RunAsync(PickAndImportFilesAsync);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HideInactiveScrollBars();
        ApplySelectionRectangleAppearance();
        UpdateEmptyState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopScrollBarHideTimer();
        ResetStackInteractionVisuals();
        PersistSurfaceReorder();
        App.Current.WidgetManager?.NotifyQuickLookSurfaceUnavailable(this);
    }

    private void Items_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        ReconcileCutStateAfterItemsChanged(e);
        UpdateEmptyState();
    }

    private void ReconcileCutStateAfterItemsChanged(
        NotifyCollectionChangedEventArgs e)
    {
        WidgetItem[] removedItems = e.OldItems?
            .OfType<WidgetItem>()
            .ToArray() ?? [];
        if (removedItems.Length > 0)
        {
            string[] replacementPaths = e.NewItems?
                .OfType<WidgetItem>()
                .Select(item => item.Path)
                .ToArray() ?? [];
            _cutClipboardPaths = FileCutStatePolicy.RemoveDepartedPaths(
                _cutClipboardPaths,
                removedItems.Select(item => item.Path),
                replacementPaths);

            foreach (WidgetItem item in removedItems)
            {
                item.IsCut = false;
            }
        }

        // Recompute every remaining item so newly inserted or rebound surfaces
        // never inherit a previous container's cut appearance.
        ApplyCutState();
    }

    private void UpdateEmptyState()
    {
        if (!IsLoaded)
        {
            return;
        }

        EmptyState.Visibility =
            !ViewModel.IsLoading && !ViewModel.VisibleItems.Any()
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ToggleViewButton_Click(object sender, RoutedEventArgs e)
    {
        string[] selectedPaths = GetSelectedItems()
            .Select(item => item.Path)
            .ToArray();
        ViewModel.ToggleViewMode();
        DispatcherQueue.TryEnqueue(() =>
        {
            ListViewBase activeView =
                ViewModel.IconViewVisibility == Visibility.Visible
                    ? ItemsGrid
                    : ItemsList;
            RestoreSelection(activeView, selectedPaths);
            UpdateSelectionCommandBar();
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(RefreshAsync);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(PickAndImportFilesAsync);
    }

    private async void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WidgetStackItem stack)
        {
            ToggleStackFromInput(stack);
            return;
        }

        if (e.ClickedItem is not WidgetItem item)
        {
            return;
        }

        bool controlPressed =
            Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool shiftPressed =
            Win32Helper.IsKeyPressed(VirtualKey.Shift);

        if (_settingsService.Settings.DoubleClickToOpen &&
            !controlPressed &&
            !shiftPressed)
        {
            long now = Environment.TickCount64;
            long doubleClickMilliseconds =
                (long)Win32Helper.WindowsDoubleClickInterval.TotalMilliseconds;
            if (ReferenceEquals(_lastClickedItem, item) &&
                now - _lastItemClickTick > doubleClickMilliseconds)
            {
                _lastClickedItem = null;
                _lastItemClickTick = 0;
                await RenameItemAsync(item);
                return;
            }

            _lastClickedItem = item;
            _lastItemClickTick = now;
            return;
        }

        if (!_settingsService.Settings.DoubleClickToOpen &&
            !controlPressed &&
            !shiftPressed)
        {
            await ActivateItemAsync(item);
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        ViewModel.ResumeBackgroundActivity();
        QueueDiskReconciliationIfStale("reveal-completed");
    }

    private void ToggleStackFromInput(WidgetStackItem stack)
    {
        long now = Environment.TickCount64;
        if (string.Equals(
                _lastStackInputKey,
                stack.StackKey,
                StringComparison.Ordinal) &&
            now - _lastStackInputTick < StackDuplicateInputWindowMs)
        {
            return;
        }

        _lastStackInputKey = stack.StackKey;
        _lastStackInputTick = now;
        RequestStackState(
            stack,
            !GetDesiredStackState(stack));
    }


    private async void Items_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (!_settingsService.Settings.DoubleClickToOpen ||
            FindItemFromSource(e.OriginalSource) is not { } item)
        {
            return;
        }

        if (item is WidgetStackItem)
        {
            _lastClickedItem = null;
            e.Handled = true;
            return;
        }

        _lastClickedItem = null;
        await ActivateItemAsync(item);
        e.Handled = true;
    }

    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        WidgetItem? item = FindItemFromSource(e.OriginalSource);
        if (item is null)
        {
            ClearSelection();
            FrameworkElement contentTarget =
                sender as FrameworkElement ?? Root;
            CreateContentAreaFlyout().ShowAt(
                contentTarget,
                e.GetPosition(contentTarget));
            e.Handled = true;
            return;
        }

        ListViewBase activeView = GetActiveItemsView();
        if (!activeView.SelectedItems.Contains(item))
        {
            activeView.SelectedItems.Clear();
            activeView.SelectedItems.Add(item);
        }

        MenuFlyout flyout = item is WidgetStackItem stack
            ? CreateStackFlyout(stack)
            : GetSelectedItems().Count > 1
                ? CreateMultiSelectionFlyout()
                : CreateItemFlyout(item);
        if (item is WidgetStackItem)
        {
            flyout.Closed += (_, _) =>
            {
                ItemsGrid.SelectedItems.Remove(item);
                ItemsList.SelectedItems.Remove(item);
            };
        }
        FrameworkElement target =
            FindItemElement(e.OriginalSource) ??
            sender as FrameworkElement ??
            Root;
        flyout.ShowAt(target, e.GetPosition(target));
        e.Handled = true;
    }

    private void Items_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        if (_isImportBusy)
        {
            e.Cancel = true;
            _pendingPointerDragItems = [];
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            _activeDragUsesVirtualStorageItems = false;
            _activeDragDesktopSnapshotTask = null;
            return;
        }

        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;
        _activeDragUsesVirtualStorageItems = false;
        _nativeShortcutDragHandled = false;
        _activeDragDesktopSnapshotTask = null;
        ClearFolderDropTarget();
        HideSurfaceReorderInsertionIndicator();
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        _surfaceReorderInsertionIndex = -1;
        _surfaceReorderLastPosition = default;
        _surfaceReorderHasLastPosition = false;
        WidgetStackItem? stack =
            e.Items.OfType<WidgetStackItem>().FirstOrDefault();
        if (stack is not null)
        {
            _pendingPointerDragItems = [];
            _stackPointerDragStarted = true;
            e.Data.RequestedOperation = DataPackageOperation.Link;
            e.Data.Properties[
                DeskBoxDragData.SourceWidgetIdProperty] = WidgetId;
            e.Data.Properties[
                DeskBoxDragData.InternalFileDragTokenProperty] =
                DeskBoxDragData.InternalFileDragToken;
            e.Data.Properties[
                DeskBoxDragData.StackReorderKeyProperty] =
                stack.StackKey;
            e.Data.Properties.Title = stack.Name;
            e.Data.SetText(stack.Name);
            return;
        }

        WidgetItem[] eventItems = e.Items
            .OfType<WidgetItem>()
            .ToArray();
        IReadOnlyList<WidgetItem> pointerSelection =
            _pendingPointerDragItems.Length > 1
                ? _pendingPointerDragItems
                : GetSelectedItems();
        _pendingPointerDragItems = [];
        WidgetItem[] selectedItems = FileItemDragPackage.ResolveDraggedItems(
                eventItems,
                pointerSelection)
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Path) &&
                (File.Exists(item.Path) || Directory.Exists(item.Path)))
            .ToArray();
        if (!FileItemDragPackage.TryPrepare(
                e.Data,
                selectedItems,
                WidgetId,
                paths => _fileService.GetStorageItems(paths),
                paths => paths.Count == 1
                    ? Path.GetFileName(paths[0])
                    : paths.Count.ToString(),
                out FileItemDragPackageResult result))
        {
            e.Cancel = true;
            return;
        }

        _activeDragSourcePaths = result.SourcePaths.ToArray();
        _activeDragHasStorageItems = result.HasStorageItems;
        _activeDragUsesVirtualStorageItems =
            result.UsesVirtualStorageItems;
        if (result.UsesVirtualStorageItems &&
            ViewModel.FollowsDefaultStoragePath)
        {
            e.Data.RequestedOperation = DataPackageOperation.Move;
            _activeDragDesktopSnapshotTask =
                CaptureDesktopEntrySnapshotAsync();
        }
    }

    private void Items_DragStarting(
        UIElement sender,
        DragStartingEventArgs e)
    {
        string[] sourcePaths = _activeDragSourcePaths.Length > 0
            ? _activeDragSourcePaths
            : GetSelectedItems()
                .Where(item => item is not WidgetStackItem)
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        if (!ViewModel.FollowsDefaultStoragePath ||
            !VirtualShortcutDragProvider.CanProvide(sourcePaths))
        {
            return;
        }

        // WinUI's DataPackage path for .lnk files is virtual because the
        // StorageFile broker rejects some shell shortcuts. That virtual
        // payload is copy-only from Explorer's point of view and the old
        // completion fallback could therefore redirect the drag to Desktop.
        // Give Explorer the real paths through OLE instead: Explorer then
        // decides the actual destination and performs its normal move/copy.
        if (NativeFileDragSource.TryRun(
                _hostWindowHandle,
                sourcePaths,
                out uint nativeEffect))
        {
            e.Cancel = true;
            _nativeShortcutDragHandled = true;
            string[] nativePaths = sourcePaths.ToArray();
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(150);
                    if (nativeEffect == NativeDropEffectPolicy.Move)
                    {
                        await ObserveExternalDragOutAsync(
                            nativePaths,
                            _lifetimeCancellation.Token);
                    }
                    else
                    {
                        await RefreshAsync();
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[NativeDrag] completion refresh failed: {ex.Message}");
                }
            });
            return;
        }

        // DataPackage.RequestedOperation is a single preferred operation,
        // while AllowedOperations controls the permitted set. Managed storage
        // shortcuts are being restored to the desktop, so both are Move.
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.AllowedOperations = DataPackageOperation.Move;
    }

    private async void Items_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs e)
    {
        string[] movedPaths = _activeDragSourcePaths.Length > 0
            ? _activeDragSourcePaths
            : e.Items
                .OfType<WidgetItem>()
                .Where(item => item is not WidgetStackItem)
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (_nativeShortcutDragHandled)
        {
            _nativeShortcutDragHandled = false;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            _activeDragUsesVirtualStorageItems = false;
            _activeDragDesktopSnapshotTask = null;
            return;
        }
        bool hasStorageItems = _activeDragHasStorageItems;
        bool usesVirtualStorageItems =
            _activeDragUsesVirtualStorageItems;
        Task<HashSet<string>?>? desktopSnapshotTask =
            _activeDragDesktopSnapshotTask;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;
        _activeDragUsesVirtualStorageItems = false;
        _activeDragDesktopSnapshotTask = null;

        try
        {
            if (usesVirtualStorageItems &&
                movedPaths.Length > 0 &&
                ViewModel.FollowsDefaultStoragePath &&
                ShellDesktopDropTarget.IsPointerOverDesktop())
            {
                HashSet<string>? desktopSnapshot = desktopSnapshotTask is null
                    ? null
                    : await desktopSnapshotTask;
                await CompleteVirtualShortcutDesktopMoveAsync(
                    movedPaths,
                    desktopSnapshot);
                return;
            }

            if (e.DropResult == DataPackageOperation.None &&
                !hasStorageItems &&
                movedPaths.Length > 0 &&
                ViewModel.FollowsDefaultStoragePath &&
                ShellDesktopDropTarget.IsPointerOverDesktop())
            {
                // Windows.Storage rejects some real shell files, most notably
                // .lnk shortcuts. Their drag package still carries DeskBox's
                // path metadata and text, but Explorer cannot consume it as a
                // native file drop. Complete the intended managed-storage
                // move only after confirming that the release target is the
                // actual Shell desktop.
                await MoveRejectedManagedDragToDesktopAsync(movedPaths);
                return;
            }

            if ((e.DropResult == DataPackageOperation.Move ||
                 (e.DropResult == DataPackageOperation.None && hasStorageItems)) &&
                movedPaths.Length > 0)
            {
                // DropResult describes the target's requested operation, not an
                // item-by-item completion result. Reconcile against a successful
                // parent enumeration so a partial/cancelled Shell move cannot
                // remove every original row.
                _ = ObserveExternalDragOutAsync(
                    movedPaths,
                    _lifetimeCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Drag completion refresh failed " +
                $"id={WidgetId}: {ex}");
        }
        finally
        {
            _pressedStack = null;
            _stackPointerDragStarted = false;
            ClearFolderDropTarget();
            ClearStackMemberDropTarget();
            if (_isSurfaceReorderDragActive &&
                _surfaceReorderHasLastPosition)
            {
                // WinUI can complete an item drag without raising Drop. The
                // last DragOver position is still the release position, so
                // commit once here instead of losing the reorder.
                CommitSurfaceReorder(_surfaceReorderLastPosition);
            }
            else
            {
                PersistSurfaceReorder();
            }
        }
    }

    private static Task<HashSet<string>?> CaptureDesktopEntrySnapshotAsync()
    {
        return Task.Run<HashSet<string>?>(() =>
        {
            try
            {
                string desktopPath = FileService.GetDesktopPaths().UserDesktop;
                return Directory
                    .EnumerateFileSystemEntries(desktopPath)
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DragStart] Failed to capture desktop snapshot: " +
                    $"{ex.Message}");
                return null;
            }
        });
    }

    private async Task CompleteVirtualShortcutDesktopMoveAsync(
        IReadOnlyCollection<string> sourcePaths,
        HashSet<string>? desktopSnapshot)
    {
        string[] remainingPaths = sourcePaths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (remainingPaths.Length == 0)
        {
            return;
        }

        IReadOnlySet<string> materializedSources =
            desktopSnapshot is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : await FindMaterializedVirtualShortcutSourcesAsync(
                    remainingPaths,
                    desktopSnapshot);
        int completedByVirtualMove = 0;
        foreach (string sourcePath in materializedSources)
        {
            try
            {
                File.Delete(sourcePath);
                completedByVirtualMove++;
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DragComplete] Failed to finalize virtual shortcut " +
                    $"move source='{sourcePath}': {ex.Message}");
            }
        }

        string[] fallbackPaths = remainingPaths
            .Where(File.Exists)
            .ToArray();
        if (fallbackPaths.Length > 0)
        {
            await MoveRejectedManagedDragToDesktopAsync(fallbackPaths);
            return;
        }

        if (completedByVirtualMove > 0)
        {
            await ViewModel.RefreshFolderContentsAsync();
            ShowFeedback(new WidgetFeedbackRequest(
                _localizationService.Format(
                    "Widget.MovedToDesktop",
                    completedByVirtualMove),
                WidgetFeedbackSeverity.Success,
                "file-virtual-drag-desktop-move"));
            App.Log(
                $"[DragComplete] Finalized virtual shortcut desktop move " +
                $"id={WidgetId} paths={completedByVirtualMove}");
        }
    }

    private static async Task<IReadOnlySet<string>>
        FindMaterializedVirtualShortcutSourcesAsync(
            IReadOnlyList<string> sourcePaths,
            IReadOnlySet<string> desktopSnapshot)
    {
        // Directory enumeration and SHA-256 hashing are both unbounded file
        // system work. Keep the full reconciliation loop off the UI thread so
        // a slow shell extension, antivirus filter, or desktop provider cannot
        // stall every DeskBox window after the drag completes.
        return await Task.Run<IReadOnlySet<string>>(async () =>
        {
            string desktopPath = FileService.GetDesktopPaths().UserDesktop;
            Dictionary<string, string> sourceFingerprints = sourcePaths
                .ToDictionary(
                    path => path,
                    ComputeFileFingerprint,
                    StringComparer.OrdinalIgnoreCase);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(attempt == 0 ? 160 : 120)
                    .ConfigureAwait(false);
                string[] candidates;
                try
                {
                    candidates = Directory
                        .EnumerateFiles(desktopPath, "*.lnk")
                        .Select(Path.GetFullPath)
                        .Where(path => !desktopSnapshot.Contains(path))
                        .ToArray();
                }
                catch
                {
                    continue;
                }

                var candidateFingerprints = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (string candidate in candidates)
                {
                    try
                    {
                        candidateFingerprints[candidate] =
                            ComputeFileFingerprint(candidate);
                    }
                    catch
                    {
                    }
                }

                var matchedSources = sourceFingerprints
                    .Where(source => candidateFingerprints.Any(candidate =>
                        string.Equals(
                            source.Value,
                            candidate.Value,
                            StringComparison.Ordinal)))
                    .Select(source => source.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (matchedSources.Count == sourcePaths.Count ||
                    (attempt == 5 && matchedSources.Count > 0))
                {
                    return matchedSources;
                }
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        });
    }

    private static string ComputeFileFingerprint(string path)
    {
        using FileStream stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private async Task MoveRejectedManagedDragToDesktopAsync(
        IReadOnlyCollection<string> sourcePaths)
    {
        // A shell text-path compatibility handler can occasionally finish just
        // after WinUI reports None. Give it one short turn, then operate only
        // on sources that still exist so the fallback can never duplicate a
        // successful external move.
        await Task.Delay(120);
        if (_isDisposed)
        {
            return;
        }

        HashSet<string> pathSet = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            App.Log(
                $"[DragComplete] Skipped managed desktop fallback id={WidgetId}; " +
                "the shell already consumed every source path.");
            return;
        }

        List<WidgetItem> draggedItems = ViewModel.Items
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Path) &&
                pathSet.Contains(Path.GetFullPath(item.Path)))
            .ToList();
        if (draggedItems.Count == 0)
        {
            App.Log(
                $"[DragComplete] Managed desktop fallback found no live items " +
                $"id={WidgetId} paths={pathSet.Count}");
            return;
        }

        App.Log(
            $"[DragComplete] Using managed desktop fallback id={WidgetId} " +
            $"paths={draggedItems.Count} reason=StorageItemsUnavailable");
        await RunAsync(async () =>
        {
            int moved = await ViewModel.MoveItemsBackToDesktopAsync(
                draggedItems,
                useShellProgress: true);
            _cutClipboardPaths = [];
            ApplyCutState();
            ShowFeedback(new WidgetFeedbackRequest(
                moved > 0
                    ? _localizationService.Format(
                        "Widget.MovedToDesktop",
                        moved)
                    : T("Widget.NoItemsMoved"),
                moved > 0
                    ? WidgetFeedbackSeverity.Success
                    : WidgetFeedbackSeverity.Info,
                "file-drag-desktop-fallback"));
        });
    }

    private async Task ObserveExternalDragOutAsync(
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var remainingPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remainingPaths.Count == 0)
        {
            return;
        }

        int delayMs = 300;
        const int MaxAttempts = 11;
        try
        {
            for (int attempt = 0;
                 attempt < MaxAttempts &&
                 !_isDisposed &&
                 remainingPaths.Count > 0;
                 attempt++)
            {
                await Task.Delay(delayMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<string> missingPaths =
                    await ViewModel.GetConfirmedMissingPathsAsync(remainingPaths);
                if (missingPaths.Count > 0)
                {
                    await ViewModel.HandleItemsMovedOutAsync(missingPaths);
                    foreach (string path in missingPaths)
                    {
                        remainingPaths.Remove(path);
                    }

                    // Re-read the directory as a reconciliation step. This covers
                    // batched Shell moves and folder-watcher notifications that were
                    // coalesced while the grouped Surface was inactive.
                    await ViewModel.RefreshFolderContentsAsync();
                    UpdateEmptyState();
                    App.Log(
                        $"[WidgetSurface] External drag-out reconciled " +
                        $"id={WidgetId} removed={missingPaths.Count} " +
                        $"remaining={remainingPaths.Count}");
                }

                delayMs = (int)Math.Min(delayMs * 2, 300_000);
            }
        }
        catch (OperationCanceledException)
        {
            // The Surface was replaced, its group switched member, or the app closed.
        }
        catch (ObjectDisposedException)
        {
            // The content host disposed the member while a Shell move was pending.
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] External drag-out reconciliation failed " +
                $"id={WidgetId}: {ex}");
        }
    }

    private async Task RenameItemAsync(WidgetItem item)
    {
        // Let the MenuFlyout finish closing before taking keyboard focus.
        await Task.Yield();
        await StartItemRenameAsync(item);
    }

    private async Task RenameStackAsync(WidgetStackItem stack)
    {
        // Let the MenuFlyout finish closing before taking keyboard focus.
        await Task.Yield();
        await StartItemRenameAsync(stack);
    }

    private async Task StartItemRenameAsync(WidgetItem item)
    {
        FrameworkElement? target = item is WidgetStackItem stack
            ? await FindOrRealizeStackRenameTargetAsync(stack)
            : await FindOrRealizeItemRenameTargetAsync(item);
        UIElement? contentHost = SelectionOverlay.Parent as UIElement;
        if (target is null || contentHost is null)
        {
            App.Log(
                $"[WidgetSurface] Inline rename target unavailable " +
                $"id={WidgetId} target={item.Name}");
            return;
        }

        WidgetItem renameItem = target.DataContext as WidgetItem ??
            FindDisplayedItem(item) ??
            item;
        FrameworkElement? nameElement = FindItemNameElement(renameItem);

        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(renameItem);
        _itemRenameTarget = renameItem;
        _isCancellingItemRename = false;
        ItemRenameTextBox.Text = renameItem.Name;

        if (nameElement is TextBlock nameText)
        {
            _itemRenameNameText = nameText;
            nameText.Visibility = Visibility.Collapsed;
            ItemRenameTextBox.FontSize =
                nameText.FontSize > 0 ? nameText.FontSize : 14;
            ItemRenameTextBox.TextAlignment = nameText.TextAlignment;
            ItemRenameTextBox.HorizontalContentAlignment =
                nameText.HorizontalAlignment switch
                {
                    HorizontalAlignment.Center => HorizontalAlignment.Center,
                    HorizontalAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
            ItemRenameTextBox.TextWrapping = nameText.TextWrapping;
        }
        else
        {
            ItemRenameTextBox.FontSize = ViewModel.IsListMode
                ? ViewModel.ListLabelFontSize
                : ViewModel.IconLabelFontSize;
            ItemRenameTextBox.TextAlignment = ViewModel.IsListMode
                ? TextAlignment.Left
                : TextAlignment.Center;
            ItemRenameTextBox.TextWrapping = TextWrapping.NoWrap;
        }

        PositionItemRenameTextBox(target, contentHost);
        ItemRenameTextBox.Visibility = Visibility.Visible;
        ItemRenameTextBox.IsHitTestVisible = true;
        App.Current?.WidgetManager?.BeginWidgetInteraction(
            "surface-file-item-rename-opened");

        SelectItemNameForRename(
            ItemRenameTextBox,
            renameItem is WidgetStackItem || renameItem.IsFolder);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_itemRenameTarget, renameItem))
            {
                SelectItemNameForRename(
                    ItemRenameTextBox,
                    renameItem is WidgetStackItem || renameItem.IsFolder);
            }
        });

        await Task.CompletedTask;
    }

    private async void ItemRenameTextBox_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitItemRenameAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelItemRename();
        }
    }

    private async void ItemRenameTextBox_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (_isCancellingItemRename)
        {
            _isCancellingItemRename = false;
            return;
        }

        await CommitItemRenameAsync();
    }

    private async Task CommitItemRenameAsync()
    {
        if (_isCommittingItemRename ||
            _itemRenameTarget is null ||
            ItemRenameTextBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = ItemRenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            CancelItemRename();
            return;
        }

        _isCommittingItemRename = true;
        try
        {
            if (_itemRenameTarget is WidgetStackItem stack)
            {
                ViewModel.SetStackNameOverride(stack.StackKey, newName);
            }
            else
            {
                await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            }
            CompleteItemRename();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Inline rename failed id={WidgetId}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                _localizationService.T("Widget.RenameFailed"),
                WidgetFeedbackSeverity.Error,
                "file-rename-error"));
            ItemRenameTextBox.Focus(FocusState.Programmatic);
            ItemRenameTextBox.SelectAll();
        }
        finally
        {
            _isCommittingItemRename = false;
        }
    }

    private void CancelItemRename()
    {
        _isCancellingItemRename = true;
        CompleteItemRename();
    }

    private void CompleteItemRename()
    {
        ItemRenameTextBox.Visibility = Visibility.Collapsed;
        ItemRenameTextBox.IsHitTestVisible = false;
        ItemRenameTextBox.Text = string.Empty;
        if (_itemRenameNameText is not null)
        {
            _itemRenameNameText.Visibility = Visibility.Visible;
            _itemRenameNameText = null;
        }

        _itemRenameTarget = null;
        App.Current?.WidgetManager?.EndWidgetInteraction(
            "surface-file-item-rename-closed");
    }

    private void PositionItemRenameTextBox(
        FrameworkElement target,
        UIElement contentHost)
    {
        Windows.Foundation.Point topLeft = target
            .TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        const double border = 1;
        const double horizontalPadding = 2;
        double offsetX = topLeft.X - border - horizontalPadding;
        double offsetY = topLeft.Y - border;
        double hostPaddingHorizontal = 0;
        double hostPaddingVertical = 0;
        if (contentHost is Grid grid)
        {
            hostPaddingHorizontal = grid.Padding.Left + grid.Padding.Right;
            hostPaddingVertical = grid.Padding.Top + grid.Padding.Bottom;
            offsetX -= grid.Padding.Left;
            offsetY -= grid.Padding.Top;
        }

        double height = Math.Max(target.ActualHeight + (2 * border), 20);
        double width;
        if (contentHost is FrameworkElement host)
        {
            double contentWidth =
                Math.Max(60, host.ActualWidth - hostPaddingHorizontal);
            double availableWidth =
                Math.Max(60, contentWidth - offsetX - 8);
            width = ViewModel.IsListMode
                ? Math.Clamp(availableWidth, 80, contentWidth)
                : Math.Clamp(
                    target.ActualWidth +
                    (2 * (border + horizontalPadding)),
                    60,
                    availableWidth);
            double contentHeight =
                Math.Max(20, host.ActualHeight - hostPaddingVertical);
            height = Math.Min(
                height,
                Math.Max(20, contentHeight - offsetY - 4));
        }
        else
        {
            width = Math.Max(
                target.ActualWidth +
                (2 * (border + horizontalPadding)),
                60);
        }

        ItemRenameTextBox.Width = width;
        ItemRenameTextBox.Height = height;
        ItemRenameTextBox.Margin =
            new Thickness(offsetX, offsetY, 0, 0);
    }

    private FrameworkElement? FindItemNameElement(WidgetItem item)
    {
        if (item is WidgetStackItem stack)
        {
            return FindStackNameElement(stack);
        }

        if (GetActiveItemsView().ContainerFromItem(item)
            is not SelectorItem container)
        {
            return null;
        }

        return FindItemSurface(item) is Border border
            ? FileItemSurface.FindOwner(border)?.ItemNameText
            : null;
    }

    private async Task<FrameworkElement?>
        FindOrRealizeItemRenameTargetAsync(WidgetItem item)
    {
        const int realizationPasses = 5;
        ViewModel.RevealItemForInteraction(item.Path);
        for (int pass = 0; pass < realizationPasses; pass++)
        {
            ListViewBase activeView = GetActiveItemsView();
            WidgetItem? displayedItem = FindDisplayedItem(item);
            if (_isDisposed)
            {
                return null;
            }

            if (displayedItem is not null)
            {
                // The new item can sort outside the current viewport. Always
                // reveal the projected item before asking for its container.
                activeView.ScrollIntoView(displayedItem);
                activeView.UpdateLayout();
                FrameworkElement? target =
                    FindItemNameElement(displayedItem) ??
                    FindItemSurface(displayedItem);
                if (target is not null)
                {
                    return target;
                }
            }

            if (!await YieldForItemContainerRealizationAsync())
            {
                break;
            }
        }

        WidgetItem? finalItem = FindDisplayedItem(item);
        return finalItem is null
            ? null
            : FindItemNameElement(finalItem) ??
              FindItemSurface(finalItem);
    }

    private async Task<FrameworkElement?>
        FindOrRealizeStackRenameTargetAsync(WidgetStackItem stack)
    {
        const int realizationPasses = 5;
        for (int pass = 0; pass < realizationPasses; pass++)
        {
            if (_isDisposed)
            {
                return null;
            }

            ListViewBase activeView = GetActiveItemsView();
            activeView.ScrollIntoView(stack);
            activeView.UpdateLayout();
            FrameworkElement? target = FindStackNameElement(stack) ??
                FindStackSurface(stack);
            if (target is not null)
            {
                return target;
            }

            if (!await YieldForItemContainerRealizationAsync())
            {
                break;
            }
        }

        return FindStackNameElement(stack) ?? FindStackSurface(stack);
    }

    private Border? FindStackSurface(WidgetStackItem stack) =>
        _stackSurfaces.FirstOrDefault(surface =>
            ReferenceEquals(surface.DataContext, stack));

    private FrameworkElement? FindStackNameElement(WidgetStackItem stack)
    {
        Border? surface = FindStackSurface(stack);
        return surface is null
            ? null
            : FindDescendantByTag(surface, "StackName");
    }

    private static FrameworkElement? FindDescendantByTag(
        DependencyObject parent,
        string tag)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element &&
                string.Equals(element.Tag as string, tag, StringComparison.Ordinal))
            {
                return element;
            }

            FrameworkElement? match = FindDescendantByTag(child, tag);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private Task<bool> YieldForItemContainerRealizationAsync()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => completion.TrySetResult(!_isDisposed)))
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private static void SelectItemNameForRename(
        TextBox textBox,
        bool isFolder)
    {
        textBox.Focus(FocusState.Programmatic);
        string text = textBox.Text;
        if (isFolder)
        {
            textBox.SelectAll();
            return;
        }

        int dotIndex = text.LastIndexOf('.');
        if (dotIndex > 0 && text.Length - dotIndex - 1 <= 8)
        {
            textBox.Select(0, dotIndex);
        }
        else
        {
            textBox.SelectAll();
        }
    }

    private async Task DeleteItemAsync(WidgetItem item)
    {
        await RunAsync(() => ViewModel.DeleteItemsAsync([item]));
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format("Widget.MovedToRecycleBin", 1),
            WidgetFeedbackSeverity.Success,
            "file-delete"));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_isImportBusy)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            return;
        }

        if (IsInternalReorderDrag(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ApplyDropVisual(FileDropVisualState.None);
            HandleSurfaceRealTimeReorder(
                e.DataView.Properties,
                e.GetPosition(GetActiveItemsView()));
            return;
        }

        if (HasSurfacePathDropData(e.DataView))
        {
            string[] synchronousPaths = GetPackagePaths(e.DataView);
            if (IsUnsafeFolderDrop(synchronousPaths, ViewModel.CurrentFolderPath))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = T("Widget.Error.UnsafeFolderTransfer");
                ApplyDropVisual(FileDropVisualState.None);
                return;
            }

            e.AcceptedOperation = ResolveSurfaceDropOperation(e.DataView);
            e.DragUIOverride.IsGlyphVisible =
                e.AcceptedOperation != DataPackageOperation.None;
            e.DragUIOverride.IsCaptionVisible =
                e.AcceptedOperation != DataPackageOperation.None;
            e.DragUIOverride.Caption =
                GetSurfaceDropCaption(e.AcceptedOperation);
            ApplyDropVisual(FileDropVisualState.None);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            ApplyDropVisual(FileDropVisualState.None);
        }
    }

    private static bool IsUnsafeFolderDrop(
        IReadOnlyList<string> sourcePaths,
        string? destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return false;
        }

        string normalizedDestination = Path.GetFullPath(destinationFolder);
        return sourcePaths.Any(sourcePath =>
            !string.IsNullOrWhiteSpace(sourcePath) &&
            Directory.Exists(sourcePath) &&
            FileService.IsPathUnderDirectory(normalizedDestination, sourcePath));
    }

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        ApplyDropVisual(FileDropVisualState.None);
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        ExternalFileDragEnded?.Invoke(this, EventArgs.Empty);
        // Leaving the surface means the user may be dragging to Explorer,
        // another widget or another application. Discard the internal preview;
        // only a confirmed drop back onto this surface may change ordering.
        PersistSurfaceReorder();
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();
        ClearStackMemberDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        ExternalFileDragEnded?.Invoke(this, EventArgs.Empty);
        if (_isImportBusy)
        {
            App.LogVerbose(
                $"[WidgetSurface] Ignored overlapping file drop id={WidgetId} " +
                "stage=before-read");
            return;
        }

        if (IsInternalReorderDrag(e.DataView))
        {
            _surfaceReorderStackKey ??= TryGetString(
                e.DataView.Properties,
                DeskBoxDragData.StackReorderKeyProperty);
            HandleSurfaceFinalReorder(
                GetPackagePaths(e.DataView),
                e.GetPosition(GetActiveItemsView()));
            PersistSurfaceReorder();
            return;
        }

        var deferral = e.GetDeferral();
        // Start visible feedback before asking the source application for its
        // StorageItems. Explorer, cloud providers and virtual-file sources can
        // spend seconds materializing a large payload before paths are
        // available; that preparation time is part of the import operation.
        BeginTrackedImport();
        try
        {
            using DroppedFileBatch batch = await GetSurfaceDropFilesAsync(e.DataView);
            IReadOnlyList<DroppedFilePath> droppedFiles = batch.Files;
            string[] paths = droppedFiles
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (droppedFiles.Count > 0)
            {
                DataPackageOperation accepted =
                    e.AcceptedOperation == DataPackageOperation.None
                        ? ResolveSurfaceDropOperation(e.DataView)
                        : e.AcceptedOperation;
                bool mapped = !string.IsNullOrWhiteSpace(
                    ViewModel.MappedFolderPath);
                bool? moveWhenMapped = mapped
                    ? accepted != DataPackageOperation.Copy
                    : null;
                string? sourceWidgetId = TryGetString(
                    e.DataView.Properties,
                    "DeskBoxSourceWidgetId");
                IReadOnlyList<string> completedSourcePaths =
                    await ImportDroppedFilesAsync(
                        droppedFiles,
                        moveWhenMapped);
                if (moveWhenMapped == true &&
                    sourceWidgetId is { Length: > 0 } &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        completedSourcePaths);
                }

                ShowFeedback(new(
                    _localizationService.Format(
                        moveWhenMapped == true
                            ? "Widget.MovedCount"
                            : "Widget.PastedCount",
                        droppedFiles.Count),
                    WidgetFeedbackSeverity.Success,
                    "file-drop"));
            }
        }
        catch (OperationCanceledException)
        {
            App.Log($"[WidgetSurface] File drop canceled id={WidgetId}");
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Canceled);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] File drop failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "file-drop-error"));
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Failed);
            }
        }
        finally
        {
            // Empty/unsupported payloads return before ImportDroppedFilesAsync
            // owns completion. Never leave their preparation session busy.
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
            ApplyDropVisual(FileDropVisualState.None);
            deferral.Complete();
        }
    }

    private void SetImportBusy(bool isBusy)
    {
        SetBusyOverlay(
            isBusy,
            "Widget.Import.Title",
            "Widget.Import.Description");
    }

    internal void SetMigrationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetMigrationBusy(isBusy));
            return;
        }

        SetBusyOverlay(
            isBusy,
            "Widget.Migration.Title",
            "Widget.Migration.Description");
    }

    internal void SetDesktopOrganizationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetDesktopOrganizationBusy(isBusy));
            return;
        }

        SetBusyOverlay(
            isBusy,
            "DesktopOrganization.Busy.Title",
            "DesktopOrganization.Busy.Description");
    }

    private void SetBusyOverlay(
        bool isBusy,
        string titleKey,
        string descriptionKey)
    {
        if (_isImportBusy == isBusy)
        {
            return;
        }

        _isImportBusy = isBusy;
        if (isBusy)
        {
            _importBusyStartedAtUtc = DateTimeOffset.UtcNow;
            ImportTitleText.Text = T(titleKey);
            ImportDescriptionText.Text = T(descriptionKey);
            ImportProgressBar.Value = 0;
            ImportStateIcon.Glyph = "\uE896";
            ImportStateIcon.Foreground = ImportProgressBar.Foreground;
            ApplyDropVisual(FileDropVisualState.None);
        }

        ImportProgressCard.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ImportProgressBar.IsIndeterminate = isBusy;
        ImportPercentText.Text = string.Empty;
        ImportCancelButton.Visibility = Visibility.Collapsed;
        SelectionCommandBar.IsEnabled = !isBusy;
        if (!isBusy)
        {
            _importBusyStartedAtUtc = null;
        }
        ImportBusyChanged?.Invoke(isBusy);
    }

    internal bool IsInternalReorderDrag(DataPackageView dataView)
    {
        return string.Equals(
                   TryGetString(
                       dataView.Properties,
                       "DeskBoxInternalDragToken"),
                   "DeskBox.WidgetItemDrag.v2",
                   StringComparison.Ordinal) &&
               string.Equals(
                   TryGetString(
                       dataView.Properties,
                       "DeskBoxSourceWidgetId"),
                   WidgetId,
                   StringComparison.Ordinal) &&
               (GetPackagePaths(dataView).Length > 0 ||
                !string.IsNullOrWhiteSpace(
                    TryGetString(
                        dataView.Properties,
                        DeskBoxDragData.StackReorderKeyProperty)));
    }

    private static bool HasSurfacePathDropData(DataPackageView dataView)
    {
        return GetPackagePaths(dataView).Length > 0 ||
               DeskBoxDragData.HasImportableFileData(dataView);
    }

    private DataPackageOperation ResolveSurfaceDropOperation(
        DataPackageView dataView)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return DataPackageOperation.Link;
        }

        CoreVirtualKeyStates controlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Control);
        bool copyRequested =
            controlState.HasFlag(CoreVirtualKeyStates.Down);
        DataPackageOperation requested = dataView.RequestedOperation;
        if (requested == DataPackageOperation.None)
        {
            return DataPackageOperation.Move;
        }

        if (copyRequested &&
            requested.HasFlag(DataPackageOperation.Copy))
        {
            return DataPackageOperation.Copy;
        }

        if (requested.HasFlag(DataPackageOperation.Move))
        {
            return DataPackageOperation.Move;
        }

        return requested.HasFlag(DataPackageOperation.Copy)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private string GetSurfaceDropCaption(
        DataPackageOperation operation)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return T("Widget.DragCaption.Reference");
        }

        string operationText = T(
            operation == DataPackageOperation.Copy
                ? "Common.Copy"
                : "Common.Move");
        return _localizationService.Format(
            ViewModel.FollowsDefaultStoragePath
                ? "Widget.DragCaption.Managed"
                : "Widget.DragCaption.Mapped",
            operationText);
    }

    private static async Task<DroppedFileBatch> GetSurfaceDropFilesAsync(
        DataPackageView dataView)
    {
        string[] paths = GetPackagePaths(dataView);
        if (paths.Length > 0)
        {
            DroppedFilePath[] files = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    try
                    {
                        return Path.GetFullPath(path);
                    }
                    catch
                    {
                        return string.Empty;
                    }
                })
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new DroppedFilePath(
                    path,
                    Path.GetFileName(path),
                    ForceManagedCopy: false))
                .ToArray();
            return new DroppedFileBatch(files, temporaryDirectory: null, skippedCount: 0);
        }

        return await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
    }

    private async Task<IReadOnlyList<string>> ImportDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        bool? moveWhenMapped)
    {
        EnsureTrackedImportStarted();
        IProgress<FileService.FileTransferProgress> progress =
            new CallbackProgress<FileService.FileTransferProgress>(
                ReportImportProgress);
        var movedSourcePaths = new List<string>();
        int importedItemCount = 0;
        try
        {
            string[] regularPaths = droppedFiles
                .Where(file => !file.ForceManagedCopy)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (regularPaths.Length > 0)
            {
                IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                    regularPaths,
                    moveWhenMapped,
                    useShellProgress: moveWhenMapped == true,
                    ownerWindowHandle: _hostWindowHandle,
                    progress: progress,
                    cancellationToken: ActiveImportCancellationToken);
                importedItemCount += completed.Count;
                if (moveWhenMapped == true)
                {
                    movedSourcePaths.AddRange(completed);
                }
            }

            string[] managedCopyPaths = droppedFiles
                .Where(file => file.ForceManagedCopy)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (managedCopyPaths.Length > 0)
            {
                // Virtual browser files and URL downloads live in a temporary
                // directory owned by DroppedFileBatch. They must always be copied.
                IReadOnlyList<string> completed = await ViewModel.ImportPathsAsync(
                    managedCopyPaths,
                    moveWhenMapped: false,
                    useShellProgress: false,
                    ownerWindowHandle: _hostWindowHandle,
                    progress: progress,
                    cancellationToken: ActiveImportCancellationToken);
                importedItemCount += completed.Count;
            }

            await CompleteTrackedImportAsync(ImportCompletionState.Completed);
            global::DeskBox.App.Current.NotifyOnboardingFileImportCompleted(
                importedItemCount);
            return movedSourcePaths;
        }
        catch (OperationCanceledException)
        {
            await CompleteTrackedImportAsync(ImportCompletionState.Canceled);
            throw;
        }
        catch
        {
            await CompleteTrackedImportAsync(ImportCompletionState.Failed);
            throw;
        }
    }

    /// <summary>
    /// Imports a file payload received by the owning surface window's native
    /// drag-drop bridge. Grouped file content has no HWND of its own, so this
    /// mirrors the regular surface import pipeline after the host extracts the
    /// native OLE or WM_DROPFILES payload.
    /// </summary>
    internal async Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles)
    {
        if (_isDisposed || _isImportBusy)
        {
            return false;
        }

        DroppedFilePath[] droppedFiles = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DroppedFilePath(
                path,
                Path.GetFileName(path),
                ForceManagedCopy: containsTemporaryFiles))
            .ToArray();
        if (droppedFiles.Length == 0)
        {
            return false;
        }

        bool mapped = !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
        bool? moveWhenMapped = mapped
            ? containsTemporaryFiles || Win32Helper.IsKeyPressed(VirtualKey.Control)
                ? false
                : true
            : null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string importId = Guid.NewGuid().ToString("N")[..8];
        App.Log(
            $"[Import] Native import start id={importId} widget={WidgetId} " +
            $"count={droppedFiles.Length} move={moveWhenMapped == true} " +
            $"owner=0x{_hostWindowHandle.ToInt64():X}");
        try
        {
            await ImportDroppedFilesAsync(droppedFiles, moveWhenMapped);
            App.Log(
                $"[Import] Native import completed id={importId} widget={WidgetId} " +
                $"count={droppedFiles.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
            ShowFeedback(new(
                _localizationService.Format(
                    moveWhenMapped == true
                        ? "Widget.MovedCount"
                        : "Widget.PastedCount",
                    droppedFiles.Length),
                WidgetFeedbackSeverity.Success,
                "native-file-drop"));
            return true;
        }
        catch (OperationCanceledException)
        {
            App.Log(
                $"[Import] Native import canceled id={importId} widget={WidgetId} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return false;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Native file drop failed id={WidgetId} " +
                $"import={importId} elapsedMs={stopwatch.ElapsedMilliseconds}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "native-file-drop-error"));
            return false;
        }
        finally
        {
            App.Log(
                $"[Import] Native import finalized id={importId} widget={WidgetId} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
    }

    private void HandleSurfaceRealTimeReorder(
        DataPackagePropertySetView properties,
        Windows.Foundation.Point position)
    {
        string? stackKey = TryGetString(
            properties,
            DeskBoxDragData.StackReorderKeyProperty);
        if (!string.IsNullOrWhiteSpace(stackKey))
        {
            _isSurfaceReorderDragActive = true;
            _surfaceReorderStackKey = stackKey;
            _surfaceReorderPaths = [];
            UpdateSurfaceReorderPreview(position);
            return;
        }

        string[] paths = properties.TryGetValue(
                "DeskBoxSourcePaths",
                out object? value)
            ? value switch
            {
                string[] array => array,
                IEnumerable<string> sequence => sequence.ToArray(),
                _ => []
            }
            : [];
        if (paths.Length == 0)
        {
            return;
        }

        HashSet<string> pathSet = paths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem? draggedItem = ViewModel.Items.FirstOrDefault(item =>
            pathSet.Contains(Path.GetFullPath(item.Path)));
        if (draggedItem is null)
        {
            return;
        }

        if (!_isSurfaceReorderDragActive)
        {
            if (ViewModel.UsesStackProjection)
            {
                if (!ViewModel.PrepareVisibleItemReorder(draggedItem))
                {
                    return;
                }
            }
            else if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }

            _isSurfaceReorderDragActive = true;
            _surfaceReorderPaths = paths;
        }

        UpdateSurfaceReorderPreview(position);
    }

    private void HandleSurfaceFinalReorder(
        IReadOnlyList<string> paths,
        Windows.Foundation.Point position)
    {
        if (!_isSurfaceReorderDragActive)
        {
            _surfaceReorderPaths = paths.ToArray();
            _isSurfaceReorderDragActive =
                _surfaceReorderPaths.Length > 0;
        }

        CommitSurfaceReorder(position);
    }

    private void UpdateSurfaceReorderPreview(
        Windows.Foundation.Point position)
    {
        _surfaceReorderLastPosition = position;
        _surfaceReorderHasLastPosition = true;
        ListViewBase activeView = GetActiveItemsView();
        _surfaceReorderInsertionIndex =
            ReorderDropIndexCalculator.Compute(
                activeView,
                position,
                _surfaceReorderInsertionIndex);
        UpdateSurfaceReorderInsertionIndicator(position);
    }

    private void UpdateSurfaceReorderInsertionIndicator(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        if (!_isSurfaceReorderDragActive ||
            _surfaceReorderInsertionIndex < 0 ||
            !ReorderDropIndexCalculator.TryGetInsertionIndicatorPlacement(
                activeView,
                SelectionOverlay,
                _surfaceReorderInsertionIndex,
                position,
                out ReorderInsertionIndicatorPlacement placement))
        {
            HideSurfaceReorderInsertionIndicator();
            return;
        }

        bool wasVisible =
            ReorderInsertionIndicator.Visibility == Visibility.Visible;
        ReorderInsertionIndicator.Width = placement.Bounds.Width;
        ReorderInsertionIndicator.Height = placement.Bounds.Height;
        ReorderInsertionLine.Width = placement.IsVertical
            ? 1.5
            : placement.Bounds.Width;
        ReorderInsertionLine.Height = placement.IsVertical
            ? placement.Bounds.Height
            : 1.5;
        if (ReorderInsertionGlow.Background is LinearGradientBrush glowBrush)
        {
            glowBrush.StartPoint = placement.IsVertical
                ? new Windows.Foundation.Point(0, 0.5)
                : new Windows.Foundation.Point(0.5, 0);
            glowBrush.EndPoint = placement.IsVertical
                ? new Windows.Foundation.Point(1, 0.5)
                : new Windows.Foundation.Point(0.5, 1);
        }
        Canvas.SetLeft(
            ReorderInsertionIndicator,
            placement.Bounds.X);
        Canvas.SetTop(
            ReorderInsertionIndicator,
            placement.Bounds.Y);
        ReorderInsertionIndicator.Opacity = 1;
        ReorderInsertionIndicator.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            ReorderInsertionIndicatorAnimator.Start(
                ReorderInsertionIndicator);
        }
    }

    private void HideSurfaceReorderInsertionIndicator()
    {
        ReorderInsertionIndicatorAnimator.Stop(
            ReorderInsertionIndicator);
        ReorderInsertionIndicator.Visibility = Visibility.Collapsed;
        ReorderInsertionIndicator.Opacity = 0;
        ReorderInsertionIndicator.Width = 0;
        ReorderInsertionIndicator.Height = 0;
    }

    private void ApplySurfaceReorder(
        Windows.Foundation.Point position)
    {
        ListViewBase activeView = GetActiveItemsView();
        int targetIndex = ReorderDropIndexCalculator.Compute(
            activeView,
            position,
            _surfaceReorderInsertionIndex);
        _surfaceReorderInsertionIndex = targetIndex;

        if (!string.IsNullOrWhiteSpace(_surfaceReorderStackKey))
        {
            if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }
            ViewModel.MoveStackForReorder(
                _surfaceReorderStackKey,
                targetIndex);
            return;
        }

        if (_surfaceReorderPaths.Length == 0)
        {
            return;
        }

        HashSet<string> pathSet = _surfaceReorderPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem? draggedItem = ViewModel.Items.FirstOrDefault(item =>
            pathSet.Contains(Path.GetFullPath(item.Path)));
        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.UsesStackProjection
            ? activeView.Items.IndexOf(draggedItem)
            : ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        if (ViewModel.UsesStackProjection)
        {
            if (!draggedItem.IsStackChild &&
                ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }
            ViewModel.MoveVisibleItemForReorder(
                draggedItem,
                targetIndex);
            return;
        }

        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(
            draggedItem,
            targetIndex);
    }

    private void PersistSurfaceReorder()
    {
        HideSurfaceReorderInsertionIndicator();
        _isSurfaceReorderDragActive = false;
        _surfaceReorderPaths = [];
        _surfaceReorderStackKey = null;
        _surfaceReorderInsertionIndex = -1;
    }

    private void CommitSurfaceReorder(
        Windows.Foundation.Point position)
    {
        if (!_isSurfaceReorderDragActive)
        {
            return;
        }

        ApplySurfaceReorder(position);
        if (string.IsNullOrWhiteSpace(_surfaceReorderStackKey))
        {
            ViewModel.PersistManualOrder();
        }

        PersistSurfaceReorder();
    }

    private void ApplyDropVisual(FileDropVisualState state)
    {
        // Match the standalone file widget: keep content readable and let the
        // native drag caption communicate the operation and destination type.
        DropOverlay.Visibility = Visibility.Collapsed;
        DropOverlay.Opacity = 0;
        ItemsGrid.Opacity = 1;
        ItemsList.Opacity = 1;
        EmptyState.Opacity = 1;
    }

    private static Microsoft.UI.Xaml.Media.Brush? ResolveBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (await TryHandleClipboardShortcutAsync(e))
        {
            return;
        }

        if (await TryHandleSpacePreviewAsync(e))
        {
            return;
        }

        if (e.Handled)
        {
            return;
        }

        CoreVirtualKeyStates controlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool control = controlState.HasFlag(CoreVirtualKeyStates.Down);
        CoreVirtualKeyStates shiftState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool shift = shiftState.HasFlag(CoreVirtualKeyStates.Down);
        CoreVirtualKeyStates menuState =
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        bool alt = menuState.HasFlag(CoreVirtualKeyStates.Down);
        if (alt && e.Key == VirtualKey.Up && ViewModel.CanNavigateUp)
        {
            e.Handled = true;
            await NavigateUpFromSurfaceAsync();
            return;
        }

        if (control && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            ListViewBase activeView =
                ViewModel.IconViewVisibility == Visibility.Visible
                    ? ItemsGrid
                    : ItemsList;
            activeView.SelectedItems.Clear();
            foreach (WidgetItem item in activeView.Items
                         .OfType<WidgetItem>()
                         .Where(item => item is not WidgetStackItem))
            {
                activeView.SelectedItems.Add(item);
            }
            UpdateSelectionCommandBar();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (ViewModel.HasExpandedStack)
            {
                if (ViewModel.GetExpandedStack() is { } expandedStack)
                {
                    RequestStackState(
                        expandedStack,
                        expanded: false);
                }
                e.Handled = true;
                return;
            }

            if (App.Current.WidgetManager is { } manager)
            {
                _ = manager.CloseQuickLookPreviewAsync();
            }
            e.Handled = true;
            ClearSelection();
            _cutClipboardPaths = [];
            ApplyCutState();
            return;
        }

        if (control && shift && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            CopySelectedPathsToClipboard();
            return;
        }

        if (e.Key == VirtualKey.F2 &&
            GetSelectedItems().FirstOrDefault() is { } renameTarget)
        {
            e.Handled = true;
            await RenameItemAsync(renameTarget);
            return;
        }

        if (e.Key == VirtualKey.Delete &&
            GetSelectedItems() is { Count: > 0 } deleteTargets)
        {
            e.Handled = true;
            await DeleteItemsAsync(deleteTargets);
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            GetSelectedItems().FirstOrDefault() is { } openTarget)
        {
            e.Handled = true;
            await ActivateItemAsync(openTarget);
            return;
        }

        if (e.Key == VirtualKey.F5)
        {
            e.Handled = true;
            await RunAsync(RefreshAsync);
        }
    }

    private async void ItemsView_PreviewKeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        ShowScrollBarTemporarily(sender as ListViewBase);
        if (e.Key == VirtualKey.Enter &&
            sender is ListViewBase
            {
                SelectedItem: WidgetStackItem stack
            })
        {
            e.Handled = true;
            ToggleStackFromInput(stack);
            return;
        }

        if (TryHandleTypeAhead(e))
        {
            return;
        }

        if (await TryHandleClipboardShortcutAsync(e))
        {
            return;
        }

        if (await TryHandleSpacePreviewAsync(e) || e.Handled)
        {
            return;
        }

        QueueQuickLookBoundaryNavigation(e);
    }

    private bool TryHandleTypeAhead(KeyRoutedEventArgs e)
    {
        if (e.Handled ||
            e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source) ||
            Win32Helper.IsKeyPressed(VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(VirtualKey.Menu) ||
            Win32Helper.IsKeyPressed(VirtualKey.Shift) ||
            !TryGetTypeAheadCharacter(e.Key, out char character))
        {
            return false;
        }

        long now = Environment.TickCount64;
        long timeout = 900;
        string proposed = now - _typeAheadLastInputTick <= timeout
            ? _typeAheadBuffer + character
            : character.ToString();
        ListViewBase activeView = GetActiveItemsView();
        WidgetItem? selected = activeView.SelectedItems
            .OfType<WidgetItem>()
            .FirstOrDefault(item => item is not WidgetStackItem);
        WidgetItem[] items = activeView.Items
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .ToArray();

        WidgetItem? match = FindTypeAheadMatch(items, proposed, selected);
        if (match is null && proposed.Length > 1)
        {
            proposed = character.ToString();
            match = FindTypeAheadMatch(items, proposed, selected);
        }

        if (match is null)
        {
            return false;
        }

        _typeAheadBuffer = proposed;
        _typeAheadLastInputTick = now;
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(match);
        activeView.ScrollIntoView(match);
        activeView.Focus(FocusState.Programmatic);
        UpdateSelectionCommandBar();
        e.Handled = true;
        return true;
    }

    private static WidgetItem? FindTypeAheadMatch(
        IReadOnlyList<WidgetItem> items,
        string prefix,
        WidgetItem? selected)
    {
        if (items.Count == 0 || string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        int selectedIndex = selected is null
            ? -1
            : Array.IndexOf(items.ToArray(), selected);
        IEnumerable<WidgetItem> ordered = items
            .Skip(selectedIndex + 1)
            .Concat(items.Take(selectedIndex + 1));
        return ordered.FirstOrDefault(item =>
            item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase));
    }

    private static bool TryGetTypeAheadCharacter(VirtualKey key, out char character)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            character = (char)('a' + ((int)key - (int)VirtualKey.A));
            return true;
        }

        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            character = (char)('0' + ((int)key - (int)VirtualKey.Number0));
            return true;
        }

        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            character = (char)('0' + ((int)key - (int)VirtualKey.NumberPad0));
            return true;
        }

        character = default;
        return false;
    }

    internal async Task<bool> TryHandleClipboardShortcutAsync(
        KeyRoutedEventArgs e)
    {
        if (e.Handled ||
            e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source) ||
            !Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            return false;
        }

        bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        if (e.Key is VirtualKey.C or VirtualKey.X && !shift)
        {
            e.Handled = true;
            await RunAsync(() => CopySelectionToClipboardAsync(
                cut: e.Key == VirtualKey.X));
            return true;
        }

        if (e.Key == VirtualKey.V)
        {
            e.Handled = true;
            await RunAsync(PasteFromClipboardAsync);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleSpacePreviewAsync(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space ||
            e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source))
        {
            return false;
        }

        IReadOnlyList<WidgetItem> selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 ||
            selectedItems.Any(item => item is WidgetStackItem))
        {
            return false;
        }

        // Match the standalone file widget: ListView/GridView handles Space
        // for selection and otherwise swallows the key before normal KeyDown.
        e.Handled = true;
        WidgetItem previewTarget = selectedItems[0];
        if (App.Current.WidgetManager is { } manager)
        {
            await manager.TryToggleQuickLookPreviewAsync(
                this,
                previewTarget.Path);
        }
        else if (s_quickLookService.CanPreview(previewTarget.Path))
        {
            await s_quickLookService.TryToggleAsync(previewTarget.Path);
        }

        return true;
    }

    private ListViewBase GetActiveItemsView()
    {
        return ViewModel.IconViewVisibility == Visibility.Visible
            ? ItemsGrid
            : ItemsList;
    }

    private IReadOnlyList<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView().SelectedItems
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .Distinct()
            .ToList();
    }

    internal WidgetItem? GetPrimaryQuickLookSelection()
    {
        IReadOnlyList<WidgetItem> selectedItems = GetSelectedItems();
        return selectedItems.Count == 1 ? selectedItems[0] : null;
    }

    internal IReadOnlyList<string> GetQuickLookNavigationPaths() =>
        GetActiveItemsView().Items
            .OfType<WidgetItem>()
            .Where(item =>
                item is not WidgetStackItem &&
                !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => item.Path)
            .ToArray();

    internal bool TrySelectQuickLookTarget(string path)
    {
        ListViewBase activeView = GetActiveItemsView();
        WidgetItem? target = activeView.Items
            .OfType<WidgetItem>()
            .FirstOrDefault(item =>
                item is not WidgetStackItem &&
                string.Equals(
                    item.Path,
                    path,
                    StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        ClearItemSelection();
        activeView.SelectedItem = target;
        activeView.ScrollIntoView(target);
        return true;
    }

    internal void FocusQuickLookNavigationTarget()
    {
        ListViewBase activeView = GetActiveItemsView();
        activeView.UpdateLayout();
        if (activeView.SelectedItem is { } selected &&
            activeView.ContainerFromItem(selected) is Control container)
        {
            container.Focus(FocusState.Programmatic);
            return;
        }

        activeView.Focus(FocusState.Programmatic);
    }

    private void QueueQuickLookBoundaryNavigation(KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FileItemSelectionGeometry.HasAncestor<TextBox>(source) ||
            e.Key is not (VirtualKey.Left or VirtualKey.Up or
                VirtualKey.Right or VirtualKey.Down) ||
            Win32Helper.IsKeyPressed(VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(VirtualKey.Shift) ||
            Win32Helper.IsKeyPressed(VirtualKey.Menu) ||
            GetPrimaryQuickLookSelection() is not { } selected ||
            App.Current.WidgetManager is not { } manager ||
            !manager.IsCurrentQuickLookPreviewTarget(this, selected.Path))
        {
            return;
        }

        string originalPath = selected.Path;
        VirtualKey key = e.Key;
        DispatcherQueue.TryEnqueue(async () =>
            await manager.ContinueQuickLookNavigationAfterNativeAsync(
                this,
                originalPath,
                key));
    }

    private void Items_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        if (sender is ListViewBase listView)
        {
            WidgetStackItem[] selectedStacks = listView.SelectedItems
                .OfType<WidgetStackItem>()
                .Where(stack => stack.IsExpanded)
                .ToArray();
            if (selectedStacks.Length > 0)
            {
                // An expanded stack header is an interaction surface, not a
                // file selection. Keeping one selected during collapse lets
                // WinUI recycle that container onto a member on the next
                // expansion. Collapsed headers remain selectable long enough
                // for the existing stack-reorder drag gesture to start.
                _isSynchronizingSelection = true;
                try
                {
                    foreach (WidgetStackItem stack in selectedStacks)
                    {
                        listView.SelectedItems.Remove(stack);
                    }
                }
                finally
                {
                    _isSynchronizingSelection = false;
                }
            }
        }

        if (e.AddedItems.OfType<WidgetItem>()
            .Any(item => item is not WidgetStackItem))
        {
            ClearOtherWidgetSelections();
            if (GetPrimaryQuickLookSelection() is { } selected)
            {
                _ = App.Current.WidgetManager?
                    .FollowQuickLookSelectionAsync(this, selected.Path);
            }
        }

        RefreshItemSelectionVisuals();
        UpdateSelectionCommandBar();
    }

    private void UpdateSelectionCommandBar()
    {
        SelectionCommandBar.Visibility = Visibility.Collapsed;
    }

    private async void OpenSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            await ActivateItemAsync(item);
        }
    }

    private async void CopySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CopySelectionToClipboardAsync(cut: false));
    }

    private async void CutSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CopySelectionToClipboardAsync(cut: true));
    }

    private async void DeleteSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems() is { Count: > 0 } items)
        {
            await DeleteItemsAsync(items);
        }
    }

    private async void RenameSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedItems().SingleOrDefault() is { } item)
        {
            await RenameItemAsync(item);
        }
    }

    private static void RestoreSelection(
        ListViewBase view,
        IReadOnlyList<string> selectedPaths)
    {
        view.SelectedItems.Clear();
        foreach (WidgetItem item in view.Items.OfType<WidgetItem>())
        {
            if (selectedPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            {
                view.SelectedItems.Add(item);
            }
        }
    }

    private async Task CopySelectionToClipboardAsync(bool cut)
    {
        string[] paths = GetSelectedItems()
            .Select(item => item.Path)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        string clipboardText = string.Join(Environment.NewLine, paths);
        DeskBoxClipboardWriteScope.MarkWrite(
            text: clipboardText,
            paths: paths);
        bool shellClipboardSet =
            ShellClipboardHelper.TrySetFileDropList(paths, cut);
        if (!shellClipboardSet)
        {
            var package = new DataPackage
            {
                RequestedOperation =
                    cut ? DataPackageOperation.Move : DataPackageOperation.Copy
            };
            IReadOnlyList<IStorageItem> storageItems =
                await _fileService.GetStorageItemsAsync(paths);
            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems);
            }
            else
            {
                package.SetText(clipboardText);
            }
            package.Properties["DeskBoxSourceWidgetId"] = WidgetId;
            package.Properties["DeskBoxSourcePaths"] = paths;
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        _cutClipboardPaths = cut ? paths : [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                cut ? "Widget.CutCount" : "Widget.CopyCount",
                paths.Length),
            WidgetFeedbackSeverity.Success,
            cut ? "file-cut" : "file-copy"));
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_isDisposed || _isImportBusy)
        {
            return;
        }

        DataPackageView? clipboard = TryGetClipboardContent();
        string[] sourcePaths = clipboard is null
            ? []
            : GetPackagePaths(clipboard);
        bool move = clipboard?.RequestedOperation.HasFlag(
            DataPackageOperation.Move) == true;

        if (ShellClipboardHelper.TryGetFileDropList(
                out string[] shellPaths,
                out bool shellCut))
        {
            if (sourcePaths.Length == 0)
            {
                sourcePaths = shellPaths;
            }

            move |= shellCut;
        }

        if (sourcePaths.Length == 0 &&
            clipboard?.Contains(StandardDataFormats.StorageItems) == true)
        {
            IReadOnlyList<IStorageItem> storageItems =
                await clipboard.GetStorageItemsAsync();
            sourcePaths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (sourcePaths.Length == 0)
        {
            return;
        }

        IReadOnlyList<string> completedSourcePaths =
            await ImportPathsWithTrackedProgressAsync(
            sourcePaths,
            moveWhenMapped: move);
        if (move &&
            clipboard is not null &&
            TryGetString(clipboard.Properties, "DeskBoxSourceWidgetId")
                is { Length: > 0 } sourceWidgetId &&
            App.Current?.WidgetManager is { } manager)
        {
            await manager.NotifyItemsMovedOutAsync(
                sourceWidgetId,
                completedSourcePaths);
        }

        _cutClipboardPaths = [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                move ? "Widget.MovedCount" : "Widget.PastedCount",
                sourcePaths.Length),
            WidgetFeedbackSeverity.Success,
            move ? "file-move" : "file-paste"));
    }

    private static DataPackageView? TryGetClipboardContent()
    {
        try
        {
            return Clipboard.GetContent();
        }
        catch
        {
            return null;
        }
    }

    private bool CanPasteFromClipboard()
    {
        if (ShellClipboardHelper.HasFileDropList())
        {
            return true;
        }

        DataPackageView? clipboard = TryGetClipboardContent();
        return clipboard is not null &&
            (GetPackagePaths(clipboard).Length > 0 ||
             clipboard.Contains(StandardDataFormats.StorageItems));
    }

    private static string[] GetPackagePaths(DataPackageView package)
    {
        if (!package.Properties.TryGetValue(
                "DeskBoxSourcePaths",
                out object? value))
        {
            return [];
        }

        return value switch
        {
            string[] paths => paths,
            IEnumerable<string> paths => paths.ToArray(),
            _ => []
        };
    }

    private static string? TryGetString(
        DataPackagePropertySetView properties,
        string key)
    {
        return properties.TryGetValue(key, out object? value)
            ? value as string
            : null;
    }

    private async Task DeleteItemsAsync(IReadOnlyList<WidgetItem> items)
    {
        await RunAsync(() => ViewModel.DeleteItemsAsync(items));
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.MovedToRecycleBin",
                items.Count),
            WidgetFeedbackSeverity.Success,
            "file-delete"));
    }

    private void ApplyCutState()
    {
        foreach (WidgetItem item in ViewModel.Items)
        {
            item.IsCut = _cutClipboardPaths.Contains(
                item.Path,
                StringComparer.OrdinalIgnoreCase);
        }

        UpdateItemSurfaceVisuals();
    }

    private async Task PickAndImportFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
        InitializeWithWindow.Initialize(
            picker,
            owner == IntPtr.Zero ? foreground : owner);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await ImportPathsWithTrackedProgressAsync(
                files.Select(file => file.Path),
                moveWhenMapped: null);
        }
    }

    private async Task<IReadOnlyList<string>>
        ImportPathsWithTrackedProgressAsync(
            IEnumerable<string> paths,
            bool? moveWhenMapped)
    {
        if (_isDisposed || _isImportBusy)
        {
            return [];
        }

        DroppedFilePath[] droppedFiles = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DroppedFilePath(
                path,
                Path.GetFileName(path),
                ForceManagedCopy: false))
            .ToArray();
        if (droppedFiles.Length == 0)
        {
            return [];
        }

        BeginTrackedImport();
        try
        {
            return await ImportDroppedFilesAsync(
                droppedFiles,
                moveWhenMapped);
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            App.Log($"[WidgetSurface] File action canceled id={WidgetId}");
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] File action failed id={WidgetId}: {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "file-action-error"));
        }
        finally
        {
            UpdateEmptyState();
        }
    }

    private string T(string key) => _localizationService.T(key);

    private void ShowFeedback(WidgetFeedbackRequest request)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(request));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        PersistSurfaceReorder();
        App.Current.WidgetManager?.NotifyQuickLookSurfaceUnavailable(this);
        _isDisposed = true;
        _isReadyForReuse = false;
        _lifetimeCancellation.Cancel();
        CancelAndResetTrackedImport();
        _lifetimeCancellation.Dispose();
        if (_isImportBusy)
        {
            SetImportBusy(false);
        }
        if (_itemRenameTarget is not null)
        {
            CancelItemRename();
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DisposeScrollBarActivityTracking();
        ActualThemeChanged -= FileSurfaceContent_ActualThemeChanged;
        ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
        ViewModel.Dispose();
    }
}
