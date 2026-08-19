using System.Drawing;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using DeskBox.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Helpers;

/// <summary>
/// Extracts native Windows file and folder icons using the Win32 Shell API.
/// </summary>
public static class IconHelper
{
    // Keep thumbnails enabled, but cap the resident process caches more
    // tightly. The visible WidgetItem keeps its current BitmapImage alive, so
    // evicting an inactive cache entry does not make the displayed thumbnail
    // disappear; it only prevents repeated background refreshes from growing
    // the global cache indefinitely.
    private const int MaxIconCacheEntries = 96;
    private const long MaxIconCacheBytes = 12L * 1024 * 1024;
    private const int MaxDecodedBitmapCacheEntries = 64;
    private const long MaxDecodedBitmapCacheBytes = 16L * 1024 * 1024;
    private const int MaxThumbnailCacheEntries = 32;
    private const long MaxThumbnailCacheBytes = 8L * 1024 * 1024;
    private const string SharedCacheScope = "shared";

    // Icon bytes cache: path → PNG bytes (for shell icons, not image thumbnails)
    private static readonly ConcurrentDictionary<string, byte[]?> s_iconBytesCache = new(StringComparer.OrdinalIgnoreCase);

    // Bitmap cache for shell icons (not image thumbnails)
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> s_bitmapImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_bitmapCacheLock = new();
    private static readonly LinkedList<string> s_bitmapLru = new();
    private static readonly Dictionary<string, LinkedListNode<string>> s_bitmapLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_bitmapEstimatedBytes = new(StringComparer.OrdinalIgnoreCase);
    private static long s_totalBitmapEstimatedBytes;

    // ── Media thumbnail LRU cache (separate from icon cache) ──────
    // Uses a linked list + dictionary for simple LRU eviction.
    private static readonly object s_thumbLock = new();
    private static readonly LinkedList<string> s_thumbLru = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> s_thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> s_thumbEstimatedBytes = new(StringComparer.OrdinalIgnoreCase);
    private static long s_totalThumbnailEstimatedBytes;

    internal readonly record struct IdleIconCacheReleaseResult(
        int ReleasedThumbnails,
        int ReleasedDecodedBitmaps,
        int ReleasedIconByteEntries,
        long ReleasedEstimatedBytes)
    {
        public bool ReleasedAnything =>
            ReleasedThumbnails > 0 ||
            ReleasedDecodedBitmaps > 0 ||
            ReleasedIconByteEntries > 0;
    }

    private static readonly SemaphoreSlim s_iconLoadSemaphore = new(4, 4);
    private static readonly SemaphoreSlim s_thumbLoadSemaphore = new(2, 2);

