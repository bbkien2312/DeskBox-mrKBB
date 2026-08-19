using DeskBox.Controls;
using DeskBox.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Tests;

public sealed class VirtualShortcutDragProviderTests
{
    [Fact]
    public void CanProvide_AcceptsOneOrMoreExistingShortcuts()
    {
        Assert.True(VirtualShortcutDragProvider.CanProvide(
            [@"E:\DeskBox\my\One.lnk", @"E:\DeskBox\my\Two.LNK"],
            _ => true));
    }

    [Fact]
    public void CanProvide_RejectsMixedFileTypes()
    {
        Assert.False(VirtualShortcutDragProvider.CanProvide(
            [@"E:\DeskBox\my\One.lnk", @"E:\DeskBox\my\Readme.txt"],
            _ => true));
    }

    [Fact]
    public void CanProvide_RejectsMissingShortcut()
    {
        Assert.False(VirtualShortcutDragProvider.CanProvide(
            [@"E:\DeskBox\my\Missing.lnk"],
            _ => false));
    }

    [Fact]
    public void CanProvide_RejectsEmptySelection()
    {
        Assert.False(VirtualShortcutDragProvider.CanProvide(
            [],
            _ => true));
    }

    [Fact]
    public void RequiresStorageBrokerBypass_AcceptsBlockedOrUnreadableShortcuts()
    {
        string[] paths = [@"E:\DeskBox\my\Hidden.lnk"];

        Assert.True(VirtualShortcutDragProvider.RequiresStorageBrokerBypass(
            paths,
            _ => true,
            _ => System.IO.FileAttributes.Hidden |
                 System.IO.FileAttributes.System));
        Assert.True(VirtualShortcutDragProvider.RequiresStorageBrokerBypass(
            paths,
            _ => true,
            _ => throw new UnauthorizedAccessException()));
        Assert.False(VirtualShortcutDragProvider.RequiresStorageBrokerBypass(
            paths,
            _ => true,
            _ => System.IO.FileAttributes.Archive));
    }

    [Fact]
    public void Provider_AdvertisesOnDemandStorageItems()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/VirtualShortcutDragProvider.cs"));

        Assert.Contains(
            "SetDataProvider(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StandardDataFormats.StorageItems",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StorageFile.CreateStreamedFileAsync",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DragPackage_BypassesSynchronousStorageBroker_ForShortcuts()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string shortcutPath = Path.Combine(tempDirectory, "Hidden app.lnk");
        File.WriteAllBytes(shortcutPath, [0x4C, 0x00, 0x00, 0x00]);
        File.SetAttributes(
            shortcutPath,
            File.GetAttributes(shortcutPath) |
            System.IO.FileAttributes.Hidden |
            System.IO.FileAttributes.System);

        try
        {
            var dataPackage = new DataPackage();
            int brokerCallCount = 0;
            bool prepared = FileItemDragPackage.TryPrepare(
                dataPackage,
                [new WidgetItem { Path = shortcutPath, IsShortcut = true }],
                "widget-test",
                _ =>
                {
                    brokerCallCount++;
                    return Array.Empty<IStorageItem>();
                },
                _ => "Hidden app.lnk",
                out FileItemDragPackageResult result);

            Assert.True(prepared);
            Assert.True(result.UsesVirtualStorageItems);
            Assert.False(result.HasStorageItems);
            Assert.Equal(0, brokerCallCount);
        }
        finally
        {
            if (File.Exists(shortcutPath))
            {
                File.SetAttributes(
                    shortcutPath,
                    System.IO.FileAttributes.Normal);
            }
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void DragPackage_FallsBackToVirtualStorageItemsWhenBrokerReturnsNothing()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string shortcutPath = Path.Combine(tempDirectory, "App.lnk");
        File.WriteAllBytes(shortcutPath, [0x4C, 0x00, 0x00, 0x00]);

        try
        {
            var dataPackage = new DataPackage();
            int brokerCallCount = 0;
            bool prepared = FileItemDragPackage.TryPrepare(
                dataPackage,
                [new WidgetItem { Path = shortcutPath, IsShortcut = true }],
                "widget-test",
                _ =>
                {
                    brokerCallCount++;
                    return Array.Empty<IStorageItem>();
                },
                _ => "App.lnk",
                out FileItemDragPackageResult result);

            Assert.True(prepared);
            Assert.True(result.UsesVirtualStorageItems);
            Assert.False(result.HasStorageItems);
            Assert.Equal(1, brokerCallCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
