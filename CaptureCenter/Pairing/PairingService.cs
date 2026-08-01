using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureCenter.Pairing;

public sealed record DiscoveredPeer(string DeviceId, string DeviceName, string Address, int PairingPort, DateTime LastSeen);

public enum PairingOutcome { Approved, Denied, TimedOut, Failed }

public sealed record PairingResult(PairingOutcome Outcome, string? Secret = null, string? Error = null);

/// <summary>
/// Discovery + pairing handshake between two Backtrack installs on the same LAN.
/// Deliberately plain TCP/UDP sockets with a tiny newline-delimited JSON protocol,
/// not HttpListener -- HttpListener's http.sys binding needs either admin rights or
/// a one-time `netsh http add urlacl` reservation for anything beyond localhost,
/// which would make "Share my clips" silently fail (or need elevation) for most
/// users. Plain Sockets have no such restriction.
/// </summary>
public sealed class PairingService : IDisposable
{
    private const int BroadcastPort = 47811;
    public const int DefaultPairingPort = 47812;
    private const string AnnounceType = "backtrack-announce";

    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _discovered = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();

    private UdpClient? _announceSender;
    private UdpClient? _discoveryListener;
    private TcpListener? _pairingServer;
    private CancellationTokenSource? _cts;

    /// <summary>Fires on the host when an incoming pairing request needs a human decision -- (deviceName, code, requestId).</summary>
    public event Action<string, string, string>? PairingRequested;

    public event Action? DiscoveredPeersChanged;

    private sealed class PendingRequest
    {
        public required string DeviceId;
        public required string DeviceName;
        public required string Code;
        public volatile bool Decided;
        public bool Approved;
        public string? Secret;
    }

    public PairingService(AppSettings settings)
    {
        _settings = settings;
    }

    public IReadOnlyCollection<DiscoveredPeer> DiscoveredPeers =>
        _discovered.Values.Where(p => DateTime.UtcNow - p.LastSeen < TimeSpan.FromSeconds(10)).ToList();

    // ------------------------------------------------------------- discovery

    /// <summary>Always listening in the background (cheap: one idle UDP socket) so Settings has a live list ready the moment it's opened.</summary>
    public void StartDiscoveryListener()
    {
        if (_discoveryListener is not null)
            return;
        try
        {
            _discoveryListener = new UdpClient();
            _discoveryListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discoveryListener.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
            _ = ListenLoopAsync(_discoveryListener);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pairing discovery listener failed to start: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(UdpClient listener)
    {
        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await listener.ReceiveAsync();
            }
            catch
            {
                return; // socket disposed -- shutting down
            }

            try
            {
                var msg = JsonSerializer.Deserialize<AnnounceMessage>(result.Buffer);
                if (msg is null || msg.Type != AnnounceType || msg.DeviceId == _settings.DeviceId)
                    continue; // ignore malformed packets and our own broadcasts

                var peer = new DiscoveredPeer(msg.DeviceId, msg.DeviceName, result.RemoteEndPoint.Address.ToString(), msg.PairingPort, DateTime.UtcNow);
                _discovered[msg.DeviceId] = peer;
                DiscoveredPeersChanged?.Invoke();
            }
            catch
            {
                // Not a Backtrack announcement (or a corrupt one) -- ignore.
            }
        }
    }

