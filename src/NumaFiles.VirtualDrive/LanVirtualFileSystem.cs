using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using NumaFiles.Core.Models;
using NumaFiles.LanShare;
using FspFileInfo = Fsp.Interop.FileInfo;
using FspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace NumaFiles.VirtualDrive;

public sealed class LanVirtualFileSystem : FileSystemBase
{
    private const uint FileAttributeReadonly = 0x01;
    private const uint FileAttributeHidden = 0x02;
    private const uint FileAttributeSystem = 0x04;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileDirectoryFile = 0x00000001;

    private static readonly byte[] VirtualDriveDesktopIniBytes = System.Text.Encoding.Unicode.GetBytes(
        "\uFEFF[.ShellClassInfo]\r\nIconResource=%SystemRoot%\\System32\\imageres.dll,-32\r\n");

    private static readonly TimeSpan DirCacheTtl = TimeSpan.FromSeconds(3);

    private readonly ConnectedRemoteEndpoint _endpoint;
    private readonly RemoteLanFileSystemProvider _provider;
    private readonly Dictionary<string, string> _rootAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _driveRootAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedDir> _dirCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _stagingRoot;

    private sealed record CachedDir(DateTime FetchedUtc, List<FileSystemItem> Items);

    private sealed class OpenHandle
    {
        public required string VirtualPath { get; init; }
        public required string RemotePath { get; init; }
        public required bool IsDirectory { get; init; }
        public bool IsVirtualDesktopIni { get; init; }
        public bool IsDriveRootAlias { get; init; }
        public string? LocalCachePath { get; set; }
        public FileStream? LocalStream { get; set; }
        public bool IsDirty { get; set; }
        public bool DeleteOnClose { get; set; }
        public FileSystemItem? ItemMeta { get; set; }
        public List<FileSystemItem>? DirSnapshot { get; set; }
    }

    public LanVirtualFileSystem(ConnectedRemoteEndpoint endpoint)
    {
        _endpoint = endpoint;
        _provider = new RemoteLanFileSystemProvider(endpoint);
        _stagingRoot = Path.Combine(Path.GetTempPath(), "NumaLinkDrive", endpoint.SessionId);
        Directory.CreateDirectory(_stagingRoot);
        BuildRootAliases();
    }

