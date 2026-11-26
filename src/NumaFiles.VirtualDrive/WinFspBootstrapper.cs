using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace NumaFiles.VirtualDrive;

public static class WinFspBootstrapper
{
    public const string OfficialMsiFileName = "winfsp-2.2.26323.msi";
    public const string OfficialMsiUrl =
        "https://github.com/winfsp/winfsp/releases/download/v2.2/winfsp-2.2.26323.msi";

    private static bool _nativeLoaded;
    private static bool _resolverRegistered;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public static void RegisterAssemblyResolver()
    {
        if (_resolverRegistered)
            return;

        _resolverRegistered = true;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name?.Equals("winfsp-msil", StringComparison.OrdinalIgnoreCase) == true)
            {
                var msilPath = FindWinFspMsilPath();
                if (msilPath is not null)
                    return ctx.LoadFromAssemblyPath(msilPath);
            }
            return null;
        };
    }

    public static string? FindWinFspMsilPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-msil.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin", "winfsp-msil.dll")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindWinFspDllPath()
    {
        var dllName = Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", dllName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin", dllName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsInstalled() => FindWinFspDllPath() is not null && FindWinFspMsilPath() is not null;

    public static void EnsureNativeLibraryLoaded()
    {
        RegisterAssemblyResolver();

        if (_nativeLoaded)
            return;

        var dllPath = FindWinFspDllPath()
            ?? throw new InvalidOperationException("No se encontró winfsp-x64.dll. Instala WinFsp primero.");

        var binDir = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(binDir))
            SetDllDirectory(binDir);

        if (!NativeLibrary.TryLoad(dllPath, out _))
            throw new DllNotFoundException($"No se pudo cargar {dllPath}.");

        _nativeLoaded = true;
    }

    public static async Task<bool> InstallAsync(IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (IsInstalled())
            return true;

        var msiPath = FindLocalMsi();
        if (msiPath is null)
        {
            status?.Report("Descargando WinFsp 2.2 (2.2 MB)...");
            msiPath = Path.Combine(Path.GetTempPath(), OfficialMsiFileName);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NumaLink/1.0");
            await using var stream = await http.GetStreamAsync(OfficialMsiUrl, ct).ConfigureAwait(false);
            await using var fs = new FileStream(msiPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        status?.Report("Instalando WinFsp (acepta el permiso de Windows)...");
        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{msiPath}\" /qn /norestart",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return false;

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return IsInstalled();
    }

    private static string? FindLocalMsi()
    {
        var baseDir = AppContext.BaseDirectory;
        var probes = new[]
        {
            Path.Combine(baseDir, OfficialMsiFileName),
            Path.Combine(baseDir, "installer", OfficialMsiFileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "installer", OfficialMsiFileName))
        };

        return probes.FirstOrDefault(File.Exists);
    }
}
