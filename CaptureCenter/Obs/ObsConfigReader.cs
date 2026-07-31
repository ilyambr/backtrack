using System;
using System.IO;
using System.Text.Json;

namespace CaptureCenter.Obs;

/// <summary>
/// Reads obs-websocket's own config file so the user never has to copy/paste
/// the password OBS generated for itself into this app separately. We only
/// ever read this file, never write it -- enabling the server itself is a
/// security-relevant setting left for the user to flip in OBS's own UI
/// (Tools > WebSocket Server Settings).
/// </summary>
public static class ObsConfigReader
{
    public static (bool ServerEnabled, string? Password) ReadLocalConfig()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obs-studio", "plugin_config", "obs-websocket", "config.json");

            if (!File.Exists(path))
                return (false, null);

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            bool enabled = root.TryGetProperty("server_enabled", out JsonElement se) && se.GetBoolean();
            bool authRequired = !root.TryGetProperty("auth_required", out JsonElement ar) || ar.GetBoolean();
            string? password = authRequired && root.TryGetProperty("server_password", out JsonElement pw)
                ? pw.GetString()
                : null;

            return (enabled, password);
        }
        catch
        {
            return (false, null);
        }
    }
}
