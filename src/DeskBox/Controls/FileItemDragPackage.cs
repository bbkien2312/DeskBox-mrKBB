using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls;

public readonly record struct FileItemDragPackageResult(
    IReadOnlyList<string> SourcePaths,
    bool HasStorageItems,
    bool UsesVirtualStorageItems);

/// <summary>
/// Creates the common file-item drag payload. Hosts remain responsible for
/// deciding which items are dragged and how the completed drop is reconciled.
/// </summary>
public static class FileItemDragPackage
{
    public static IReadOnlyList<WidgetItem> ResolveDraggedItems(
        IReadOnlyList<WidgetItem> eventItems,
        IReadOnlyList<WidgetItem> selectedItems)
    {
        WidgetItem[] distinctEventItems = eventItems.Distinct().ToArray();
        WidgetItem[] distinctSelectedItems = selectedItems.Distinct().ToArray();
        if (distinctSelectedItems.Length <= 1 || distinctEventItems.Length == 0)
        {
            return distinctEventItems;
        }

        // Some WinUI ListView input paths report only the pointer anchor in
        // DragItemsStarting even though it belongs to a larger selection. The
        // visible selection is authoritative whenever the event anchor is one
        // of its members.
        return distinctEventItems.Any(distinctSelectedItems.Contains)
            ? distinctSelectedItems
            : distinctEventItems;
    }

    public static bool TryPrepare(
        DataPackage dataPackage,
        IReadOnlyList<WidgetItem> draggedItems,
        string sourceWidgetId,
        Func<IEnumerable<string>, IReadOnlyList<IStorageItem>> getStorageItems,
        Func<IReadOnlyList<string>, string> getTitle,
        out FileItemDragPackageResult result)
    {
        result = default;
        if (draggedItems.Count == 0)
        {
            return false;
        }

        string[] sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            return false;
        }

        dataPackage.RequestedOperation =
            DataPackageOperation.Copy |
            DataPackageOperation.Move |
            DataPackageOperation.Link;

        // WinRT's StorageFile broker can reject shortcuts carrying Hidden or
        // System attributes with UNABLE_TO_MASK_PATH. More importantly, this
        // event is raised on the UI STA, so synchronously waiting for that
        // broker can deadlock the drag/drop message loop. Shortcuts already
        // have a streamed provider that does not need the broker; choose it
        // before attempting any synchronous StorageItems lookup.
        bool usesVirtualStorageItems = false;
        IReadOnlyList<IStorageItem> storageItems = [];
        if (VirtualShortcutDragProvider.RequiresStorageBrokerBypass(
                sourcePaths) &&
            VirtualShortcutDragProvider.TryAttach(
                dataPackage,
                sourcePaths))
        {
            usesVirtualStorageItems = true;
            App.LogVerbose(
                $"[DragStart] Bypassed WinRT StorageItems broker for " +
                $"virtual shortcuts paths={sourcePaths.Length}");
        }
        else
        {
            storageItems = getStorageItems(sourcePaths);
            if (storageItems.Count == sourcePaths.Length)
            {
                dataPackage.SetStorageItems(storageItems, readOnly: false);
            }
            else if (VirtualShortcutDragProvider.CanProvide(sourcePaths) &&
                     VirtualShortcutDragProvider.TryAttach(
                         dataPackage,
                         sourcePaths))
            {
                // Some .lnk files are readable from the filesystem but are
                // rejected by the WinRT broker. Explorer needs a complete
                // StorageItems payload for a direct drop into another folder;
                // path metadata alone only works with DeskBox's Desktop
                // fallback path.
                usesVirtualStorageItems = true;
                App.LogVerbose(
                    $"[DragStart] Fell back to virtual shortcut " +
                    $"StorageItems paths={sourcePaths.Length} " +
                    $"brokerItems={storageItems.Count}");
            }
            else if (storageItems.Count > 0)
            {
                dataPackage.SetStorageItems(storageItems, readOnly: false);
            }
        }

        dataPackage.Properties[DeskBoxDragData.SourceWidgetIdProperty] =
            sourceWidgetId;
        dataPackage.Properties[DeskBoxDragData.SourcePathsProperty] =
            sourcePaths;
        dataPackage.Properties[
            DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        dataPackage.Properties.Title = getTitle(sourcePaths);
        dataPackage.SetText(string.Join(Environment.NewLine, sourcePaths));

        result = new FileItemDragPackageResult(
            sourcePaths,
            storageItems.Count > 0,
            usesVirtualStorageItems);
        return true;
    }
}
