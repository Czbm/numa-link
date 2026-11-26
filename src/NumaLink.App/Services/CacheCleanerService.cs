using System.IO;

namespace NumaLink.App.Services;

public static class CacheCleanerService
{
    private static readonly string[] CacheDirs =
    [
        Path.Combine(Path.GetTempPath(), "NumaLinkDrive"),
        Path.Combine(Path.GetTempPath(), "NumaFilesCache")
    ];

    public static long GetCacheSizeBytes()
    {
        long total = 0;
        foreach (var dir in CacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
            }
            catch { }
        }
        return total;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    public static int ClearCache()
    {
        int deleted = 0;
        foreach (var dir in CacheDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // Skip files currently open by another app
                    }
                }
            }
            catch { }
        }
        return deleted;
    }
}
