using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Windows.Forms;
using System.Reflection;
using Microsoft.Win32;

namespace BacktrackSetup;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Backtrack");

        DialogResult res = MessageBox.Show($"Install Backtrack to:\n{installDir}\n\nThis will also add Backtrack to your Start Menu.", "Backtrack Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (res != DialogResult.OK) return;

        try
        {
            // Kill running Backtrack process if open
            foreach (var proc in Process.GetProcessesByName("Backtrack"))
            {
                try { proc.Kill(); proc.WaitForExit(2000); } catch { }
            }

            Directory.CreateDirectory(installDir);

            // Extract embedded payload.zip
            Assembly asm = Assembly.GetExecutingAssembly();
            string resourceName = "BacktrackSetup.payload.zip";
            using (Stream? stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    MessageBox.Show("Installer error: embedded payload.zip resource not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string tempZipPath = Path.Combine(Path.GetTempPath(), "BacktrackPayloadTemp.zip");
                using (FileStream fs = File.Create(tempZipPath))
                {
                    stream.CopyTo(fs);
                }

                ZipFile.ExtractToDirectory(tempZipPath, installDir, overwriteFiles: true);

                try { File.Delete(tempZipPath); } catch { }
            }

            // Create Start Menu Shortcut
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            string shortcutPath = Path.Combine(startMenuDir, "Backtrack.lnk");
            string exePath = Path.Combine(installDir, "Backtrack.exe");

            CreateShortcut(shortcutPath, exePath, installDir, "Backtrack - Instant replay for OBS");

            // Add Control Panel Uninstall Registry Key
            string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Backtrack";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(uninstallKeyPath))
            {
                key.SetValue("DisplayName", "Backtrack");
                key.SetValue("DisplayVersion", "0.1.3");
                key.SetValue("Publisher", "ilyambr");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", exePath);
                key.SetValue("UninstallString", $"powershell -Command \"Remove-Item -Path '{installDir}' -Recurse -Force; Remove-Item -Path '{shortcutPath}' -Force; Remove-Item -Path 'HKCU:\\{uninstallKeyPath}' -Force\"");
            }

            DialogResult runNow = MessageBox.Show("Backtrack has been installed successfully and added to your Start Menu!\n\nLaunch Backtrack now?", "Setup Complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (runNow == DialogResult.Yes && File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = installDir });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType != null)
        {
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell != null)
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = description;
                shortcut.Save();
            }
        }
    }
}
