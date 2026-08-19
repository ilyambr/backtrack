using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Backtrack.Interop;

/// <summary>
/// Uninstalls Backtrack itself, from inside Backtrack -- Settings &gt;
/// Destructive &gt; Uninstall Backtrack. Reuses the exact UninstallString the
/// installer (installer/Program.cs) already wrote to the registry rather than
/// re-deriving the install dir/shortcut path here a second time, so this
/// can't drift out of sync with whatever the installer actually did.
/// </summary>
public static class SelfUninstall
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Backtrack";

    public static bool IsInstalledViaInstaller()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("UninstallString") is string s && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>
    /// Launches a detached wrapper that waits for THIS process to actually
    /// exit before running the real uninstall command, then returns
    /// immediately so the caller can shut the app down. The uninstall
    /// command deletes this very exe's own folder -- Windows won't allow
    /// that while it's still loaded and running, so the ordering here isn't
    /// optional: start the wrapper, then quit, in that order, every time.
    /// </summary>
    public static (bool Success, string? Error) BeginUninstall()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        string? uninstallString = key?.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(uninstallString))
            return (false, "Couldn't find Backtrack's uninstall registry entry -- this build may not have come from the installer (e.g. a dev/source build), so there's nothing registered to run.");

        int pid = Environment.ProcessId;
        string wrapperPath = Path.Combine(Path.GetTempPath(), $"backtrack-uninstall-{Guid.NewGuid():N}.cmd");
        // The self-deleting-batch-file trick ("del %~f0" as the last line) --
        // standard cmd.exe idiom, works because cmd has already read the rest
        // of the file into memory before that line executes.
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
