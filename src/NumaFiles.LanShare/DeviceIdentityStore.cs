using System.Text.Json;
using NumaFiles.Core.Models;

namespace NumaFiles.LanShare;

public sealed record TrustedDeviceRecord
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required AccessPolicy Policy { get; init; }
    public DateTime AddedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class DeviceIdentityData
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = Environment.MachineName;
    public List<TrustedDeviceRecord> TrustedDevices { get; set; } = new();
}

public sealed class DeviceIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly object _gate = new();
    private DeviceIdentityData _data;

    public DeviceIdentityStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NumaFiles");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "identity.json");
        _data = LoadOrCreate();
    }

    public string DeviceId => _data.DeviceId;
    public string DisplayName
    {
        get
        {
            lock (_gate)
                return _data.DisplayName;
        }
    }

    public void SetDisplayName(string newName)
    {
        var clean = string.IsNullOrWhiteSpace(newName) ? Environment.MachineName : newName.Trim();
        lock (_gate)
        {
            _data.DisplayName = clean;
            Persist();
        }
    }

    public IReadOnlyList<TrustedDeviceRecord> GetTrustedDevices()
    {
        lock (_gate)
            return _data.TrustedDevices.ToList();
    }

    public TrustedDeviceRecord? FindTrusted(string deviceId)
    {
        lock (_gate)
            return _data.TrustedDevices.FirstOrDefault(t =>
                t.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
    }

    public void SaveTrusted(TrustedDeviceRecord record)
    {
        lock (_gate)
        {
            _data.TrustedDevices.RemoveAll(t =>
                t.DeviceId.Equals(record.DeviceId, StringComparison.OrdinalIgnoreCase));
            _data.TrustedDevices.Add(record);
            Persist();
        }
    }

    public bool RevokeTrusted(string deviceId)
    {
        lock (_gate)
        {
            var removed = _data.TrustedDevices.RemoveAll(t =>
                t.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Persist();
                return true;
            }
            return false;
        }
    }

    private DeviceIdentityData LoadOrCreate()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<DeviceIdentityData>(json, JsonOptions);
                if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.DeviceId))
                    return loaded;
            }
        }
        catch
        {
            // Fall through and recreate cleanly
        }

        var created = new DeviceIdentityData();
        _data = created;
        Persist();
        return created;
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Ignore disk write errors to keep UI resilient
        }
    }
}
