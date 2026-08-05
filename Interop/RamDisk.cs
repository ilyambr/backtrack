using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Backtrack.Interop;

/// <summary>
/// Installs (once) and mounts/unmounts an ImDisk-backed RAM disk, so OBS's
/// Replay Buffer can write its (large, constantly-rewritten) buffer file to
/// memory instead of a real drive -- writing a multi-GB buffer file to RAM is
/// near-instant vs. several seconds to even a fast SSD. The small final
/// trimmed clip still lands on persistent storage afterward; see
/// obs-replay-slider's "Move clips to:" destination folder, which MainWindow
/// points at ClipsFolder once this disk is mounted.
///
/// ImDisk (ltr-data.se), not a custom driver: free, open source, and its
/// driver ships signed by a certificate Windows already trusts, so it works
/// under Secure Boot without this app needing its own signing setup. The
/// install package is bundled unmodified under ThirdParty/ImDisk per its
/// license (see the LICENSE.md/README.md alongside it).
/// </summary>
public static class RamDisk
{
    // The actual kernel driver service is "ImDisk" (confirmed against
    // imdisk.inf's [DefaultInstall.ntamd64.Services] -- "ImDskSvc" alongside it
    // is a separate Win32 helper service, not the driver itself).
    private const string ServiceName = "ImDisk";

