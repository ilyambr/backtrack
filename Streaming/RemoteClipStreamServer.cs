using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backtrack.Pairing;

namespace Backtrack.Streaming;

/// <summary>
/// A tiny loopback-only HTTP server that lets libvlc play a remote clip
/// directly over the network instead of Backtrack downloading the whole
/// file to disk first (see OpenRemoteClipStreamingAsync). libvlc's own
/// Media can be pointed at any HTTP URL and buffers/plays progressively on
/// its own -- this server's only job is translating that HTTP request
/// (including any Range header libvlc sends when seeking) into this app's
/// existing pairing-protocol get_clip request against the transmitter PC,
/// relaying bytes straight through as they arrive. Nothing ever touches
/// disk here -- a genuinely different tradeoff from RemoteCache/
/// DownloadRemoteClipAsync's own download-then-play path, deliberately: no
/// local copy survives after playback, by design (see OpenRemoteClipStreamingAsync's
/// own comment).
///
/// Loopback (127.0.0.1) only, never 0.0.0.0 -- nothing outside this PC
/// should ever be able to hit this, and Windows Firewall doesn't filter
/// loopback traffic at all, so this needs no firewall rule the way the
/// real pairing ports do.
/// </summary>
public sealed class RemoteClipStreamServer
{
    private readonly PairingService _pairing;
    private HttpListener? _listener;
    private int _port;

    // Keyed by a random per-open-clip token (not the relative path itself --
    // a path can contain characters that don't round-trip cleanly through a
    // URL segment, and a token also means a stale/old tab can't accidentally
    // keep working after a newer clip replaces it). One entry per
    // currently-open streamed clip; in practice just ever the most recent
    // one, but keyed rather than a single field in case a stale request from
    // a clip that was just switched away from is still in flight.
    //
    // Just the relative path -- no size tracked here at all anymore. Each
    // request asks the transmitter fresh (see OpenRemoteClipStreamAsync) and
    // trusts THAT answer for Content-Length, never a value cached from
    // whenever this session was first prepared -- see HandleRequestAsync's
    // own comment for the real bug that fixed.
    private readonly ConcurrentDictionary<string, string> _sessions = new();

    public event Action<string, long>? StreamStarted;

    public RemoteClipStreamServer(PairingService pairing)
    {
        _pairing = pairing;
    }

