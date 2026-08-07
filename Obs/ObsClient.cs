using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.Obs;

/// <summary>
/// A hand-rolled obs-websocket v5 client over <see cref="ClientWebSocket"/> --
/// no third-party package, just the wire protocol: Hello -> Identify ->
/// Identified, then Request/RequestResponse pairs matched by requestId, plus
/// an Event stream for anything OBS pushes unprompted.
/// </summary>
/// <summary>Thrown when there's simply nothing to connect to (OBS closed, or its WebSocket server disabled) -- as opposed to a real protocol/auth error once a connection is actually made.</summary>
public sealed class ObsUnreachableException : Exception
{
    public ObsUnreachableException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ObsClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private CancellationTokenSource? _receiveCts;

    public bool IsConnected { get; private set; }
    public event Action<string, JsonElement>? EventReceived;
    public event Action? Disconnected;

    public async Task ConnectAsync(string url, string? password, CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        try
        {
            await _ws.ConnectAsync(new Uri(url), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing there to connect to at all (OBS closed, or its WebSocket
            // server disabled) -- distinct from a real protocol/auth failure
            // below, which only happens once a connection is actually made.
            throw new ObsUnreachableException("OBS isn't running, or its WebSocket server is off", ex);
        }

        // Everything below assumes the WebSocket-level connect above already
        // succeeded (_ws is open) -- if the obs-websocket handshake itself
        // fails (unexpected Hello, wrong password on Identified, etc.), that
        // open socket was previously left dangling: nothing closed or
        // disposed it, and RetryLoopAsync calls ConnectAsync again every 5s
        // forever while disconnected, leaking one real OS socket handle per
        // attempt. Catch and clean up here instead of leaving _ws open.
        try
        {
            JsonElement hello = await ReceiveOneAsync(ct);
            if (hello.GetProperty("op").GetInt32() != 0)
                throw new InvalidOperationException("Expected Hello (op 0) as the first message from OBS");

            JsonElement helloData = hello.GetProperty("d");
            int rpcVersion = helloData.GetProperty("rpcVersion").GetInt32();

            var identify = new Dictionary<string, object?>
            {
                ["rpcVersion"] = rpcVersion,
                // General|Config|Scenes|Inputs|Transitions|Filters|Outputs|SceneItems|MediaInputs|Vendors
                // ((1<<10)-1 = 1023) plus InputVolumeMeters (1<<16 = 65536) -- that one's a
                // separate "high-volume" category not included in the low bits at all,
                // needed for the mic status badge's live audio-level monitoring.
                ["eventSubscriptions"] = 1023 | (1 << 16),
            };

            if (helloData.TryGetProperty("authentication", out JsonElement auth))
            {
                string challenge = auth.GetProperty("challenge").GetString()!;
                string salt = auth.GetProperty("salt").GetString()!;
                identify["authentication"] = ComputeAuthString(password ?? "", salt, challenge);
            }

            await SendAsync(1, identify, ct);

            JsonElement identified = await ReceiveOneAsync(ct);
            if (identified.GetProperty("op").GetInt32() != 2)
                throw new InvalidOperationException("OBS did not send Identified -- check the password");
        }
        catch (Exception) when (CleanupFailedConnectWebSocket())
        {
            throw; // unreachable -- the when-filter never returns true, it only runs cleanup as a side effect
        }

        IsConnected = true;
        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    /// <summary>
    /// Disposes and clears a WebSocket left over from a failed post-connect
    /// handshake. Run from an exception filter (always returns false, so the
    /// original exception's stack trace is preserved) rather than a catch
    /// block, purely so cleanup happens without needing a separate rethrow.
    /// </summary>
    private bool CleanupFailedConnectWebSocket()
    {
        _ws?.Dispose();
        _ws = null;
        return false;
    }

    // Per the obs-websocket v5 auth spec:
    //   secret = base64(sha256(password + salt))
    //   authentication = base64(sha256(secret + challenge))
    private static string ComputeAuthString(string password, string salt, string challenge)
    {
        byte[] secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        string secretB64 = Convert.ToBase64String(secretHash);
        byte[] authHash = SHA256.HashData(Encoding.UTF8.GetBytes(secretB64 + challenge));
        return Convert.ToBase64String(authHash);
    }

    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        if (_ws is null || !IsConnected)
            throw new InvalidOperationException("Not connected to OBS");

        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        var payload = new Dictionary<string, object?> { ["requestType"] = requestType, ["requestId"] = requestId };
        if (requestData is not null)
            payload["requestData"] = requestData;

        await SendAsync(6, payload, ct);

        await using (ct.Register(() => tcs.TrySetCanceled()).ConfigureAwait(false))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async Task SendAsync(int op, object? data, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = op, ["d"] = data });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task<JsonElement> ReceiveOneAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("OBS closed the connection");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        using JsonDocument doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
            {
                JsonElement msg = await ReceiveOneAsync(ct);
                int op = msg.GetProperty("op").GetInt32();
                JsonElement d = msg.TryGetProperty("d", out JsonElement dd) ? dd : default;

                if (op == 7 && d.TryGetProperty("requestId", out JsonElement ridProp))
                {
                    string rid = ridProp.GetString() ?? "";
                    if (_pending.TryRemove(rid, out TaskCompletionSource<JsonElement>? tcs))
                    {
                        bool ok = d.TryGetProperty("requestStatus", out JsonElement rs) &&
                                  rs.TryGetProperty("result", out JsonElement res) && res.GetBoolean();
                        if (ok)
                        {
                            tcs.TrySetResult(d.TryGetProperty("responseData", out JsonElement rd) ? rd.Clone() : default);
                        }
                        else
                        {
                            string comment = "request failed";
                            if (d.TryGetProperty("requestStatus", out JsonElement rs2) && rs2.TryGetProperty("comment", out JsonElement c))
                                comment = c.GetString() ?? comment;
                            tcs.TrySetException(new InvalidOperationException(comment));
                        }
                    }
                }
                else if (op == 5 && d.TryGetProperty("eventType", out JsonElement et))
                {
                    EventReceived?.Invoke(et.GetString() ?? "", d.TryGetProperty("eventData", out JsonElement ed) ? ed.Clone() : default);
                }
            }
        }
        catch
        {
            // Connection dropped -- ObsService owns the reconnect policy.
        }
        finally
        {
            IsConnected = false;
            foreach (var kv in _pending)
                kv.Value.TrySetException(new InvalidOperationException("Disconnected from OBS"));
            _pending.Clear();
            Disconnected?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // best effort
            }
        }
        _ws?.Dispose();
    }
}
