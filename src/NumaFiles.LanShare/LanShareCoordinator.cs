using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public interface ILanShareCoordinator : IDisposable
{
    string LocalDeviceName { get; }
    int ListeningPort { get; }
    VisibilityMode Visibility { get; set; }
    IReadOnlyList<LanPeer> NearbyPeers { get; }
    IReadOnlyList<LanSession> ActiveSessions { get; }
    IReadOnlyList<LanSession> IncomingSessions { get; }
    IReadOnlyList<ConnectedRemoteEndpoint> ConnectedEndpoints { get; }
    IReadOnlyList<TrustedDeviceRecord> TrustedDevices { get; }

    Func<IncomingPairRequest, Task<PairDecision>>? IncomingRequestHandler { get; set; }
    event EventHandler? StateChanged;

    void Start();
    void SetLocalDeviceName(string displayName);
    Task<PairResponsePacket> RequestConnectionAsync(LanPeer peer, string verificationCode, CancellationToken ct = default);
    Task<PairDecision> SimulateIncomingRequestAsync(string fromDisplayName, string verificationCode);
    bool UpdateIncomingSessionPolicy(string sessionId, AccessPolicy newPolicy);
    void DisconnectSession(string sessionId);
    void RevokeTrustedDevice(string deviceId);
}

public sealed class LanShareCoordinator : ILanShareCoordinator
{
    private readonly DeviceIdentityStore _identity;
    private readonly AccessPolicyResolver _policyResolver;
    private readonly LanPairingService _pairing;
    private readonly LanDiscoveryService _discovery;
    private VisibilityMode _visibility = VisibilityMode.EveryoneOnLan;

    public LanShareCoordinator()
    {
        _identity = new DeviceIdentityStore();
        _policyResolver = new AccessPolicyResolver();
        _pairing = new LanPairingService(_identity, _policyResolver, () => _visibility);
        _discovery = new LanDiscoveryService(_identity, () => _pairing.ListeningPort, () => _visibility);

        _discovery.PeersChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _pairing.SessionsChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public string LocalDeviceName => _identity.DisplayName;
    public int ListeningPort => _pairing.ListeningPort;

    public VisibilityMode Visibility
    {
        get => _visibility;
        set
        {
            if (_visibility == value) return;
            _visibility = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<LanPeer> NearbyPeers => _discovery.GetPeers();
    public IReadOnlyList<LanSession> ActiveSessions => _pairing.GetAllSessions();
    public IReadOnlyList<LanSession> IncomingSessions => _pairing.GetIncomingSessions();
    public IReadOnlyList<ConnectedRemoteEndpoint> ConnectedEndpoints => _pairing.GetOutgoingConnections();
    public IReadOnlyList<TrustedDeviceRecord> TrustedDevices => _identity.GetTrustedDevices();

    public Func<IncomingPairRequest, Task<PairDecision>>? IncomingRequestHandler
    {
        get => _pairing.IncomingRequestHandler;
        set => _pairing.IncomingRequestHandler = value;
    }

    public event EventHandler? StateChanged;

    public void Start()
    {
        _pairing.Start();
        _discovery.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLocalDeviceName(string displayName)
    {
        _identity.SetDisplayName(displayName);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<PairResponsePacket> RequestConnectionAsync(
        LanPeer peer,
        string verificationCode,
        CancellationToken ct = default)
    {
        var response = await _pairing.RequestConnectionAsync(peer, verificationCode, ct).ConfigureAwait(false);
        if (response.Accepted)
            _discovery.UpdatePeerConnectionState(peer.DeviceId, isConnected: true);
        return response;
    }

    public Task<PairDecision> SimulateIncomingRequestAsync(string fromDisplayName, string verificationCode) =>
        _pairing.SimulateIncomingRequestAsync(fromDisplayName, verificationCode);

    public bool UpdateIncomingSessionPolicy(string sessionId, AccessPolicy newPolicy)
    {
        var ok = _pairing.UpdateIncomingSessionPolicy(sessionId, newPolicy);
        if (ok) StateChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public void DisconnectSession(string sessionId)
    {
        var session = _pairing.GetAllSessions().FirstOrDefault(s => s.SessionId == sessionId);
        if (_pairing.DisconnectSession(sessionId) && session is not null)
        {
            _discovery.UpdatePeerConnectionState(session.Peer.DeviceId, isConnected: false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RevokeTrustedDevice(string deviceId)
    {
        if (_identity.RevokeTrusted(deviceId))
        {
            _discovery.UpdatePeerConnectionState(deviceId, isConnected: false, isTrusted: false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _discovery.Dispose();
        _pairing.Dispose();
    }
}