    private void BuildRootAliases()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _endpoint.AllowedRoots)
        {
            var trimmed = root.TrimEnd('\\', '/');
            var isDriveRoot = trimmed.Length == 2 && trimmed.EndsWith(':');
            string baseAlias;
            if (_endpoint.RootDisplayNames is not null &&
                _endpoint.RootDisplayNames.TryGetValue(root, out var friendly) &&
                !string.IsNullOrWhiteSpace(friendly))
            {
                baseAlias = friendly.Trim();
            }
            else if (isDriveRoot)
            {
                baseAlias = $"Disco {trimmed.TrimEnd(':')}";
            }
            else
            {
                var lastSeg = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                baseAlias = string.IsNullOrWhiteSpace(lastSeg) ? "Compartido" : lastSeg;
            }

            var alias = baseAlias;
            int suffix = 2;
            while (!used.Add(alias))
                alias = $"{baseAlias}_{suffix++}";

            _rootAliases[alias] = root;
            if (isDriveRoot)
                _driveRootAliases.Add(alias);
        }
    }

    public override int Init(object Host)
    {
        var host = (FileSystemHost)Host;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)_endpoint.SessionId.GetHashCode();
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = false;
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out FspVolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        VolumeInfo.TotalSize = 1024UL * 1024 * 1024 * 500; // 500 GB virtual display
        VolumeInfo.FreeSize = 1024UL * 1024 * 1024 * 250;
        VolumeInfo.SetVolumeLabel($"{_endpoint.Peer.DisplayName} (Numa)");
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(
        string FileName,
        out uint FileAttributes,
        ref byte[] SecurityDescriptor)
    {
        FileAttributes = 0;

        if (IsRootPath(FileName))
        {
            FileAttributes = FileAttributeDirectory;
            return STATUS_SUCCESS;
        }

        if (IsVirtualDriveDesktopIni(FileName))
        {
            FileAttributes = FileAttributeHidden | FileAttributeSystem | FileAttributeReadonly;
            return STATUS_SUCCESS;
        }

        if (!TryResolveRemotePath(FileName, out var remotePath, out var isRootAlias))
            return STATUS_OBJECT_NAME_NOT_FOUND;

        if (isRootAlias)
        {
            var alias = FileName.Trim('\\');
            FileAttributes = _driveRootAliases.Contains(alias)
                ? FileAttributeDirectory | FileAttributeReadonly
                : FileAttributeDirectory;
            return STATUS_SUCCESS;
        }

        var item = FindItemByVirtualPath(FileName);
        if (item is null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FileAttributes = ComputeAttributes(item.IsDirectory);
        return STATUS_SUCCESS;
    }

    public override int Open(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        out object FileNode,
        out object FileDesc,
        out FspFileInfo FileInfo,
        out string NormalizedName)
    {
        FileNode = null!;
        FileDesc = null!;
        FileInfo = default;
        NormalizedName = FileName;

        if (IsRootPath(FileName))
        {
            FileDesc = new OpenHandle
            {
                VirtualPath = "\\",
                RemotePath = RemoteLanFileSystemProvider.VirtualRootPath,
                IsDirectory = true
            };
            FillDirectoryInfo(out FileInfo, DateTime.UtcNow);
            return STATUS_SUCCESS;
        }

        if (IsVirtualDriveDesktopIni(FileName))
        {
            FileDesc = new OpenHandle
            {
                VirtualPath = FileName,
                RemotePath = string.Empty,
                IsDirectory = false,
                IsVirtualDesktopIni = true
            };
            FillVirtualDesktopIniInfo(out FileInfo);
            return STATUS_SUCCESS;
        }

        if (!TryResolveRemotePath(FileName, out var remotePath, out var isRootAlias))
            return STATUS_OBJECT_NAME_NOT_FOUND;

        if (isRootAlias)
        {
            var isDrive = _driveRootAliases.Contains(FileName.Trim('\\'));
            FileDesc = new OpenHandle
            {
                VirtualPath = FileName,
                RemotePath = remotePath,
                IsDirectory = true,
                IsDriveRootAlias = isDrive
            };
            FillDirectoryInfo(out FileInfo, DateTime.UtcNow, isDrive);
            return STATUS_SUCCESS;
        }

        var item = FindItemByVirtualPath(FileName);
        if (item is null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FileDesc = new OpenHandle
        {
            VirtualPath = FileName,
            RemotePath = item.FullPath,
            IsDirectory = item.IsDirectory,
            ItemMeta = item
        };

        FillFileInfo(out FileInfo, item);
        return STATUS_SUCCESS;
    }

    public override int Create(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        uint FileAttributes,
        byte[] SecurityDescriptor,
        ulong AllocationSize,
        out object FileNode,
        out object FileDesc,
        out FspFileInfo FileInfo,
        out string NormalizedName)
    {
        FileNode = null!;
        FileDesc = null!;
        FileInfo = default;
        NormalizedName = FileName;

        if (!_provider.CanWrite)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (!TryResolveRemotePath(FileName, out var remotePath, out var isRootAlias) || isRootAlias)
            return STATUS_ACCESS_DENIED;

        var isDir = (CreateOptions & FileDirectoryFile) != 0;
        var parentRemote = GetRemoteParent(remotePath);
        var name = GetRemoteFileName(remotePath);
        if (string.IsNullOrWhiteSpace(parentRemote) || string.IsNullOrWhiteSpace(name))
            return STATUS_ACCESS_DENIED;

        if (isDir)
        {
            try
            {
                _provider.CreateDirectoryAsync(parentRemote, name).GetAwaiter().GetResult();
                InvalidateDirCache(parentRemote);
            }
            catch
            {
                return STATUS_ACCESS_DENIED;
            }

            FileDesc = new OpenHandle
            {
                VirtualPath = FileName,
                RemotePath = remotePath,
                IsDirectory = true
            };
            FillDirectoryInfo(out FileInfo, DateTime.UtcNow);
            return STATUS_SUCCESS;
        }

        var stagingPath = GetStagingFilePath(FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        var fs = new FileStream(stagingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);

        FileDesc = new OpenHandle
        {
            VirtualPath = FileName,
            RemotePath = remotePath,
            IsDirectory = false,
            LocalCachePath = stagingPath,
            LocalStream = fs,
            IsDirty = true
        };

        FillFileInfo(out FileInfo, new FileSystemItem
        {
            Name = name,
            FullPath = remotePath,
            IsDirectory = false,
            SizeBytes = 0,
            LastModifiedUtc = DateTime.UtcNow
        });
        return STATUS_SUCCESS;
    }

    public override int Read(
        object FileNode,
        object FileDesc,
        IntPtr Buffer,
        ulong Offset,
        uint Length,
        out uint PBytesTransferred)
    {
        PBytesTransferred = 0;
        if (FileDesc is not OpenHandle handle || handle.IsDirectory)
            return STATUS_INVALID_DEVICE_REQUEST;

        if (handle.IsVirtualDesktopIni)
        {
            if ((long)Offset >= VirtualDriveDesktopIniBytes.Length)
                return STATUS_END_OF_FILE;

            var avail = (int)Math.Min(Length, (ulong)(VirtualDriveDesktopIniBytes.Length - (long)Offset));
            Marshal.Copy(VirtualDriveDesktopIniBytes, (int)Offset, Buffer, avail);
            PBytesTransferred = (uint)avail;
            return STATUS_SUCCESS;
        }

        try
        {
            EnsureLocalFileMaterialized(handle);
            if (handle.LocalStream is null)
                return STATUS_END_OF_FILE;

            if ((long)Offset >= handle.LocalStream.Length)
                return STATUS_END_OF_FILE;

            handle.LocalStream.Seek((long)Offset, SeekOrigin.Begin);
            var temp = new byte[Length];
            var read = handle.LocalStream.Read(temp, 0, (int)Length);
            if (read == 0)
                return STATUS_END_OF_FILE;

            Marshal.Copy(temp, 0, Buffer, read);
            PBytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }
        catch
        {
            return STATUS_IO_DEVICE_ERROR;
        }
    }

    public override int Write(
        object FileNode,
        object FileDesc,
        IntPtr Buffer,
        ulong Offset,
        uint Length,
        bool WriteToEndOfFile,
        bool ConstrainedIo,
        out uint PBytesTransferred,
        out FspFileInfo FileInfo)
    {
        PBytesTransferred = 0;
        FileInfo = default;

        if (!_provider.CanWrite)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (FileDesc is not OpenHandle handle || handle.IsDirectory)
            return STATUS_INVALID_DEVICE_REQUEST;

        if (handle.IsVirtualDesktopIni)
            return STATUS_MEDIA_WRITE_PROTECTED;

        try
        {
            EnsureLocalFileMaterialized(handle, forWrite: true);
            if (handle.LocalStream is null)
                return STATUS_IO_DEVICE_ERROR;

            if (WriteToEndOfFile)
                handle.LocalStream.Seek(0, SeekOrigin.End);
            else
                handle.LocalStream.Seek((long)Offset, SeekOrigin.Begin);

            var temp = new byte[Length];
            Marshal.Copy(Buffer, temp, 0, (int)Length);
            handle.LocalStream.Write(temp, 0, (int)Length);
            handle.IsDirty = true;
            PBytesTransferred = Length;

            FillFileInfo(out FileInfo, new FileSystemItem
            {
                Name = Path.GetFileName(handle.RemotePath),
                FullPath = handle.RemotePath,
                IsDirectory = false,
                SizeBytes = handle.LocalStream.Length,
                LastModifiedUtc = DateTime.UtcNow
            });
            return STATUS_SUCCESS;
        }
        catch
        {
            return STATUS_IO_DEVICE_ERROR;
        }
    }

    public override int GetFileInfo(object FileNode, object FileDesc, out FspFileInfo FileInfo)
    {
        FileInfo = default;
        if (FileDesc is not OpenHandle handle)
            return STATUS_INVALID_HANDLE;

        if (handle.IsVirtualDesktopIni)
        {
            FillVirtualDesktopIniInfo(out FileInfo);
            return STATUS_SUCCESS;
        }

        if (handle.IsDirectory)
        {
            FillDirectoryInfo(out FileInfo, handle.ItemMeta?.LastModifiedUtc ?? DateTime.UtcNow, handle.IsDriveRootAlias);
            return STATUS_SUCCESS;
        }

        if (handle.LocalStream is not null)
        {
            FillFileInfo(out FileInfo, new FileSystemItem
            {
                Name = Path.GetFileName(handle.RemotePath),
                FullPath = handle.RemotePath,
                IsDirectory = false,
                SizeBytes = handle.LocalStream.Length,
                LastModifiedUtc = DateTime.UtcNow
            });
            return STATUS_SUCCESS;
        }

        var meta = handle.ItemMeta ?? FindItemByVirtualPath(handle.VirtualPath);
        if (meta is null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FillFileInfo(out FileInfo, meta);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object FileNode,
        object FileDesc,
        ulong NewSize,
        bool SetAllocationSize,
        out FspFileInfo FileInfo)
    {
        FileInfo = default;
        if (!_provider.CanWrite)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (FileDesc is not OpenHandle handle || handle.IsDirectory)
            return STATUS_INVALID_DEVICE_REQUEST;

        if (handle.IsVirtualDesktopIni)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (!SetAllocationSize)
        {
            EnsureLocalFileMaterialized(handle, forWrite: true);
            handle.LocalStream?.SetLength((long)NewSize);
            handle.IsDirty = true;
        }

        return GetFileInfo(FileNode, FileDesc, out FileInfo);
    }

    public override int CanDelete(object FileNode, object FileDesc, string FileName)
    {
        if (!_provider.CanWrite)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (IsRootPath(FileName) || (TryResolveRemotePath(FileName, out _, out var isRootAlias) && isRootAlias))
            return STATUS_ACCESS_DENIED;

        return STATUS_SUCCESS;
    }

    public override int Rename(object FileNode, object FileDesc, string FileName, string NewFileName, bool ReplaceIfExists)
    {
        if (!_provider.CanWrite)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (!TryResolveRemotePath(FileName, out var oldRemote, out var isOldRoot) || isOldRoot)
            return STATUS_ACCESS_DENIED;
        if (!TryResolveRemotePath(NewFileName, out var newRemote, out var isNewRoot) || isNewRoot)
            return STATUS_ACCESS_DENIED;

        try
        {
            _provider.RenameAsync(oldRemote, GetRemoteFileName(newRemote)).GetAwaiter().GetResult();
            InvalidateDirCache(GetRemoteParent(oldRemote));
            return STATUS_SUCCESS;
        }
        catch
        {
            return STATUS_ACCESS_DENIED;
        }
    }

    public override void Cleanup(object FileNode, object FileDesc, string FileName, uint Flags)
    {
        if (FileDesc is not OpenHandle handle)
            return;

        if ((Flags & CleanupDelete) != 0 && _provider.CanWrite)
        {
            handle.DeleteOnClose = true;
            try
            {
                handle.LocalStream?.Dispose();
                handle.LocalStream = null;
                _provider.DeleteAsync(handle.RemotePath).GetAwaiter().GetResult();
                InvalidateDirCache(Path.GetDirectoryName(handle.RemotePath));
            }
            catch
            {
                // Best effort delete
            }
            return;
        }

        FlushIfDirty(handle);
    }

    public override void Close(object FileNode, object FileDesc)
    {
        if (FileDesc is not OpenHandle handle)
            return;

        FlushIfDirty(handle);
        handle.LocalStream?.Dispose();
        handle.LocalStream = null;
    }

    public override bool ReadDirectoryEntry(
        object FileNode,
        object FileDesc,
        string Pattern,
        string Marker,
        ref object Context,
        out string FileName,
        out FspFileInfo FileInfo)
    {
        FileName = null!;
        FileInfo = default;

        if (FileDesc is not OpenHandle handle || !handle.IsDirectory)
            return false;

        if (handle.DirSnapshot is null)
            handle.DirSnapshot = GetDirectoryEntries(handle.VirtualPath, handle.RemotePath);

        int index = Context is int idx ? idx : 0;
        if (Marker is not null && Context is null)
        {
            index = handle.DirSnapshot.FindIndex(i => i.Name.Equals(Marker, StringComparison.OrdinalIgnoreCase)) + 1;
        }

        if (index < 0 || index >= handle.DirSnapshot.Count)
            return false;

        var item = handle.DirSnapshot[index];
        Context = index + 1;
        FileName = item.Name;
        if (IsRootPath(handle.VirtualPath) && _driveRootAliases.Contains(item.Name))
            FillDirectoryInfo(out FileInfo, item.LastModifiedUtc == default ? DateTime.UtcNow : item.LastModifiedUtc, isDriveRoot: true);
        else
            FillFileInfo(out FileInfo, item);
        return true;
    }

    private void FlushIfDirty(OpenHandle handle)
    {
        if (!handle.IsDirty || handle.DeleteOnClose || handle.LocalCachePath is null)
            return;

        try
        {
            handle.LocalStream?.Flush();
            handle.LocalStream?.Dispose();
            handle.LocalStream = null;

            var parentRemote = GetRemoteParent(handle.RemotePath);
            if (!string.IsNullOrWhiteSpace(parentRemote))
            {
                _provider.UploadFileAsync(parentRemote, handle.LocalCachePath).GetAwaiter().GetResult();
                InvalidateDirCache(parentRemote);
            }
            handle.IsDirty = false;
        }
        catch
        {
            // Keep resilient
        }
    }

    private void EnsureLocalFileMaterialized(OpenHandle handle, bool forWrite = false)
    {
        if (handle.LocalStream is not null)
            return;

        var cached = _provider.MaterializeFileAsync(handle.RemotePath).GetAwaiter().GetResult();
        handle.LocalCachePath = cached;
        handle.LocalStream = new FileStream(
            cached,
            FileMode.Open,
            forWrite ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite);
    }

    private List<FileSystemItem> GetDirectoryEntries(string virtualPath, string remotePath)
    {
        if (IsRootPath(virtualPath))
        {
            return _rootAliases.Select(kv => new FileSystemItem
            {
                Name = kv.Key,
                FullPath = kv.Value,
                IsDirectory = true,
                LastModifiedUtc = DateTime.UtcNow
            }).ToList();
        }

        if (_dirCache.TryGetValue(remotePath, out var cached) &&
            DateTime.UtcNow - cached.FetchedUtc < DirCacheTtl)
        {
            return cached.Items;
        }

        var items = new List<FileSystemItem>();
        try
        {
            var enumerator = _provider.EnumerateAsync(remotePath, includeHidden: false).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    items.Add(enumerator.Current);
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Return empty on transient error
        }

        _dirCache[remotePath] = new CachedDir(DateTime.UtcNow, items);
        return items;
    }

    private FileSystemItem? FindItemByVirtualPath(string virtualPath)
    {
        var trimmed = virtualPath.Trim('\\');
        var lastSlash = trimmed.LastIndexOf('\\');
        if (lastSlash < 0)
            return null;

        var parentVirtual = "\\" + trimmed[..lastSlash];
        var itemName = trimmed[(lastSlash + 1)..];
        if (!TryResolveRemotePath(parentVirtual, out var parentRemote, out _))
            return null;

        var entries = GetDirectoryEntries(parentVirtual, parentRemote);
        return entries.FirstOrDefault(e => e.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolveRemotePath(string virtualPath, out string remotePath, out bool isRootAlias)
    {
        remotePath = string.Empty;
        isRootAlias = false;

        var trimmed = virtualPath.Trim('\\');
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var sepIndex = trimmed.IndexOf('\\');
        var alias = sepIndex < 0 ? trimmed : trimmed[..sepIndex];
        if (!_rootAliases.TryGetValue(alias, out var rootReal))
            return false;

        if (sepIndex < 0)
        {
            remotePath = rootReal;
            isRootAlias = true;
            return true;
        }

        var rel = trimmed[(sepIndex + 1)..];
        remotePath = CombineRemotePath(rootReal, rel);
        return true;
    }

    private static string CombineRemotePath(string rootReal, string relativeWindowsPath)
    {
        if (rootReal.StartsWith('/'))
        {
            var posixRel = relativeWindowsPath.Replace('\\', '/').TrimStart('/');
            return $"{rootReal.TrimEnd('/')}/{posixRel}";
        }
        return Path.Combine(rootReal, relativeWindowsPath);
    }

    private static string? GetRemoteParent(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            return null;

        if (remotePath.StartsWith('/'))
        {
            var trimmed = remotePath.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            return idx <= 0 ? "/" : trimmed[..idx];
        }

        return Path.GetDirectoryName(remotePath);
    }

    private static string GetRemoteFileName(string remotePath)
    {
        var parts = remotePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private string GetStagingFilePath(string virtualPath)
    {
        var clean = virtualPath.TrimStart('\\');
        return Path.Combine(_stagingRoot, clean);
    }

    private void InvalidateDirCache(string? remotePath)
    {
        if (!string.IsNullOrWhiteSpace(remotePath))
            _dirCache.TryRemove(remotePath, out _);
    }

    private static bool IsRootPath(string path) =>
        string.IsNullOrWhiteSpace(path) || path == "\\" || path == "/";

    private uint ComputeAttributes(bool isDirectory)
    {
        var attr = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        if (!_provider.CanWrite && !isDirectory)
            attr |= FileAttributeReadonly;
        return attr;
    }

    private bool IsVirtualDriveDesktopIni(string virtualPath)
    {
        var trimmed = virtualPath.Trim('\\');
        var parts = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && parts[1].Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            && _driveRootAliases.Contains(parts[0]);
    }

    private static void FillVirtualDesktopIniInfo(out FspFileInfo info)
    {
        info = default;
        info.FileAttributes = FileAttributeHidden | FileAttributeSystem | FileAttributeReadonly;
        info.FileSize = (ulong)VirtualDriveDesktopIniBytes.Length;
        info.AllocationSize = 4096;
        var ft = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        info.CreationTime = ft;
        info.LastAccessTime = ft;
        info.LastWriteTime = ft;
        info.ChangeTime = ft;
    }

    private void FillDirectoryInfo(out FspFileInfo info, DateTime utc, bool isDriveRoot = false)
    {
        info = default;
        info.FileAttributes = isDriveRoot
            ? FileAttributeDirectory | FileAttributeReadonly
            : FileAttributeDirectory;
        var ft = (ulong)utc.ToFileTimeUtc();
        info.CreationTime = ft;
        info.LastAccessTime = ft;
        info.LastWriteTime = ft;
        info.ChangeTime = ft;
    }

    private void FillFileInfo(out FspFileInfo info, FileSystemItem item)
    {
        info = default;
        info.FileAttributes = ComputeAttributes(item.IsDirectory);
        info.FileSize = item.IsDirectory ? 0UL : (ulong)Math.Max(0, item.SizeBytes);
        info.AllocationSize = (info.FileSize + 4095UL) / 4096UL * 4096UL;
        var utc = item.LastModifiedUtc == default ? DateTime.UtcNow : item.LastModifiedUtc;
        var ft = (ulong)utc.ToFileTimeUtc();
        info.CreationTime = ft;
        info.LastAccessTime = ft;
        info.LastWriteTime = ft;
        info.ChangeTime = ft;
    }
}
