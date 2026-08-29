using System;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{
    private string HandlePairRequest(JsonElement request)
    {
        string deviceId = request.TryGetProperty("deviceId", out JsonElement d) ? d.GetString() ?? "" : "";
        string deviceName = request.TryGetProperty("deviceName", out JsonElement n) ? n.GetString() ?? "Unknown device" : "Unknown device";

        if (string.IsNullOrEmpty(deviceId))
            return JsonSerializer.Serialize(new { error = "missing deviceId" });

        if (deviceId == _settings.AuthorizedClientDeviceId && !string.IsNullOrEmpty(_settings.AuthorizedClientSecret))
        {
            return JsonSerializer.Serialize(new
            {
                requestId = "auto",
                code = "",
                secret = _settings.AuthorizedClientSecret
            });
        }

        foreach (var p in _pendingRequests.Values)
        {
            if (!p.Decided)
                return JsonSerializer.Serialize(new { error = "busy" });
        }

        string requestId = Guid.NewGuid().ToString("N");
        string code = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

        var pending = new PendingRequest
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Code = code,
        };
        _pendingRequests[requestId] = pending;

        PairingRequested?.Invoke(deviceName, code, requestId);

        return JsonSerializer.Serialize(new { requestId, code });
    }

    private string HandlePairStatus(JsonElement request)
    {
        string requestId = request.TryGetProperty("requestId", out JsonElement r) ? r.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(requestId))
            return JsonSerializer.Serialize(new { error = "missing requestId" });

        if (requestId == "auto" && !string.IsNullOrEmpty(_settings.AuthorizedClientSecret))
        {
            return JsonSerializer.Serialize(new { status = "approved", secret = _settings.AuthorizedClientSecret });
        }

        if (!_pendingRequests.TryGetValue(requestId, out PendingRequest? pending))
            return JsonSerializer.Serialize(new { status = "not_found" });

        if (!pending.Decided)
            return JsonSerializer.Serialize(new { status = "pending" });

        if (pending.Approved && pending.Secret is not null)
        {
            _pendingRequests.TryRemove(requestId, out _);
            return JsonSerializer.Serialize(new { status = "approved", secret = pending.Secret });
        }

        _pendingRequests.TryRemove(requestId, out _);
        return JsonSerializer.Serialize(new { status = "denied" });
    }

    private bool IsAuthorizedClient(JsonElement request)
    {
        if (string.IsNullOrEmpty(_settings.AuthorizedClientSecret))
            return false;

        string secret = request.TryGetProperty("secret", out JsonElement s) ? s.GetString() ?? "" : "";
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(_settings.AuthorizedClientSecret));
    }

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

    public void DenyRequest(string requestId)
    {
        if (_pendingRequests.TryGetValue(requestId, out PendingRequest? pending))
        {
            pending.Approved = false;
            pending.Decided = true;
        }
    }

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

            if (string.IsNullOrEmpty(response.Code))
                return new PairingResult(PairingOutcome.Denied, Error: "That PC is already handling another pairing request -- try again in a moment.");

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
}
