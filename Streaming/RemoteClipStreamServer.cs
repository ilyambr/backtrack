using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backtrack.Pairing;

namespace Backtrack.Streaming;

public sealed class RemoteClipStreamServer : IDisposable
{
    private readonly PairingService _pairing;
    private HttpListener? _listener;
    private int _port;

    private sealed record StreamSession(string RelativePath, DateTime CreatedUtc, DateTime LastAccessUtc);
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private Timer? _pruneTimer;

    public event Action<string, long>? StreamStarted;

    public RemoteClipStreamServer(PairingService pairing)
    {
        _pairing = pairing;
        _pruneTimer = new Timer(_ => PruneExpiredSessions(TimeSpan.FromMinutes(15)), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

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
            }
        }

        Debug.WriteLine("RemoteClipStreamServer: couldn't bind any port in its whole range -- remote clip streaming will fail until Backtrack restarts.");
    }

    public string PrepareStream(string relativePath)
    {
        EnsureStarted();
        string token = Guid.NewGuid().ToString("N");
        _sessions[token] = new StreamSession(relativePath, DateTime.UtcNow, DateTime.UtcNow);
        return $"http://127.0.0.1:{_port}/stream/{token}";
    }

    public void ReleaseSession(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }

    public void UpdateSessionPath(string token, string newRelativePath)
    {
        if (_sessions.TryGetValue(token, out var existing))
            _sessions[token] = existing with { RelativePath = newRelativePath, LastAccessUtc = DateTime.UtcNow };
    }

    public void PruneExpiredSessions(TimeSpan maxAge)
    {
        DateTime cutoff = DateTime.UtcNow - maxAge;
        foreach (var kv in _sessions)
        {
            if (kv.Value.LastAccessUtc < cutoff)
                _sessions.TryRemove(kv.Key, out _);
        }
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
                return;
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
            if (!match.Success || !_sessions.TryGetValue(match.Groups[1].Value, out StreamSession? session))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            string token = match.Groups[1].Value;
            string relativePath = session.RelativePath;
            _sessions[token] = session with { LastAccessUtc = DateTime.UtcNow };

            long offset = 0;
            string? rangeHeader = context.Request.Headers["Range"];
            if (rangeHeader is not null)
            {
                Match rangeMatch = Regex.Match(rangeHeader, @"bytes=(\d+)-");
                if (rangeMatch.Success)
                    offset = long.Parse(rangeMatch.Groups[1].Value);
            }
            offset = Math.Max(offset, 0);

            (bool opened, string? openError, upstreamClient, System.Net.Sockets.NetworkStream? sourceStream, long remaining) =
                await _pairing.OpenRemoteClipStreamAsync(relativePath, offset, CancellationToken.None);
            if (!opened || sourceStream is null)
            {
                Debug.WriteLine($"RemoteClipStreamServer: couldn't open '{relativePath}' from offset {offset}: {openError}");
                context.Response.StatusCode = 504; // Gateway Timeout / Upstream error
                return;
            }

            long total = offset + remaining;
            StreamStarted?.Invoke(token, total);
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.ContentType = "video/mp4";
            context.Response.ContentLength64 = remaining;
            if (offset > 0)
            {
                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {offset}-{total - 1}/{total}";
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            await sourceStream.CopyToAsync(context.Response.OutputStream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RemoteClipStreamServer: request ended: {ex.Message}");
        }
        finally
        {
            upstreamClient?.Dispose();
            try { context.Response.Close(); } catch { }
        }
    }

    public void Stop()
    {
        try { _pruneTimer?.Dispose(); } catch { }
        _pruneTimer = null;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