    // ServiceController.GetServices() only enumerates Win32-type services, not
    // kernel/file-system drivers -- it silently omits "ImDisk" (a
    // SERVICE_KERNEL_DRIVER) regardless of name, so checking the registry
    // directly is the only reliable way to see whether the driver is present.
    public static bool IsDriverInstalled()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return key is not null;
    }

    /// <summary>
    /// Runs the vendor's own install.cmd elevated, with IMDISK_SILENT_SETUP=1 so
    /// it skips every message box. That env var is set inside the elevated shell
    /// itself (via "cmd /c set X=1&amp;&amp; script"), not passed in through
    /// ProcessStartInfo.EnvironmentVariables -- environment variables set there
    /// are not reliably delivered when UseShellExecute+runas hands the launch off
    /// to the elevation broker instead of this process spawning the child
    /// directly. This is the one and only UAC prompt the whole feature needs;
    /// everything else here runs unelevated.
    /// </summary>
    public static (bool Success, string? Error) InstallDriverElevated()
    {
        string bundleDir = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "ImDisk");
        string installScript = Path.Combine(bundleDir, "install.cmd");
        if (!File.Exists(installScript))
            return (false, $"Bundled ImDisk installer is missing ({installScript}).");

        // UseShellExecute+"runas" (needed to actually trigger the UAC prompt) can't
        // be combined with RedirectStandardOutput/Error -- .NET flatly disallows
        // that combination -- so there was no way to see WHY a failed install
        // failed beyond a bare exit code, ever. Routing through a temp wrapper
        // script that redirects install.cmd's own output to a log file (then
        // reading that file back afterward) is the standard workaround: it lets
        // the elevated process run exactly as before while still capturing what
        // it actually printed.
        string logPath = Path.Combine(Path.GetTempPath(), $"backtrack-imdisk-install-{Guid.NewGuid():N}.log");
        string wrapperPath = Path.Combine(Path.GetTempPath(), $"backtrack-imdisk-wrapper-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(wrapperPath,
                "@echo off\r\n" +
                "set IMDISK_SILENT_SETUP=1\r\n" +
                $"cd /d \"{bundleDir}\"\r\n" +
                // ".\install.cmd", not the bare filename -- cmd.exe's own command
                // resolution does NOT search the current directory for a plain
                // relative name (that's only how *interactive* typing behaves);
                // without the explicit ".\" prefix this fails with
                // "'install.cmd' is not recognized..." even though the file is
                // right there, and cmd.exe still exits 0, making the failure
                // invisible unless the log is actually checked.
                $"call .\\install.cmd > \"{logPath}\" 2>&1\r\n" +
                "exit /b %ERRORLEVEL%\r\n");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{wrapperPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using Process? proc = Process.Start(psi);
            proc?.WaitForExit();

            string log = "";
            try
            {
                if (File.Exists(logPath))
                    log = File.ReadAllText(logPath).Trim();
            }
            catch { /* best effort -- still report the exit code below either way */ }

            if (proc is null || proc.ExitCode != 0)
            {
                string exitCode = proc?.ExitCode.ToString() ?? "unknown";
                return (false, string.IsNullOrWhiteSpace(log)
                    ? $"ImDisk installer did not complete successfully (exit code {exitCode})."
                    : $"ImDisk installer did not complete successfully (exit code {exitCode}):\n{log}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED -- user said no to the UAC prompt.
            return (false, "Admin permission was declined, so the ImDisk driver wasn't installed.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(wrapperPath); } catch { /* best effort cleanup */ }
            try { File.Delete(logPath); } catch { /* best effort cleanup */ }
        }

        return IsDriverInstalled() ? (true, null) : (false, "Driver install finished but the service still isn't present.");
    }

    /// <summary>
    /// Mounts (or re-mounts, if a stale one is already sitting on that drive
    /// letter) an NTFS RAM disk. No elevation needed -- imdisk.exe talks to the
    /// already-installed, already-running driver/service, which is configured
    /// to allow ordinary authenticated users to create/manage its own devices.
    /// </summary>
    public static (bool Success, string? Error) Mount(char driveLetter, int sizeMb)
    {
        if (IsMounted(driveLetter))
            Unmount(driveLetter);

        (int exitCode, string output) = RunImDisk($"-a -s {sizeMb}M -m {driveLetter}: -p \"/fs:ntfs /q /y\"");
        if (exitCode != 0 || !IsMounted(driveLetter))
        {
            // A failed attempt (e.g. it ran out of memory partway through
            // formatting -- this is a real, size-dependent failure mode for a
            // memory-backed disk, not a bug) can still leave a half-created
            // device claiming this drive letter even though the volume itself
            // never came up, which would collide with the next attempt. Force
            // it off; -D (capital) works even when the plain -d unmount above
            // can't (the volume was never fully live, so there's nothing for a
            // graceful dismount to release).
            RunImDisk($"-D -m {driveLetter}:");
            return (false, string.IsNullOrWhiteSpace(output) ? $"imdisk exited with code {exitCode}." : output);
        }

        return (true, null);
    }

    public static (bool Success, string? Error) Unmount(char driveLetter)
    {
        if (!IsMounted(driveLetter))
            return (true, null);

        (int exitCode, string output) = RunImDisk($"-d -m {driveLetter}:");
        if (exitCode != 0)
            return (false, string.IsNullOrWhiteSpace(output) ? $"imdisk exited with code {exitCode}." : output);

        return (true, null);
    }

    public static bool IsMounted(char driveLetter) => Directory.Exists($"{driveLetter}:\\");

    private static (int ExitCode, string Output) RunImDisk(string arguments, int timeoutMs = 60_000)
    {
        string bundledExe = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "ImDisk", "cli", "amd64", "imdisk.exe");
        // Falls back to a bare PATH lookup if somehow not bundled (e.g. the driver
        // was already installed system-wide by something else) -- a real install
        // always registers imdisk.exe under System32, which is already on PATH.
        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(bundledExe) ? bundledExe : "imdisk.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process proc = Process.Start(psi)!;
        // Both reads must be started before WaitForExit, and drained
        // concurrently, not one-after-the-other -- imdisk.exe writes to both
        // stdout and stderr, and reading either stream fully before starting the
        // other risks the classic redirect deadlock: the child blocks trying to
        // write to the stream nobody's draining yet, while this thread blocks
        // waiting for the stream it IS reading to hit EOF, which never happens
        // because the child is stuck. That's exactly what was hanging Mount/
        // Unmount (and freezing the whole UI, since these used to be called
        // directly on the UI thread) on anything past a trivially small disk.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        // A hard ceiling, not a normal-case expectation -- a RAM-backed quick
        // format should never actually take anywhere near this long. This is
        // just so a stuck imdisk.exe (for whatever reason) can't hang the whole
        // app indefinitely the way it did before the deadlock fix above.
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, $"imdisk.exe didn't finish within {timeoutMs / 1000}s and was killed. Try a smaller size.");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        // Both, not "stdout, falling back to stderr only if stdout is empty" --
        // imdisk.exe prints its ordinary progress lines ("Creating device...")
        // to stdout and then the actual failure reason (e.g. "Error creating
        // virtual disk: Not enough memory resources are available...") to
        // stderr, so preferring stdout whenever it's non-empty was showing the
        // harmless progress line and silently dropping the one line that
        // actually explained what went wrong.
        string combined = string.Join("\n", new[] { stdout, stderr }
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
        return (proc.ExitCode, combined);
    }
}