    /// <summary>Broadcasts this machine as pairable every few seconds. Call when "Share my clips" is turned on.</summary>
    public void StartAnnouncing()
    {
        if (_announceSender is not null)
            return;

        _cts = new CancellationTokenSource();
        _announceSender = new UdpClient { EnableBroadcast = true };
        _ = AnnounceLoopAsync(_cts.Token);
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        var msg = new AnnounceMessage(AnnounceType, _settings.DeviceId, Environment.MachineName, DefaultPairingPort);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(msg);
        var endpoint = new IPEndPoint(IPAddress.Broadcast, BroadcastPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _announceSender!.SendAsync(payload, payload.Length, endpoint);
            }
            catch
            {
                // A transient network hiccup -- just try again next tick.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    public void StopAnnouncing()
    {
        _cts?.Cancel();
        _announceSender?.Dispose();
        _announceSender = null;
    }

    // ------------------------------------------------------- host: pairing server

    /// <summary>Starts answering pairing requests. Call alongside StartAnnouncing when "Share my clips" is enabled.</summary>
    public void StartPairingServer()
    {
        if (_pairingServer is not null)
            return;
        try
        {
            _pairingServer = new TcpListener(IPAddress.Any, DefaultPairingPort);
            _pairingServer.Start();
            _ = AcceptLoopAsync(_pairingServer);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pairing server failed to start: {ex.Message}");
        }
    }

    public void StopPairingServer()
    {
        _pairingServer?.Stop();
        _pairingServer = null;
    }

    private async Task AcceptLoopAsync(TcpListener server)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync();
            }
            catch
            {
                return; // listener stopped
            }

            _ = HandleConnectionAsync(client);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                string? line = await ReadLineAsync(client.GetStream());
                if (line is null)
                    return;

                using JsonDocument doc = JsonDocument.Parse(line);
                string? type = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;

                string response = type switch
                {
                    "pair_request" => HandlePairRequest(doc.RootElement),
                    "pair_status" => HandlePairStatus(doc.RootElement),
                    _ => JsonSerializer.Serialize(new { error = "unknown request type" }),
                };

                await WriteLineAsync(client.GetStream(), response);
            }
            catch
            {
                // Malformed request from whatever's on the other end -- just drop the connection.
            }
        }
    }

    private string HandlePairRequest(JsonElement request)
    {
        string deviceId = request.TryGetProperty("deviceId", out JsonElement d) ? d.GetString() ?? "" : "";
        string deviceName = request.TryGetProperty("deviceName", out JsonElement n) ? n.GetString() ?? "Unknown device" : "Unknown device";
        string requestId = Guid.NewGuid().ToString("N");
        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        _pendingRequests[requestId] = new PendingRequest { DeviceId = deviceId, DeviceName = deviceName, Code = code };
        PairingRequested?.Invoke(deviceName, code, requestId);

        return JsonSerializer.Serialize(new PairRequestResponse(requestId, code));
    }

    private string HandlePairStatus(JsonElement request)
    {
        string requestId = request.TryGetProperty("requestId", out JsonElement r) ? r.GetString() ?? "" : "";
        if (!_pendingRequests.TryGetValue(requestId, out PendingRequest? pending))
            return JsonSerializer.Serialize(new PairStatusResponse("denied", null));

        if (!pending.Decided)
            return JsonSerializer.Serialize(new PairStatusResponse("pending", null));

        _pendingRequests.TryRemove(requestId, out _);
        return JsonSerializer.Serialize(new PairStatusResponse(pending.Approved ? "approved" : "denied", pending.Secret));
    }

    /// <summary>Called from the PairingRequestOverlay's Allow button.</summary>
    public void ApproveRequest(string requestId)
    {
        if (!_pendingRequests.TryGetValue(requestId, out PendingRequest? pending))
            return;

        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        pending.Secret = secret;
        pending.Approved = true;
        pending.Decided = true;

        _settings.AuthorizedClientDeviceId = pending.DeviceId;
        _settings.AuthorizedClientName = pending.DeviceName;
        _settings.AuthorizedClientSecret = secret;
        _settings.Save();
    }

    /// <summary>Called from the PairingRequestOverlay's Deny button.</summary>
    public void DenyRequest(string requestId)
    {
        if (_pendingRequests.TryGetValue(requestId, out PendingRequest? pending))
        {
            pending.Approved = false;
            pending.Decided = true;
        }
    }

    // ---------------------------------------------------------- client: request pairing

    /// <summary>
    /// Sends a pairing request and polls for the human decision on the other end.
    /// onCodeReceived fires as soon as the host hands back the code, so the caller
    /// can show it immediately (before the host user has actually clicked anything)
    /// so both sides display it for comparison at the same time.
    /// </summary>
    public async Task<PairingResult> RequestPairingAsync(DiscoveredPeer peer, Action<string> onCodeReceived, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(peer.Address, peer.PairingPort, ct);

            var request = JsonSerializer.Serialize(new { type = "pair_request", deviceId = _settings.DeviceId, deviceName = Environment.MachineName });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return new PairingResult(PairingOutcome.Failed, Error: "No response from the other PC.");

            var response = JsonSerializer.Deserialize<PairRequestResponse>(responseLine);
            if (response is null)
                return new PairingResult(PairingOutcome.Failed, Error: "Unexpected response from the other PC.");

            onCodeReceived(response.Code);

            var deadline = DateTime.UtcNow.AddSeconds(65);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                using var statusClient = new TcpClient();
                await statusClient.ConnectAsync(peer.Address, peer.PairingPort, ct);
                var statusRequest = JsonSerializer.Serialize(new { type = "pair_status", requestId = response.RequestId });
                await WriteLineAsync(statusClient.GetStream(), statusRequest);
                string? statusLine = await ReadLineAsync(statusClient.GetStream());
                if (statusLine is null)
                    continue;

                var status = JsonSerializer.Deserialize<PairStatusResponse>(statusLine);
                if (status?.Status == "approved")
                {
                    _settings.PairedPeerDeviceId = peer.DeviceId;
                    _settings.PairedPeerName = peer.DeviceName;
                    _settings.PairedPeerHost = peer.Address;
                    _settings.PairedPeerPort = peer.PairingPort;
                    _settings.PairedPeerSecret = status.Secret;
                    _settings.Save();
                    return new PairingResult(PairingOutcome.Approved, status.Secret);
                }
                if (status?.Status == "denied")
                    return new PairingResult(PairingOutcome.Denied);
            }

            return new PairingResult(PairingOutcome.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new PairingResult(PairingOutcome.Failed, Error: "Cancelled.");
        }
        catch (Exception ex)
        {
            return new PairingResult(PairingOutcome.Failed, Error: ex.Message);
        }
    }

    // ------------------------------------------------------------------- wire helpers

    private static async Task WriteLineAsync(NetworkStream stream, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        using var ms = new System.IO.MemoryStream();
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

    public void Dispose()
    {
        _cts?.Cancel();
        _announceSender?.Dispose();
        _discoveryListener?.Dispose();
        _pairingServer?.Stop();
    }

    private sealed record AnnounceMessage(string Type, string DeviceId, string DeviceName, int PairingPort);
    private sealed record PairRequestResponse(string RequestId, string Code);
    private sealed record PairStatusResponse(string Status, string? Secret);
}
