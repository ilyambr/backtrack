using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Backtrack.Core;
using Backtrack.Obs;

namespace Backtrack.Pairing;

public sealed record BufferPreferencesSnapshot(List<string> HiddenBuffers, Dictionary<string, string> NameOverrides);

public sealed partial class PairingService
{
    public event Action? OnBufferPreferencesChangedFromRemote;

    private bool IsAuthorizedForBufferPrefs(JsonElement request)
    {
        if (IsAuthorizedClient(request))
            return true;

        string secret = request.TryGetProperty("secret", out JsonElement s) ? s.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(secret))
            return false;

        try
        {
            (_, string? localObsPassword) = ObsConfigReader.ReadLocalConfig();
            if (!string.IsNullOrEmpty(localObsPassword) &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(localObsPassword)))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private string HandleGetBufferPreferences(JsonElement request)
    {
        if (!IsAuthorizedForBufferPrefs(request))
            return JsonSerializer.Serialize(new { success = false, error = "unauthorized" });

        return JsonSerializer.Serialize(new
        {
            success = true,
            hiddenBuffers = _settings.HiddenBufferLabels.ToList(),
            nameOverrides = _settings.LocalRowNameOverrides
        });
    }

    private string HandleSetBufferPreferences(JsonElement request)
    {
        if (!IsAuthorizedForBufferPrefs(request))
            return JsonSerializer.Serialize(new { success = false, error = "unauthorized" });

        bool changed = false;
        if (request.TryGetProperty("hiddenBuffers", out JsonElement hb) && hb.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in hb.EnumerateArray())
            {
                string? s = el.GetString();
                if (!string.IsNullOrEmpty(s))
                    set.Add(s);
            }
            if (!_settings.HiddenBufferLabels.SetEquals(set))
            {
                _settings.HiddenBufferLabels = set;
                changed = true;
            }
        }

        if (request.TryGetProperty("nameOverrides", out JsonElement no) && no.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in no.EnumerateObject())
            {
                string? val = prop.Value.GetString();
                if (!string.IsNullOrEmpty(val))
                    dict[prop.Name] = val;
            }
            _settings.LocalRowNameOverrides = dict;
            changed = true;
        }

        if (changed)
        {
            _settings.Save();
            OnBufferPreferencesChangedFromRemote?.Invoke();
        }

        return JsonSerializer.Serialize(new { success = true });
    }

    public async Task<BufferPreferencesSnapshot?> GetRemoteBufferPreferencesAsync()
    {
        string host = !string.IsNullOrEmpty(_settings.PairedPeerHost)
            ? _settings.PairedPeerHost
            : (_settings.ObsIsRemote ? _settings.ObsHost : "");
        int port = _settings.PairedPeerPort > 0
            ? _settings.PairedPeerPort
            : DefaultPairingPort;

        string secret = !string.IsNullOrEmpty(_settings.PairedPeerSecret)
            ? _settings.PairedPeerSecret
            : (_settings.ObsIsRemote && !string.IsNullOrEmpty(_settings.ObsRemotePassword) ? _settings.ObsRemotePassword : "");

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(secret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(2));
            string request = JsonSerializer.Serialize(new
            {
                type = "get_buffer_preferences",
                secret
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(TimeSpan.FromSeconds(2));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(2));
            if (responseLine != null)
            {
                using JsonDocument doc = JsonDocument.Parse(responseLine);
                if (doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean())
                {
                    var hidden = new List<string>();
                    if (doc.RootElement.TryGetProperty("hiddenBuffers", out JsonElement hb) && hb.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in hb.EnumerateArray())
                        {
                            string? str = el.GetString();
                            if (!string.IsNullOrEmpty(str))
                                hidden.Add(str);
                        }
                    }

                    var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (doc.RootElement.TryGetProperty("nameOverrides", out JsonElement no) && no.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in no.EnumerateObject())
                        {
                            string? val = prop.Value.GetString();
                            if (!string.IsNullOrEmpty(val))
                                overrides[prop.Name] = val;
                        }
                    }

                    return new BufferPreferencesSnapshot(hidden, overrides);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] GetRemoteBufferPreferencesAsync failed: {ex.Message}");
        }
        return null;
    }

    public async Task<bool> SendUpdateBufferPreferencesAsync(IEnumerable<string> hiddenBuffers, IDictionary<string, string> nameOverrides)
    {
        string host = !string.IsNullOrEmpty(_settings.PairedPeerHost)
            ? _settings.PairedPeerHost
            : (_settings.ObsIsRemote ? _settings.ObsHost : "");
        int port = _settings.PairedPeerPort > 0
            ? _settings.PairedPeerPort
            : DefaultPairingPort;

        string secret = !string.IsNullOrEmpty(_settings.PairedPeerSecret)
            ? _settings.PairedPeerSecret
            : (_settings.ObsIsRemote && !string.IsNullOrEmpty(_settings.ObsRemotePassword) ? _settings.ObsRemotePassword : "");

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(secret))
            return false;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(2));
            string request = JsonSerializer.Serialize(new
            {
                type = "set_buffer_preferences",
                secret,
                hiddenBuffers = hiddenBuffers.ToList(),
                nameOverrides
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(TimeSpan.FromSeconds(2));
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(2));
            if (responseLine != null)
            {
                using JsonDocument doc = JsonDocument.Parse(responseLine);
                return doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Pairing] SendUpdateBufferPreferencesAsync failed: {ex.Message}");
        }
        return false;
    }
}
