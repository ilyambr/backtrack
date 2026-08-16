using System;
using System.IO;
using System.Text.Json;

namespace Backtrack.Obs;

/// <summary>
/// Reads (and, in one narrow case, writes) obs-websocket's own config file so
/// the user never has to copy/paste the password OBS generated for itself
/// into this app separately.
/// </summary>
public static class ObsConfigReader
{
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    public static (bool ServerEnabled, string? Password) ReadLocalConfig()
    {
        try
        {
            string path = ConfigPath;
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

    /// <summary>
    /// Local-mode only: silently flips server_enabled to true in this file
    /// when it's off, so the NEXT time OBS launches it just connects, no
    /// manual trip to Tools > WebSocket Server Settings required. Meant to be
    /// called only while OBS itself isn't running (see MainWindow's own
    /// gate) -- obs-websocket only reads this file at its own startup, so
    /// rewriting it while OBS is already open with the server off wouldn't
    /// take effect until a restart anyway, and risks a lost-update race if
    /// OBS happens to save its own settings back to this same file in
    /// between. Every other field (password, port, auth_required, ...) is
    /// passed through untouched. Returns true only if it actually changed
    /// something, so the caller can log/react once instead of every poll tick.
    /// </summary>
    public static bool TryEnableServer()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
                return false;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("server_enabled", out JsonElement se) && se.GetBoolean())
                return false; // already on -- nothing to do

            using MemoryStream ms = new();
            using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                bool wroteServerEnabled = false;
                foreach (JsonProperty prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("server_enabled"))
                    {
                        writer.WriteBoolean("server_enabled", true);
                        wroteServerEnabled = true;
                    }
                    else
                        prop.WriteTo(writer);
                }
                // The key not existing at all (not just being false) is the
                // same "off" as far as the caller's check above is concerned
                // (TryGetProperty returns false either way) -- rewriting the
                // loop above alone would silently never actually add it,
                // still returning true below, which looked "fixed" but
                // wasn't: the next poll would read it as off again and retry
                // forever, logging every tick.
                if (!wroteServerEnabled)
                    writer.WriteBoolean("server_enabled", true);
                writer.WriteEndObject();
            }

            File.WriteAllBytes(path, ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
