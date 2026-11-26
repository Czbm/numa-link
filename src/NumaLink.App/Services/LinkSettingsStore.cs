using System.IO;
using System.Text.Json;
using NumaFiles.Core.Models;

namespace NumaLink.App.Services;

public enum LinkLayoutStyle
{
    QuickShareTabs,
    TwoColumnHorizontal,
    BentoGrid
}

public enum LinkColorMode
{
    Dark,
    Light
}

public enum LinkLanguage
{
    Es,
    En
}

public sealed class LinkSettings
{
    public const string DefaultUpdateRepo = "Czbm/numa-link";

    public LinkLayoutStyle Layout { get; set; } = LinkLayoutStyle.QuickShareTabs;
    public LinkColorMode ColorMode { get; set; } = LinkColorMode.Dark;
    public LinkLanguage Language { get; set; } = LinkLanguage.Es;
    public VisibilityMode Visibility { get; set; } = VisibilityMode.EveryoneOnLan;
    public bool StartWithWindows { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateRepo { get; set; } = DefaultUpdateRepo;
}

public static class LinkSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NumaFiles",
        "numalink_settings.json");

    public static LinkSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<LinkSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.UpdateRepo))
                    {
                        loaded.UpdateRepo = LinkSettings.DefaultUpdateRepo;
                        Save(loaded);
                    }
                    return loaded;
                }
            }
        }
        catch { }
        return new LinkSettings();
    }

    public static void Save(LinkSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch { }
    }
}
