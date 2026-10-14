using System;
using System.IO;
using System.Linq;
using Android.Content;
using Content.Android.Launcher.Storage;

namespace Content.Android.Launcher.Utility;

public static class ContentCacheManager
{
    private const string Tag = "SS14Cache";

    public static string GetContentCacheDir(Context context)
    {
        var dir = Path.Combine(context.FilesDir?.AbsolutePath ?? AppContext.BaseDirectory, "content_cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetEngineCacheDir()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApp, "Space Station 14", "cache", "builds");
    }

    public static long GetTotalCacheSizeBytes(Context context)
    {
        long total = 0;
        try
        {
            var contentDir = GetContentCacheDir(context);
            if (Directory.Exists(contentDir))
            {
                total += Directory.EnumerateFiles(contentDir).Sum(f => new FileInfo(f).Length);
            }
        }
        catch { }

        try
        {
            var engineDir = GetEngineCacheDir();
            if (Directory.Exists(engineDir))
            {
                total += Directory.EnumerateFiles(engineDir).Sum(f => new FileInfo(f).Length);
            }
        }
        catch { }

        return total;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 МБ";
        double mb = bytes / (1024.0 * 1024.0);
        return $"{mb:F1} МБ";
    }

    // [SolomonNumb1] deletes previous server zip when a new content build is downloaded
    public static void PurgeOldBuildForServer(Context context, LauncherStorage storage, string serverAddress, string newZipPath)
    {
        try
        {
            var newZipName = Path.GetFileName(newZipPath);
            // Keep previous builds available for export and reuse.
            storage.SetServerBuildZip(serverAddress, newZipName);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"Failed to purge old build for {serverAddress}: {ex.Message}");
        }
    }

    // [SolomonNumb1] removes abandoned .tmp files and prunes builds older than retention threshold
    public static void PruneStaleCache(Context context, LauncherStorage storage, int maxBuildsToKeep = 2, int maxDaysOld = 3)
    {
        try
        {
            var contentDir = GetContentCacheDir(context);
            if (!Directory.Exists(contentDir)) return;

            // 1. Delete all leftover temporary files
            foreach (var tmp in Directory.EnumerateFiles(contentDir, "*.tmp"))
            {
                try { File.Delete(tmp); } catch { }
            }

            var engineDir = GetEngineCacheDir();
            if (Directory.Exists(engineDir))
            {
                foreach (var tmp in Directory.EnumerateFiles(engineDir, "*.tmp"))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }

            // 2. Clean up old builds in content_cache
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(maxDaysOld);
            var zipFiles = Directory.EnumerateFiles(contentDir, "*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            for (var i = 0; i < zipFiles.Count; i++)
            {
                var f = zipFiles[i];
                // If it exceeds the max allowed builds or is older than cutoff (except the newest build)
                if (i >= maxBuildsToKeep || (i > 0 && f.LastWriteTimeUtc < cutoff))
                {
                    try
                    {
                        f.Delete();
                        global::Android.Util.Log.Info(Tag, $"Pruned stale cached build: {f.Name}");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"PruneStaleCache failed: {ex.Message}");
        }
    }

    // [SolomonNumb1] wipes all cached server content and engine builds on user request, freeing storage
    public static long ClearAllCache(Context context, LauncherStorage storage)
    {
        long freedBytes = 0;
        try
        {
            var contentDir = GetContentCacheDir(context);
            if (Directory.Exists(contentDir))
            {
                foreach (var file in Directory.EnumerateFiles(contentDir))
                {
                    try
                    {
                        var len = new FileInfo(file).Length;
                        File.Delete(file);
                        freedBytes += len;
                    }
                    catch { }
                }
            }

            var engineDir = GetEngineCacheDir();
            if (Directory.Exists(engineDir))
            {
                foreach (var file in Directory.EnumerateFiles(engineDir))
                {
                    try
                    {
                        var len = new FileInfo(file).Length;
                        File.Delete(file);
                        freedBytes += len;
                    }
                    catch { }
                }
            }

            storage.ClearServerBuildCache();
            global::Android.Util.Log.Info(Tag, $"Cleared all server cache. Freed {FormatBytes(freedBytes)}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"ClearAllCache failed: {ex.Message}");
        }

        return freedBytes;
    }
}
