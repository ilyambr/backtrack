using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backtrack.Core;
using Backtrack.Obs;

namespace Backtrack.StreamDeck;

/// <summary>
/// Dedicated, modular Localhost IPC & WebSocket Server for Elgato Stream Deck integration.
/// Handles two-way WebSocket communication on 127.0.0.1:44558 with zero external dependencies.
/// </summary>
public sealed class StreamDeckIpcServer : IDisposable
{
    public const int DefaultPort = 44558;
    private readonly ObsService _obs;
    private readonly AppSettings _settings;
    private readonly Action _toggleHudAction;
    private readonly Action? _addBookmarkAction;

    private TcpListener? _server;
    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<string, DateTime> _recordRowActiveSinceUtc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _mainRecordActiveSinceUtc;

    private sealed class ClientConnection
    {
        public required TcpClient TcpClient;
        public required NetworkStream Stream;
        public bool IsWebSocket;
    }

    public bool IsRunning => _server is not null;

    public static string NormalizeName(string? raw, AppSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string name = raw.Trim();
        if (settings != null && settings.LocalRowNameOverrides.TryGetValue(name, out string? custom) && !string.IsNullOrWhiteSpace(custom))
        {
            name = custom.Trim();
        }
        if (name.EndsWith(" - Source Record", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - " - Source Record".Length).Trim();
        else if (name.EndsWith(" - Source-Record", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - " - Source-Record".Length).Trim();
        return name;
    }

    private readonly Func<string, DateTime?>? _getRowActiveSinceUtc;

    public StreamDeckIpcServer(ObsService obs, AppSettings settings, Action toggleHudAction, Action? addBookmarkAction = null, Func<string, DateTime?>? getRowActiveSinceUtc = null)
    {
        _obs = obs;
        _settings = settings;
        _toggleHudAction = toggleHudAction;
        _addBookmarkAction = addBookmarkAction;
        _getRowActiveSinceUtc = getRowActiveSinceUtc;

        HookObsEvents();
    }

    public void Start(int port = DefaultPort)
    {
        if (_server is not null)
            return;

        try
        {
            _cts = new CancellationTokenSource();
            _server = new TcpListener(IPAddress.Loopback, port);
            _server.Start();

            _heartbeatTimer = new Timer(_ =>
            {
                if (!_clients.IsEmpty && _obs.IsConnected)
                {
                    _ = BroadcastStateSnapshotAsync();
                }
            }, null, 1000, 1000);

            _ = AcceptLoopAsync(_server, _cts.Token);
            AppLog.Write($"[StreamDeck] IPC Server listening on http://127.0.0.1:{port}/");
        }
        catch (Exception ex)
        {
            AppLog.Write($"[StreamDeck] Failed to start IPC Server on port {port}: {ex.Message}");
            _server = null;
        }
    }

    public void Stop()
    {
        try
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _cts?.Cancel();
            foreach (var kv in _clients)
            {
                try { kv.Value.Stream.Dispose(); kv.Value.TcpClient.Dispose(); } catch { }
            }
            _clients.Clear();

            _server?.Stop();
            _server = null;
            AppLog.Write("[StreamDeck] IPC Server stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"[StreamDeck] Error stopping IPC Server: {ex.Message}");
        }
    }

    private void HookObsEvents()
    {
        _obs.StateChanged += () => _ = BroadcastStateSnapshotAsync();
        _obs.RecordingStateChanged += (active, path) =>
        {
            _ = BroadcastAsync("recording_state", new { active, path });
            _ = BroadcastStateSnapshotAsync();
        };
        _obs.ReplaySaving += source => _ = BroadcastAsync("replay_saving", new { source });
        _obs.ReplaySaved += (key, path) => _ = BroadcastAsync("replay_saved", new { key, path });
        _obs.EncoderOverloadDetected += info => _ = BroadcastAsync("encoder_overload", info);
    }

    private async Task AcceptLoopAsync(TcpListener server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await server.AcceptTcpClientAsync(ct);
                Guid id = Guid.NewGuid();
                var conn = new ClientConnection
                {
                    TcpClient = client,
                    Stream = client.GetStream(),
                    IsWebSocket = false
                };
                _clients[id] = conn;
                _ = HandleClientAsync(id, conn, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                AppLog.Write($"[StreamDeck] AcceptLoop error: {ex.Message}");
            }
        }
    }

    private static bool IsAllowedOrigin(string headerText, out string? matchedOrigin)
    {
        matchedOrigin = null;
        var match = Regex.Match(headerText, @"Origin:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return true;

        matchedOrigin = match.Groups[1].Value.Trim();
        string originLower = matchedOrigin.ToLowerInvariant();

        return originLower == "null" ||
               originLower.StartsWith("file://") ||
               originLower.StartsWith("http://localhost") ||
               originLower.StartsWith("http://127.0.0.1") ||
               originLower.StartsWith("https://localhost") ||
               originLower.StartsWith("https://127.0.0.1") ||
               originLower.Contains("elgato") ||
               originLower.Contains("streamdeck");
    }

    private async Task HandleClientAsync(Guid id, ClientConnection conn, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        try
        {
            int read = await conn.Stream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read == 0) return;

            string headerText = Encoding.UTF8.GetString(buffer, 0, read);

            if (!IsAllowedOrigin(headerText, out string? origin))
            {
                AppLog.Write($"[StreamDeck] Blocked unauthorized cross-site origin: {origin}");
                string forbidden = "HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\nForbidden: Cross-origin access denied\r\n";
                byte[] forbiddenBytes = Encoding.UTF8.GetBytes(forbidden);
                await conn.Stream.WriteAsync(forbiddenBytes, 0, forbiddenBytes.Length, ct);
                return;
            }

            if (headerText.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(headerText, @"Sec-WebSocket-Key:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string secKey = match.Groups[1].Value.Trim();
                    string acceptKey = Convert.ToBase64String(
                        SHA1.HashData(Encoding.UTF8.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                    string handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                                       "Upgrade: websocket\r\n" +
                                       "Connection: Upgrade\r\n" +
                                       $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
                    byte[] handshakeBytes = Encoding.UTF8.GetBytes(handshake);
                    await conn.Stream.WriteAsync(handshakeBytes, 0, handshakeBytes.Length, ct);

                    conn.IsWebSocket = true;

                    var initialSnapshot = await BuildStateSnapshotAsync();
                    await SendWebSocketJsonAsync(conn.Stream, new { @event = "state_snapshot", data = initialSnapshot }, ct);

                    await WebSocketLoopAsync(conn, ct);
                    return;
                }
            }

            string firstLine = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            string[] parts = firstLine.Split(' ');
            string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "GET";
            string path = parts.Length > 1 ? parts[1].ToLowerInvariant() : "/";

            string corsHeader = origin != null ? $"Access-Control-Allow-Origin: {origin}\r\n" : "";

            if (method == "OPTIONS")
            {
                string resp = "HTTP/1.1 204 No Content\r\n" +
                              corsHeader +
                              "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                              "Access-Control-Allow-Headers: Content-Type\r\n\r\n";
                byte[] respBytes = Encoding.UTF8.GetBytes(resp);
                await conn.Stream.WriteAsync(respBytes, 0, respBytes.Length, ct);
                return;
            }

            if (path == "/status" || path == "/")
            {
                var snapshot = await BuildStateSnapshotAsync();
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { status = "ok", version = "1.0", snapshot });
                string resp = "HTTP/1.1 200 OK\r\n" +
                              corsHeader +
                              "Content-Type: application/json; charset=utf-8\r\n" +
                              $"Content-Length: {json.Length}\r\n\r\n";
                byte[] respHeader = Encoding.UTF8.GetBytes(resp);
                await conn.Stream.WriteAsync(respHeader, 0, respHeader.Length, ct);
                await conn.Stream.WriteAsync(json, 0, json.Length, ct);
                return;
            }

            if (path == "/action" && method == "POST")
            {
                int bodyIdx = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                string body = bodyIdx >= 0 ? headerText.Substring(bodyIdx + 4) : "";
                string action = "";
                string source = "";

                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                        source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                    }
                    catch { }
                }

                bool result = await ExecuteActionAsync(action, source);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { success = result, action, source });
                string resp = "HTTP/1.1 200 OK\r\n" +
                              corsHeader +
                              "Content-Type: application/json; charset=utf-8\r\n" +
                              $"Content-Length: {json.Length}\r\n\r\n";
                byte[] respHeader = Encoding.UTF8.GetBytes(resp);
                await conn.Stream.WriteAsync(respHeader, 0, respHeader.Length, ct);
                await conn.Stream.WriteAsync(json, 0, json.Length, ct);
                return;
            }

            string notFound = "HTTP/1.1 404 Not Found\r\n\r\n";
            await conn.Stream.WriteAsync(Encoding.UTF8.GetBytes(notFound), 0, notFound.Length, ct);
        }
        catch { }
        finally
        {
            if (!conn.IsWebSocket)
            {
                _clients.TryRemove(id, out _);
                try { conn.Stream.Dispose(); conn.TcpClient.Dispose(); } catch { }
            }
        }
    }

    private async Task WebSocketLoopAsync(ClientConnection conn, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        while (!ct.IsCancellationRequested && conn.TcpClient.Connected)
        {
            try
            {
                int read = await conn.Stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read < 2) break;

                bool fin = (buffer[0] & 0x80) != 0;
                int opcode = buffer[0] & 0x0F;
                if (opcode == 0x8) break;

                bool masked = (buffer[1] & 0x80) != 0;
                ulong payloadLen = (ulong)(buffer[1] & 0x7F);
                int offset = 2;

                if (payloadLen == 126)
                {
                    payloadLen = (ulong)((buffer[2] << 8) | buffer[3]);
                    offset = 4;
                }
                else if (payloadLen == 127)
                {
                    payloadLen = BitConverter.ToUInt64(buffer, 2);
                    offset = 10;
                }

                byte[] masks = new byte[4];
                if (masked)
                {
                    Array.Copy(buffer, offset, masks, 0, 4);
                    offset += 4;
                }

                byte[] payload = new byte[payloadLen];
                for (ulong i = 0; i < payloadLen; i++)
                {
                    payload[i] = masked ? (byte)(buffer[offset + (int)i] ^ masks[i % 4]) : buffer[offset + (int)i];
                }

                string text = Encoding.UTF8.GetString(payload);
                using var doc = JsonDocument.Parse(text);
                string action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                string source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";

                _ = ExecuteActionAsync(action, source);
            }
            catch { break; }
        }
    }

    public async Task<bool> ExecuteActionAsync(string action, string source)
    {
        try
        {
            switch (action.ToLowerInvariant())
            {
                case "save_replay":
                case "clip":
                    return await SaveReplayAsync(source);

                case "toggle_record":
                case "record":
                    return await ToggleRecordAsync(source);

                case "cancel_recording":
                case "cancel":
                    return await CancelRecordingAsync(source);

                case "add_bookmark":
                case "bookmark":
                    _addBookmarkAction?.Invoke();
                    _ = BroadcastAsync("bookmark_added", new { timestamp = DateTime.UtcNow });
                    return true;

                case "toggle_hud":
                case "hud":
                    _toggleHudAction.Invoke();
                    return true;

                case "get_state":
                case "snapshot":
                    _ = BroadcastStateSnapshotAsync();
                    return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[StreamDeck] Action '{action}' failed: {ex.Message}");
        }
        return false;
    }

    private readonly ConcurrentDictionary<string, DateTime> _lastReplaySaveUtc = new(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> SaveReplayAsync(string source)
    {
        if (!_obs.IsConnected) return false;

        var allRows = await _obs.ListReplayRowsAsync();
        ReplayRow? targetRow = null;

        if (string.IsNullOrWhiteSpace(source) || source.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            targetRow = allRows.FirstOrDefault(r => r.Status == 1) ?? allRows.FirstOrDefault();
        }
        else
        {
            targetRow = allRows.FirstOrDefault(r => NormalizeName(r.Label, _settings).Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                                    r.Label.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                                    r.Key.Equals(source, StringComparison.OrdinalIgnoreCase));
        }

        if (targetRow != null)
        {
            int preferredSeconds = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60;
            if (_lastReplaySaveUtc.TryGetValue(targetRow.Key, out DateTime lastSave))
            {
                double elapsed = (DateTime.UtcNow - lastSave).TotalSeconds;
                if (elapsed > 1 && elapsed < preferredSeconds)
                {
                    int effectiveSeconds = (int)Math.Ceiling(elapsed);
                    AppLog.Write($"[StreamDeck] Smart deduplication for {targetRow.Label}: clipping {effectiveSeconds}s since last save");
                    try { await _obs.SetReplayRowLengthAsync(targetRow.Key, effectiveSeconds); } catch { }
                }
                else if (preferredSeconds > 0)
                {
                    try { await _obs.SetReplayRowLengthAsync(targetRow.Key, preferredSeconds); } catch { }
                }
            }
            _lastReplaySaveUtc[targetRow.Key] = DateTime.UtcNow;

            await _obs.SaveReplayRowAsync(targetRow.Key);
            return true;
        }

        return false;
    }

    private async Task<bool> ToggleRecordAsync(string source)
    {
        if (!_obs.IsConnected) return false;

        if (string.IsNullOrWhiteSpace(source) || source.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            await _obs.ToggleRecordAsync();
            return true;
        }

        var recRows = await _obs.ListRecordRowsAsync();
        var match = recRows.FirstOrDefault(r => NormalizeName(r.Label, _settings).Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                               r.Label.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                               r.SourceName.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                               r.Key.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            if (match.Status == 2)
                await _obs.StopRecordRowAsync(match.Key);
            else
                await _obs.StartRecordRowAsync(match.Key);
            return true;
        }

        return false;
    }

    private async Task<bool> CancelRecordingAsync(string? source = null)
    {
        if (!_obs.IsConnected) return false;

        var recRows = await _obs.ListRecordRowsAsync();
        bool anyCancelled = false;

        foreach (var r in recRows.Where(r => r.Status == 2))
        {
            try
            {
                await _obs.CancelRecordRowAsync(r.Key);
                anyCancelled = true;
            }
            catch { }
        }

        var recStatus = await _obs.GetRecordStatusAsync();
        if (recStatus.Active)
        {
            try
            {
                await _obs.StopMainRecordAsync();
                anyCancelled = true;
            }
            catch { }
        }

        return anyCancelled;
    }

    public async Task<object> BuildStateSnapshotAsync()
    {
        bool obsConnected = _obs.IsConnected;
        List<object> replayBuffers = new();
        List<object> recordSources = new();
        bool isMainRecording = false;
        long recordDurationMs = 0;
        long mainRecordStartUnixMs = 0;

        if (obsConnected)
        {
            try
            {
                int preferredLen = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60;
                var rRows = await _obs.ListReplayRowsAsync();
                foreach (var r in rRows)
                {
                    int rowLen = _settings.PreferredClipLengthSeconds > 0
                        ? _settings.PreferredClipLengthSeconds
                        : (r.LengthSeconds > 0 ? r.LengthSeconds : preferredLen);

                    replayBuffers.Add(new
                    {
                        key = r.Key,
                        label = NormalizeName(r.Label, _settings),
                        raw_label = r.Label,
                        status = r.Status,
                        length = rowLen,
                        dest = r.DestDir
                    });
                }

                var recRows = await _obs.ListRecordRowsAsync();
                foreach (var rec in recRows)
                {
                    long rowDurationMs = 0;
                    long rowStartUnixMs = 0;
                    bool isRowRecording = (rec.Status == 2); // RecordStatusRecording == 2

                    if (isRowRecording)
                    {
                        DateTime startedAt = _getRowActiveSinceUtc?.Invoke(rec.Key) 
                            ?? _recordRowActiveSinceUtc.GetOrAdd(rec.Key, DateTime.UtcNow);
                        _recordRowActiveSinceUtc[rec.Key] = startedAt;
                        rowDurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                        rowStartUnixMs = new DateTimeOffset(startedAt).ToUnixTimeMilliseconds();
                    }
                    else
                    {
                        _recordRowActiveSinceUtc.TryRemove(rec.Key, out _);
                    }

                    recordSources.Add(new
                    {
                        key = rec.Key,
                        label = NormalizeName(rec.Label, _settings),
                        raw_label = rec.Label,
                        source = rec.SourceName,
                        filter = rec.FilterName,
                        status = rec.Status,
                        is_recording = isRowRecording,
                        duration_ms = rowDurationMs,
                        start_time_ms = rowStartUnixMs
                    });
                }

                var status = await _obs.GetRecordStatusAsync();
                isMainRecording = status.Active;
                if (isMainRecording)
                {
                    if (status.DurationMs > 0)
                    {
                        _mainRecordActiveSinceUtc = DateTime.UtcNow.AddMilliseconds(-status.DurationMs);
                        recordDurationMs = status.DurationMs;
                    }
                    else
                    {
                        _mainRecordActiveSinceUtc ??= DateTime.UtcNow;
                        recordDurationMs = (long)(DateTime.UtcNow - _mainRecordActiveSinceUtc.Value).TotalMilliseconds;
                    }
                    mainRecordStartUnixMs = new DateTimeOffset(_mainRecordActiveSinceUtc.Value).ToUnixTimeMilliseconds();
                }
                else
                {
                    _mainRecordActiveSinceUtc = null;
                }
            }
            catch { }
        }

        bool isAnySourceRecording = recordSources.Any(s => ((dynamic)s).is_recording == true);
        bool isRecording = isMainRecording || isAnySourceRecording;

        return new
        {
            obs_connected = obsConnected,
            is_recording = isRecording,
            preferred_clip_length_seconds = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60,
            record_duration_ms = recordDurationMs,
            main_record_start_time_ms = mainRecordStartUnixMs,
            replay_buffers = replayBuffers,
            record_sources = recordSources
        };
    }

    public async Task BroadcastStateSnapshotAsync()
    {
        var snapshot = await BuildStateSnapshotAsync();
        await BroadcastAsync("state_snapshot", snapshot);
    }

    public async Task BroadcastAsync(string eventName, object data)
    {
        if (_clients.IsEmpty) return;

        var payload = new { @event = eventName, data };
        foreach (var kv in _clients)
        {
            if (kv.Value.IsWebSocket && kv.Value.TcpClient.Connected)
            {
                try
                {
                    await SendWebSocketJsonAsync(kv.Value.Stream, payload, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private static async Task SendWebSocketJsonAsync(NetworkStream stream, object payload, CancellationToken ct)
    {
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] frame;

        if (raw.Length < 126)
        {
            frame = new byte[2 + raw.Length];
            frame[0] = 0x81;
            frame[1] = (byte)raw.Length;
            Array.Copy(raw, 0, frame, 2, raw.Length);
        }
        else if (raw.Length <= 65535)
        {
            frame = new byte[4 + raw.Length];
            frame[0] = 0x81;
            frame[1] = 126;
            frame[2] = (byte)((raw.Length >> 8) & 0xFF);
            frame[3] = (byte)(raw.Length & 0xFF);
            Array.Copy(raw, 0, frame, 4, raw.Length);
        }
        else
        {
            frame = new byte[10 + raw.Length];
            frame[0] = 0x81;
            frame[1] = 127;
            byte[] lenBytes = BitConverter.GetBytes((ulong)raw.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
            Array.Copy(lenBytes, 0, frame, 2, 8);
            Array.Copy(raw, 0, frame, 10, raw.Length);
        }

        await stream.WriteAsync(frame, 0, frame.Length, ct);
    }

    public void Dispose()
    {
        Stop();
    }
}
