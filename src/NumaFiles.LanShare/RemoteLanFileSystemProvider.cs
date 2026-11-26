using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NumaFiles.Core.Abstractions;
using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public sealed class RemoteLanFileSystemProvider : IFileSystemProvider
{
    public const string VirtualRootPath = "::ROOT::";

    private readonly ConnectedRemoteEndpoint _endpoint;
    private readonly string _cacheRoot;

    public RemoteLanFileSystemProvider(ConnectedRemoteEndpoint endpoint)
    {
        _endpoint = endpoint;
        _cacheRoot = Path.Combine(Path.GetTempPath(), "NumaFilesCache", endpoint.SessionId);
        Directory.CreateDirectory(_cacheRoot);
    }

    public string ProviderId => _endpoint.SessionId;
    public string DisplayName => _endpoint.Peer.DisplayName;
    public bool IsRemote => true;
    public bool CanWrite => _endpoint.Policy.Permission == AccessPermission.ReadWrite;

    public Task<IReadOnlyList<DriveItem>> GetDrivesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DriveItem>>(Array.Empty<DriveItem>());

    public IReadOnlyList<QuickAccessItem> GetQuickAccessItems() => Array.Empty<QuickAccessItem>();

    private async Task<(TcpClient Client, Stream Stream)> ConnectTlsAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_endpoint.Peer.HostAddress, _endpoint.Peer.Port, ct).ConfigureAwait(false);
            var ssl = await LanTlsHelper.WrapClientAsync(client.GetStream(), _endpoint.Peer.HostAddress, ct).ConfigureAwait(false);
            return (client, ssl);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<FileSystemItem> EnumerateAsync(
        string path,
        bool includeHidden = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var isRoot = string.IsNullOrWhiteSpace(path) || path == VirtualRootPath;
        var req = new FileRpcRequest
        {
            Type = isRoot ? "list_roots" : "list_dir",
            SessionId = _endpoint.SessionId,
            SessionToken = _endpoint.SessionToken,
            Path = isRoot ? null : path,
            IncludeHidden = includeHidden
        };

        var (client, stream) = await ConnectTlsAsync(ct).ConfigureAwait(false);
        using (client)
        await using (stream)
        {
            await LanPairingService.WriteFrameAsync(stream, JsonSerializer.Serialize(req), ct).ConfigureAwait(false);
            var respJson = await LanPairingService.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<FileListResponse>(respJson);

            if (resp is null || !resp.Ok)
                throw new IOException(resp?.Error ?? "No se pudo leer la carpeta remota.");

            foreach (var item in resp.Items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    public string? GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == VirtualRootPath)
            return null;

        var isPosix = path.StartsWith('/');
        var normalized = path.TrimEnd('\\', '/');
        foreach (var root in _endpoint.AllowedRoots)
        {
            var normRoot = root.TrimEnd('\\', '/');
            if (normalized.Equals(normRoot, StringComparison.OrdinalIgnoreCase))
                return VirtualRootPath;
        }

        if (isPosix)
        {
            var idx = normalized.LastIndexOf('/');
            return idx <= 0 ? VirtualRootPath : normalized[..idx];
        }

        if (normalized.Length <= 2 && normalized.EndsWith(':'))
            return VirtualRootPath;

        return Directory.GetParent(normalized)?.FullName ?? VirtualRootPath;
    }

    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path);

    public async Task<string> MaterializeFileAsync(
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var (client, stream) = await ConnectTlsAsync(ct).ConfigureAwait(false);
        using (client)
        await using (stream)
        {
            var req = new FileRpcRequest
            {
                Type = "download_file",
                SessionId = _endpoint.SessionId,
                SessionToken = _endpoint.SessionToken,
                Path = remotePath
            };

            await LanPairingService.WriteFrameAsync(stream, JsonSerializer.Serialize(req), ct).ConfigureAwait(false);
            var headerJson = await LanPairingService.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            var header = JsonSerializer.Deserialize<FileTransferHeaderResponse>(headerJson);

            if (header is null || !header.Ok)
                throw new IOException(header?.Error ?? "No se pudo descargar el archivo remoto.");

            var safeName = Path.GetFileName(header.FileName ?? Path.GetFileName(remotePath));
            var localCachePath = Path.Combine(_cacheRoot, safeName);

            await using (var fs = new FileStream(localCachePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long totalRead = 0;
                long totalSize = header.SizeBytes;

                while (totalRead < totalSize)
                {
                    var toRead = (int)Math.Min(buffer.Length, totalSize - totalRead);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    totalRead += read;

                    if (totalSize > 0)
                        progress?.Report((double)totalRead / totalSize);
                }
            }

            progress?.Report(1.0);
            return localCachePath;
        }
    }

    public async Task UploadFileAsync(
        string targetDirectory,
        string localFilePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!CanWrite)
            throw new UnauthorizedAccessException("Esta sesión LAN es de solo lectura.");

        var fi = new FileInfo(localFilePath);
        var (client, stream) = await ConnectTlsAsync(ct).ConfigureAwait(false);
        using (client)
        await using (stream)
        {
            var req = new FileRpcRequest
            {
                Type = "upload_file",
                SessionId = _endpoint.SessionId,
                SessionToken = _endpoint.SessionToken,
                Path = targetDirectory,
                FileName = fi.Name,
                SizeBytes = fi.Length
            };

            await LanPairingService.WriteFrameAsync(stream, JsonSerializer.Serialize(req), ct).ConfigureAwait(false);
            var ackJson = await LanPairingService.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            var ack = JsonSerializer.Deserialize<FileTransferHeaderResponse>(ackJson);
            if (ack is null || !ack.Ok)
                throw new IOException(ack?.Error ?? "No se permitió subir el archivo.");

            await using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long sent = 0;
                int read;
                while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sent += read;
                    if (fi.Length > 0)
                        progress?.Report((double)sent / fi.Length);
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            var doneJson = await LanPairingService.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            var done = JsonSerializer.Deserialize<FileTransferHeaderResponse>(doneJson);
            if (done is null || !done.Ok)
                throw new IOException(done?.Error ?? "Error al finalizar la subida.");

            progress?.Report(1.0);
        }
    }

    public Task CreateDirectoryAsync(string parentDirectory, string folderName, CancellationToken ct = default) =>
        SendSimpleRpcAsync(new FileRpcRequest
        {
            Type = "create_dir",
            SessionId = _endpoint.SessionId,
            SessionToken = _endpoint.SessionToken,
            Path = parentDirectory,
            NewName = folderName
        }, ct);

    public Task RenameAsync(string fullPath, string newName, CancellationToken ct = default) =>
        SendSimpleRpcAsync(new FileRpcRequest
        {
            Type = "rename_item",
            SessionId = _endpoint.SessionId,
            SessionToken = _endpoint.SessionToken,
            Path = fullPath,
            NewName = newName
        }, ct);

    public Task DeleteAsync(string fullPath, CancellationToken ct = default) =>
        SendSimpleRpcAsync(new FileRpcRequest
        {
            Type = "delete_item",
            SessionId = _endpoint.SessionId,
            SessionToken = _endpoint.SessionToken,
            Path = fullPath
        }, ct);

    private async Task SendSimpleRpcAsync(FileRpcRequest req, CancellationToken ct)
    {
        if (!CanWrite)
            throw new UnauthorizedAccessException("Esta sesión LAN es de solo lectura.");

        var (client, stream) = await ConnectTlsAsync(ct).ConfigureAwait(false);
        using (client)
        await using (stream)
        {
            await LanPairingService.WriteFrameAsync(stream, JsonSerializer.Serialize(req), ct).ConfigureAwait(false);
            var respJson = await LanPairingService.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<SimpleRpcResponse>(respJson);
            if (resp is null || !resp.Ok)
                throw new IOException(resp?.Error ?? "No se pudo completar la operación remota.");
        }
    }
}
