using System;
using System.Diagnostics;
using System.IO;
using Backtrack.Pairing;

namespace Backtrack.Interop;

public static class FirewallRules
{
    private const string InboundUdpRuleName = "Backtrack Discovery (UDP-In)";
    private const string OutboundUdpRuleName = "Backtrack Discovery (UDP-Out)";
    private const string InboundTcpRuleName = "Backtrack Pairing (TCP-In)";
    private const string OutboundTcpRuleName = "Backtrack Pairing (TCP-Out)";

    public static (bool Success, string? Error) AddRulesElevated()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "Backtrack.exe");

        string logPath = Path.Combine(Path.GetTempPath(), $"backtrack-firewall-{Guid.NewGuid():N}.log");
        string wrapperPath = Path.Combine(Path.GetTempPath(), $"backtrack-firewall-{Guid.NewGuid():N}.cmd");
        try
        {
            string script =
                "@echo off\r\n" +
                $"echo === UDP in === >> \"{logPath}\"\r\n" +
                $"netsh advfirewall firewall add rule name=\"{InboundUdpRuleName}\" dir=in action=allow protocol=UDP localport={PairingService.BroadcastPort} program=\"{exePath}\" profile=any >> \"{logPath}\" 2>&1\r\n" +
                $"echo === UDP out === >> \"{logPath}\"\r\n" +
                $"netsh advfirewall firewall add rule name=\"{OutboundUdpRuleName}\" dir=out action=allow protocol=UDP localport={PairingService.BroadcastPort} program=\"{exePath}\" profile=any >> \"{logPath}\" 2>&1\r\n" +
                $"echo === TCP in === >> \"{logPath}\"\r\n" +
                $"netsh advfirewall firewall add rule name=\"{InboundTcpRuleName}\" dir=in action=allow protocol=TCP localport={PairingService.DefaultPairingPort} program=\"{exePath}\" profile=any >> \"{logPath}\" 2>&1\r\n" +
                $"echo === TCP out === >> \"{logPath}\"\r\n" +
                $"netsh advfirewall firewall add rule name=\"{OutboundTcpRuleName}\" dir=out action=allow protocol=TCP localport={PairingService.DefaultPairingPort} program=\"{exePath}\" profile=any >> \"{logPath}\" 2>&1\r\n" +
                "exit /b %ERRORLEVEL%\r\n";
            File.WriteAllText(wrapperPath, script);

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
            catch { }

            if (proc is null || proc.ExitCode != 0)
            {
                string exitCode = proc?.ExitCode.ToString() ?? "unknown";
                return (false, string.IsNullOrWhiteSpace(log)
                    ? $"Firewall rules could not be added (exit code {exitCode})."
                    : $"Firewall rules could not be added (exit code {exitCode}):\n{log}");
            }

            return (true, null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Admin permission was declined, so the firewall rules weren't added. Clip sharing with another PC may be blocked until they're added manually or Backtrack is allowed to try again.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(wrapperPath); } catch { }
            try { File.Delete(logPath); } catch { }
        }
    }
}
