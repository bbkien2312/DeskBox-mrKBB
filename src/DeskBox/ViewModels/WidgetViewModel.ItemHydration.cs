using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    private void EnsureFolderBackedConfig()
    {
        if (!string.IsNullOrWhiteSpace(Config.MappedFolderPath))
        {
            Config.MappedFolderPath = Path.GetFullPath(Config.MappedFolderPath);
            return;
        }

        Config.FollowsDefaultStoragePath = true;
        Config.ManagedFolderName = string.IsNullOrWhiteSpace(Config.ManagedFolderName)
            ? CreateAvailableManagedFolderName(Config.Name, Config.Id)
            : Config.ManagedFolderName;
        Config.MappedFolderPath = Path.Combine(
            SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath),
            Config.ManagedFolderName);
        Directory.CreateDirectory(Config.MappedFolderPath);
        Config.Items.Clear();
        ResetAddedAtTracking();
        _settingsService.SaveDebounced();
    }

    private string CreateAvailableManagedFolderName(string displayName, string widgetId)
    {
        string baseFolderName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(baseFolderName))
        {
            baseFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var usedNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
                             !string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            .Select(widget => widget.ManagedFolderName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string candidate = baseFolderName;
        int suffix = 2;
        while (usedNames.Contains(candidate) || Directory.Exists(Path.Combine(rootPath, candidate)))
        {
            candidate = $"{baseFolderName} ({suffix++})";
        }

        return candidate;
    }

    private async Task<bool> LoadFolderContentsAsync(
        string folderPath,
        bool clearIconCacheBeforeHydration = false,
        CancellationToken cancellationToken = default,
        Action? beforeItemsReplaced = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.LoadFolderContents",
            $"id={Config.Id} path={folderPath}");

        IReadOnlyList<WidgetItem> items;
        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            FolderEnumerationResult userResult = await Task.Run(
                () => _fileService.EnumerateDirectoryForRefreshAsync(
                    userDesktop,
                    hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                    showImageFilesAsIcons: _showImageFilesAsIcons,
                    showFileExtensions: _showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                    loadIcons: false,
                    loadFolderItemCounts: false),
                cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FolderEnumerationResult publicResult = await Task.Run(
                () => _fileService.EnumerateDirectoryForRefreshAsync(
                    publicDesktop,
                    hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                    showImageFilesAsIcons: _showImageFilesAsIcons,
                    showFileExtensions: _showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                    loadIcons: false,
                    loadFolderItemCounts: false),
                cancellationToken).WaitAsync(cancellationToken);

            if (!FolderSnapshotStatusPolicy.IsSuccessful(userResult.Status) ||
                !FolderSnapshotStatusPolicy.IsSuccessful(publicResult.Status))
            {
                App.Log(
                    $"[FolderRefresh] Desktop snapshot incomplete; retaining existing items " +
                    $"user={userResult.Status} public={publicResult.Status}");
                return false;
            }

            items = userResult.Items.Concat(publicResult.Items)
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => !item.IsFolder)
                .ThenBy(item => item.Name, NaturalStringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else
        {
            FolderEnumerationResult result = await Task.Run(
                () => _fileService.EnumerateDirectoryForRefreshAsync(
                    folderPath,
                    hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                    showImageFilesAsIcons: _showImageFilesAsIcons,
                    showFileExtensions: _showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                    loadIcons: false,
                    loadFolderItemCounts: false),
                cancellationToken).WaitAsync(cancellationToken);
            if (!FolderSnapshotStatusPolicy.IsSuccessful(result.Status))
            {
                App.Log(
                    $"[FolderRefresh] Snapshot {result.Status}; retaining existing items for '{folderPath}'");
                return false;
            }

            items = result.Items;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ApplyPersistedAddedTimes(items);
        cancellationToken.ThrowIfCancellationRequested();
        beforeItemsReplaced?.Invoke();
        SyncFolderItems(items);
        SortItems();
        if (clearIconCacheBeforeHydration)
        {
            ClearCurrentItemIconCache();
        }

        StartItemHydration();
        return true;
    }

    private void SyncFolderItems(IReadOnlyList<WidgetItem> refreshedItems)
    {
        var existingByPath = Items
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var refreshedPaths = refreshedItems
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Config.SortMode == WidgetSortMode.Manual)
        {
            List<string> liveOrderPaths = Items.Select(item => item.Path).ToList();
            List<WidgetItem> snapshotItems = refreshedItems
                .Select(refreshedItem =>
                {
                    if (existingByPath.TryGetValue(refreshedItem.Path, out WidgetItem? existingItem))
                    {
                        ApplyRuntimeItemData(existingItem, refreshedItem);
                        return existingItem;
                    }

                    return refreshedItem;
                })
                .ToList();
            IReadOnlyList<WidgetItem> reconciled = WidgetManualOrderPolicy.Reconcile(
                snapshotItems,
                liveOrderPaths,
                Config.Items,
                item => item.Path);

            ApplyReconciledManualOrder(reconciled);
            NormalizeSortOrder();
            PersistManualOrderSnapshotIfChanged();
            return;
        }

        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!refreshedPaths.Contains(Items[index].Path))
            {
                Items.RemoveAt(index);
            }
        }

        for (int targetIndex = 0; targetIndex < refreshedItems.Count; targetIndex++)
        {
            var refreshedItem = refreshedItems[targetIndex];
            if (!existingByPath.TryGetValue(refreshedItem.Path, out var existingItem))
            {
                Items.Insert(targetIndex, refreshedItem);
                continue;
            }

            ApplyRuntimeItemData(existingItem, refreshedItem);
            int currentIndex = Items.IndexOf(existingItem);
            if (currentIndex < 0)
            {
                Items.Insert(targetIndex, existingItem);
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }

        NormalizeSortOrder();
    }

    private void ApplyReconciledManualOrder(IReadOnlyList<WidgetItem> reconciled)
    {
        var retained = reconciled.ToHashSet();
        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!retained.Contains(Items[index]))
            {
                Items.RemoveAt(index);
            }
        }

        for (int targetIndex = 0; targetIndex < reconciled.Count; targetIndex++)
        {
            WidgetItem item = reconciled[targetIndex];
            int currentIndex = Items.IndexOf(item);
            if (currentIndex < 0)
            {
                Items.Insert(targetIndex, item);
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }
    }

    private void StartItemHydration()
    {
        int generation = Interlocked.Increment(ref _itemHydrationGeneration);
        _ = HydrateInitialIconsAsync(generation);
        _ = HydrateFolderItemCountsAsync(generation);
        _ = HydrateShortcutTargetsThenShellKindsAsync(generation);
    }

    private void ClearCurrentItemIconCache()
    {
        foreach (var item in Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                item.Icon = null;
                _fileService.ClearIconCache(item.Path, _hideShortcutArrowOverlay, _showImageFilesAsIcons);
            }
        }
    }

    private void RefreshAllIcons()
    {
        ClearCurrentItemIconCache();
        StartItemHydration();
    }

    /// <summary>
    /// Hydrates only the initial visible-size budget.  Remaining items are
    /// hydrated by FileItemSurface when WinUI realizes them in the viewport.
    /// Avoiding a whole-list retry is important for boxes with many shortcuts.
    /// </summary>
    private Task HydrateInitialIconsAsync(int generation)
    {
        return HydrateIconsAsync(generation, clearCacheBeforeLoad: false, InitialIconHydrationLimit);
    }

    internal void EnsureVisibleItemIcon(WidgetItem? item)
    {
        if (_isDisposed || item is null || item.Icon is not null || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        lock (_visibleIconHydrationItems)
        {
            if (!_visibleIconHydrationItems.Add(item))
            {
                return;
            }
        }

        int generation = Volatile.Read(ref _itemHydrationGeneration);
        _ = HydrateVisibleItemIconAsync(item, generation);
    }

    internal void MarkItemSurfaceVisible(WidgetItem? item)
    {
        if (item is null)
        {
            return;
        }

        lock (_realizedItemSurfaces)
        {
            _realizedItemSurfaces.Add(item);
        }

        EnsureVisibleItemIcon(item);
    }

    internal void MarkItemSurfaceNotVisible(WidgetItem? item)
    {
        if (item is null)
        {
            return;
        }

        lock (_realizedItemSurfaces)
        {
            _realizedItemSurfaces.Remove(item);
        }

        _ = ReleaseUnrealizedItemIconAsync(item, Volatile.Read(ref _itemHydrationGeneration));
    }

    private async Task ReleaseUnrealizedItemIconAsync(WidgetItem item, int generation)
    {
        // WinUI can briefly recycle a container during a layout pass.  A short
        // delay avoids flicker while still releasing thumbnails after scrolling.
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (_isDisposed || generation != Volatile.Read(ref _itemHydrationGeneration))
        {
            return;
        }

        lock (_realizedItemSurfaces)
        {
            if (_realizedItemSurfaces.Contains(item))
            {
                return;
            }
        }

        void Release()
        {
            if (generation == Volatile.Read(ref _itemHydrationGeneration) &&
                Items.Contains(item))
            {
                item.Icon = null;
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Release();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Release);
        }
    }

    private async Task HydrateVisibleItemIconAsync(WidgetItem item, int generation)
    {
        try
        {
            var result = await HydrateIconAsync(item, generation, clearCacheBeforeLoad: false);
            if (result.Item is not null &&
                generation == Volatile.Read(ref _itemHydrationGeneration) &&
                Items.Contains(result.Item))
            {
                SetItemIcon(result.Item, result.Icon, result.Item.Path, generation);
            }
        }
        finally
        {
            lock (_visibleIconHydrationItems)
            {
                _visibleIconHydrationItems.Remove(item);
            }
        }
    }

    private async Task HydrateIconsAsync(int generation, bool clearCacheBeforeLoad, int maximumItems = int.MaxValue)
    {
        var items = Items
            .Where(item => item.Icon is null)
            .OrderByDescending(item => item.IsShortcut)
            .ThenBy(item => item.SortOrder)
            .Take(Math.Max(0, maximumItems))
            .ToList();

        for (int start = 0; start < items.Count; start += IconHydrationBatchSize)
        {
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            var batch = items
                .Skip(start)
                .Take(IconHydrationBatchSize)
                .Where(item => Items.Contains(item) && !string.IsNullOrWhiteSpace(item.Path))
                .Select(item => HydrateIconAsync(item, generation, clearCacheBeforeLoad))
                .ToArray();
            var results = await Task.WhenAll(batch);

            foreach (var (item, icon) in results)
            {
                if (item is null)
                {
                    continue;
                }

                if (generation != Volatile.Read(ref _itemHydrationGeneration) ||
                    !Items.Contains(item))
                {
                    return;
                }

                SetItemIcon(item, icon, item.Path, generation);
            }

            await Task.Yield();
        }
    }

    private async Task<(WidgetItem? Item, Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon)> HydrateIconAsync(
        WidgetItem item,
        int generation,
        bool clearCacheBeforeLoad)
    {
        string path = item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, null);
        }

        try
        {
            if (clearCacheBeforeLoad)
            {
                _fileService.ClearIconCache(path, _hideShortcutArrowOverlay, _showImageFilesAsIcons);
            }

            var icon = await _fileService.GetIconAsync(
                path,
                _hideShortcutArrowOverlay,
                _showImageFilesAsIcons,
                _iconDecodePixelWidth);
            return (item, icon);
        }
        catch (Exception ex)
        {
            App.Log($"[IconHydration] Failed to load icon for '{path}' in widget '{Name}' ({Config.Id}): {ex.Message}");
            return (item, null);
        }
    }

    private async Task HydrateFolderItemCountsAsync(int generation)
    {
        var folders = Items
            .Where(item => item.IsFolder && !item.IsFolderItemCountLoaded)
            .ToList();
        int processed = 0;

        foreach (var item in folders)
        {
            if (generation != Volatile.Read(ref _itemHydrationGeneration) ||
                !Items.Contains(item) ||
                !Directory.Exists(item.Path))
            {
                return;
            }

            string path = item.Path;
            try
            {
                int count = await _fileService.CountVisibleChildrenAsync(path);
                SetFolderItemCount(item, count, path, generation);
            }
            catch (Exception ex)
            {
                // Keep the last known count and leave the item retryable. A
                // transient UNC/provider failure must not become a cached zero.
                MarkFolderItemCountUnavailable(item, path, generation, ex);
            }
            processed++;

            if (processed % FolderCountHydrationBatchSize == 0)
            {
                await Task.Delay(FolderCountHydrationYieldMs);
            }
        }
    }

    private async Task HydrateShortcutTargetsThenShellKindsAsync(int generation)
    {
        await HydrateShortcutTargetsAsync(generation);
        await HydrateShellKindsAsync(generation);
    }

    private async Task HydrateShortcutTargetsAsync(int generation)
    {
        var shortcuts = Items
            .Where(item => item.IsShortcut)
            .OrderBy(item => item.SortOrder)
            .ToList();

        for (int start = 0; start < shortcuts.Count; start += ShortcutTargetHydrationBatchSize)
        {
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            var batch = shortcuts
                .Skip(start)
                .Take(ShortcutTargetHydrationBatchSize)
                .Where(item => Items.Contains(item) && !string.IsNullOrWhiteSpace(item.Path))
                .Select(async item =>
                {
                    string expectedPath = item.Path;
                    string targetPath = await _fileService.GetStoredShortcutTargetAsync(expectedPath);
                    return (Item: item, ExpectedPath: expectedPath, TargetPath: targetPath);
                })
                .ToArray();
            var results = await Task.WhenAll(batch);

            foreach (var result in results)
            {
                SetShortcutTarget(
                    result.Item,
                    result.TargetPath,
                    result.ExpectedPath,
                    generation);
            }

            await Task.Yield();
        }
    }

    private async Task HydrateShellKindsAsync(int generation)
    {
        var items = Items
            .Where(item => !item.IsShellKindLoaded)
            .OrderBy(item => item.SortOrder)
            .ToList();

        for (int start = 0; start < items.Count; start += ShellKindHydrationBatchSize)
        {
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            var batch = items
                .Skip(start)
                .Take(ShellKindHydrationBatchSize)
                .Where(item => Items.Contains(item) && !string.IsNullOrWhiteSpace(item.Path))
                .Select(async item =>
                {
                    string expectedPath = item.Path;
                    string kind = await _fileService.GetShellKindAsync(item);
                    return (Item: item, ExpectedPath: expectedPath, Kind: kind);
                })
                .ToArray();
            var results = await Task.WhenAll(batch);

            foreach (var result in results)
            {
                SetShellKind(
                    result.Item,
                    result.Kind,
                    result.ExpectedPath,
                    generation);
            }

            await Task.Yield();
        }
    }

    private void SetItemIcon(
        WidgetItem item,
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage? icon,
        string expectedPath,
        int generation)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.Icon = icon;
            }

            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.Icon = icon;
            }
        });
    }

    private void SetFolderItemCount(WidgetItem item, int count, string expectedPath, int generation)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.FolderItemCount = count;
                item.IsFolderItemCountLoaded = true;
            }

            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.FolderItemCount = count;
                item.IsFolderItemCountLoaded = true;
            }
        });
    }

    private void SetShortcutTarget(
        WidgetItem item,
        string targetPath,
        string expectedPath,
        int generation)
    {
        void Apply()
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.TargetPath = targetPath;
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
    }

    private void MarkFolderItemCountUnavailable(
        WidgetItem item,
        string expectedPath,
        int generation,
        Exception exception)
    {
        App.LogVerbose(
            $"[FolderRefresh] Folder count unavailable for '{expectedPath}': " +
            exception.Message);

        void Apply()
        {
            if (!CanApplyHydrationResult(item, expectedPath, generation))
            {
                return;
            }

            // Preserve the previous value and deliberately keep the loaded
            // flag false so the next hydration generation retries it.
            item.IsFolderItemCountLoaded = false;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
    }

    private void SetShellKind(WidgetItem item, string kind, string expectedPath, int generation)
    {
        void Apply()
        {
            if (!CanApplyHydrationResult(item, expectedPath, generation))
            {
                return;
            }

            bool categoryMayChange = !string.Equals(item.ShellKind, kind, StringComparison.OrdinalIgnoreCase);
            item.ShellKind = kind;
            item.IsShellKindLoaded = true;
            if (categoryMayChange && FileStackGroupBy == SettingsService.FileStackGroupByKind)
            {
                QueueStackDisplayRebuild();
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
    }

    private bool CanApplyHydrationResult(WidgetItem item, string expectedPath, int generation)
    {
        return generation == Volatile.Read(ref _itemHydrationGeneration) &&
               Items.Contains(item) &&
               string.Equals(item.Path, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshShortcutIconsAsync()
    {
        int shortcutCount = Items.Count(item => item.IsShortcut);
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshShortcutIcons",
            $"id={Config.Id} count={shortcutCount}");

        foreach (var item in Items.Where(item => item.IsShortcut))
        {
            item.Icon = await _fileService.GetIconAsync(
                item.Path,
                _hideShortcutArrowOverlay,
                _showImageFilesAsIcons,
                _iconDecodePixelWidth);
        }
    }

    private void RefreshItemDisplayNames()
    {
        foreach (var item in Items)
        {
            item.Name = FileService.GetDisplayName(
                item.Path,
                item.IsFolder,
                _showFileExtensions,
                _hideShortcutExtensionWhenShowingFileExtensions);
        }

        SortItems();
    }
}
