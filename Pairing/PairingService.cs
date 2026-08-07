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
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.Pairing;

public sealed record DiscoveredPeer(string DeviceId, string DeviceName, string Address, int PairingPort, DateTime LastSeen);

public enum PairingOutcome { Approved, Denied, TimedOut, Failed }

public sealed record PairingResult(PairingOutcome Outcome, string? Secret = null, string? Error = null);

/// <summary>A snapshot of one instance's RAM disk config, sent over the wire so a paired PC can view/change it remotely.</summary>
public sealed record RamDiskSnapshot(bool Enabled, char DriveLetter, int SizeMb, bool Mounted);

/// <summary>Ok: true = confirmed current (already was, or just got updated); false = check/update failed; null = not checked yet.</summary>
public sealed record PluginVersionInfo(string InstalledVersion, bool? Ok);

/// <summary>Result of a remote-triggered check-and-apply for both companion OBS plugins.</summary>
public sealed record PluginVersionsSnapshot(PluginVersionInfo ReplaySlider, PluginVersionInfo SourceRecord);

/// <summary>One clip file in a paired PC's remote Gallery listing.</summary>
public sealed record RemoteGalleryFile(string Name, long Size, DateTime Modified);

/// <summary>One folder's worth of a paired PC's ClipsFolder tree -- subfolder names plus clip files, same shape LoadGallery() builds locally.</summary>
public sealed record RemoteGalleryListing(IReadOnlyList<string> Folders, IReadOnlyList<RemoteGalleryFile> Files);

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

                // get_clip is the one request type whose response isn't a single
                // JSON line -- it writes a JSON header line and then (on success)
                // streams the raw file bytes directly after it, so it has to own
                // writing to the connection itself instead of going through the
                // uniform "one line in, one line out" dispatch below.
                if (type == "get_clip")
                {
                    await HandleGetClipAsync(doc.RootElement, client.GetStream());
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

        /* PairingRequested is answered by a single shared overlay window (see
         * MainWindow's subscription) -- ShowRequest() just overwrites its
         * Allow/Deny callbacks and displayed code with whatever request last
         * called it, with no idea a prior request was still undecided. If a
         * second request arrives while the first is still sitting there
         * waiting on a human, the human ends up approving whichever one is
         * currently on screen, but that only marks THAT request's entry
         * Decided -- the other one is silently orphaned: never approved,
         * never denied, and (if its own connecting client is still polling)
         * eventually just times out on that end. Worse, if it's approved on
         * a later overlap, the two sides can walk away with secrets from two
         * different handshakes that were never meant to be compared, which
         * is exactly the "secrets don't match" symptom this was root-caused
         * from. Reject overlapping requests outright instead: the connecting
         * side gets an immediate, unambiguous "denied" and can just retry
         * once the first attempt is resolved, rather than either side ending
         * up with a secret the other doesn't recognize. */
        bool busy = _pendingRequests.Values.Any(p => !p.Decided);
        string code = busy ? "" : RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        var pending = new PendingRequest { DeviceId = deviceId, DeviceName = deviceName, Code = code };
        if (busy)
        {
            pending.Decided = true;
            pending.Approved = false;
        }
        _pendingRequests[requestId] = pending;

        if (!busy)
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

    // ------------------------------------------------ host: remote RAM disk control
    //
    // RAM disk is the one Backtrack setting that's genuinely local to whichever PC
    // runs OBS -- unlike everything else in Settings (buffer duration, hidden
    // buffers, etc.), which already works fine remotely since it's just an
    // obs-websocket call that doesn't care which machine it's talking to. A paired
    // receiver PC needs an actual channel to read/change it on the transmitter, so
    // this reuses the same paired-client secret the "Share my clips" handshake
    // already established, rather than inventing a second trust mechanism.

    /// <summary>Set by MainWindow so an authenticated request can read this instance's live RAM disk state.</summary>
    public Func<RamDiskSnapshot>? GetRamDiskSnapshot { get; set; }

    /// <summary>Set by MainWindow so an authenticated request can actually apply a new configuration (mount/unmount, save, push to OBS).</summary>
    public Func<bool, char, int, Task<(bool Success, string? Error)>>? ApplyRamDiskSnapshot { get; set; }

    private string HandleGetRamDiskSettings(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        RamDiskSnapshot? snapshot = GetRamDiskSnapshot?.Invoke();
        if (snapshot is null)
            return JsonSerializer.Serialize(new { error = "RAM disk control isn't wired up on this instance." });

        return JsonSerializer.Serialize(snapshot);
    }

    private async Task<string> HandleSetRamDiskSettingsAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        if (ApplyRamDiskSnapshot is null)
            return JsonSerializer.Serialize(new { success = false, error = "RAM disk control isn't wired up on this instance." });

        bool enabled = request.TryGetProperty("enabled", out JsonElement e) && e.GetBoolean();
        string driveText = request.TryGetProperty("driveLetter", out JsonElement d) ? d.GetString() ?? "R" : "R";
        char driveLetter = driveText.Length > 0 ? char.ToUpperInvariant(driveText[0]) : 'R';
        int sizeMb = request.TryGetProperty("sizeMb", out JsonElement s) ? s.GetInt32() : 2048;

        (bool success, string? error) = await ApplyRamDiskSnapshot(enabled, driveLetter, sizeMb);
        return JsonSerializer.Serialize(new { success, error });
    }

    /// <summary>
    /// Constant-time compare against this instance's own AuthorizedClientSecret --
    /// that field only gets set once a human on this PC has actually clicked
    /// "Allow" on a pairing request (see ApproveRequest), so it doubles as "is
    /// anyone even paired with this PC" and "does this caller know the secret."
    /// </summary>
    private bool IsAuthorizedClient(JsonElement request)
    {
        if (string.IsNullOrEmpty(_settings.AuthorizedClientSecret))
            return false;

        string secret = request.TryGetProperty("secret", out JsonElement s) ? s.GetString() ?? "" : "";
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(_settings.AuthorizedClientSecret));
    }

    // ---------------------------------------------------- host: remote gallery
    //
    // Lets a paired receiver PC browse and play/download this instance's own
    // ClipsFolder tree remotely -- "Connect to another PC's clips" only ever
    // actually wired up RAM disk control and plugin updates before this;
    // Gallery itself was purely local-filesystem regardless of pairing state.

    /// <summary>
    /// Resolves a client-supplied relative path against this instance's own
    /// ClipsFolder root, refusing anything that would escape it (../, an
    /// absolute path, a symlink-style trick via GetFullPath normalization) --
    /// a paired client is trusted to browse ITS clips, not arbitrary files
    /// elsewhere on this PC.
    /// </summary>
    private bool TryResolveGalleryPath(string relativePath, out string fullPath, out string? error)
    {
        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = string.IsNullOrEmpty(relativePath) ? root : Path.GetFullPath(Path.Combine(root, relativePath));

        if (candidate != root && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = "";
            error = "That path is outside the clips folder.";
            return false;
        }

        fullPath = candidate;
        error = null;
        return true;
    }

    private string HandleListGallery(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError))
            return JsonSerializer.Serialize(new { error = pathError });

        try
        {
            if (!Directory.Exists(fullPath))
                return JsonSerializer.Serialize(new { error = "That folder doesn't exist on this PC." });

            string[] folders = Directory.GetDirectories(fullPath)
                .Select(d => Path.GetFileName(d) ?? "")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var files = Directory.EnumerateFiles(fullPath)
                .Where(f => GalleryFormats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTimeUtc })
                .ToArray();

            return JsonSerializer.Serialize(new { folders, files });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Writes a JSON header line ({"success":true,"size":N} or
    /// {"success":false,"error":"..."}), then on success streams the raw file
    /// bytes directly after it -- the one request type in this protocol that
    /// isn't a single JSON-line response, since a clip can be way too large
    /// to buffer into one JSON string first.
    /// </summary>
    private async Task HandleGetClipAsync(JsonElement request, NetworkStream stream)
    {
        if (!IsAuthorizedClient(request))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." }));
            return;
        }

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError) ||
            !GalleryFormats.VideoExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = pathError ?? "Not a clip file." }));
            return;
        }

        if (!File.Exists(fullPath))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "That clip doesn't exist on this PC anymore." }));
            return;
        }

        try
        {
            using FileStream fileStream = File.OpenRead(fullPath);
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = true, size = fileStream.Length }));
            await fileStream.CopyToAsync(stream);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            // Too late to send a clean JSON error once bytes may already be
            // flowing -- just drop the connection, the client's own read will
            // come up short of the promised size and treat that as a failure.
            System.Diagnostics.Debug.WriteLine($"get_clip failed mid-transfer: {ex.Message}");
        }
    }

    // ---------------------------------------------- client: remote RAM disk control

    /// <summary>Fetches the paired transmitter PC's current RAM disk configuration. Null if not paired, unreachable, or denied.</summary>
    public async Task<RamDiskSnapshot?> GetRemoteRamDiskSettingsAsync()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "get_ramdisk_settings", secret = _settings.PairedPeerSecret });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            return JsonSerializer.Deserialize<RamDiskSnapshot>(responseLine);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks the paired transmitter PC to apply a new RAM disk configuration.</summary>
    public async Task<(bool Success, string? Error)> SetRemoteRamDiskSettingsAsync(bool enabled, char driveLetter, int sizeMb)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.");

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new
            {
                type = "set_ramdisk_settings",
                secret = _settings.PairedPeerSecret,
                enabled,
                driveLetter = driveLetter.ToString(),
                sizeMb,
            });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return (false, "No response from the transmitter PC.");

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            return (success, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------- client: remote gallery

    /// <summary>Lists one folder (relative to the paired transmitter PC's own ClipsFolder root; empty = root) of its clips. Null if not paired, unreachable, or denied.</summary>
    public async Task<RemoteGalleryListing?> ListRemoteGalleryAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "list_gallery", secret = _settings.PairedPeerSecret, path = relativePath });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            List<string> folders = doc.RootElement.GetProperty("folders").EnumerateArray()
                .Select(e => e.GetString() ?? "").ToList();
            List<RemoteGalleryFile> files = doc.RootElement.GetProperty("files").EnumerateArray()
                .Select(e => new RemoteGalleryFile(
                    e.GetProperty("name").GetString() ?? "",
                    e.GetProperty("size").GetInt64(),
                    e.GetProperty("modified").GetDateTime()))
                .ToList();
            return new RemoteGalleryListing(folders, files);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads one clip (relative path, as returned by ListRemoteGalleryAsync)
    /// from the paired transmitter PC into destPath, overwriting it. Reports
    /// (0-1) progress as bytes arrive, since a clip can be large enough that a
    /// silent multi-second wait would otherwise look like Backtrack hung.
    /// </summary>
    public async Task<(bool Success, string? Error)> DownloadRemoteClipAsync(string relativePath, string destPath, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.");

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "get_clip", secret = _settings.PairedPeerSecret, path = relativePath });
            NetworkStream stream = client.GetStream();
            await WriteLineAsync(stream, request);

            string? headerLine = await ReadLineAsync(stream);
            if (headerLine is null)
                return (false, "No response from the transmitter PC.");

            using JsonDocument doc = JsonDocument.Parse(headerLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            if (!success)
                return (false, doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : "Download failed.");

            long size = doc.RootElement.GetProperty("size").GetInt64();

            // ReadLineAsync above consumes the header line byte-by-byte up to and
            // including its trailing '\n' and nothing past it (see its own
            // implementation), so the stream is positioned exactly at the start
            // of the raw file bytes that follow -- safe to read directly here.
            string tempPath = destPath + ".partial";
            long received = 0;
            var buffer = new byte[81920];
            await using (var file = System.IO.File.Create(tempPath))
            {
                while (received < size)
                {
                    int toRead = (int)Math.Min(buffer.Length, size - received);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
                    if (read == 0)
                        break; // connection dropped mid-transfer
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    progress?.Report(size > 0 ? (double)received / size : 1.0);
                }
            }

            if (received != size)
            {
                System.IO.File.Delete(tempPath);
                return (false, "Connection dropped before the whole clip arrived.");
            }

            System.IO.File.Delete(destPath); // ok if it doesn't exist
            System.IO.File.Move(tempPath, destPath);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // -------------------------------------------- host/client: remote plugin updates
    //
    // Same shape as RAM disk above: obs-replay-slider/obs-source-record version
    // checking and auto-updating reads/writes a hardcoded local OBS install path
    // (see UpdateService), so it's just as wrong to run from a receiver PC as
    // mounting a RAM disk there is. Reuses the same paired-client secret.

    /// <summary>Set by MainWindow so an authenticated request can trigger the real check-and-apply on this instance and get the result back.</summary>
    public Func<Task<PluginVersionsSnapshot>>? CheckAndApplyPluginUpdatesRemotely { get; set; }

    private async Task<string> HandleCheckPluginUpdatesAsync(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        if (CheckAndApplyPluginUpdatesRemotely is null)
            return JsonSerializer.Serialize(new { error = "Plugin update control isn't wired up on this instance." });

        PluginVersionsSnapshot snapshot = await CheckAndApplyPluginUpdatesRemotely();
        return JsonSerializer.Serialize(snapshot);
    }

    /// <summary>Triggers a real check-and-apply of both OBS plugins on the paired transmitter PC. Null if not paired, unreachable, or denied.</summary>
    public async Task<PluginVersionsSnapshot?> CheckRemotePluginUpdatesAsync()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "check_plugin_updates", secret = _settings.PairedPeerSecret });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;

            return JsonSerializer.Deserialize<PluginVersionsSnapshot>(responseLine);
        }
        catch
        {
            return null;
        }
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

            /* An empty code means the host already had another pairing request
             * awaiting a decision and rejected this one outright (see the busy
             * check in HandlePairRequest) -- skip straight to that instead of
             * flashing a blank code for the one second it takes the first poll
             * below to come back "denied". */
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
