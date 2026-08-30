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

public sealed partial class StreamDeckIpcServer : IDisposable
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
    private readonly Action<int>? _onClipDurationChanged;

    public StreamDeckIpcServer(ObsService obs, AppSettings settings, Action toggleHudAction, Action? addBookmarkAction = null, Func<string, DateTime?>? getRowActiveSinceUtc = null, Action<int>? onClipDurationChanged = null)
    {
        _obs = obs;
        _settings = settings;
        _toggleHudAction = toggleHudAction;
        _addBookmarkAction = addBookmarkAction;
        _getRowActiveSinceUtc = getRowActiveSinceUtc;
        _onClipDurationChanged = onClipDurationChanged;

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
                int duration = 0;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                        source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                        if (doc.RootElement.TryGetProperty("duration", out var d))
                        {
                            if (d.ValueKind == JsonValueKind.Number) duration = d.GetInt32();
                            else if (d.ValueKind == JsonValueKind.String && int.TryParse(d.GetString(), out int parsedD)) duration = parsedD;
                        }
                    }
                    catch { }
                }

                bool result = await ExecuteActionAsync(action, source, duration);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { success = result, action, source, duration });
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
                int duration = 0;
                if (doc.RootElement.TryGetProperty("duration", out var d))
                {
                    if (d.ValueKind == JsonValueKind.Number) duration = d.GetInt32();
                    else if (d.ValueKind == JsonValueKind.String && int.TryParse(d.GetString(), out int parsedD)) duration = parsedD;
                }

                _ = ExecuteActionAsync(action, source, duration);
            }
            catch { break; }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
