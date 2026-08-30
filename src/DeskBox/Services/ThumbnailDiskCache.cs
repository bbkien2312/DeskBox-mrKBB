using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeskBox.Services;

/// <summary>
/// Small persistent cache for image-file thumbnails. The cache stores only
/// reduced PNGs, keyed by path + file version + requested decode width. It is
/// deliberately demand-driven: startup never enumerates this directory.
/// </summary>
internal static class ThumbnailDiskCache
{
    private static readonly bool s_isX86Process = RuntimeInformation.ProcessArchitecture == Architecture.X86;
    private static long MaxCacheBytes => (s_isX86Process ? 48L : 128L) * 1024 * 1024;
    private static int MaxCacheFiles => s_isX86Process ? 750 : 2_000;
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> s_pending = new(StringComparer.OrdinalIgnoreCase);
    private static int s_cleanupRunning;

    internal static string DirectoryPath => Path.Combine(
        DeskBoxDataPathService.Current.RootPath,
        "cache",
        "thumbnails");

    /// <summary>
    /// Creates the visible cache root without pre-generating thumbnails. This
    /// makes its location predictable while keeping startup I/O negligible.
    /// </summary>
    internal static void EnsureInitialized()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            UpdateDiagnostics();
            QueueCleanup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogVerbose($"[ThumbnailDiskCache] Initialization skipped: {ex.Message}");
        }
    }

    internal static Task<byte[]?> GetOrCreateAsync(string sourcePath, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return Task.FromResult<byte[]?>(null);
        }

        string cachePath;
        try
        {
            cachePath = GetCachePath(sourcePath, decodePixelWidth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return s_pending.GetOrAdd(cachePath, _ => ReadOrCreateAsync(sourcePath, decodePixelWidth, cachePath));
    }

    private static async Task<byte[]?> ReadOrCreateAsync(
        string sourcePath,
        int decodePixelWidth,
        string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                byte[] cached = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
                if (cached.Length > 0)
                {
                    TryTouch(cachePath);
                    UpdateDiagnostics();
                    return cached;
                }
            }

            byte[]? thumbnail = await Task.Run(
                () => CreateThumbnailBytes(sourcePath, decodePixelWidth)).ConfigureAwait(false);
            if (thumbnail is not { Length: > 0 })
            {
                return null;
            }

            Directory.CreateDirectory(DirectoryPath);
            string temporaryPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, thumbnail).ConfigureAwait(false);
                File.Move(temporaryPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            UpdateDiagnostics();
            QueueCleanup();
            return thumbnail;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            ExternalException or ArgumentException or OutOfMemoryException)
        {
            App.LogVerbose($"[ThumbnailDiskCache] Skipped '{sourcePath}': {ex.Message}");
            return null;
        }
        finally
        {
            s_pending.TryRemove(cachePath, out _);
        }
    }

    private static byte[]? CreateThumbnailBytes(string sourcePath, int requestedWidth)
    {
        using var source = Image.FromFile(sourcePath, useEmbeddedColorManagement: false);
        if (source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        int width = Math.Clamp(requestedWidth, 24, 256);
        int height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static string GetCachePath(string sourcePath, int decodePixelWidth)
    {
        var info = new FileInfo(sourcePath);
        string identity = string.Join(
            "|",
            Path.GetFullPath(sourcePath),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            Math.Clamp(decodePixelWidth, 24, 256));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(DirectoryPath, hash + ".png");
    }

    private static void QueueCleanup()
    {
        if (Interlocked.Exchange(ref s_cleanupRunning, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    return;
                }

                var files = Directory.EnumerateFiles(DirectoryPath, "*.png")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastAccessTimeUtc)
                    .ToList();
                long totalBytes = files.Sum(file => file.Length);
                foreach (FileInfo file in files.Skip(MaxCacheFiles))
                {
                    totalBytes -= file.Length;
                    TryDelete(file.FullName);
                }

                foreach (FileInfo file in files.Take(MaxCacheFiles).Reverse())
                {
                    if (totalBytes <= MaxCacheBytes)
                    {
                        break;
                    }

                    totalBytes -= file.Length;
                    TryDelete(file.FullName);
                }

                UpdateDiagnostics();
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref s_cleanupRunning, 0);
            }
        });
    }

    internal static void UpdateDiagnostics()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath))
            {
                PerformanceLogger.DiskThumbnailCacheCount = 0;
                PerformanceLogger.DiskThumbnailCacheBytes = 0;
                return;
            }

            FileInfo[] files = Directory.EnumerateFiles(DirectoryPath, "*.png")
                .Select(path => new FileInfo(path))
                .ToArray();
            PerformanceLogger.DiskThumbnailCacheCount = files.Length;
            PerformanceLogger.DiskThumbnailCacheBytes = files.Sum(file => file.Length);
        }
        catch
        {
        }
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
