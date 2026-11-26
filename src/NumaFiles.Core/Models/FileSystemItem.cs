namespace NumaFiles.Core.Models;

public sealed record FileSystemItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public string Extension { get; init; } = string.Empty;
    public string TypeDisplay { get; init; } = string.Empty;
    public bool IsHidden { get; init; }
    public string ProviderId { get; init; } = "local";
}

public sealed record DriveItem
{
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public string VolumeLabel { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public string ProviderId { get; init; } = "local";

    public double UsedRatio => TotalBytes > 0 ? Math.Clamp((double)(TotalBytes - FreeBytes) / TotalBytes, 0, 1) : 0;

    public string DisplayTitle => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"Disco local ({RootPath.TrimEnd('\\')})"
        : $"{VolumeLabel} ({RootPath.TrimEnd('\\')})";
}

public sealed record QuickAccessItem(string Name, string Path, string IconKey);