    /// <summary>
    /// Starts listening if not already running. Tries a small fixed range of
    /// ports (the real pairing TCP/UDP ports plus one, see PairingService's
    /// own DefaultPairingPort/BroadcastPort) rather than just failing outright
    /// if the first choice is somehow taken -- another app (or a second
    /// Backtrack instance, however unlikely) squatting on one exact port
    /// shouldn't take this whole feature down.
    /// </summary>
    public void EnsureStarted()
    {
        if (_listener is not null)
            return;

        for (int candidatePort = 47813; candidatePort < 47823; candidatePort++)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
                listener.Start();
                _listener = listener;
                _port = candidatePort;
                _ = AcceptLoopAsync(listener);
                return;
            }
            catch (HttpListenerException)
            {
                // Port already in use -- try the next one.
            }
        }

        Debug.WriteLine("RemoteClipStreamServer: couldn't bind any port in its whole range -- remote clip streaming will fail until Backtrack restarts.");
    }

    /// <summary>
    /// Registers a new streaming session for one clip and returns the local
    /// URL to hand libvlc. No size passed in at all -- every request against
    /// this token asks the transmitter for the clip's real, current size
    /// fresh (see HandleRequestAsync), so there's nothing here that can ever
    /// go stale.
    /// </summary>
    public string PrepareStream(string relativePath)
    {
        EnsureStarted();
        string token = Guid.NewGuid().ToString("N");
        _sessions[token] = relativePath;
        return $"http://127.0.0.1:{_port}/stream/{token}";
    }

    /// <summary>
    /// Updates an already-open session's relative path in place -- for a
    /// remote rename applied WHILE that same clip is actively streaming
    /// (see MainWindow.PlayerRename_Click's remote branch): the clip itself
    /// on the transmitter didn't change, just the path it lives at, and any
    /// already-in-flight relay request keeps reading from wherever it
    /// already connected regardless. Only a FUTURE seek (a fresh HTTP Range
    /// request against this same token) would otherwise ask for the OLD,
    /// now-renamed path and 404 -- this is what keeps that working without
    /// needing to restart playback over a brand new URL/token.
    /// </summary>
    public void UpdateSessionPath(string token, string newRelativePath)
    {
        if (_sessions.ContainsKey(token))
            _sessions[token] = newRelativePath;
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (!listener.IsListening)
            {
                return; // Stop() called -- GetContextAsync throwing is the normal way that surfaces.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RemoteClipStreamServer: accept failed: {ex.Message}");
                continue;
            }

            _ = HandleRequestAsync(context);
        }
    }

    private static readonly Regex TokenPattern = new(@"^/stream/([0-9a-f]{32})$", RegexOptions.Compiled);

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        System.Net.Sockets.TcpClient? upstreamClient = null;
        try
        {
            Match match = TokenPattern.Match(context.Request.Url?.AbsolutePath ?? "");
            if (!match.Success || !_sessions.TryGetValue(match.Groups[1].Value, out string? relativePath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            string token = match.Groups[1].Value;

            // libvlc sends "bytes=N-" when it seeks (never a bounded "N-M"
            // range in practice for this kind of open-ended media playback,
            // but only the start offset is actually needed here regardless --
            // this server always streams from that point to the real end).
            // Clamped to a floor of 0 only -- the real ceiling isn't known
            // yet at this point (see below), unlike the old version of this
            // method which had a cached total to clamp against.
            long offset = 0;
            string? rangeHeader = context.Request.Headers["Range"];
            if (rangeHeader is not null)
            {
                Match rangeMatch = Regex.Match(rangeHeader, @"bytes=(\d+)-");
                if (rangeMatch.Success)
                    offset = long.Parse(rangeMatch.Groups[1].Value);
            }
            offset = Math.Max(offset, 0);

            // Connects and reads the transmitter's response header (which
            // always reflects the file's real, current size -- freshly read
            // off disk on that end, see StreamFileResponseAsync) BEFORE this
            // commits to any HTTP headers of its own -- see
            // OpenRemoteClipStreamAsync's own comment for the stale-size bug
            // this replaced.
            (bool opened, string? openError, upstreamClient, System.Net.Sockets.NetworkStream? sourceStream, long remaining) =
                await _pairing.OpenRemoteClipStreamAsync(relativePath, offset, CancellationToken.None);
            if (!opened || sourceStream is null)
            {
                Debug.WriteLine($"RemoteClipStreamServer: couldn't open '{relativePath}' from offset {offset}: {openError}");
                context.Response.StatusCode = 502;
                return;
            }

            long total = offset + remaining;
            StreamStarted?.Invoke(token, total);
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.ContentType = "video/mp4";
            context.Response.ContentLength64 = remaining;
            if (offset > 0)
            {
                // 206 Partial Content -- what tells libvlc (and any other real
                // HTTP client) this server actually honors Range requests, so
                // seeking keeps working instead of it giving up after the
                // first one and re-downloading from the start every time.
                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {offset}-{total - 1}/{total}";
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            // No WriteTimeout override here -- HttpListenerResponse.OutputStream
            // doesn't reliably support setting one (CanTimeout is false on its
            // real implementation), and setting it anyway throws immediately,
            // before a single byte goes out. Confirmed live as an earlier
            // cause of a total playback failure.
            await sourceStream.CopyToAsync(context.Response.OutputStream);
        }
        catch (Exception ex)
        {
            // The client (libvlc) disconnecting mid-stream -- e.g. it seeked
            // again before this response finished, or playback just stopped
            // -- surfaces here as a write failure on the now-closed response.
            // Entirely normal, not worth logging as an error every time.
            Debug.WriteLine($"RemoteClipStreamServer: request ended: {ex.Message}");
        }
        finally
        {
            upstreamClient?.Dispose();
            try { context.Response.Close(); } catch { /* best effort -- may already be closed/broken */ }
        }
    }

    /// <summary>Called once, from App shutdown -- releases the port cleanly instead of leaving it bound until the process actually dies.</summary>
    public void Stop()
    {
        try { _listener?.Stop(); } catch { /* best effort */ }
        _listener = null;
    }
}
