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
    private readonly ConcurrentDictionary<string, (string RelativePath, long TotalSize)> _sessions = new();

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
    /// URL to hand libvlc. totalSize comes from the caller's already-known
    /// RemoteGalleryFile.Size (from list_gallery), not a fresh network round
    /// trip -- libvlc needs a real Content-Length up front to know the clip
    /// has an end at all (otherwise it can render as a live/unseekable
    /// stream instead of a normal seekable video).
    /// </summary>
    public string PrepareStream(string relativePath, long totalSize)
    {
        EnsureStarted();
        string token = Guid.NewGuid().ToString("N");
        _sessions[token] = (relativePath, totalSize);
        return $"http://127.0.0.1:{_port}/stream/{token}";
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
        try
        {
            Match match = TokenPattern.Match(context.Request.Url?.AbsolutePath ?? "");
            if (!match.Success || !_sessions.TryGetValue(match.Groups[1].Value, out var session))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            (string relativePath, long totalSize) = session;

            // libvlc sends "bytes=N-" when it seeks (never a bounded "N-M"
            // range in practice for this kind of open-ended media playback,
            // but only the start offset is actually needed here regardless --
            // this server always streams from that point to the real end).
            long offset = 0;
            string? rangeHeader = context.Request.Headers["Range"];
            if (rangeHeader is not null)
            {
                Match rangeMatch = Regex.Match(rangeHeader, @"bytes=(\d+)-");
                if (rangeMatch.Success)
                    offset = long.Parse(rangeMatch.Groups[1].Value);
            }
            offset = Math.Clamp(offset, 0, totalSize);

            long remaining = totalSize - offset;
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
                context.Response.Headers["Content-Range"] = $"bytes {offset}-{totalSize - 1}/{totalSize}";
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            using var cts = new CancellationTokenSource();
            context.Response.OutputStream.WriteTimeout = Timeout.Infinite;
            (bool success, string? error) = await _pairing.StreamRemoteClipToAsync(relativePath, offset, context.Response.OutputStream, cts.Token);
            if (!success)
                Debug.WriteLine($"RemoteClipStreamServer: relay for '{relativePath}' from offset {offset} ended early: {error}");
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
