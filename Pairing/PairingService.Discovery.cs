using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.Pairing;

public sealed partial class PairingService
{

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
                return;
            }

            try
            {
                var msg = JsonSerializer.Deserialize<AnnounceMessage>(result.Buffer, JsonOptions);
                if (msg is null || msg.Type != AnnounceType || msg.DeviceId == _settings.DeviceId)
                    continue;

                var peer = new DiscoveredPeer(msg.DeviceId, msg.DeviceName, result.RemoteEndPoint.Address.ToString(), msg.PairingPort, DateTime.UtcNow);
                _discovered[msg.DeviceId] = peer;
                DiscoveredPeersChanged?.Invoke();
                if (!string.IsNullOrEmpty(msg.InstanceId))
                {
                    string senderIp = result.RemoteEndPoint.Address.ToString();
                    bool isPaired = (!string.IsNullOrEmpty(_settings.PairedPeerDeviceId) && msg.DeviceId == _settings.PairedPeerDeviceId) ||
                                    (!string.IsNullOrEmpty(_settings.PairedPeerHost) && (senderIp == _settings.PairedPeerHost || msg.DeviceId == _settings.PairedPeerHost));
                    if (isPaired)
                    {
                        if (_peerInstanceIds.TryGetValue(msg.DeviceId, out string? lastInst) && lastInst != msg.InstanceId)
                        {
                            AppLog.Write($"[pairing] Paired peer '{msg.DeviceName}' restart detected (instance: {msg.InstanceId})");
                            PairedPeerRestarted?.Invoke();
                        }
                        _peerInstanceIds[msg.DeviceId] = msg.InstanceId;
                    }
                }
            }
            catch
            {
            }
        }
    }

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
        var msg = new AnnounceMessage(AnnounceType, _settings.DeviceId, Environment.MachineName, DefaultPairingPort, ProcessInstanceId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(msg);
        var endpoint = new IPEndPoint(IPAddress.Broadcast, BroadcastPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _announceSender!.SendAsync(payload, payload.Length, endpoint);
            if (!string.IsNullOrEmpty(_settings.PairedPeerHost) && IPAddress.TryParse(_settings.PairedPeerHost, out var directIp))
            {
                try { await _announceSender!.SendAsync(payload, payload.Length, new IPEndPoint(directIp, BroadcastPort)); }
                catch { }
            }
            }
            catch
            {
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
}
