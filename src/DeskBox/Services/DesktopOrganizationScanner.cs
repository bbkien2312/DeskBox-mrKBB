using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationScanner
{
    // Files up to and including 100 MiB are eligible for quick organization.
    // Keep the comparison in CreateSnapshot strictly greater-than so the
    // advertised limit is inclusive for the boundary file.
    public const long SlowItemThresholdBytes = 100L * 1024 * 1024;
    public const long QuickBatchSizeLimitBytes = 100L * 1024 * 1024;
    public const int QuickBatchItemLimit = 200;

    private static readonly string[] TemporarySuffixes =
    [
        ".tmp",
        ".temp",
        ".part",
        ".partial",
        ".crdownload",
        ".download",
        ".opdownload",
        ".aria2",
        ".!ut",
        ".bc!"
    ];

    private readonly DesktopOrganizationClassifier _classifier;
    private readonly Func<string> _desktopPathProvider;
    private readonly Func<string> _publicDesktopPathProvider;

    public DesktopOrganizationScanner(
        DesktopOrganizationClassifier classifier,
        Func<string>? desktopPathProvider = null,
        Func<string>? publicDesktopPathProvider = null)
    {
        _classifier = classifier;
        _desktopPathProvider = desktopPathProvider ??
            (() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        _publicDesktopPathProvider = publicDesktopPathProvider ??
            (() => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
    }

    public Task<DesktopOrganizationScanResult> ScanAsync(
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? additionalRoots = null,
        bool includePublicDesktopItems = true,
        bool includeFolders = false)
    {
        string[] roots = additionalRoots?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray() ?? [];
        return Task.Run(
            () => Scan(includeSlowItems, cancellationToken, roots, includePublicDesktopItems, includeFolders),
            cancellationToken);
    }

    internal DesktopOrganizationFileSnapshot CreateAutoOrganizationSnapshot(string path) =>
        CreateSnapshot(
            path,
            includeSlowItems: false,
            includeFolders: true);

    private DesktopOrganizationScanResult Scan(
        bool includeSlowItems,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string> additionalRoots,
        bool includePublicDesktopItems,
        bool includeFolders)
    {
        string desktopPath = Path.GetFullPath(_desktopPathProvider());
        string publicDesktopPath = NormalizeOptionalPath(_publicDesktopPathProvider());
        var items = new List<DesktopOrganizationFileSnapshot>();

        var roots = new List<string>();
        if (Directory.Exists(desktopPath))
        {
            roots.Add(desktopPath);
        }
        if (includePublicDesktopItems &&
            !string.IsNullOrWhiteSpace(publicDesktopPath) &&
            !roots.Contains(publicDesktopPath, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(publicDesktopPath);
        }

        foreach (string root in additionalRoots
                     .Concat(roots)
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(CreateSnapshot(path, includeSlowItems, includeFolders));
            }
        }

        if (!includeSlowItems)
        {
            ApplyQuickBatchLimit(items);
        }

        return new DesktopOrganizationScanResult
        {
            DesktopPath = desktopPath,
            Items = items
        };
    }

    private static void ApplyQuickBatchLimit(
        IList<DesktopOrganizationFileSnapshot> items)
    {
        long acceptedBytes = 0;
        int acceptedCount = 0;
        foreach (int index in items
                     .Select((item, index) => new { item, index })
                     .Where(entry => entry.item.IsEligible)
                     .OrderBy(entry => entry.item.Size)
                     .ThenBy(entry => entry.item.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => entry.index))
        {
            DesktopOrganizationFileSnapshot item = items[index];
            if (acceptedCount >= QuickBatchItemLimit ||
                acceptedBytes + item.Size > QuickBatchSizeLimitBytes)
            {
                items[index] = item with
                {
                    ExclusionReason = DesktopOrganizationExclusionReason.BatchLimit
                };
                continue;
            }

            acceptedCount++;
            acceptedBytes += item.Size;
        }
    }

    private DesktopOrganizationFileSnapshot CreateSnapshot(
        string path,
        bool includeSlowItems,
        bool includeFolders)
    {
        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileName(fullPath);
        long size = 0;
        DateTime lastWriteTimeUtc = DateTime.MinValue;
        DesktopOrganizationClassification classification =
            new(DesktopOrganizationCategoryIds.Other, null, string.Empty);
        DesktopOrganizationExclusionReason reason;

        try
        {
            FileAttributes attributes = File.GetAttributes(fullPath);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isSystemLink = !isDirectory && IsSystemLink(fullPath);
            classification = _classifier.Classify(fullPath, isDirectory, isSystemLink);
            bool isHiddenOrSystem = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                                    name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
            bool isTemporary = (attributes & FileAttributes.Temporary) != 0 ||
                               HasTemporarySuffix(name);

            reason = isDirectory && !includeFolders
                ? DesktopOrganizationExclusionReason.Folder
                : isHiddenOrSystem
                    ? DesktopOrganizationExclusionReason.HiddenOrSystem
                    : (attributes & FileAttributes.ReparsePoint) != 0
                        ? DesktopOrganizationExclusionReason.ReparsePoint
                        : (attributes & FileAttributes.Offline) != 0
                            ? DesktopOrganizationExclusionReason.OfflinePlaceholder
                            : isTemporary
                                ? DesktopOrganizationExclusionReason.TemporaryOrDownloading
                                : DesktopOrganizationExclusionReason.None;

            if (!isDirectory)
            {
                var file = new FileInfo(fullPath);
                size = file.Length;
                lastWriteTimeUtc = file.LastWriteTimeUtc;
                if (reason == DesktopOrganizationExclusionReason.None &&
                    !includeSlowItems &&
                    size > SlowItemThresholdBytes)
                {
                    reason = DesktopOrganizationExclusionReason.SlowItem;
                }
            }
            else
            {
                var directory = new DirectoryInfo(fullPath);
                lastWriteTimeUtc = directory.LastWriteTimeUtc;
            }
        }
        catch
        {
            reason = DesktopOrganizationExclusionReason.Unavailable;
        }

        return new DesktopOrganizationFileSnapshot(
            fullPath,
            name,
            classification.Extension,
            size,
            lastWriteTimeUtc,
            classification.CategoryId,
            classification.SubtypeId,
            reason);
    }

    private static bool IsSystemLink(string path)
    {
        string extension = DesktopOrganizationClassifier.NormalizeExtension(Path.GetExtension(path));
        if (extension is not ".lnk" and not ".url" and not ".appref-ms")
        {
            return false;
        }

        try
        {
            string? target = ShortcutHelper.Resolve(path)?.TargetPath;
            return !string.IsNullOrWhiteSpace(target) &&
                   (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("::{", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasTemporarySuffix(string name) =>
        TemporarySuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeOptionalPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static bool IsUnderPath(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(
            $"{normalizedParent}{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }
}
