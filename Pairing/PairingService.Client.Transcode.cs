using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Backtrack.Core;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    public async Task<(bool Success, string? Error, string? NewPath, long Size)> TrimRemoteClipAsync(string relativePath, TimeSpan start, TimeSpan end, bool replaceOriginal)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            AppLog.Write("[trim_clip] not sent: not paired with a transmitter PC");
            return (false, "Not paired with a transmitter PC.", null, 0);
        }

        AppLog.Write($"[trim_clip] sending: path='{relativePath}' start={start.TotalSeconds:0.###}s end={end.TotalSeconds:0.###}s replace={replaceOriginal} -> {_settings.PairedPeerHost}:{_settings.PairedPeerPort}");
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            var fields = new Dictionary<string, object?>
            {
                ["type"] = "trim_clip",
                ["secret"] = _settings.PairedPeerSecret,
                ["path"] = relativePath,
                ["startSeconds"] = start.TotalSeconds,
                ["endSeconds"] = end.TotalSeconds,
                ["replaceOriginal"] = replaceOriginal,
            };
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(MutationRequestTimeout);
            AppLog.Write("[trim_clip] connected and sent -- waiting on response");
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TrimRequestTimeout);
            if (responseLine is null)
            {
                AppLog.Write("[trim_clip] connection closed with no response");
                return (false, "No response from the transmitter PC.", null, 0);
            }

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? path = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
            long size = doc.RootElement.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : 0;
            return (success, success ? null : (error ?? "Trim failed."), path, size);
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time.", null, 0);
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[trim_clip] request threw", ex);
            return (false, ex.Message, null, 0);
        }
    }

    public async Task<(bool Success, string? Error, string? NewPath, long Size)> CompressRemoteClipAsync(string relativePath, double targetMb)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null, 0);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            var fields = new Dictionary<string, object?>
            {
                ["type"] = "compress_clip",
                ["secret"] = _settings.PairedPeerSecret,
                ["path"] = relativePath,
                ["targetMb"] = targetMb,
            };
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(MutationRequestTimeout);
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TrimRequestTimeout);
            if (responseLine is null)
                return (false, "No response from the transmitter PC.", null, 0);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? path = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
            long size = doc.RootElement.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : 0;
            return (success, success ? null : (error ?? "Compression failed."), path, size);
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time.", null, 0);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    public async Task<(bool Success, string? Error, string? NewPath, long Size)> MergeRemoteClipsAsync(string originRelativePath, string dedupRelativePath, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null, 0);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            var fields = new Dictionary<string, object?>
            {
                ["type"] = "merge_clips",
                ["secret"] = _settings.PairedPeerSecret,
                ["originPath"] = originRelativePath,
                ["dedupPath"] = dedupRelativePath,
            };
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(MutationRequestTimeout);

            while (true)
            {
                string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(TrimRequestTimeout);
                if (responseLine is null)
                    return (false, "No response from the transmitter PC.", null, 0);

                using JsonDocument doc = JsonDocument.Parse(responseLine);
                if (doc.RootElement.TryGetProperty("progress", out JsonElement progEl))
                {
                    double progVal = progEl.GetDouble();
                    progress?.Report(progVal);
                    continue;
                }

                bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
                string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
                string? path = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
                long size = doc.RootElement.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : 0;
                return (success, success ? null : (error ?? "Merge failed."), path, size);
            }
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time.", null, 0);
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[merge_clips] request threw", ex);
            return (false, ex.Message, null, 0);
        }
    }
}
