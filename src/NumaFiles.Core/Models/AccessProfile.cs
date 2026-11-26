namespace NumaFiles.Core.Models;

public enum AccessLevel
{
    Full,
    Media,
    Custom
}

public enum AccessPermission
{
    ReadOnly,
    ReadWrite
}

public enum VisibilityMode
{
    EveryoneOnLan,
    TrustedOnly,
    Hidden
}

public sealed record AccessPolicy
{
    public required AccessLevel Level { get; init; }
    public AccessPermission Permission { get; init; } = AccessPermission.ReadOnly;
    public IReadOnlyList<string> CustomRoots { get; init; } = Array.Empty<string>();
    public bool RememberDevice { get; init; }
}

public sealed record LanPeer
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string HostAddress { get; init; }
    public int Port { get; init; }
    public string DeviceType { get; init; } = "windows";
    public bool IsTrusted { get; init; }
    public bool IsConnected { get; init; }
    public DateTime LastSeenUtc { get; init; } = DateTime.UtcNow;
}

public sealed record LanSession
{
    public required string SessionId { get; init; }
    public required LanPeer Peer { get; init; }
    public required AccessPolicy Policy { get; init; }
    public DateTime ConnectedAtUtc { get; init; } = DateTime.UtcNow;
}
