using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Backtrack.Interop;

public static class RamDisk
{
    private const string ServiceName = "ImDisk";

    public static bool IsDriverInstalled()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return key is not null;
    }

    public static (bool Success, string? Error) InstallDriverElevated()
    {
        string bundleDir = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "ImDisk");
        string installScript = Path.Combine(bundleDir, "install.cmd");
        if (!File.Exists(installScript))
            return (false, $"Bundled ImDisk installer is missing ({installScript}).");

        string logPath = Path.Combine(Path.GetTempPath(), $"backtrack-imdisk-install-{Guid.NewGuid():N}.log");
        string wrapperPath = Path.Combine(Path.GetTempPath(), $"backtrack-imdisk-wrapper-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllText(wrapperPath,
                "@echo off\r\n" +
                "set IMDISK_SILENT_SETUP=1\r\n" +
                $"cd /d \"{bundleDir}\"\r\n" +
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

    public static (bool Success, string? Error) Mount(char driveLetter, int sizeMb)
    {
        if (IsMounted(driveLetter))
            Unmount(driveLetter);

        (int exitCode, string output) = RunImDisk($"-a -s {sizeMb}M -m {driveLetter}: -p \"/fs:ntfs /q /y\"");
        if (exitCode != 0 || !IsMounted(driveLetter))
        {
            RunImDisk($"-D -m {driveLetter}:");
            string reason = string.IsNullOrWhiteSpace(output) ? $"imdisk exited with code {exitCode}." : output;
            return (false, TranslateMountError(reason, sizeMb));
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

    private static string TranslateMountError(string reason, int sizeMb)
    {
        if (!reason.Contains("not enough memory", StringComparison.OrdinalIgnoreCase))
            return reason;

        return $"Windows didn't have enough free memory to create a {sizeMb} MB RAM disk just now. " +
            "This comes out of the system's overall memory headroom (RAM + page file), not just free RAM -- " +
            "other running apps can eat into that even when Task Manager shows plenty free. " +
            "Try again in a moment, close something memory-heavy, or lower the size in Settings.\n\n" +
            $"(ImDisk said: {reason})";
    }

    private static (int ExitCode, string Output) RunImDisk(string arguments, int timeoutMs = 60_000)
    {
        string bundledExe = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "ImDisk", "cli", "amd64", "imdisk.exe");
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
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, $"imdisk.exe didn't finish within {timeoutMs / 1000}s and was killed. Try a smaller size.");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        string combined = string.Join("\n", new[] { stdout, stderr }
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
        return (proc.ExitCode, combined);
    }
}
