using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public sealed record LanBeaconPacket
{
    public string Magic { get; init; } = "NUMA_FILES_V1";
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required int TcpPort { get; init; }
    public required VisibilityMode Visibility { get; init; }
    public string DeviceType { get; init; } = "windows";
}

public sealed record LanEnvelope
{
    public required string Type { get; init; }
}

public sealed record PairRequestPacket
{
    public string Type { get; init; } = "pair_request";
    public required string RequestId { get; init; }
    public required string FromDeviceId { get; init; }
    public required string FromDisplayName { get; init; }
    public required string VerificationCode { get; init; }
}

public sealed record PairResponsePacket
{
    public string Type { get; init; } = "pair_response";
    public required string RequestId { get; init; }
    public required bool Accepted { get; init; }
    public string? SessionId { get; init; }
    public string? SessionToken { get; init; }
    public AccessPolicy? Policy { get; init; }
    public IReadOnlyList<string> AllowedRoots { get; init; } = Array.Empty<string>();
    public Dictionary<string, string>? RootDisplayNames { get; init; }
    public string? Reason { get; init; }
}

public sealed record FileRpcRequest
{
    public required string Type { get; init; } // list_roots | list_dir | download_file | upload_file | create_dir | rename_item | delete_item
    public required string SessionId { get; init; }
    public required string SessionToken { get; init; }
    public string? Path { get; init; }
    public string? FileName { get; init; }
    public string? NewName { get; init; }
    public long SizeBytes { get; init; }
    public bool IncludeHidden { get; init; }
}

public sealed record SimpleRpcResponse
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
}

public sealed record FileListResponse
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<FileSystemItem> Items { get; init; } = Array.Empty<FileSystemItem>();
}

public sealed record FileTransferHeaderResponse
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public string? FileName { get; init; }
    public long SizeBytes { get; init; }
}

public sealed record IncomingPairRequest
{
    public required string RequestId { get; init; }
    public required string FromDeviceId { get; init; }
    public required string FromDisplayName { get; init; }
    public required string RemoteAddress { get; init; }
    public required string VerificationCode { get; init; }
}

public sealed record PairDecision
{
    public required bool Accepted { get; init; }
    public AccessPolicy? Policy { get; init; }
    public string? Reason { get; init; }

    public static PairDecision Reject(string reason = "Solicitud rechazada") =>
        new() { Accepted = false, Reason = reason };

    public static PairDecision Accept(AccessPolicy policy) =>
        new() { Accepted = true, Policy = policy };
}

public sealed record ConnectedRemoteEndpoint
{
    public required string SessionId { get; init; }
    public required string SessionToken { get; init; }
    public required LanPeer Peer { get; init; }
    public required AccessPolicy Policy { get; init; }
    public IReadOnlyList<string> AllowedRoots { get; init; } = Array.Empty<string>();
    public Dictionary<string, string>? RootDisplayNames { get; init; }
}
