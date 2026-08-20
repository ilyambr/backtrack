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

/// <summary>
/// Digest is the matched asset's own "sha256:..." content hash (GitHub computes
/// and returns this for uploaded release assets) -- the authoritative signal
/// for "is this literally the same file", immune to clock skew or metadata-only
/// touches. PublishedAt is that same asset's "updated_at" (not the release's
/// created_at), kept as a fallback for the rare asset that has no digest.
/// Either one exists specifically because re-uploading a replacement file to an
/// existing release changes neither the release's tag nor its created_at --
/// this repo's own release workflow (see obs-replay-slider / obs-source-record)
/// sometimes reuses the same version tag for a small fix, so version-number
/// comparison alone misses that case entirely.
/// </summary>
public sealed record ReleaseInfo(string Version, string? DownloadUrl, DateTimeOffset? PublishedAt, string? Digest);

/// <summary>
/// Checks GitHub's "latest release" endpoint (never drafts/prereleases -- that
/// API only ever returns the most recent published one, which matters here
/// since e.g. obs-replay-slider has newer draft releases sitting ahead of its
/// actual published latest) for the app itself and for the two OBS plugins,
/// and can silently apply whichever updates it finds.
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// Set from AppSettings.DeveloperModeEnabled (Settings > Experimental >
    /// Diagnostics) -- the actual, sole authority for IsDevBuild below now,
    /// not an override on top of a path guess. MainWindow.LoadSettingsUi
    /// pre-sets it to true, once, the first time IsRunningFromDevLocation
    /// suggests it (see that property's own comment) -- after that one-time
    /// nudge it's fully user-controlled either direction, including turning
    /// it back off while running somewhere IsRunningFromDevLocation would
    /// still flag, or on while running from a genuinely installed copy.
    /// </summary>
    public static bool DeveloperModeEnabled { get; set; }

    /// <summary>
    /// Auto-update is deliberately never allowed to run here: a locally
    /// compiled binary's digest will essentially never match the official
    /// release's (builds aren't byte-reproducible across machines/compile
    /// runs even from identical source), so a dev build would ALWAYS look
    /// "out of date" by the digest check regardless of its version string --
    /// and worse, letting the startup auto-apply run would silently overwrite
    /// whatever's actively being tested with the real published release.
    ///
    /// Used to be a path comparison against a single hardcoded install
    /// location, which broke the moment the installer could put Backtrack
    /// anywhere else (see the installer's own new folder-picker) -- ANY
    /// custom-but-legitimate install location would have permanently and
    /// silently misidentified itself as a dev build forever, no way to
    /// self-correct. DeveloperModeEnabled is the real signal now;
    /// IsRunningFromDevLocation only ever feeds it a one-time initial guess.
    /// </summary>
    public static bool IsDevBuild => DeveloperModeEnabled;

    /// <summary>
    /// True unless running from wherever the installer itself last recorded
    /// as the real install location (its own uninstall registry key's
    /// InstallLocation value -- see installer/Program.cs), or that key
    /// doesn't exist at all (never installed through it). Purely a one-time
    /// suggestion signal for MainWindow.LoadSettingsUi to pre-set
    /// DeveloperModeEnabled with -- see that property's own comment on why
    /// this isn't IsDevBuild's actual authority anymore.
    /// </summary>
    public static bool IsRunningFromDevLocation
    {
        get
        {
            string? installedDir = Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Backtrack")?
                .GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(installedDir))
                return true; // never installed through installer/Program.cs at all

            string running = AppContext.BaseDirectory.TrimEnd('\\', '/');
            return !string.Equals(running, installedDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

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
    /// True only when obs64.exe actually exists at the resolved install dir --
    /// real proof OBS is installed on THIS machine, regardless of which of
    /// ResolveObsInstallDir's three sources found it (registry, a running
    /// process, or the bare hardcoded-default guess). A receiver-only PC
    /// (paired to a transmitter's OBS over the network, see PairingService)
    /// legitimately has no local OBS install at all -- callers use this to
    /// skip the plugin update check/install entirely there instead of
    /// silently downloading and running an installer that has nothing to
    /// install into, which just surfaced as update errors.
    /// </summary>
    public bool IsObsInstalled => File.Exists(Obs64Path);

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
                        // Not present on every asset ever uploaded (GitHub only
                        // started computing this at some point) -- null here just
                        // means callers fall back to the PublishedAt comparison.
                        digest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                        break;
                    }
                }
            }

            return new ReleaseInfo(tag, downloadUrl, publishedAt, digest);
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

    /// <summary>The exact sentinel GetInstalledPluginVersion returns when the DLL genuinely isn't there -- a shared named constant so callers checking for "actually missing" (not just "an old version") compare against the one real source of truth instead of a second `new Version(0, 0, 0)` literal that could drift from it.</summary>
    public static readonly Version MissingPluginVersion = new(0, 0, 0);

    public Version GetInstalledPluginVersion(string dllFileName)
    {
        string path = Path.Combine(ObsPluginsDir, dllFileName);
        if (!File.Exists(path))
            return MissingPluginVersion;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0), Math.Max(info.FileBuildPart, 0));
    }

    /// <summary>
    /// Downloads and silently installs a plugin's Windows installer, closing OBS
    /// first if it's running (installing over a loaded plugin DLL fails while
    /// OBS holds it open).
    ///
    /// reopenAfterInstall=false (used when updating more than one plugin in the
    /// same batch -- see CheckForUpdatesAsync) skips relaunching here; the
    /// caller is responsible for doing that itself, once, after every plugin in
    /// the batch has been installed. Reopening after EACH individual plugin
    /// used to mean: close OBS, install plugin 1, relaunch OBS, then almost
    /// immediately close it again for plugin 2 -- OBS still mid-startup (main
    /// window not up yet, websocket server not listening yet) got killed out
    /// from under itself, which is exactly the kind of race that would explain
    /// the second plugin's update looking like it failed right after the first
    /// one succeeded. Returns whether OBS was actually running (and so got
    /// closed) either way, so the caller knows whether it owes a reopen.
    /// </summary>
    public async Task<bool> InstallPluginUpdateAsync(string downloadUrl, string? expectedDigest = null, bool reopenAfterInstall = true)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"cc_update_{Guid.NewGuid():N}.exe");
        await DownloadFileAsync(downloadUrl, tempPath, expectedDigest);

        bool wasObsRunning = await CloseObsIfRunningAsync();

        var psi = new ProcessStartInfo(tempPath, InnoSetupSilentArgs) { UseShellExecute = true };
        using Process? installer = Process.Start(psi);
        if (installer is not null)
            await installer.WaitForExitAsync();

        try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }

        if (reopenAfterInstall && wasObsRunning)
            RelaunchObsIfInstalled();

        return wasObsRunning;
    }

    /// <summary>Extracted so a caller managing OBS lifecycle across several plugin installs (see reopenAfterInstall above) can call this itself, once, after the whole batch.</summary>
    public void RelaunchObsIfInstalled()
    {
        if (File.Exists(Obs64Path))
            Process.Start(new ProcessStartInfo(Obs64Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Obs64Path) });
    }

    // AppId GUIDs baked into each plugin's own Inno Setup installer.iss -- Inno
    // registers its uninstall entry under "{AppId}_is1", not by display name,
    // so this GUID is the only lookup key stable across every version either
    // plugin has ever shipped.
    private const string SourceRecordAppId = "E0B6FC31-8FD5-4921-95DA-066EBE79A2AE";
    private const string ReplaySliderAppId = "CA1D94AF-4931-4719-9192-E307B75887E9";

    public Task<(bool Success, string? Error)> UninstallSourceRecordAsync() => UninstallInnoPluginAsync(SourceRecordAppId, "Source Record");
    public Task<(bool Success, string? Error)> UninstallReplaySliderAsync() => UninstallInnoPluginAsync(ReplaySliderAppId, "Replay Slider");

    /// <summary>
    /// Runs an Inno-Setup-installed plugin's own bundled uninstaller silently
    /// (the same unins000.exe Windows' own "Apps &amp; features" would run),
    /// closing OBS first since the uninstaller can't delete a plugin DLL OBS
    /// still has loaded -- same reasoning as InstallPluginUpdateAsync above,
    /// just never reopening OBS afterward (there's nothing to reopen it for).
    /// </summary>
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

    /// <summary>
    /// Inno Setup writes its uninstall registry key to whichever hive/view
    /// matches how the installer itself was built (LocalMachine for a normal
    /// system-wide install, the 32-bit view on a 32-bit build even on 64-bit
    /// Windows) -- checking all three here instead of guessing one avoids a
    /// false "not installed" for a real install that just landed somewhere
    /// other than the one view checked.
    /// </summary>
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
    public async Task ApplySelfUpdateAsync(string downloadUrl, string version, string? expectedDigest = null)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "Backtrack.exe");

        string zipPath = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}.zip");
        string extractDir = Path.Combine(Path.GetTempPath(), $"backtrack_update_{Guid.NewGuid():N}");
        await DownloadFileAsync(downloadUrl, zipPath, expectedDigest);
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

    /// <summary>
    /// expectedDigest is GitHub's own "digest" field for this release asset
    /// (ReleaseInfo.Digest, "sha256:&lt;hex&gt;") -- GetLatestReleaseAsync was
    /// already fetching this for every asset, but only ever used it to detect
    /// "did the asset change" (ShouldApplyUpdate), never to confirm the bytes
    /// that actually landed on disk are the bytes GitHub said they'd be. HTTPS
    /// (already in use here) protects the transfer itself; this catches
    /// anything else that could put different bytes at that URL by the time
    /// they're downloaded -- a compromised/rotated release asset, a corrupted
    /// download, a proxy/cache doing something it shouldn't. Doesn't (can't)
    /// protect against a compromised GitHub account publishing a legitimately
    /// matching malicious build in the first place; still real value for the
    /// same reason checking a downloaded installer's own published checksum
    /// always is elsewhere. Null (an old asset with no digest published, or
    /// a caller that doesn't have one) just skips the check entirely rather
    /// than blocking on something that was never verifiable to begin with.
    /// </summary>
    private static async Task DownloadFileAsync(string url, string destPath, string? expectedDigest = null)
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
