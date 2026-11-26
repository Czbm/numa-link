using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace NumaLink.App.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string Message,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? DirectExeUrl = null);

public static class UpdateCheckerService
{
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.6" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(string? githubRepo, LinkLanguage lang, CancellationToken ct = default)
    {
        var en = lang == LinkLanguage.En;
        var repo = string.IsNullOrWhiteSpace(githubRepo)
            ? LinkSettings.DefaultUpdateRepo
            : githubRepo.Trim();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"NumaLink/{CurrentVersion}");

            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp)
                ? (tagProp.GetString() ?? "").TrimStart('v', 'V')
                : "";

            var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp)
                ? urlProp.GetString()
                : $"https://github.com/{repo}/releases/latest";

            string? directExeUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assetsProp) &&
                assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null && name.Equals("NumaLink.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        directExeUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                        break;
                    }
                }
            }

            if (Version.TryParse(tag, out var remoteVer) &&
                Version.TryParse(CurrentVersion, out var localVer) &&
                remoteVer > localVer)
            {
                return new UpdateCheckResult(
                    true,
                    true,
                    en ? $"New version available: v{tag}" : $"Nueva versión disponible: v{tag}",
                    tag,
                    htmlUrl,
                    directExeUrl);
            }

            return new UpdateCheckResult(
                true,
                false,
                en ? $"You are up to date (v{CurrentVersion})." : $"Tienes la última versión (v{CurrentVersion}).",
                CurrentVersion,
                htmlUrl,
                directExeUrl);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                false,
                false,
                en ? $"Could not check updates: {ex.Message}" : $"No se pudo verificar actualizaciones: {ex.Message}");
        }
    }

    public static async Task<bool> DownloadAndApplyUpdateAsync(
        string directExeUrl,
        LinkLanguage lang,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var en = lang == LinkLanguage.En;
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var tempExe = Path.Combine(Path.GetTempPath(), $"NumaLink_update_{Guid.NewGuid():N}.exe");
        status?.Report(en ? "Downloading update..." : "Descargando actualización...");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"NumaLink/{CurrentVersion}");
            using var response = await http.GetAsync(directExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

            var buffer = new byte[65536];
            long received = 0;
            int read;
            int lastPercent = -1;

            while ((read = await netStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (totalBytes > 0)
                {
                    var pct = (int)(received * 100L / totalBytes);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        status?.Report(en ? $"Downloading update... {pct}%" : $"Descargando actualización... {pct}%");
                    }
                }
            }
        }

        if (new FileInfo(tempExe).Length < 1024 * 1024)
        {
            try { File.Delete(tempExe); } catch { }
            return false;
        }

        status?.Report(en ? "Applying update and restarting..." : "Aplicando actualización y reiniciando...");

        var pid = Environment.ProcessId;
        var batPath = Path.Combine(Path.GetTempPath(), $"numalink_apply_{Guid.NewGuid():N}.cmd");
        var script = $"""
            @echo off
            setlocal
            :waitloop
            tasklist /FI "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
            if not errorlevel 1 (
              timeout /t 1 /nobreak >NUL
              goto waitloop
            )
            taskkill /F /IM NumaLink.exe /T >NUL 2>&1
            move /Y "{tempExe}" "{currentExe}" >NUL
            start "" "{currentExe}"
            del "%~f0"
            """;

        await File.WriteAllTextAsync(batPath, script, ct).ConfigureAwait(false);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        return true;
    }
}
