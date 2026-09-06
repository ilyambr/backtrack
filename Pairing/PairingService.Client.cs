using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    private static readonly TimeSpan TrimRequestTimeout = TimeSpan.FromMinutes(5);

    public async Task<RamDiskSnapshot?> GetRemoteRamDiskSettingsAsync()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "get_ramdisk_settings", secret = _settings.PairedPeerSecret });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            return JsonSerializer.Deserialize<RamDiskSnapshot>(responseLine);
        }
        catch { return null; }
    }

    public async Task<(bool Success, string? Error)> SetRemoteRamDiskSettingsAsync(bool enabled, char driveLetter, int sizeMb)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.");

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            string request = JsonSerializer.Serialize(new
            {
                type = "set_ramdisk_settings",
                secret = _settings.PairedPeerSecret,
                enabled,
                driveLetter = driveLetter.ToString(),
                sizeMb,
            });
            await WriteLineAsync(client.GetStream(), request).WaitAsync(MutationRequestTimeout);
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(MutationRequestTimeout);
            if (responseLine is null)
                return (false, "No response from the transmitter PC.");

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            return (success, success ? null : (error ?? "Failed to update RamDisk settings on transmitter PC."));
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error, TcpClient? Client, NetworkStream? Stream, long RemainingSize)> OpenRemoteClipStreamAsync(string relativePath, long offset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null, null, 0);

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort, cancellationToken);
            string request = JsonSerializer.Serialize(new { type = "get_clip", secret = _settings.PairedPeerSecret, path = relativePath, offset });
            NetworkStream stream = client.GetStream();
            await WriteLineAsync(stream, request);

            string? headerLine = await ReadLineAsync(stream);
            if (headerLine is null)
            {
                client.Dispose();
                return (false, "No response from the transmitter PC.", null, null, 0);
            }

            using JsonDocument doc = JsonDocument.Parse(headerLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            if (!success)
            {
                client.Dispose();
                return (false, doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : "Streaming failed.", null, null, 0);
            }

            long size = doc.RootElement.GetProperty("size").GetInt64();
            return (true, null, client, stream, size);
        }
        catch (Exception ex)
        {
            client.Dispose();
            return (false, ex.Message, null, null, 0);
        }
    }

    public async Task<PluginVersionsSnapshot?> CheckRemotePluginUpdatesAsync()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "check_plugin_updates", secret = _settings.PairedPeerSecret });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            return JsonSerializer.Deserialize<PluginVersionsSnapshot>(responseLine);
        }
        catch { return null; }
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
                return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
            if (buffer[0] == (byte)'\n')
                return Encoding.UTF8.GetString(ms.ToArray());
            ms.WriteByte(buffer[0]);
        }
    }
}
