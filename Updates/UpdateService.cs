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
using Microsoft.Win32;

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
    // Not hardcoded -- see ResolveObsInstallDir. Cached once resolved with real
    // confidence (registry or a currently-running OBS), but NOT cached when it
    // falls all the way through to the bare default guess, so a later call
    // (e.g. once OBS actually starts) still gets a chance to find the real path
    // instead of being stuck on a wrong guess for the rest of the session.
    private static string? _cachedObsInstallDir;
    private static string ObsInstallDir => _cachedObsInstallDir ?? ResolveObsInstallDir();
    private static string ObsPluginsDir => Path.Combine(ObsInstallDir, "obs-plugins", "64bit");
    private static string Obs64Path => Path.Combine(ObsInstallDir, "bin", "64bit", "obs64.exe");

    /// <summary>
    /// OBS's own (Inno Setup) installer writes its install directory to
    /// HKLM\SOFTWARE\OBS Studio's default value -- confirmed directly against a
    /// real install -- so reading that instead of assuming the common default
    /// path is what actually survives someone installing to a different drive.
    /// Portable installs never touch the registry at all, so those fall back to
    /// deriving the path from obs64.exe's own location if it happens to be
    /// running right now (".../bin/64bit/obs64.exe" -> three levels up). If
    /// neither source has an answer yet, falls back to the old hardcoded
    /// default so callers always get *something* rather than null.
    /// </summary>
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
            // Key missing, access denied, etc. -- fall through to the next source.
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
            // Process/module inspection can throw (e.g. access denied on a
            // 32-bit/64-bit module mismatch) -- fall through either way.
        }

        return @"C:\Program Files\obs-studio";
    }

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
        // This same client is also used to download release assets (the self-update
        // zip is 200MB+), not just lightweight API calls -- a 15s timeout meant for
        // the latter silently killed every download attempt well before it could
        // finish, which looked like the update check just doing nothing.
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
    ///
    /// Passes the new version as a --updated= argument to the relaunched process,
    /// since this process is about to exit (Application.Current.Shutdown() runs
    /// right after this returns) -- a toast shown here would close with the window
    /// before anyone could see it. The freshly-launched process reads that arg on
    /// startup and shows the toast itself instead (see App.xaml.cs).
    /// </summary>
    public async Task ApplySelfUpdateAsync(string downloadUrl, string version)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "Backtrack.exe");

        string zipPath = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}.zip");
        string extractDir = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}");
        await DownloadFileAsync(downloadUrl, zipPath);
        // ZipFile.ExtractToDirectory is fully synchronous -- since none of the
        // awaits above use ConfigureAwait(false), the continuation after them
        // resumes on the UI thread by default, so calling it directly here froze
        // the whole app (including the Check Now button) for as long as
        // extracting a 200MB+ self-contained build took. Task.Run moves the
        // actual CPU/IO-bound work off the UI thread; awaiting it still keeps
        // this method's own async flow correct.
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
             robocopy "{extractDir}" "{installDir}" /E /IS /IT
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

    private static async Task DownloadFileAsync(string url, string destPath)
    {
        using HttpResponseMessage response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using FileStream file = File.Create(destPath);
        await response.Content.CopyToAsync(file);
    }
}
