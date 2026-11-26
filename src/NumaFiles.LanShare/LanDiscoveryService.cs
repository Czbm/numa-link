using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public sealed class LanDiscoveryService : IDisposable
{
    public const int DiscoveryPort = 45821;
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(10);

    private readonly DeviceIdentityStore _identity;
    private readonly Func<int> _tcpPortProvider;
    private readonly Func<VisibilityMode> _visibilityProvider;
    private readonly ConcurrentDictionary<string, LanPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private UdpClient? _listener;

    public LanDiscoveryService(
        DeviceIdentityStore identity,
        Func<int> tcpPortProvider,
        Func<VisibilityMode> visibilityProvider)
    {
        _identity = identity;
        _tcpPortProvider = tcpPortProvider;
        _visibilityProvider = visibilityProvider;
    }

    public event EventHandler? PeersChanged;

    public IReadOnlyList<LanPeer> GetPeers() =>
        _peers.Values
            .OrderByDescending(p => p.IsConnected)
            .ThenByDescending(p => p.IsTrusted)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public void UpdatePeerConnectionState(string deviceId, bool isConnected, bool? isTrusted = null)
    {
        if (_peers.TryGetValue(deviceId, out var existing))
        {
            _peers[deviceId] = existing with
            {
                IsConnected = isConnected,
                IsTrusted = isTrusted ?? existing.IsTrusted
            };
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _listener.EnableBroadcast = true;

            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
        catch
        {
            // Port binding failed; still allow outbound broadcasts
        }

        _ = Task.Run(() => BroadcastAndCleanupLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var result = await _listener.ReceiveAsync(ct).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var packet = JsonSerializer.Deserialize<LanBeaconPacket>(json);
                if (packet is null || packet.Magic != "NUMA_FILES_V1")
                    continue;

                if (packet.DeviceId.Equals(_identity.DeviceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (packet.Visibility == VisibilityMode.Hidden)
                    continue;

                var isTrusted = _identity.FindTrusted(packet.DeviceId) is not null;
                var wasConnected = _peers.TryGetValue(packet.DeviceId, out var prev) && prev.IsConnected;

                var updated = new LanPeer
                {
                    DeviceId = packet.DeviceId,
                    DisplayName = packet.DisplayName,
                    HostAddress = result.RemoteEndPoint.Address.ToString(),
                    Port = packet.TcpPort,
                    DeviceType = string.IsNullOrWhiteSpace(packet.DeviceType) ? "windows" : packet.DeviceType,
                    IsTrusted = isTrusted,
                    IsConnected = wasConnected,
                    LastSeenUtc = DateTime.UtcNow
                };

                var changed = prev is null
                    || prev.DisplayName != updated.DisplayName
                    || prev.HostAddress != updated.HostAddress
                    || prev.Port != updated.Port
                    || prev.DeviceType != updated.DeviceType
                    || prev.IsTrusted != updated.IsTrusted;

                _peers[packet.DeviceId] = updated;
                if (changed)
                    PeersChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task BroadcastAndCleanupLoopAsync(CancellationToken ct)
    {
        using var sender = new UdpClient { EnableBroadcast = true };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var visibility = _visibilityProvider();
                var port = _tcpPortProvider();
                if (visibility != VisibilityMode.Hidden && port > 0)
                {
                    var packet = new LanBeaconPacket
                    {
                        DeviceId = _identity.DeviceId,
                        DisplayName = _identity.DisplayName,
                        TcpPort = port,
                        Visibility = visibility
                    };

                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
                    foreach (var target in GetBroadcastEndpoints())
                    {
                        try
                        {
                            await sender.SendAsync(payload, payload.Length, target).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignore individual interface errors
                        }
                    }
                }

                // Prune expired peers
                var cutoff = DateTime.UtcNow - PeerTimeout;
                var removedAny = false;
                foreach (var kv in _peers)
                {
                    if (!kv.Value.IsConnected && kv.Value.LastSeenUtc < cutoff)
                    {
                        if (_peers.TryRemove(kv.Key, out _))
                            removedAny = true;
                    }
                }

                if (removedAny)
                    PeersChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep loop resilient
            }

            try
            {
                await Task.Delay(BroadcastInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static IEnumerable<IPEndPoint> GetBroadcastEndpoints()
    {
        var endpoints = new HashSet<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ipBytes = ua.Address.GetAddressBytes();
                    var maskBytes = ua.IPv4Mask.GetAddressBytes();
                    if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                    endpoints.Add(new IPAddress(broadcast));
                }
            }
        }
        catch
        {
            // Fallback to 255.255.255.255
        }

        return endpoints.Select(ip => new IPEndPoint(ip, DiscoveryPort));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Dispose();
    }
}
