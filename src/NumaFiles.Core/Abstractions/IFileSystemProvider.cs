using NumaFiles.Core.Models;

namespace NumaFiles.Core.Abstractions;

public interface IFileSystemProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsRemote { get; }
    bool CanWrite { get; }

    Task<IReadOnlyList<DriveItem>> GetDrivesAsync(CancellationToken ct = default);
    IReadOnlyList<QuickAccessItem> GetQuickAccessItems();
    IAsyncEnumerable<FileSystemItem> EnumerateAsync(string path, bool includeHidden = false, CancellationToken ct = default);
    string? GetParentPath(string path);
    bool DirectoryExists(string path);
    Task<string> MaterializeFileAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default);
    Task UploadFileAsync(string targetDirectory, string localFilePath, IProgress<double>? progress = null, CancellationToken ct = default);
    Task CreateDirectoryAsync(string parentDirectory, string folderName, CancellationToken ct = default);
    Task RenameAsync(string fullPath, string newName, CancellationToken ct = default);
    Task DeleteAsync(string fullPath, CancellationToken ct = default);
}
