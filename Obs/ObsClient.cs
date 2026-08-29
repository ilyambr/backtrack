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
            throw new ObsUnreachableException("OBS isn't running, or its WebSocket server is off", ex);
        }

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
            throw;
        }

        IsConnected = true;
        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    private bool CleanupFailedConnectWebSocket()
    {
        _ws?.Dispose();
        _ws = null;
        return false;
    }

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
            }
        }
        _ws?.Dispose();
    }
}
