using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Updates;

public sealed record ReleaseInfo(string Version, string? DownloadUrl);

/// <summary>
/// Checks GitHub's "latest release" endpoint (never drafts/prereleases -- that
/// API only ever returns the most recent published one, which matters here
/// since e.g. obs-replay-slider has newer draft releases sitting ahead of its
/// actual published latest) for the app itself and for the two OBS plugins,
/// and can silently apply whichever updates it finds.
/// </summary>
public sealed class UpdateService
{
    private const string ObsInstallDir = @"C:\Program Files\obs-studio";
    private const string ObsPluginsDir = ObsInstallDir + @"\obs-plugins\64bit";
    private const string Obs64Path = ObsInstallDir + @"\bin\64bit\obs64.exe";

    // Inno Setup's standard unattended flags: no UI, no "reboot now?" prompt, and
    // /SP- skips the "This will install... Do you wish to continue?" prompt too.
    private const string InnoSetupSilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        // GitHub's API rejects requests with no User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Backtrack", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.Timeout = TimeSpan.FromSeconds(15);
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
            if (doc.RootElement.TryGetProperty("assets", out JsonElement assets))
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                    if (name is not null && assetPredicate(name))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        break;
                    }
                }
            }

            return new ReleaseInfo(tag, downloadUrl);
        }
        catch
        {
            // No network, GitHub unreachable, repo has zero releases yet, etc. -- just
            // means "nothing to update", not worth surfacing as an error.
            return null;
        }
    }

    /// <summary>Compares only Major.Minor.Build -- release tags are plain "0.2.8", not 4-part assembly versions.</summary>
    public static bool IsNewer(string candidateVersion, Version installed)
    {
        if (!TryParseTriple(candidateVersion, out Version candidate))
            return false;
        return candidate > installed;
    }

    public static bool IsNewer(string candidateVersion, string installedVersion)
    {
        if (!TryParseTriple(installedVersion, out Version installed))
            return true; // no installed version known -- treat anything found as newer
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

    // ------------------------------------------------------------- plugins

    public Version GetInstalledPluginVersion(string dllFileName)
    {
        string path = Path.Combine(ObsPluginsDir, dllFileName);
        if (!File.Exists(path))
            return new Version(0, 0, 0);
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0), Math.Max(info.FileBuildPart, 0));
    }

    /// <summary>
    /// Downloads and silently installs a plugin's Windows installer, closing OBS
    /// first if it's running (installing over a loaded plugin DLL fails while
    /// OBS holds it open) and relaunching OBS afterward if it was running.
    /// </summary>
    public async Task InstallPluginUpdateAsync(string downloadUrl)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"cc_update_{Guid.NewGuid():N}.exe");
        await DownloadFileAsync(downloadUrl, tempPath);

        bool wasObsRunning = await CloseObsIfRunningAsync();

        var psi = new ProcessStartInfo(tempPath, InnoSetupSilentArgs) { UseShellExecute = true };
        using Process? installer = Process.Start(psi);
        if (installer is not null)
            await installer.WaitForExitAsync();

        try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }

        if (wasObsRunning && File.Exists(Obs64Path))
            Process.Start(new ProcessStartInfo(Obs64Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Obs64Path) });
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
                // Ignore -- fall through to the wait/kill below regardless.
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

    // --------------------------------------------------------------- self

    public static Version CurrentAppVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Downloads a "backtrack-{version}-windows-x64.zip" release asset, extracts
    /// it, and writes a small batch script that waits for this process to exit,
    /// mirrors the new files over the current install directory, and relaunches --
    /// a running .exe can't overwrite itself directly, so a detached helper script
    /// does the actual file swap after Backtrack has exited.
    /// </summary>
    public async Task ApplySelfUpdateAsync(string downloadUrl)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "Backtrack.exe");

        string zipPath = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}.zip");
        string extractDir = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}");
        await DownloadFileAsync(downloadUrl, zipPath);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

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
             robocopy "{extractDir}" "{installDir}" /E /IS /IT
             del "{zipPath}"
             rmdir /S /Q "{extractDir}"
             start "" "{exePath}"
             del "%~f0"
             """);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static async Task DownloadFileAsync(string url, string destPath)
    {
        using HttpResponseMessage response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using FileStream file = File.Create(destPath);
        await response.Content.CopyToAsync(file);
    }
}
