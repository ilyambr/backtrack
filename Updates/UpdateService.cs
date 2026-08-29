using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Backtrack.Updates;

public sealed record ReleaseInfo(string Version, string? DownloadUrl, DateTimeOffset? PublishedAt, string? Digest);

public sealed class UpdateService
{
    public static bool DeveloperModeEnabled { get; set; }

    public static bool IsDevBuild => DeveloperModeEnabled;

    public static bool IsRunningFromDevLocation
    {
        get
        {
            string? installedDir = Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Backtrack")?
                .GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(installedDir))
                return true;

            string running = AppContext.BaseDirectory.TrimEnd('\\', '/');
            return !string.Equals(running, installedDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? _cachedObsInstallDir;
    private static string ObsInstallDir => _cachedObsInstallDir ?? ResolveObsInstallDir();
    private static string ObsPluginsDir => Path.Combine(ObsInstallDir, "obs-plugins", "64bit");
    private static string Obs64Path => Path.Combine(ObsInstallDir, "bin", "64bit", "obs64.exe");

    public bool IsObsInstalled => File.Exists(Obs64Path);

    private static string ResolveObsInstallDir()
    {
        try
        {
            if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio")?.GetValue(null) is string regPath
                && Directory.Exists(regPath))
            {
                _cachedObsInstallDir = regPath;
                return regPath;
            }
        }
        catch
        {
        }

        try
        {
            string? exePath = Process.GetProcessesByName("obs64").FirstOrDefault()?.MainModule?.FileName;
            string? installDir = exePath is null ? null : Directory.GetParent(exePath)?.Parent?.Parent?.FullName;
            if (installDir is not null && Directory.Exists(installDir))
            {
                _cachedObsInstallDir = installDir;
                return installDir;
            }
        }
        catch
        {
        }

        return @"C:\Program Files\obs-studio";
    }

    private const string InnoSetupSilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Backtrack", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(string owner, string repo, Func<string, bool> assetPredicate)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            if (!response.IsSuccessStatusCode)
                return null;

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            string? tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrEmpty(tag))
                return null;

