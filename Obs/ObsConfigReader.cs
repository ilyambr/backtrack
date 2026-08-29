using System;
using System.IO;
using System.Text.Json;

namespace Backtrack.Obs;

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
                return false;

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
