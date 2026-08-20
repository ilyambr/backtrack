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
using Backtrack.Interop;

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
    // Public so Interop/FirewallRules.cs can reference the exact same values
    // when setting up its inbound/outbound rules, instead of duplicating
    // these two numbers as a second, driftable copy over there.
    public const int BroadcastPort = 47811;
    public const int DefaultPairingPort = 47812;
    private const string AnnounceType = "backtrack-announce";

    // Bounds delete_clip/rename_clip specifically -- those two are the ones a
    // caller (QueueRemoteDeleteWithUndo's onExpire, in particular) treats as
    // "fire once and reconcile the UI from whatever comes back." Without a
    // timeout, a peer that accepted the TCP connection but then went
    // unreachable mid-request (sleep, frozen, dropped Tailscale route -- no
    // RST, so ConnectAsync/ReadAsync never fault or return 0) leaves that
    // await hanging forever: the undo toast has already expired and cleared
    // _pendingRemoteDeletePaths by then, so the clip sits there indefinitely
    // with the UI unable to say whether it's deleted, still there, or stuck.
    private static readonly TimeSpan MutationRequestTimeout = TimeSpan.FromSeconds(15);

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

                // get_clip/get_thumbnail are the request types whose response
                // isn't a single JSON line -- each writes a JSON header line and
                // then (on success) streams raw bytes directly after it, so they
                // have to own writing to the connection themselves instead of
                // going through the uniform "one line in, one line out" dispatch
                // below.
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
                // put_clip is get_clip's mirror image -- the CLIENT streams raw
                // bytes right after its own request line instead of the host
                // streaming them after its response, so this needs the same
                // direct-stream-access carve-out as the two above.
                if (type == "put_clip")
                {
                    await HandlePutClipAsync(doc.RootElement, client.GetStream());
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

    /// <summary>
    /// Set by MainWindow so an authenticated request can reuse this instance's
    /// own local thumbnail cache (MainWindow.EnsureThumbnailCachedAsync) instead
    /// of this class duplicating LibVLC frame-grab logic it has no access to --
    /// takes a resolved local full path, returns the cached .jpg's path (running
    /// generation first if it wasn't already cached for this PC's own Gallery).
    /// </summary>
    public Func<string, Task<string?>>? EnsureThumbnailCachedForRemote { get; set; }

    /// <summary>
    /// Set by MainWindow so list_gallery can hide obviously-broken/glitched
    /// clips (e.g. a save triggered right as OBS started) the exact same way
    /// the local Gallery does -- takes a resolved local full path, returns
    /// its cached duration in milliseconds if known (null if not yet
    /// probed, in which case HandleListGallery shows it optimistically
    /// rather than hiding it on a guess, same as MainWindow.LoadGallery).
    /// </summary>
    public Func<string, long?>? GetCachedDurationMsForRemote { get; set; }

    /// <summary>
    /// The relative path (forward-slash separated, matching MainWindow's own
    /// RemoteClipRelativePath convention client-side) of the single most-
    /// recently-written clip anywhere in this PC's whole clips tree -- the
    /// remote-gallery equivalent of MainWindow.GetNewestClipPath, which only
    /// ever scans _settings.ClipsFolder on THIS instance and so never had any
    /// way to answer that question for a paired PC's tree. Without this,
    /// list_gallery's own per-folder listing has no way to know whether
    /// anything in it is the newest clip system-wide, so a remote Gallery
    /// could never show the same "newest" dot the local one does.
    /// </summary>
    private string HandleNewestClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { error = "Not authorized -- pair with this PC first." });

        try
        {
            string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root))
                return JsonSerializer.Serialize(new { path = (string?)null });

            string? newest = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => GalleryFormats.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                // Same glitched-clip filter HandleListGallery already applies --
                // without it, this could point a paired PC's "newest" dot at a
                // sub-2s glitched clip that list_gallery itself hides, leading
                // nowhere a receiver could actually find it.
                .Where(f => GetCachedDurationMsForRemote?.Invoke(f.FullName) is not < 2000)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;

            string? relativePath = newest is null ? null
                : Path.GetRelativePath(root, newest).Replace(Path.DirectorySeparatorChar, '/');
            return JsonSerializer.Serialize(new { path = relativePath });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
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
                // Same filter as MainWindow.LoadGallery: hide a clip only once
                // its duration is actually known and under 2s (a save
                // triggered right as OBS started, or a buffer barely armed
                // before saving) -- an unprobed clip (null) shows optimistically
                // rather than being hidden on a guess.
                .Where(f => GetCachedDurationMsForRemote?.Invoke(f.FullName) is not < 2000)
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
    /// Sends this instance's own real clip to the Recycle Bin on behalf of a
    /// paired receiver PC -- the "delete a clip remotely, deleting it for
    /// real on the stream PC" half of the round-trip; a receiver's own local
    /// cached copy (if it downloaded this clip before) is the receiver's own
    /// concern to clean up, not this instance's.
    /// </summary>
    private string HandleDeleteClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError) ||
            !GalleryFormats.VideoExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()))
            return JsonSerializer.Serialize(new { success = false, error = pathError ?? "Not a clip file." });

        try
        {
            if (!File.Exists(fullPath))
                return JsonSerializer.Serialize(new { success = false, error = "That clip doesn't exist on this PC anymore." });

            return RecycleBin.Delete(fullPath)
                ? JsonSerializer.Serialize(new { success = true })
                : JsonSerializer.Serialize(new { success = false, error = "The Recycle Bin operation failed." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Renames this instance's own real clip file on behalf of a paired
    /// receiver PC. Same "just fail on a name collision" behavior as
    /// MainWindow.BeginRename's local rename (no auto-dedupe) -- unlike
    /// HandlePutClipAsync below, which DOES need to dedupe since a
    /// trim-save-new upload is closer in spirit to CopyToThisPcAsync than to
    /// a deliberate rename. Returns the new relative path so the receiver's
    /// own Gallery/Player can reflect it without a full reload round trip.
    /// </summary>
    private string HandleRenameClip(JsonElement request)
    {
        if (!IsAuthorizedClient(request))
            return JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." });

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        string newName = request.TryGetProperty("newName", out JsonElement n) ? n.GetString() ?? "" : "";

        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError) ||
            !GalleryFormats.VideoExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()))
            return JsonSerializer.Serialize(new { success = false, error = pathError ?? "Not a clip file." });
        if (string.IsNullOrWhiteSpace(newName))
            return JsonSerializer.Serialize(new { success = false, error = "Name can't be empty." });

        try
        {
            if (!File.Exists(fullPath))
                return JsonSerializer.Serialize(new { success = false, error = "That clip doesn't exist on this PC anymore." });

            string newRelativePath = WithNewFileName(relativePath, newName + Path.GetExtension(fullPath));
            if (!TryResolveGalleryPath(newRelativePath, out string newFullPath, out string? newPathError))
                return JsonSerializer.Serialize(new { success = false, error = newPathError ?? "Invalid name." });
            if (File.Exists(newFullPath))
                return JsonSerializer.Serialize(new { success = false, error = "A clip with that name already exists." });

            File.Move(fullPath, newFullPath);
            return JsonSerializer.Serialize(new { success = true, path = newRelativePath });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>Swaps the file-name component of a '/'-separated relative gallery path, keeping whatever folder prefix it had.</summary>
    private static string WithNewFileName(string relativePath, string newFileName)
    {
        int lastSlash = relativePath.LastIndexOf('/');
        string folderPrefix = lastSlash < 0 ? "" : relativePath[..lastSlash];
        return folderPrefix.Length == 0 ? newFileName : $"{folderPrefix}/{newFileName}";
    }

    /// <summary>
    /// get_clip's mirror image: the CLIENT streams the raw file bytes right
    /// after its own request line (already positioned exactly there, same
    /// reasoning as get_clip's own response-side comment), and this writes
    /// them to disk here -- the "send a trimmed/edited clip back to the
    /// stream PC" half of the round trip. `overwrite:true` (a trim-replace)
    /// writes straight over the named path; `overwrite:false` (trim-save-new,
    /// or any future "upload as a new clip") dedupes onto "(2)", "(3)", etc.
    /// instead of clobbering something already there under that exact name,
    /// same idea as BuildRecordFolderRowAsync-style collision avoidance
    /// elsewhere in this app, just server-side since only the host actually
    /// knows what's already sitting in that folder.
    /// </summary>
    private async Task HandlePutClipAsync(JsonElement request, NetworkStream stream)
    {
        if (!IsAuthorizedClient(request))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "Not authorized -- pair with this PC first." }));
            return;
        }

        string relativePath = request.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
        long size = request.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : -1;
        bool overwrite = request.TryGetProperty("overwrite", out JsonElement ow) && ow.GetBoolean();

        if (!TryResolveGalleryPath(relativePath, out string fullPath, out string? pathError) ||
            !GalleryFormats.VideoExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()) || size < 0)
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = pathError ?? "Not a valid clip upload." }));
            return;
        }

        string finalRelativePath = relativePath;
        if (!overwrite && File.Exists(fullPath))
        {
            string dir = Path.GetDirectoryName(fullPath)!;
            string baseName = Path.GetFileNameWithoutExtension(fullPath);
            string ext = Path.GetExtension(fullPath);
            int i = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
                i++;
            } while (File.Exists(candidate));
            fullPath = candidate;
            finalRelativePath = WithNewFileName(relativePath, Path.GetFileName(fullPath));
        }

        string tempPath = fullPath + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            long received = 0;
            var buffer = new byte[81920];
            await using (var file = File.Create(tempPath))
            {
                while (received < size)
                {
                    int toRead = (int)Math.Min(buffer.Length, size - received);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
                    if (read == 0)
                        break; // connection dropped mid-transfer
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                }
            }

            if (received != size)
            {
                File.Delete(tempPath);
                await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "Connection dropped before the whole file arrived." }));
                return;
            }

            File.Delete(fullPath); // ok if it doesn't exist -- the overwrite:true case
            File.Move(tempPath, fullPath);
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = true, path = finalRelativePath }));
        }
        catch (Exception ex)
        {
            try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = ex.Message }));
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

        await StreamFileResponseAsync(stream, fullPath);
    }

    /// <summary>
    /// Same wire shape as HandleGetClipAsync (JSON header line, then raw bytes
    /// on success), but for one clip's thumbnail -- reuses this instance's own
    /// local thumbnail cache (see EnsureThumbnailCachedForRemote) rather than
    /// duplicating LibVLC frame-grab logic here, so a clip already thumbnailed
    /// for this PC's own Gallery (see PrewarmGalleryThumbnailsAsync) is usually
    /// an instant response, not a fresh generation per remote request.
    /// </summary>
    private async Task HandleGetThumbnailAsync(JsonElement request, NetworkStream stream)
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

        if (EnsureThumbnailCachedForRemote is null)
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "Thumbnails aren't available on this PC right now." }));
            return;
        }

        string? thumbnailPath;
        try
        {
            thumbnailPath = await EnsureThumbnailCachedForRemote(fullPath);
        }
        catch (Exception ex)
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = ex.Message }));
            return;
        }

        if (thumbnailPath is null || !File.Exists(thumbnailPath))
        {
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = false, error = "Couldn't generate a thumbnail for that clip." }));
            return;
        }

        await StreamFileResponseAsync(stream, thumbnailPath);
    }

    private static async Task StreamFileResponseAsync(NetworkStream stream, string localPath)
    {
        try
        {
            using FileStream fileStream = File.OpenRead(localPath);
            await WriteLineAsync(stream, JsonSerializer.Serialize(new { success = true, size = fileStream.Length }));
            await fileStream.CopyToAsync(stream);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            // Too late to send a clean JSON error once bytes may already be
            // flowing -- just drop the connection, the client's own read will
            // come up short of the promised size and treat that as a failure.
            System.Diagnostics.Debug.WriteLine($"File stream response failed mid-transfer: {ex.Message}");
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

    /// <summary>
    /// Forward-slash relative path of the paired transmitter PC's own newest
    /// clip, anywhere in its whole clips tree -- the remote counterpart to
    /// MainWindow.GetNewestClipPath, used to drive the same "newest" dot in
    /// the remote Gallery that the local one already has. Null if not
    /// paired, unreachable, denied, or the transmitter genuinely has no
    /// clips yet.
    /// </summary>
    public async Task<string?> GetRemoteNewestClipPathAsync()
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return null;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = "newest_clip", secret = _settings.PairedPeerSecret });
            await WriteLineAsync(client.GetStream(), request);
            string? responseLine = await ReadLineAsync(client.GetStream());
            if (responseLine is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("error", out _))
                return null;
            return doc.RootElement.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

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
    public Task<(bool Success, string? Error)> DownloadRemoteClipAsync(string relativePath, string destPath, IProgress<double>? progress = null) =>
        DownloadStreamedFileAsync("get_clip", relativePath, destPath, progress);

    /// <summary>Same idea as DownloadRemoteClipAsync, but for one clip's thumbnail -- see HandleGetThumbnailAsync on the host side.</summary>
    public Task<(bool Success, string? Error)> DownloadRemoteThumbnailAsync(string relativePath, string destPath) =>
        DownloadStreamedFileAsync("get_thumbnail", relativePath, destPath);

    /// <summary>Deletes one clip on the paired transmitter PC for real (Recycle Bin, see HandleDeleteClip) -- not just this PC's own local cached copy of it.</summary>
    public Task<(bool Success, string? Error)> DeleteRemoteClipAsync(string relativePath) =>
        SendClipMutationRequestAsync("delete_clip", new Dictionary<string, object?> { ["path"] = relativePath });

    /// <summary>Renames one clip on the paired transmitter PC for real. NewPath is the new relative path (as ListRemoteGalleryAsync would report it) on success.</summary>
    public async Task<(bool Success, string? Error, string? NewPath)> RenameRemoteClipAsync(string relativePath, string newName)
    {
        (bool success, string? error, string? path) = await SendClipMutationRequestWithPathAsync("rename_clip",
            new Dictionary<string, object?> { ["path"] = relativePath, ["newName"] = newName });
        return (success, error, path);
    }

    private async Task<(bool Success, string? Error)> SendClipMutationRequestAsync(string type, Dictionary<string, object?> fields)
    {
        (bool success, string? error, _) = await SendClipMutationRequestWithPathAsync(type, fields);
        return (success, error);
    }

    private async Task<(bool Success, string? Error, string? Path)> SendClipMutationRequestWithPathAsync(string type, Dictionary<string, object?> fields)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort).WaitAsync(MutationRequestTimeout);
            fields["type"] = type;
            fields["secret"] = _settings.PairedPeerSecret;
            await WriteLineAsync(client.GetStream(), JsonSerializer.Serialize(fields)).WaitAsync(MutationRequestTimeout);
            string? responseLine = await ReadLineAsync(client.GetStream()).WaitAsync(MutationRequestTimeout);
            if (responseLine is null)
                return (false, "No response from the transmitter PC.", null);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? path = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
            return (success, success ? null : (error ?? "Request failed."), path);
        }
        catch (TimeoutException)
        {
            return (false, $"{_settings.PairedPeerName ?? "The paired PC"} didn't respond in time -- it may be asleep, unreachable, or frozen.", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// Uploads a local file to the paired transmitter PC as one of ITS real
    /// clips -- the "send an edited/trimmed clip back to the stream PC" half
    /// of the round trip, mirror image of DownloadRemoteClipAsync. `overwrite`
    /// true replaces relativePath exactly (a trim-replace); false lets
    /// HandlePutClipAsync dedupe onto "(2)"/"(3)" etc. instead (a
    /// trim-save-new) -- the actual path it landed at comes back as Path,
    /// which may differ from relativePath in that case.
    /// </summary>
    public async Task<(bool Success, string? Error, string? Path)> UploadRemoteClipAsync(
        string relativePath, string localSourcePath, bool overwrite, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.", null);

        try
        {
            using FileStream fileStream = File.OpenRead(localSourcePath);
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            NetworkStream stream = client.GetStream();
            string request = JsonSerializer.Serialize(new
            {
                type = "put_clip",
                secret = _settings.PairedPeerSecret,
                path = relativePath,
                size = fileStream.Length,
                overwrite,
            });
            await WriteLineAsync(stream, request);

            long total = fileStream.Length;
            long sent = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
                sent += read;
                progress?.Report(total > 0 ? (double)sent / total : 1.0);
            }
            await stream.FlushAsync();

            string? responseLine = await ReadLineAsync(stream);
            if (responseLine is null)
                return (false, "No response from the transmitter PC.", null);

            using JsonDocument doc = JsonDocument.Parse(responseLine);
            bool success = doc.RootElement.TryGetProperty("success", out JsonElement s) && s.GetBoolean();
            string? error = doc.RootElement.TryGetProperty("error", out JsonElement er) ? er.GetString() : null;
            string? path = doc.RootElement.TryGetProperty("path", out JsonElement pt) ? pt.GetString() : null;
            return (success, success ? null : (error ?? "Upload failed."), path);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    // Keyed by destPath: switching clips fast enough to re-click one that's
    // still downloading (or a genuine accidental double-click) used to fire
    // a second DownloadStreamedFileAsync for the exact same destPath while
    // the first was still running -- both raced to File.Create() the same
    // ".partial" temp file, and the loser surfaced a raw "being used by
    // another process" IOException to the user. Lazy<Task<...>> (not just a
    // plain Task in the dictionary) matters here: ConcurrentDictionary.GetOrAdd
    // can invoke its factory more than once under a real race even though it
    // only ever stores one result, so a bare Task factory could still start
    // the download twice; Lazy's own synchronization guarantees the download
    // itself only ever actually runs once, and every caller for that destPath
    // -- first or piggybacking -- gets the same result.
    private readonly ConcurrentDictionary<string, Lazy<Task<(bool Success, string? Error)>>> _activeDownloads = new();

    private Task<(bool Success, string? Error)> DownloadStreamedFileAsync(string requestType, string relativePath, string destPath, IProgress<double>? progress = null)
    {
        Lazy<Task<(bool Success, string? Error)>> lazy = _activeDownloads.GetOrAdd(destPath,
            _ => new Lazy<Task<(bool Success, string? Error)>>(
                () => DownloadStreamedFileCoreAsync(requestType, relativePath, destPath, progress)));
        Task<(bool Success, string? Error)> task = lazy.Value;
        _ = task.ContinueWith(completed => _activeDownloads.TryRemove(destPath, out _), TaskScheduler.Default);
        return task;
    }

    private async Task<(bool Success, string? Error)> DownloadStreamedFileCoreAsync(string requestType, string relativePath, string destPath, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerHost) || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return (false, "Not paired with a transmitter PC.");

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_settings.PairedPeerHost, _settings.PairedPeerPort);
            string request = JsonSerializer.Serialize(new { type = requestType, secret = _settings.PairedPeerSecret, path = relativePath });
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
            // Missing on the RemoteCache side until now -- HandlePutClipAsync
            // (the upload/server direction, right above) already does this
            // before its own File.Create, but this download direction never
            // did. RemoteCache mirrors the transmitter's own folder structure
            // per device (see MainWindow.xaml.cs's GetRemoteCacheDir), so the
            // first-ever download of a clip from a given subfolder (e.g.
            // "Elgato") hits a directory that genuinely doesn't exist yet --
            // confirmed live as "Could not find a part of the path
            // '...\RemoteCache\<guid>\Elgato\....partial'".
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
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
                return (false, "Connection dropped before the whole file arrived.");
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
