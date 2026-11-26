using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public sealed class AccessPolicyResolver
{
    public IReadOnlyList<string> ResolveAllowedRoots(AccessPolicy policy)
    {
        return policy.Level switch
        {
            AccessLevel.Full => DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList(),

            AccessLevel.Media => new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            }
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),

            AccessLevel.Custom => policy.CustomRoots
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),

            _ => Array.Empty<string>()
        };
    }

    public bool IsPathAllowed(string requestedPath, AccessPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return false;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(requestedPath);
        }
        catch
        {
            return false;
        }

        var roots = ResolveAllowedRoots(policy);
        foreach (var root in roots)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
