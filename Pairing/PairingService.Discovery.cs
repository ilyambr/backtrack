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
}
