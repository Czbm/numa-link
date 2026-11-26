using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public sealed class LanPairingService : IDisposable
{
    public const int PreferredTcpPort = 45822;

    private readonly DeviceIdentityStore _identity;
    private readonly AccessPolicyResolver _policyResolver;
    private readonly Func<VisibilityMode> _visibilityProvider;
    private readonly ConcurrentDictionary<string, ActiveHostSession> _incomingSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectedRemoteEndpoint> _outgoingConnections = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private sealed record ActiveHostSession(LanSession Session, string Token);

    public LanPairingService(
        DeviceIdentityStore identity,
        AccessPolicyResolver policyResolver,
        Func<VisibilityMode> visibilityProvider)
    {
        _identity = identity;
        _policyResolver = policyResolver;
        _visibilityProvider = visibilityProvider;
    }

    public int ListeningPort { get; private set; }
    public Func<IncomingPairRequest, Task<PairDecision>>? IncomingRequestHandler { get; set; }
    public event EventHandler? SessionsChanged;

    public IReadOnlyList<LanSession> GetAllSessions()
    {
        var incoming = _incomingSessions.Values.Select(v => v.Session);
        var outgoing = _outgoingConnections.Values.Select(c => new LanSession
        {
            SessionId = c.SessionId,
            Peer = c.Peer,
            Policy = c.Policy
        });
        return incoming.Concat(outgoing)
            .GroupBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(s => s.ConnectedAtUtc)
            .ToList();
    }

    public IReadOnlyList<LanSession> GetIncomingSessions() =>
        _incomingSessions.Values
            .Select(v => v.Session)
            .OrderByDescending(s => s.ConnectedAtUtc)
            .ToList();

    public IReadOnlyList<ConnectedRemoteEndpoint> GetOutgoingConnections() =>
        _outgoingConnections.Values.ToList();

    public bool UpdateIncomingSessionPolicy(string sessionId, AccessPolicy newPolicy)
    {
        if (!_incomingSessions.TryGetValue(sessionId, out var active))
            return false;

        var updatedPeer = active.Session.Peer with { IsTrusted = newPolicy.RememberDevice };
        var updatedSession = active.Session with
        {
            Peer = updatedPeer,
            Policy = newPolicy
        };

        _incomingSessions[sessionId] = active with { Session = updatedSession };

        if (newPolicy.RememberDevice)
        {
            _identity.SaveTrusted(new TrustedDeviceRecord
            {
                DeviceId = updatedPeer.DeviceId,
                DisplayName = updatedPeer.DisplayName,
                Policy = newPolicy,
                AddedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            _identity.RevokeTrusted(updatedPeer.DeviceId);
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.Any, PreferredTcpPort);
            _listener.Start();
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }

        ListeningPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public static string GenerateVerificationCode() =>
        RandomNumberGenerator.GetInt32(1000, 10000).ToString("D4");

    public async Task<PairResponsePacket> RequestConnectionAsync(
        LanPeer peer,
        string verificationCode,
        CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        await client.ConnectAsync(peer.HostAddress, peer.Port, timeoutCts.Token).ConfigureAwait(false);
        await using var stream = await LanTlsHelper.WrapClientAsync(client.GetStream(), peer.HostAddress, timeoutCts.Token).ConfigureAwait(false);

        var request = new PairRequestPacket
        {
            RequestId = Guid.NewGuid().ToString("N"),
            FromDeviceId = _identity.DeviceId,
            FromDisplayName = _identity.DisplayName,
            VerificationCode = verificationCode
        };

        await WriteFrameAsync(stream, JsonSerializer.Serialize(request), timeoutCts.Token).ConfigureAwait(false);
        var responseJson = await ReadFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PairResponsePacket>(responseJson)
            ?? new PairResponsePacket { RequestId = request.RequestId, Accepted = false, Reason = "Respuesta inválida" };

        if (response.Accepted && response.SessionId is not null && response.SessionToken is not null && response.Policy is not null)
        {
            var endpoint = new ConnectedRemoteEndpoint
            {
                SessionId = response.SessionId,
                SessionToken = response.SessionToken,
                Peer = peer with { IsConnected = true },
                Policy = response.Policy,
                AllowedRoots = response.AllowedRoots,
                RootDisplayNames = response.RootDisplayNames
            };
            _outgoingConnections[endpoint.SessionId] = endpoint;
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        return response;
    }

    public async Task<PairDecision> SimulateIncomingRequestAsync(string fromDisplayName, string verificationCode)
    {
        var req = new IncomingPairRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            FromDeviceId = "sim-" + Guid.NewGuid().ToString("N")[..8],
            FromDisplayName = fromDisplayName,
            RemoteAddress = "127.0.0.1",
            VerificationCode = verificationCode
        };

        var decision = IncomingRequestHandler is not null
            ? await IncomingRequestHandler(req).ConfigureAwait(false)
            : PairDecision.Reject("Sin manejador activo");

        if (decision.Accepted && decision.Policy is not null)
        {
            var hostSession = RegisterAcceptedIncomingSession(req, decision.Policy);
            var roots = _policyResolver.ResolveAllowedRoots(decision.Policy);

            // Also register as a browsable loopback remote drive so the user can test Phase 3 on 1 PC!
            var loopbackEndpoint = new ConnectedRemoteEndpoint
            {
                SessionId = hostSession.Session.SessionId,
                SessionToken = hostSession.Token,
                Peer = new LanPeer
                {
                    DeviceId = req.FromDeviceId,
                    DisplayName = fromDisplayName,
                    HostAddress = "127.0.0.1",
                    Port = ListeningPort,
                    IsTrusted = decision.Policy.RememberDevice,
                    IsConnected = true
                },
                Policy = decision.Policy,
                AllowedRoots = roots
            };
            _outgoingConnections[loopbackEndpoint.SessionId] = loopbackEndpoint;
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        return decision;
    }

    public bool DisconnectSession(string sessionId)
    {
        var removedIn = _incomingSessions.TryRemove(sessionId, out _);
        var removedOut = _outgoingConnections.TryRemove(sessionId, out _);
        if (removedIn || removedOut)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = await LanTlsHelper.WrapServerAsync(client.GetStream(), _identity.DeviceId, ct).ConfigureAwait(false);
                var json = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<LanEnvelope>(json);
                if (envelope is null) return;

                switch (envelope.Type)
                {
                    case "pair_request":
                        await HandlePairRequestAsync(client, stream, json, ct).ConfigureAwait(false);
                        break;
                    case "list_roots":
                    case "list_dir":
                    case "download_file":
                    case "upload_file":
                    case "create_dir":
                    case "rename_item":
                    case "delete_item":
                        await HandleFileRpcAsync(stream, json, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch
            {
                // Ignore dropped connections
            }
        }
    }

    private async Task HandlePairRequestAsync(TcpClient client, Stream stream, string json, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<PairRequestPacket>(json);
        if (req is null) return;

        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "desconocido";
        var visibility = _visibilityProvider();
        var trusted = _identity.FindTrusted(req.FromDeviceId);

        PairDecision decision;
        if (visibility == VisibilityMode.Hidden)
        {
            decision = PairDecision.Reject("Este equipo está en modo oculto.");
        }
        else if (visibility == VisibilityMode.TrustedOnly && trusted is null)
        {
            decision = PairDecision.Reject("Este equipo solo acepta equipos de confianza.");
        }
        else if (trusted is not null)
        {
            decision = PairDecision.Accept(trusted.Policy);
        }
        else if (IncomingRequestHandler is not null)
        {
            var incoming = new IncomingPairRequest
            {
                RequestId = req.RequestId,
                FromDeviceId = req.FromDeviceId,
                FromDisplayName = req.FromDisplayName,
                RemoteAddress = remoteIp,
                VerificationCode = req.VerificationCode
            };
            decision = await IncomingRequestHandler(incoming).ConfigureAwait(false);
        }
        else
        {
            decision = PairDecision.Reject("El equipo no pudo mostrar la solicitud.");
        }

        PairResponsePacket response;
        if (decision.Accepted && decision.Policy is not null)
        {
            var incoming = new IncomingPairRequest
            {
                RequestId = req.RequestId,
                FromDeviceId = req.FromDeviceId,
                FromDisplayName = req.FromDisplayName,
                RemoteAddress = remoteIp,
                VerificationCode = req.VerificationCode
            };
            var hostSession = RegisterAcceptedIncomingSession(incoming, decision.Policy);
            var roots = _policyResolver.ResolveAllowedRoots(decision.Policy);

            response = new PairResponsePacket
            {
                RequestId = req.RequestId,
                Accepted = true,
                SessionId = hostSession.Session.SessionId,
                SessionToken = hostSession.Token,
                Policy = decision.Policy,
                AllowedRoots = roots
            };
        }
        else
        {
            response = new PairResponsePacket
            {
                RequestId = req.RequestId,
                Accepted = false,
                Reason = decision.Reason ?? "Solicitud rechazada."
            };
        }

        await WriteFrameAsync(stream, JsonSerializer.Serialize(response), ct).ConfigureAwait(false);
    }

    private async Task HandleFileRpcAsync(Stream stream, string json, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<FileRpcRequest>(json);
        if (req is null) return;

        if (!_incomingSessions.TryGetValue(req.SessionId, out var hostSession) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hostSession.Token),
                Encoding.UTF8.GetBytes(req.SessionToken)))
        {
            await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileListResponse
            {
                Ok = false,
                Error = "Sesión inválida o desconectada."
            }), ct).ConfigureAwait(false);
            return;
        }

        var policy = hostSession.Session.Policy;

        switch (req.Type)
        {
            case "list_roots":
            {
                var roots = _policyResolver.ResolveAllowedRoots(policy);
                var items = roots.Select(r =>
                {
                    var trimmed = r.TrimEnd(Path.DirectorySeparatorChar);
                    var label = trimmed.Length <= 2 && trimmed.EndsWith(':')
                        ? $"Disco ({trimmed})"
                        : Path.GetFileName(trimmed);
                    return new FileSystemItem
                    {
                        Name = label,
                        FullPath = r,
                        IsDirectory = true,
                        TypeDisplay = "Carpeta compartida",
                        ProviderId = hostSession.Session.SessionId
                    };
                }).ToList();

                await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileListResponse
                {
                    Ok = true,
                    Items = items
                }), ct).ConfigureAwait(false);
                break;
            }

            case "list_dir":
            {
                var target = req.Path ?? string.Empty;
                if (!_policyResolver.IsPathAllowed(target, policy) || !Directory.Exists(target))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileListResponse
                    {
                        Ok = false,
                        Error = "Ruta no autorizada o inexistente."
                    }), ct).ConfigureAwait(false);
                    return;
                }

                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = req.IncludeHidden
                        ? FileAttributes.System
                        : FileAttributes.Hidden | FileAttributes.System
                };

                var enumerable = new FileSystemEnumerable<FileSystemItem>(
                    target,
                    (ref FileSystemEntry entry) =>
                    {
                        var name = entry.FileName.ToString();
                        var isDir = entry.IsDirectory;
                        var ext = isDir ? string.Empty : Path.GetExtension(name);
                        return new FileSystemItem
                        {
                            Name = name,
                            FullPath = entry.ToFullPath(),
                            IsDirectory = isDir,
                            SizeBytes = isDir ? 0 : entry.Length,
                            LastModifiedUtc = entry.LastWriteTimeUtc.UtcDateTime,
                            Extension = ext,
                            TypeDisplay = isDir
                                ? "Carpeta"
                                : string.IsNullOrEmpty(ext) ? "Archivo" : ext.TrimStart('.').ToUpperInvariant(),
                            IsHidden = (entry.Attributes & FileAttributes.Hidden) != 0,
                            ProviderId = hostSession.Session.SessionId
                        };
                    },
                    options);

                var list = enumerable.ToList();
                list.Sort((a, b) =>
                {
                    var cmp = b.IsDirectory.CompareTo(a.IsDirectory);
                    return cmp != 0 ? cmp : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                });

                await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileListResponse
                {
                    Ok = true,
                    Items = list
                }), ct).ConfigureAwait(false);
                break;
            }

            case "download_file":
            {
                var target = req.Path ?? string.Empty;
                if (!_policyResolver.IsPathAllowed(target, policy) || !File.Exists(target))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileTransferHeaderResponse
                    {
                        Ok = false,
                        Error = "Archivo no autorizado o inexistente."
                    }), ct).ConfigureAwait(false);
                    return;
                }

                var fi = new FileInfo(target);
                await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileTransferHeaderResponse
                {
                    Ok = true,
                    FileName = fi.Name,
                    SizeBytes = fi.Length
                }), ct).ConfigureAwait(false);

                await using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await fs.CopyToAsync(stream, 65536, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                break;
            }

            case "upload_file":
            {
                var targetDir = req.Path ?? string.Empty;
                var fileName = Path.GetFileName(req.FileName ?? string.Empty);

                if (policy.Permission != AccessPermission.ReadWrite)
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileTransferHeaderResponse
                    {
                        Ok = false,
                        Error = "Esta sesión es de solo lectura."
                    }), ct).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(fileName) || !_policyResolver.IsPathAllowed(targetDir, policy) || !Directory.Exists(targetDir))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileTransferHeaderResponse
                    {
                        Ok = false,
                        Error = "Carpeta de destino no autorizada."
                    }), ct).ConfigureAwait(false);
                    return;
                }

                await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileTransferHeaderResponse
                {
                    Ok = true,
                    FileName = fileName,
                    SizeBytes = req.SizeBytes
                }), ct).ConfigureAwait(false);

                var destPath = Path.Combine(targetDir, fileName);
                await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    var buffer = new byte[65536];
                    long remaining = req.SizeBytes;
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                        if (read == 0) break;
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        remaining -= read;
                    }
                }

                await WriteFrameAsync(stream, JsonSerializer.Serialize(new FileTransferHeaderResponse
                {
                    Ok = true,
                    FileName = fileName,
                    SizeBytes = req.SizeBytes
                }), ct).ConfigureAwait(false);
                break;
            }

            case "create_dir":
            {
                var parent = req.Path ?? string.Empty;
                var name = Path.GetFileName((req.NewName ?? string.Empty).Trim());
                if (policy.Permission != AccessPermission.ReadWrite)
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "Sesión de solo lectura." }), ct).ConfigureAwait(false);
                    return;
                }
                if (string.IsNullOrWhiteSpace(name) || !_policyResolver.IsPathAllowed(parent, policy) || !Directory.Exists(parent))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "Carpeta destino no autorizada." }), ct).ConfigureAwait(false);
                    return;
                }
                Directory.CreateDirectory(Path.Combine(parent, name));
                await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = true }), ct).ConfigureAwait(false);
                break;
            }

            case "rename_item":
            {
                var target = req.Path ?? string.Empty;
                var newName = Path.GetFileName((req.NewName ?? string.Empty).Trim());
                if (policy.Permission != AccessPermission.ReadWrite)
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "Sesión de solo lectura." }), ct).ConfigureAwait(false);
                    return;
                }
                if (string.IsNullOrWhiteSpace(newName) || !_policyResolver.IsPathAllowed(target, policy))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "Ruta no autorizada." }), ct).ConfigureAwait(false);
                    return;
                }
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent) || !_policyResolver.IsPathAllowed(parent, policy))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "No se puede renombrar la raíz." }), ct).ConfigureAwait(false);
                    return;
                }
                var dest = Path.Combine(parent, newName);
                if (Directory.Exists(target)) Directory.Move(target, dest);
                else if (File.Exists(target)) File.Move(target, dest);
                else
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "Elemento no encontrado." }), ct).ConfigureAwait(false);
                    return;
                }
                await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = true }), ct).ConfigureAwait(false);
                break;
            }

            case "delete_item":
            {
                var target = req.Path ?? string.Empty;
                if (policy.Permission != AccessPermission.ReadWrite)
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "Sesión de solo lectura." }), ct).ConfigureAwait(false);
                    return;
                }
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent) || !_policyResolver.IsPathAllowed(target, policy) || !_policyResolver.IsPathAllowed(parent, policy))
                {
                    await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = false, Error = "No se puede eliminar esta ruta." }), ct).ConfigureAwait(false);
                    return;
                }
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else if (File.Exists(target)) File.Delete(target);
                await WriteFrameAsync(stream, JsonSerializer.Serialize(new SimpleRpcResponse { Ok = true }), ct).ConfigureAwait(false);
                break;
            }
        }
    }

    private ActiveHostSession RegisterAcceptedIncomingSession(IncomingPairRequest req, AccessPolicy policy)
    {
        if (policy.RememberDevice)
        {
            _identity.SaveTrusted(new TrustedDeviceRecord
            {
                DeviceId = req.FromDeviceId,
                DisplayName = req.FromDisplayName,
                Policy = policy,
                AddedAtUtc = DateTime.UtcNow
            });
        }

        var session = new LanSession
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            Peer = new LanPeer
            {
                DeviceId = req.FromDeviceId,
                DisplayName = req.FromDisplayName,
                HostAddress = req.RemoteAddress,
                Port = 0,
                IsTrusted = policy.RememberDevice || _identity.FindTrusted(req.FromDeviceId) is not null,
                IsConnected = true
            },
            Policy = policy,
            ConnectedAtUtc = DateTime.UtcNow
        };

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var active = new ActiveHostSession(session, token);
        _incomingSessions[session.SessionId] = active;
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return active;
    }

    internal static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<string> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid LAN frame length.");

        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Stop();
    }
}
