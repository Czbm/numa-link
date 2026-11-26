using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fsp;
using Microsoft.Win32;
using NumaFiles.Core.Models;
using NumaFiles.LanShare;

namespace NumaFiles.VirtualDrive;

public sealed record MountedDriveInfo
{
    public required string SessionId { get; init; }
    public required string DeviceName { get; init; }
    public required string DriveLetter { get; init; } // e.g. "Z:"
    public required string MountPoint { get; init; }  // e.g. "Z:\\"
    public required string AccessSummary { get; init; }
}

public sealed class VirtualDriveManager : IDisposable
{
    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string? dwItem1, IntPtr dwItem2);

    private readonly ConcurrentDictionary<string, (MountedDriveInfo Info, object Host)> _mounted =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? MountedDrivesChanged;

    public IReadOnlyList<MountedDriveInfo> GetMountedDrives() =>
        _mounted.Values.Select(v => v.Info).OrderBy(i => i.DriveLetter).ToList();

    public MountedDriveInfo Mount(ConnectedRemoteEndpoint endpoint)
    {
        WinFspBootstrapper.EnsureNativeLibraryLoaded();

        if (_mounted.TryGetValue(endpoint.SessionId, out var existing))
            return existing.Info;

        var letter = FindNextAvailableDriveLetter()
            ?? throw new InvalidOperationException("No hay letras de unidad disponibles.");

        ApplyDriveExplorerIcon(letter, endpoint.Peer.DeviceType);
        var host = MountInternal(endpoint, letter);

        var level = endpoint.Policy.Level switch
        {
            AccessLevel.Full => "Completo",
            AccessLevel.Media => "Multimedia",
            AccessLevel.Custom => "Personalizado",
            _ => "LAN"
        };
        var perm = endpoint.Policy.Permission == AccessPermission.ReadWrite ? "Lectura/Escritura" : "Solo lectura";

        var info = new MountedDriveInfo
        {
            SessionId = endpoint.SessionId,
            DeviceName = endpoint.Peer.DisplayName,
            DriveLetter = letter,
            MountPoint = letter + "\\",
            AccessSummary = $"{level} · {perm}"
        };

        _mounted[endpoint.SessionId] = (info, host);
        NotifyShellDriveIcon(info.MountPoint);
        MountedDrivesChanged?.Invoke(this, EventArgs.Empty);
        return info;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object MountInternal(ConnectedRemoteEndpoint endpoint, string letter)
    {
        var fs = new LanVirtualFileSystem(endpoint);
        var host = new FileSystemHost(fs);
        var mountResult = host.Mount(letter, null, true, 0);
        if (mountResult != 0)
            throw new IOException($"No se pudo montar la unidad {letter} (código WinFsp: 0x{mountResult:X}).");
        return host;
    }

    public bool Unmount(string sessionId)
    {
        if (_mounted.TryRemove(sessionId, out var entry))
        {
            try
            {
                UnmountInternal(entry.Host);
            }
            catch
            {
                // Ignore unmount errors
            }
            ClearDriveExplorerIcon(entry.Info.DriveLetter);
            NotifyShellDriveIcon(entry.Info.MountPoint);
            MountedDrivesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }

    private static void ApplyDriveExplorerIcon(string driveLetterWithColon, string? deviceType)
    {
        try
        {
            var letterOnly = driveLetterWithColon.TrimEnd(':', '\\').ToUpperInvariant();
            if (letterOnly.Length != 1) return;

            var isMobile = string.Equals(deviceType, "android", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deviceType, "ios", StringComparison.OrdinalIgnoreCase);

            // imageres.dll,-109 = Official Windows 10/11 "This PC / Este equipo" icon
            // imageres.dll,-102 = Official Windows mobile/portable device icon
            var iconSpec = isMobile
                ? @"%SystemRoot%\System32\imageres.dll,-102"
                : @"%SystemRoot%\System32\imageres.dll,-109";

            var keyPath = $@"Software\Classes\Applications\Explorer.exe\Drives\{letterOnly}\DefaultIcon";
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key?.SetValue(string.Empty, iconSpec, RegistryValueKind.ExpandString);
        }
        catch
        {
            // Non-fatal: drive still mounts with default icon
        }
    }

    private static void ClearDriveExplorerIcon(string driveLetterWithColon)
    {
        try
        {
            var letterOnly = driveLetterWithColon.TrimEnd(':', '\\').ToUpperInvariant();
            if (letterOnly.Length != 1) return;

            var parentPath = @"Software\Classes\Applications\Explorer.exe\Drives";
            using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
            parent?.DeleteSubKeyTree(letterOnly, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void NotifyShellDriveIcon(string mountPoint)
    {
        try
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, mountPoint, IntPtr.Zero);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, null, IntPtr.Zero);
        }
        catch
        {
            // Ignore shell notify errors
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnmountInternal(object hostObj)
    {
        if (hostObj is FileSystemHost host)
            host.Unmount();
    }

    public void UnmountAll()
    {
        foreach (var id in _mounted.Keys.ToList())
            Unmount(id);
    }

    private static string? FindNextAvailableDriveLetter()
    {
        var used = new HashSet<string>(
            DriveInfo.GetDrives().Select(d => d.Name[..2]),
            StringComparer.OrdinalIgnoreCase);

        for (char c = 'Z'; c >= 'N'; c--)
        {
            var candidate = $"{c}:";
            if (!used.Contains(candidate))
                return candidate;
        }

        return null;
    }

    public void Dispose() => UnmountAll();
}
