using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Backtrack.Interop;

public static class SelfUninstall
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Backtrack";

    public static bool IsInstalledViaInstaller()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("UninstallString") is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static (bool Success, string? Error) BeginUninstall()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        string? uninstallString = key?.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(uninstallString))
            return (false, "Couldn't find Backtrack's uninstall registry entry -- this build may not have come from the installer (e.g. a dev/source build), so there's nothing registered to run.");

        int pid = Environment.ProcessId;
        string wrapperPath = Path.Combine(Path.GetTempPath(), $"backtrack-uninstall-{Guid.NewGuid():N}.cmd");
        string script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"{uninstallString}\r\n" +
            "del \"%~f0\"\r\n";

        try
        {
            File.WriteAllText(wrapperPath, script);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{wrapperPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (true, null);
    }
}
