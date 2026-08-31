using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backtrack.Core;

namespace Backtrack.Pairing;

public sealed record DiscoveredPeer(string DeviceId, string DeviceName, string Address, int PairingPort, DateTime LastSeen);
public enum PairingOutcome { Approved, Denied, TimedOut, Failed }
public sealed record PairingResult(PairingOutcome Outcome, string? Secret = null, string? Error = null);
public sealed record RamDiskSnapshot(bool Enabled, char DriveLetter, int SizeMb, bool Mounted);
public sealed record PluginVersionInfo(string InstalledVersion, bool? Ok);
public sealed record PluginVersionsSnapshot(PluginVersionInfo ReplaySlider, PluginVersionInfo SourceRecord);
public sealed record RemoteGalleryFile(string Name, long Size, DateTime Modified, bool IsDeduplicated = false, bool HasDeduplicatedChildren = false, string? OriginFileName = null, string? OriginPath = null);
public sealed record RemoteStorageInfo(bool StorageLimitEnabled, double StorageLimitGb, long ClipsFolderBytes, long DriveTotalBytes, long DriveFreeBytes);
public sealed record RemoteGalleryListing(IReadOnlyList<string> Folders, IReadOnlyList<RemoteGalleryFile> Files, RemoteStorageInfo? Storage = null);

public sealed partial class PairingService : IDisposable
{
    public const int BroadcastPort = 47811;
    public const int DefaultPairingPort = 47812;
    private const string AnnounceType = "backtrack-announce";
    private static readonly TimeSpan MutationRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _discovered = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();

    private UdpClient? _announceSender;
    private UdpClient? _discoveryListener;
    private TcpListener? _pairingServer;
    private CancellationTokenSource? _cts;

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
                return;
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
                if (line is null) return;

                using JsonDocument doc = JsonDocument.Parse(line);
                string? type = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;

                if (type == "get_clip")
                {
                    await HandleGetClipAsync(doc.RootElement, client.GetStream());
                    return;
                }
                if (type == "get_thumbnail")
                {
                    await HandleGetThumbnailAsync(doc.RootElement, client.GetStream());
                    return;
                }
                if (type == "put_clip")
                {
                    await HandlePutClipAsync(doc.RootElement, client.GetStream());
                    return;
                }

                if (type == "merge_clips")
                {
                    await HandleMergeClipsAsync(doc.RootElement, client.GetStream());
                    return;
                }

                string response = type switch
                {
                    "pair_request" => HandlePairRequest(doc.RootElement),
                    "pair_status" => HandlePairStatus(doc.RootElement),
                    "get_ramdisk_settings" => HandleGetRamDiskSettings(doc.RootElement),
                    "set_ramdisk_settings" => await HandleSetRamDiskSettingsAsync(doc.RootElement),
                    "check_plugin_updates" => await HandleCheckPluginUpdatesAsync(doc.RootElement),
                    "list_gallery" => HandleListGallery(doc.RootElement),
                    "newest_clip" => HandleNewestClip(doc.RootElement),
                    "delete_clip" => HandleDeleteClip(doc.RootElement),
                    "rename_clip" => HandleRenameClip(doc.RootElement),
                    "move_clip" => HandleMoveClip(doc.RootElement),
                    "trim_clip" => await HandleTrimClipAsync(doc.RootElement),
                    "compress_clip" => await HandleCompressClipAsync(doc.RootElement),
                    "play_audio_cue" => HandlePlayAudioCue(doc.RootElement),
                    "sync_clip_markers" => HandleSyncClipMarkers(doc.RootElement),
                    "sync_starred" => HandleSyncStarred(doc.RootElement),
                    _ => JsonSerializer.Serialize(new { error = "unknown request type" }),
                };

                await WriteLineAsync(client.GetStream(), response);
            }
            catch { }
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