            string? downloadUrl = null;
            DateTimeOffset? publishedAt = null;
            string? digest = null;
            if (doc.RootElement.TryGetProperty("assets", out JsonElement assets))
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                    if (name is not null && assetPredicate(name))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        if (asset.TryGetProperty("updated_at", out JsonElement updatedEl) && updatedEl.TryGetDateTimeOffset(out DateTimeOffset updatedAt))
                            publishedAt = updatedAt;
                        digest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                        break;
                    }
                }
            }

            return new ReleaseInfo(tag, downloadUrl, publishedAt, digest);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string candidateVersion, Version installed)
    {
        if (!TryParseTriple(candidateVersion, out Version candidate))
            return false;
        return candidate > installed;
    }

    public static bool IsNewer(string candidateVersion, string installedVersion)
    {
        if (!TryParseTriple(installedVersion, out Version installed))
            return true;
        return IsNewer(candidateVersion, installed);
    }

    private static bool TryParseTriple(string text, out Version version)
    {
        string cleaned = text.TrimStart('v', 'V');
        if (Version.TryParse(cleaned, out Version? parsed))
        {
            version = new Version(Math.Max(parsed.Major, 0), Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public static readonly Version MissingPluginVersion = new(0, 0, 0);

    public Version GetInstalledPluginVersion(string dllFileName)
    {
        string path = Path.Combine(ObsPluginsDir, dllFileName);
        if (!File.Exists(path))
            return MissingPluginVersion;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0), Math.Max(info.FileBuildPart, 0));
    }

    public async Task<(bool WasObsRunning, bool Success)> InstallPluginUpdateAsync(string downloadUrl, string? expectedDigest = null, bool reopenAfterInstall = true)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"cc_update_{Guid.NewGuid():N}.exe");
        await DownloadFileAsync(downloadUrl, tempPath, expectedDigest);

        bool wasObsRunning = await CloseObsIfRunningAsync();

        int exitCode = -1;
        var psi = new ProcessStartInfo(tempPath, InnoSetupSilentArgs) { UseShellExecute = true };
        using (Process? installer = Process.Start(psi))
        {
            if (installer is not null)
            {
                await installer.WaitForExitAsync();
                exitCode = installer.ExitCode;
            }
        }

        try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }

        if (reopenAfterInstall && wasObsRunning)
            RelaunchObsIfInstalled();

        return (wasObsRunning, exitCode == 0);
    }

    public void RelaunchObsIfInstalled()
    {
        if (File.Exists(Obs64Path))
            Process.Start(new ProcessStartInfo(Obs64Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Obs64Path) });
    }

    private const string SourceRecordAppId = "E0B6FC31-8FD5-4921-95DA-066EBE79A2AE";
    private const string ReplaySliderAppId = "CA1D94AF-4931-4719-9192-E307B75887E9";

    public Task<(bool Success, string? Error)> UninstallSourceRecordAsync() => UninstallInnoPluginAsync(SourceRecordAppId, "Source Record");
    public Task<(bool Success, string? Error)> UninstallReplaySliderAsync() => UninstallInnoPluginAsync(ReplaySliderAppId, "Replay Slider");

    private async Task<(bool Success, string? Error)> UninstallInnoPluginAsync(string appId, string displayName)
    {
        string? uninstallString = FindInnoUninstallString(appId);
        if (uninstallString is null)
            return (false, $"Couldn't find {displayName}'s uninstall entry in the registry -- it may not be installed, or wasn't installed via its .exe installer.");

        await CloseObsIfRunningAsync();

        string exePath = uninstallString.Trim('"');
        try
        {
            var psi = new ProcessStartInfo(exePath, InnoSetupSilentArgs) { UseShellExecute = true };
            using Process? proc = Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (true, null);
    }

    private static string? FindInnoUninstallString(string appId)
    {
        string subKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{{{appId}}}_is1";
        (RegistryHive Hive, RegistryView View)[] locations =
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach ((RegistryHive hive, RegistryView view) in locations)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(subKeyPath);
            if (key?.GetValue("UninstallString") is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return null;
    }

    private static async Task<bool> CloseObsIfRunningAsync()
    {
        Process[] procs = Process.GetProcessesByName("obs64");
        if (procs.Length == 0)
            return false;

        foreach (Process proc in procs)
        {
            try
            {
                proc.CloseMainWindow();
            }
            catch
            {
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && procs.Any(p => !p.HasExited))
            await Task.Delay(300);

        foreach (Process proc in procs.Where(p => !p.HasExited))
        {
            try { proc.Kill(); } catch { /* already gone */ }
        }

        return true;
    }

    public static Version CurrentAppVersion
    {
        get
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null) return new Version(0, 0, 0);
            return new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        }
    }

    public async Task ApplySelfUpdateAsync(string downloadUrl, string version, string? expectedDigest = null)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "Backtrack.exe");

        string zipPath = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}.zip");
        string extractDir = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}");
        await DownloadFileAsync(downloadUrl, zipPath, expectedDigest);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));

        string scriptPath = Path.Combine(Path.GetTempPath(), $"backtrack_apply_update_{Guid.NewGuid():N}.bat");
        File.WriteAllText(scriptPath,
            $"""
             @echo off
             :wait
             tasklist /FI "PID eq {Environment.ProcessId}" 2>NUL | find "{Environment.ProcessId}" >NUL
             if not errorlevel 1 (
                 timeout /t 1 /nobreak >NUL
                 goto wait
             )
             rem Bounded retries -- robocopy defaults to up to 1,000,000 retries
             rem at 30s apart (effectively unbounded) if any target file is
             rem briefly locked (e.g. antivirus scanning the freshly-extracted
             rem files), which used to leave Backtrack closed and not coming
             rem back for a very long time with zero feedback. /R:5 /W:2 caps
             rem that to roughly 10 extra seconds.
             robocopy "{extractDir}" "{installDir}" /E /IS /IT /R:5 /W:2
             if %errorlevel% GEQ 8 (
                 rem Exit codes 0-7 are robocopy's various shades of success;
                 rem 8+ is a real failure. Don't delete the extracted files or
                 rem this script on failure -- keep them around for a human to
                 rem inspect/retry instead of silently discarding the only
                 rem evidence something went wrong. The old install should
                 rem still be intact and launchable either way, since a
                 rem failed copy means it was never (fully) overwritten;
                 rem relaunch it plain, with no --updated= flag, so it doesn't
                 rem claim an update that didn't actually apply.
                 start "" "{exePath}"
                 exit /b 1
             )
             del "{zipPath}"
             rmdir /S /Q "{extractDir}"
             start "" "{exePath}" --updated={version}
             del "%~f0"
             """);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private const int DownloadRetryAttempts = 3;

    private static async Task DownloadFileAsync(string url, string destPath, string? expectedDigest = null)
    {
        for (int attempt = 1; attempt <= DownloadRetryAttempts; attempt++)
        {
            try
            {
                await DownloadFileOnceAsync(url, destPath, expectedDigest);
                return;
            }
            catch when (attempt < DownloadRetryAttempts)
            {
                try { File.Delete(destPath); } catch { /* best effort */ }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static async Task DownloadFileOnceAsync(string url, string destPath, string? expectedDigest)
    {
        using HttpResponseMessage response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using (FileStream file = File.Create(destPath))
        {
            await response.Content.CopyToAsync(file);
        }

        if (string.IsNullOrEmpty(expectedDigest))
            return;

        string expectedHex = expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? expectedDigest["sha256:".Length..]
            : expectedDigest;

        string actualHex;
        await using (FileStream file = File.OpenRead(destPath))
        {
            byte[] hash = await SHA256.HashDataAsync(file);
            actualHex = Convert.ToHexString(hash);
        }

        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(destPath); } catch { /* best effort -- don't leave a mismatched file lying around either way */ }
            throw new InvalidOperationException(
                "Downloaded file's checksum didn't match what GitHub reported for this release asset -- refusing to install it.");
        }
    }
}