    private sealed record IconSource(string Path, int IconIndex = 0, bool UsesExplicitIconIndex = false);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SYSICONINDEX = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHDefExtractIcon(
        string pszIconFile,
        int iIcon,
        uint uFlags,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        uint nIconSize); // MAKELONG(cxSmall, cxLarge) — low word = small, high word = large

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        ref IntPtr ppv);

    // Image list size flags for SHGetImageList
    private const int SHIL_EXTRALARGE = 0x2; // 48x48
    private const int SHIL_JUMBO = 0x4;      // 256x256 (Vista+)

    private static readonly Guid s_iidIImageList = new("46EB5926-582E-4017-9FDF-E899822AA8B3");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E899822AA8B3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int GetImageCount();

        [PreserveSig]
        int GetImageRect(int i, ref RECT pRect);

        [PreserveSig]
        int GetIcon(int i, uint flags, ref IntPtr picon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private const uint ILD_TRANSPARENT = 0x00000001;

    // Shell change notification for invalidating the icon cache.
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const long ShellInvalidateThrottleMs = 500;
    private static long s_lastShellInvalidateMs;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Asynchronously retrieve the native Windows shell icon for the given path.
    /// For image and video files, returns a thumbnail preview instead of the generic icon.
    /// </summary>
    public static async Task<BitmapImage?> GetIconAsync(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        int decodePixelWidth = 0,
        string cacheScope = SharedCacheScope)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.GetIcon", $"path={path}");
        var dispatcher = App.UiDispatcherQueue;
        if (dispatcher == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalizedCacheScope = string.IsNullOrWhiteSpace(cacheScope)
            ? SharedCacheScope
            : cacheScope.Trim();

        if (!showImageFilesAsIcons && IsMediaFile(path))
        {
            var thumbnail = await LoadMediaThumbnailAsync(
                dispatcher,
                path,
                decodePixelWidth,
                normalizedCacheScope);
            if (thumbnail is not null)
            {
                return thumbnail;
            }
        }

        IconSource iconSource = ResolveIconSource(path, hideShortcutArrowOverlay);
        if (string.IsNullOrWhiteSpace(iconSource.Path))
        {
            return null;
        }

        string cacheKey = BuildCacheKey(path, iconSource);
        int normalizedDecodePixelWidth = Math.Clamp(decodePixelWidth, 0, 256);
        string bitmapCacheKey = $"{normalizedCacheScope}|{cacheKey}:decode={normalizedDecodePixelWidth}";
        Task<BitmapImage?> bitmapTask = s_bitmapImageCache.GetOrAdd(
            bitmapCacheKey,
            _ => LoadBitmapImageAsync(
                dispatcher,
                iconSource,
                cacheKey,
                bitmapCacheKey,
                normalizedDecodePixelWidth));
        TrackDecodedBitmap(
            bitmapCacheKey,
            EstimateDecodedBitmapBytes(normalizedDecodePixelWidth));
        return await bitmapTask;
    }

    public static bool IsImageFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tiff" or ".tif" or ".heic" or ".heif";
    }

    public static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".m4v" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".webm"
            or ".mpeg" or ".mpg" or ".mpe" or ".3gp" or ".3g2" or ".mts" or ".m2ts"
            or ".ts" or ".vob" or ".flv" or ".ogv";
    }

    public static bool IsMediaFile(string path)
    {
        return IsImageFile(path) || IsVideoFile(path);
    }

    // ── Media thumbnail loading with LRU cache ───────────────────

    private static async Task<BitmapImage?> LoadMediaThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth,
        string cacheScope)
    {
        int normalizedDecodePixelWidth = decodePixelWidth <= 0
            ? 96
            : Math.Clamp(decodePixelWidth, 24, 256);
        string cacheKey = $"thumb:{cacheScope}|{path}:{GetFileIconVersion(path)}:decode={normalizedDecodePixelWidth}";

        Task<BitmapImage?>? cachedTask = null;
        lock (s_thumbLock)
        {
            // Update diagnostics
            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;

            if (s_thumbCache.TryGetValue(cacheKey, out var cached))
            {
                // Move to front of LRU
                RemoveThumbnailLruKey(cacheKey);
                s_thumbLru.AddFirst(cacheKey);
                cachedTask = cached;
            }
        }

        if (cachedTask is not null)
        {
            return await cachedTask;
        }

        // Remove stale entry for the same path but different version
        RemoveStaleThumbnailEntries(cacheScope, path, cacheKey);

        Task<BitmapImage?> task;
        lock (s_thumbLock)
        {
            if (!s_thumbCache.TryGetValue(cacheKey, out task!))
            {
                task = CreateMediaThumbnailAsync(
                    dispatcher,
                    path,
                    normalizedDecodePixelWidth);
                s_thumbCache[cacheKey] = task;
                long estimatedBytes = EstimateDecodedBitmapBytes(normalizedDecodePixelWidth);
                s_thumbEstimatedBytes[cacheKey] = estimatedBytes;
                s_totalThumbnailEstimatedBytes += estimatedBytes;
                s_thumbLru.AddFirst(cacheKey);
                EvictThumbnailCacheIfNeeded();
            }
            else
            {
                RemoveThumbnailLruKey(cacheKey);
                s_thumbLru.AddFirst(cacheKey);
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }

        var result = await task;
        if (result is null)
        {
            RemoveThumbnailTaskIfCurrent(cacheKey, task);
        }

        return result;
    }

    /// <summary>
    /// Removes old cache entries for the same file path but different
    /// modification-time version, preventing stale thumbnails from
    /// accumulating when files are edited.
    /// </summary>
    private static void RemoveStaleThumbnailEntries(
        string cacheScope,
        string path,
        string currentKey)
    {
        string pathPrefix = $"thumb:{cacheScope}|{path}:";

        lock (s_thumbLock)
        {
            if (s_thumbCache.Count == 0)
            {
                return;
            }

            var staleKeys = new List<string>();
            foreach (var key in s_thumbCache.Keys)
            {
                if (!key.Equals(currentKey, StringComparison.OrdinalIgnoreCase) &&
                    key.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    staleKeys.Add(key);
                }
            }

            foreach (var staleKey in staleKeys)
            {
                RemoveThumbnailCacheEntry(staleKey);
            }
        }
    }

    /// <summary>
    /// Evicts oldest thumbnail cache entries when the cache exceeds the
    /// maximum size.  Must be called under s_thumbLock.
    /// </summary>
    private static void EvictThumbnailCacheIfNeeded()
    {
        while ((s_thumbCache.Count > MaxThumbnailCacheEntries ||
                s_totalThumbnailEstimatedBytes > MaxThumbnailCacheBytes) &&
               s_thumbLru.Count > 0)
        {
            var oldestKey = s_thumbLru.Last!.Value;
            RemoveThumbnailCacheEntry(oldestKey);
        }

        PerformanceLogger.ThumbnailEstimatedBytes =
            Math.Max(0, s_totalThumbnailEstimatedBytes);
    }

    private static async Task<BitmapImage?> CreateMediaThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth)
    {
        await s_thumbLoadSemaphore.WaitAsync();
        try
        {
            bool isVideo = IsVideoFile(path);
            // Persist reduced image thumbnails on disk. This is consulted only
            // for a file that is being shown; no startup scan or continuous
            // refresh is introduced. Video previews stay with the Windows
            // thumbnail provider because it is the only reliable decoder for
            // the installed codecs.
            if (!isVideo)
            {
                byte[]? diskThumbnail = await ThumbnailDiskCache.GetOrCreateAsync(
                    path,
                    decodePixelWidth);
                if (diskThumbnail is { Length: > 0 })
                {
                    var cachedImage = await CreateBitmapImageAsync(
                        dispatcher,
                        diskThumbnail,
                        decodePixelWidth);
                    if (cachedImage is not null)
                    {
                        return cachedImage;
                    }
                }
            }

            // Try Windows native thumbnail first — leverages the system
            // thumbnail cache and avoids reading the full image into memory.
            var image = await TryLoadNativeThumbnailAsync(
                dispatcher,
                path,
                decodePixelWidth,
                isVideo
                    ? Windows.Storage.FileProperties.ThumbnailMode.VideosView
                    : Windows.Storage.FileProperties.ThumbnailMode.PicturesView);
            if (image is not null)
            {
                return image;
            }

            // Video decoding is intentionally left to the Windows thumbnail
            // provider. Reading an entire video as an image would waste memory
            // and cannot produce a valid BitmapImage; the caller falls back to
            // the normal shell icon when Windows has no preview.
            if (isVideo)
            {
                return null;
            }

            // Fallback: decode to the requested display size.
            byte[] bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            image = await CreateBitmapImageAsync(dispatcher, bytes, decodePixelWidth);

            return image;
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load media thumbnail for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            s_thumbLoadSemaphore.Release();
        }
    }

    private static void RemoveThumbnailTaskIfCurrent(string cacheKey, Task<BitmapImage?> task)
    {
        lock (s_thumbLock)
        {
            if (s_thumbCache.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, task))
            {
                RemoveThumbnailCacheEntry(cacheKey);
                PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            }
        }
    }

    private static void RemoveThumbnailCacheEntry(string cacheKey)
    {
        s_thumbCache.Remove(cacheKey);
        RemoveThumbnailLruKey(cacheKey);
        if (s_thumbEstimatedBytes.Remove(cacheKey, out long estimatedBytes))
        {
            s_totalThumbnailEstimatedBytes -= estimatedBytes;
        }

        PerformanceLogger.ThumbnailEstimatedBytes =
            Math.Max(0, s_totalThumbnailEstimatedBytes);
    }

    private static void RemoveThumbnailLruKey(string cacheKey)
    {
        for (var node = s_thumbLru.First; node is not null; node = node.Next)
        {
            if (node.Value.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                s_thumbLru.Remove(node);
                return;
            }
        }
    }

    /// <summary>
    /// Tries to load a thumbnail using Windows' built-in thumbnail system
    /// via StorageFile.GetThumbnailAsync().  This avoids reading the full
    /// image into memory and benefits from the OS thumbnail cache.
    /// Returns null if the native path fails (e.g. network paths, special files).
    /// </summary>
    private static async Task<BitmapImage?> TryLoadNativeThumbnailAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        string path,
        int decodePixelWidth,
        Windows.Storage.FileProperties.ThumbnailMode thumbnailMode)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await storageFile.GetThumbnailAsync(
                thumbnailMode,
                (uint)decodePixelWidth,
                Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            if (dispatcher.HasThreadAccess)
            {
                return await CreateBitmapFromStreamOnUiThread(thumbnail, decodePixelWidth);
            }

            var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    tcs.SetResult(await CreateBitmapFromStreamOnUiThread(thumbnail, decodePixelWidth));
                }
                catch (Exception ex)
                {
                    App.Log($"[IconHelper] Native thumbnail UI thread decode failed: {ex.Message}");
                    tcs.SetResult(null);
                }
            }))
            {
                tcs.SetResult(null);
            }

            return await tcs.Task;
        }
        catch
        {
            // StorageFile.GetFileFromPathAsync can fail for various reasons
            // (network paths, special files, permission issues).  Fall back
            // to the byte-array path.
            return null;
        }
    }

    private static async Task<BitmapImage?> CreateBitmapFromStreamOnUiThread(
        Windows.Storage.Streams.IRandomAccessStream stream,
        int decodePixelWidth)
    {
        var bmp = new BitmapImage();
        bmp.DecodePixelWidth = decodePixelWidth;
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    public static void ClearIconCache(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (IsMediaFile(path))
        {
            // Remove all thumbnail entries for this path (any version)
            string pathMarker = $"|{path}:";
            lock (s_thumbLock)
            {
                var keysToRemove = s_thumbCache.Keys
                    .Where(k =>
                        k.StartsWith("thumb:", StringComparison.OrdinalIgnoreCase) &&
                        k.Contains(pathMarker, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var key in keysToRemove)
                {
                    RemoveThumbnailCacheEntry(key);
                }
                PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            }

            if (!showImageFilesAsIcons)
            {
                return;
            }
        }

        IconSource iconSource = ResolveIconSource(path, hideShortcutArrowOverlay);
        if (string.IsNullOrWhiteSpace(iconSource.Path))
        {
            return;
        }

        string cacheKey = BuildCacheKey(path, iconSource);
        string bitmapCacheMarker = $"|{cacheKey}:decode=";
        foreach (string bitmapKey in s_bitmapImageCache.Keys.Where(
                     key => key.Contains(bitmapCacheMarker, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveDecodedBitmap(bitmapKey);
        }
        s_iconBytesCache.TryRemove(cacheKey, out _);
        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;

        // For directories, also invalidate the Windows shell icon cache.
        // Tools like Folder Painter modify desktop.ini to change folder icons,
        // but SHGetFileInfo/SHGetImageList return stale icons from the shell's
        // internal cache unless SHChangeNotify is called.
        if (Directory.Exists(iconSource.Path))
        {
            InvalidateShellIconCache();
        }
    }

    /// <summary>
    /// Notifies the shell that file associations (and therefore folder icons)
    /// have changed, forcing it to discard its cached icons and re-read
    /// desktop.ini on the next SHGetFileInfo call.
    /// Throttled: tools like Folder Painter often rewrite several folders in
    /// quick succession, and each broadcast makes Explorer flush its icon cache.
    /// </summary>
    private static void InvalidateShellIconCache()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref s_lastShellInvalidateMs);
        if (now - last < ShellInvalidateThrottleMs ||
            Interlocked.CompareExchange(ref s_lastShellInvalidateMs, now, last) != last)
        {
            return;
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Clears all cached thumbnails.  Called when a widget is reset or
    /// all widgets are cleared.
    /// </summary>
    public static void ClearAllThumbnailCaches()
    {
        lock (s_thumbLock)
        {
            s_thumbCache.Clear();
            s_thumbLru.Clear();
            s_thumbEstimatedBytes.Clear();
            s_totalThumbnailEstimatedBytes = 0;
            PerformanceLogger.ThumbnailCacheCount = 0;
            PerformanceLogger.ThumbnailEstimatedBytes = 0;
        }
    }

    /// <summary>
    /// Releases decoded image caches that can be recreated from disk. Visible
    /// widgets keep a half-sized warm LRU; fully hidden widgets release all
    /// decoded images and icon bytes so the 30-second cleanup is meaningful.
    /// Live XAML image sources remain valid because they own their own reference.
    /// </summary>
    internal static IdleIconCacheReleaseResult ReleaseIdleCaches(bool allWidgetsHidden)
    {
        int thumbnailCountBefore;
        int thumbnailCountAfter;
        long thumbnailBytesBefore;
        long thumbnailBytesAfter;
        lock (s_thumbLock)
        {
            thumbnailCountBefore = s_thumbCache.Count;
            thumbnailBytesBefore = s_totalThumbnailEstimatedBytes;
            int targetCount = allWidgetsHidden ? 0 : MaxThumbnailCacheEntries / 2;
            long targetBytes = allWidgetsHidden ? 0 : MaxThumbnailCacheBytes / 2;
            while ((s_thumbCache.Count > targetCount ||
                    s_totalThumbnailEstimatedBytes > targetBytes) &&
                   s_thumbLru.Last is { } oldest)
            {
                RemoveThumbnailCacheEntry(oldest.Value);
            }

            if (allWidgetsHidden && s_thumbCache.Count > 0)
            {
                s_thumbCache.Clear();
                s_thumbLru.Clear();
                s_thumbEstimatedBytes.Clear();
                s_totalThumbnailEstimatedBytes = 0;
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
            PerformanceLogger.ThumbnailEstimatedBytes =
                Math.Max(0, s_totalThumbnailEstimatedBytes);
            thumbnailCountAfter = s_thumbCache.Count;
            thumbnailBytesAfter = s_totalThumbnailEstimatedBytes;
        }

        int bitmapCountBefore;
        int bitmapCountAfter;
        long bitmapBytesBefore;
        long bitmapBytesAfter;
        lock (s_bitmapCacheLock)
        {
            bitmapCountBefore = s_bitmapImageCache.Count;
            bitmapBytesBefore = s_totalBitmapEstimatedBytes;
            int targetCount = allWidgetsHidden ? 0 : MaxDecodedBitmapCacheEntries / 2;
            long targetBytes = allWidgetsHidden ? 0 : MaxDecodedBitmapCacheBytes / 2;
            while ((s_bitmapImageCache.Count > targetCount ||
                    s_totalBitmapEstimatedBytes > targetBytes) &&
                   s_bitmapLru.Last is { } oldest)
            {
                RemoveDecodedBitmap(oldest.Value);
            }

            if (allWidgetsHidden && s_bitmapImageCache.Count > 0)
            {
                s_bitmapImageCache.Clear();
                s_bitmapLru.Clear();
                s_bitmapLruNodes.Clear();
                s_bitmapEstimatedBytes.Clear();
                s_totalBitmapEstimatedBytes = 0;
                UpdateDecodedBitmapDiagnostics();
            }

            bitmapCountAfter = s_bitmapImageCache.Count;
            bitmapBytesAfter = s_totalBitmapEstimatedBytes;
        }

        int iconByteEntriesBefore = s_iconBytesCache.Count;
        if (allWidgetsHidden)
        {
            s_iconBytesCache.Clear();
            PerformanceLogger.IconCacheCount = 0;
        }

        return new IdleIconCacheReleaseResult(
            Math.Max(0, thumbnailCountBefore - thumbnailCountAfter),
            Math.Max(0, bitmapCountBefore - bitmapCountAfter),
            Math.Max(0, iconByteEntriesBefore - s_iconBytesCache.Count),
            Math.Max(0, thumbnailBytesBefore - thumbnailBytesAfter) +
                Math.Max(0, bitmapBytesBefore - bitmapBytesAfter));
    }

    /// <summary>
    /// Clears decoded icons and thumbnails owned by a transient caller while
    /// preserving the shared file-widget cache.
    /// </summary>
    public static void ClearCacheScope(string cacheScope)
    {
        if (string.IsNullOrWhiteSpace(cacheScope))
        {
            return;
        }

        string normalizedScope = cacheScope.Trim();
        string bitmapPrefix = normalizedScope + "|";
        foreach (string key in s_bitmapImageCache.Keys.Where(
                     key => key.StartsWith(bitmapPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveDecodedBitmap(key);
        }

        string thumbnailPrefix = $"thumb:{normalizedScope}|";
        lock (s_thumbLock)
        {
            foreach (string key in s_thumbCache.Keys.Where(
                         key => key.StartsWith(thumbnailPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveThumbnailCacheEntry(key);
            }

            PerformanceLogger.ThumbnailCacheCount = s_thumbCache.Count;
        }
    }

    private static async Task<BitmapImage?> LoadBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        IconSource iconSource,
        string iconBytesCacheKey,
        string bitmapCacheKey,
        int decodePixelWidth)
    {
        if (!s_iconBytesCache.TryGetValue(iconBytesCacheKey, out var bytes))
        {
            await s_iconLoadSemaphore.WaitAsync();
            try
            {
                if (!s_iconBytesCache.TryGetValue(iconBytesCacheKey, out bytes))
                {
                    bytes = await Task.Run(() => LoadIconBytes(iconSource));
                    if (bytes is { Length: > 0 })
                    {
                        s_iconBytesCache[iconBytesCacheKey] = bytes;
                        EvictIconCachesIfNeeded();
                    }
                }
            }
            finally
            {
                s_iconLoadSemaphore.Release();
            }
        }

        // Decode near the intended display size. This keeps large icons sharp while
        // allowing WIC to perform a high-quality downsample for compact icon layouts
        // instead of asking the compositor to shrink a 256 px bitmap to ~24 px.
        var image = await CreateBitmapImageAsync(dispatcher, bytes, decodePixelWidth);
        if (image is null)
        {
            RemoveDecodedBitmap(bitmapCacheKey);
            s_iconBytesCache.TryRemove(iconBytesCacheKey, out _);
        }

        return image;
    }

    private static byte[]? LoadIconBytes(IconSource iconSource)
    {
        using var perfScope = PerformanceLogger.Measure("IconHelper.LoadIconBytes", $"path={iconSource.Path}");
        try
        {
            if (iconSource.UsesExplicitIconIndex)
            {
                var indexedBytes = LoadIndexedIconBytes(iconSource);
                if (indexedBytes is not null)
                {
                    return indexedBytes;
                }
            }

            // Shell's Jumbo image list can contain a 32/48 px icon that has already
            // been enlarged to 256 px. Explorer can still render the same executable
            // crisply because it extracts the best resource frame directly. Do that
            // first for executable-backed items (including resolved shortcuts), then
            // retain the image-list path as a compatibility fallback.
            if (Path.GetExtension(iconSource.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? executableBytes = TryExtractHighResIndexedIcon(
                    iconSource.Path,
                    iconSource.IconIndex,
                    256);
                if (executableBytes is not null)
                {
                    return executableBytes;
                }
            }

            // Get the system icon index, then extract the highest-resolution
            // version available via SHGetImageList (Jumbo 256 → ExtraLarge 48 → Large 32).
            var shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(
                iconSource.Path,
                0,
                ref shinfo,
                (uint)Marshal.SizeOf(shinfo),
                SHGFI_SYSICONINDEX);

            if (hImg == IntPtr.Zero)
            {
                // Fallback: direct large icon via SHGetFileInfo
                return LoadIconBytesFromShGetFileInfo(iconSource.Path);
            }

            int iconIndex = shinfo.iIcon;

            // Try Jumbo (256×256) first — gives crisp icons on high-DPI displays.
            byte[]? bytes = TryGetIconFromImageList(SHIL_JUMBO, iconIndex);
            if (bytes is not null)
            {
                return bytes;
            }

            // Fall back to Extra Large (48×48).
            bytes = TryGetIconFromImageList(SHIL_EXTRALARGE, iconIndex);
            if (bytes is not null)
            {
                return bytes;
            }

            // Final fallback: Large (32×32) via SHGetFileInfo.
            return LoadIconBytesFromShGetFileInfo(iconSource.Path);
        }
        catch (Exception ex)
        {
            App.Log($"[IconHelper] Failed to load icon for {iconSource.Path}: {ex.Message}");
            return null;
        }
    }

    private static byte[]? TryGetIconFromImageList(int imageListFlags, int iconIndex)
    {
        IntPtr imageListPtr = IntPtr.Zero;
        IntPtr iconHandle = IntPtr.Zero;

        try
        {
            Guid iid = s_iidIImageList;
            int hr = SHGetImageList(imageListFlags, ref iid, ref imageListPtr);
            if (hr != 0 || imageListPtr == IntPtr.Zero)
            {
                return null;
            }

            var imageList = (IImageList)Marshal.GetObjectForIUnknown(imageListPtr);
            int result = imageList.GetIcon(iconIndex, ILD_TRANSPARENT, ref iconHandle);
            if (result != 0 || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            using var icon = Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }

            if (imageListPtr != IntPtr.Zero)
            {
                Marshal.Release(imageListPtr);
            }
        }
    }

    private static byte[]? LoadIconBytesFromShGetFileInfo(string path)
    {
        var shinfo = new SHFILEINFO();
        IntPtr hImg = SHGetFileInfo(
            path,
            0,
            ref shinfo,
            (uint)Marshal.SizeOf(shinfo),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(shinfo.hIcon);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static byte[]? LoadIndexedIconBytes(IconSource iconSource)
    {
        // Try SHDefExtractIcon first — it can extract 256×256 icons from exe/dll/ico resources.
        byte[]? hiResBytes = TryExtractHighResIndexedIcon(iconSource.Path, iconSource.IconIndex, 256);
        if (hiResBytes is not null)
        {
            return hiResBytes;
        }

        // Fallback: 48×48
        hiResBytes = TryExtractHighResIndexedIcon(iconSource.Path, iconSource.IconIndex, 48);
        if (hiResBytes is not null)
        {
            return hiResBytes;
        }

        // Final fallback: ExtractIconEx (32×32 large / 16×16 small)
        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        uint count = ExtractIconEx(
            iconSource.Path,
            iconSource.IconIndex,
            largeIcons,
            smallIcons,
            1);

        IntPtr iconHandle = largeIcons[0] != IntPtr.Zero
            ? largeIcons[0]
            : smallIcons[0];

        if (count == 0 || iconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (largeIcons[0] != IntPtr.Zero)
            {
                DestroyIcon(largeIcons[0]);
            }

            if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0])
            {
                DestroyIcon(smallIcons[0]);
            }
        }
    }

    private static byte[]? TryExtractHighResIndexedIcon(string filePath, int iconIndex, int size)
    {
        IntPtr hLarge = IntPtr.Zero;
        IntPtr hSmall = IntPtr.Zero;

        try
        {
            // nIconSize: high word = large icon size, low word = small icon size
            uint nIconSize = ((uint)size << 16) | (uint)size;
            int hr = SHDefExtractIcon(filePath, iconIndex, 0, out hLarge, out hSmall, nIconSize);
            if (hr != 0 || hLarge == IntPtr.Zero)
            {
                return null;
            }

            using var icon = Icon.FromHandle(hLarge);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hLarge != IntPtr.Zero)
            {
                DestroyIcon(hLarge);
            }

            if (hSmall != IntPtr.Zero && hSmall != hLarge)
            {
                DestroyIcon(hSmall);
            }
        }
    }

    private static Task<BitmapImage?> CreateBitmapImageAsync(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        byte[]? bytes,
        int decodePixelWidth = 0)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return Task.FromResult<BitmapImage?>(null);
        }

        if (dispatcher.HasThreadAccess)
        {
            return CreateBitmapImageOnUiThreadAsync(bytes, decodePixelWidth);
        }

        var tcs = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await CreateBitmapImageOnUiThreadAsync(bytes, decodePixelWidth));
            }
            catch (Exception ex)
            {
                App.Log($"[IconHelper] UI thread set source failed: {ex.Message}");
                tcs.SetResult(null);
            }
        }))
        {
            tcs.SetResult(null);
        }

        return tcs.Task;
    }

    private static async Task<BitmapImage?> CreateBitmapImageOnUiThreadAsync(byte[] bytes, int decodePixelWidth = 0)
    {
        var bmp = new BitmapImage();
        if (decodePixelWidth > 0)
        {
            bmp.DecodePixelWidth = decodePixelWidth;
        }

        using var winrtStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(winrtStream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        winrtStream.Seek(0);
        await bmp.SetSourceAsync(winrtStream);
        return bmp;
    }

    private static IconSource ResolveIconSource(string path, bool hideShortcutArrowOverlay)
    {
        if (!ShortcutHelper.IsShortcutPath(path))
        {
            return new IconSource(path);
        }

        if (!hideShortcutArrowOverlay)
        {
            return new IconSource(path);
        }

        var shortcut = ShortcutHelper.ReadStoredMetadata(path);

        if (shortcut is null)
        {
            return new IconSource(path);
        }

        // Parse icon location — may contain a comma-separated index (e.g. "steam.exe,0")
        var (iconFilePath, iconFileIndex) = SplitIconLocation(shortcut.IconLocation);
        string? iconLocation = NormalizeIconLocation(iconFilePath);
        int resolvedIconIndex = iconFileIndex >= 0 ? iconFileIndex : shortcut.IconIndex;

        if (!string.IsNullOrWhiteSpace(iconLocation) &&
            File.Exists(iconLocation))
        {
            return new IconSource(iconLocation, resolvedIconIndex, UsesExplicitIconIndex: true);
        }

        // For Steam .url shortcuts, the IconFile may point to a steam.exe path
        // that doesn't exist on this machine. Try to locate steam.exe via registry.
        if (!string.IsNullOrWhiteSpace(shortcut.TargetPath) &&
            shortcut.TargetPath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            string? steamExe = TryFindSteamExecutable();
            if (steamExe is not null && File.Exists(steamExe))
            {
                return new IconSource(steamExe, 0, UsesExplicitIconIndex: true);
            }
        }

        // If the icon location references an .exe/.dll/.ico that doesn't exist,
        // but we have the original iconLocation string, try finding the file
        // by searching common locations (e.g., strip path and search PATH).
        if (!string.IsNullOrWhiteSpace(iconLocation))
        {
            string? foundPath = TryFindExecutableInPath(iconLocation);
            if (foundPath is not null)
            {
                return new IconSource(foundPath, resolvedIconIndex, UsesExplicitIconIndex: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(shortcut.TargetPath) &&
            (File.Exists(shortcut.TargetPath) || Directory.Exists(shortcut.TargetPath)))
        {
            return new IconSource(shortcut.TargetPath);
        }

        return new IconSource(path);
    }

    /// <summary>
    /// Splits an icon location string into (path, index).
    /// Handles formats like "C:\\path\\to\\file.exe,0" or "file.exe,-5".
    /// </summary>
    private static (string path, int index) SplitIconLocation(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return (string.Empty, -1);
        }

        string trimmed = iconLocation.Trim().Trim('"');
        int lastComma = trimmed.LastIndexOf(',');
        if (lastComma <= 0 || lastComma == trimmed.Length - 1)
        {
            return (trimmed, -1);
        }

        string indexPart = trimmed[(lastComma + 1)..];
        if (int.TryParse(indexPart, out int index))
        {
            return (trimmed[..lastComma], index);
        }

        return (trimmed, -1);
    }

    private static string? TryFindSteamExecutable()
    {
        try
        {
            // Check HKCU\Software\Valve\Steam\SteamPath
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                string exePath = Path.Combine(steamPath, "steam.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }

            // Check HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath
            using var key2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key2?.GetValue("InstallPath") is string installPath)
            {
                string exePath = Path.Combine(installPath, "steam.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }
        }
        catch
        {
            // Ignore registry access errors
        }

        // Try common install locations
        string[] commonPaths =
        {
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\Steam\steam.exe",
        };

        foreach (string p in commonPaths)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string? TryFindExecutableInPath(string filePath)
    {
        try
        {
            // If the path is just a filename (no directory), search system PATH
            if (!Path.IsPathRooted(filePath) && filePath.IndexOf(Path.DirectorySeparatorChar) < 0)
            {
                string? fullPath = FindInPath(filePath);
                if (fullPath is not null)
                {
                    return fullPath;
                }
            }

            // Try common Program Files locations
            string fileName = Path.GetFileName(filePath);
            string[] searchDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };

            foreach (string dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    private static string? FindInPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid path entries
            }
        }

        return null;
    }

    private static string? NormalizeIconLocation(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return null;
        }

        // Strip any trailing comma-separated icon index (e.g. "steam.exe,0" → "steam.exe")
        string trimmed = iconLocation.Trim().Trim('"');
        int lastComma = trimmed.LastIndexOf(',');
        if (lastComma > 0 && lastComma < trimmed.Length - 1)
        {
            string afterComma = trimmed[(lastComma + 1)..];
            if (int.TryParse(afterComma, out _))
            {
                trimmed = trimmed[..lastComma];
            }
        }

        string expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
    }

    private static string BuildCacheKey(string sourcePath, IconSource iconSource)
    {
        string resolvedPath = iconSource.Path;
        if (Directory.Exists(resolvedPath))
        {
            return $"dir:{resolvedPath}:{GetDirectoryIconVersion(resolvedPath)}";
        }

        string extension = Path.GetExtension(resolvedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return $"path:{resolvedPath}";
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            ShortcutHelper.IsShortcutPath(sourcePath))
        {
            string sourceVersion = ShortcutHelper.IsShortcutPath(sourcePath)
                ? GetFileIconVersion(sourcePath)
                : "source";
            return $"path:{resolvedPath}:{iconSource.IconIndex}:{iconSource.UsesExplicitIconIndex}:{GetFileIconVersion(resolvedPath)}:{sourceVersion}";
        }

        return $"ext:{extension}";
    }

    private static string GetDirectoryIconVersion(string directoryPath)
    {
        try
        {
            string desktopIniPath = Path.Combine(directoryPath, "desktop.ini");
            long directoryTicks = Directory.GetLastWriteTimeUtc(directoryPath).Ticks;
            long desktopIniTicks = File.Exists(desktopIniPath)
                ? File.GetLastWriteTimeUtc(desktopIniPath).Ticks
                : 0;
            return $"{directoryTicks:x}:{desktopIniTicks:x}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetFileIconVersion(string filePath)
    {
        try
        {
            return File.Exists(filePath)
                ? File.GetLastWriteTimeUtc(filePath).Ticks.ToString("x")
                : "missing";
        }
        catch
        {
            return "unknown";
        }
    }

    private static long EstimateDecodedBitmapBytes(int decodePixelWidth)
    {
        int width = decodePixelWidth <= 0 ? 256 : decodePixelWidth;
        return (long)width * width * 4;
    }

    private static void TrackDecodedBitmap(string cacheKey, long estimatedBytes)
    {
        lock (s_bitmapCacheLock)
        {
            if (s_bitmapLruNodes.TryGetValue(cacheKey, out var existingNode))
            {
                s_bitmapLru.Remove(existingNode);
                s_bitmapLru.AddFirst(existingNode);
            }
            else
            {
                var node = s_bitmapLru.AddFirst(cacheKey);
                s_bitmapLruNodes[cacheKey] = node;
                s_bitmapEstimatedBytes[cacheKey] = estimatedBytes;
                s_totalBitmapEstimatedBytes += estimatedBytes;
            }

            while ((s_bitmapImageCache.Count > MaxDecodedBitmapCacheEntries ||
                    s_totalBitmapEstimatedBytes > MaxDecodedBitmapCacheBytes) &&
                   s_bitmapLru.Last is { } oldestNode)
            {
                string oldestKey = oldestNode.Value;
                s_bitmapLru.RemoveLast();
                s_bitmapLruNodes.Remove(oldestKey);
                if (s_bitmapEstimatedBytes.Remove(oldestKey, out long removedBytes))
                {
                    s_totalBitmapEstimatedBytes -= removedBytes;
                }

                s_bitmapImageCache.TryRemove(oldestKey, out _);
            }

            UpdateDecodedBitmapDiagnostics();
        }
    }

    private static void RemoveDecodedBitmap(string cacheKey)
    {
        lock (s_bitmapCacheLock)
        {
            s_bitmapImageCache.TryRemove(cacheKey, out _);
            if (s_bitmapLruNodes.Remove(cacheKey, out var node))
            {
                s_bitmapLru.Remove(node);
            }

            if (s_bitmapEstimatedBytes.Remove(cacheKey, out long removedBytes))
            {
                s_totalBitmapEstimatedBytes -= removedBytes;
            }

            UpdateDecodedBitmapDiagnostics();
        }
    }

    private static void UpdateDecodedBitmapDiagnostics()
    {
        PerformanceLogger.DecodedBitmapCacheCount = s_bitmapImageCache.Count;
        PerformanceLogger.DecodedBitmapEstimatedBytes = Math.Max(0, s_totalBitmapEstimatedBytes);
    }

    private static void EvictIconCachesIfNeeded()
    {
        long cachedBytes = s_iconBytesCache.Values.Sum(bytes => bytes?.LongLength ?? 0);
        if (s_iconBytesCache.Count > MaxIconCacheEntries ||
            cachedBytes > MaxIconCacheBytes)
        {
            int targetCount = MaxIconCacheEntries / 2;
            long targetBytes = MaxIconCacheBytes / 2;
            foreach (var (key, bytes) in s_iconBytesCache)
            {
                if (s_iconBytesCache.Count <= targetCount &&
                    cachedBytes <= targetBytes)
                {
                    break;
                }

                if (s_iconBytesCache.TryRemove(key, out _))
                {
                    cachedBytes -= bytes?.LongLength ?? 0;
                }

                string bitmapCacheMarker = $"|{key}:decode=";
                foreach (string bitmapKey in s_bitmapImageCache.Keys.Where(
                             candidate => candidate.Contains(
                                  bitmapCacheMarker,
                                  StringComparison.OrdinalIgnoreCase)))
                {
                    RemoveDecodedBitmap(bitmapKey);
                }
            }
        }

        // Update diagnostics
        PerformanceLogger.IconCacheCount = s_iconBytesCache.Count;
    }
}
