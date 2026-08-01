using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace CaptureCenter.Remote;

/// <summary>
/// Serves the one static remote-control page over the LAN. This is *only* a
/// static file server -- it has no idea what OBS is or how to control it.
/// The page's own JavaScript connects straight to OBS's websocket (the same
/// protocol ObsClient.cs speaks), so there's no second control-relay server
/// to keep in sync with OBS's own API.
/// </summary>
public sealed class RemoteServer
{
    private readonly int _port;
    private HttpListener? _listener;

    public RemoteServer(int port = 5757)
    {
        _port = port;
    }

    public bool IsRunning => _listener?.IsListening == true;

    public string? LocalUrl => IsRunning ? $"http://{GetLanIPAddress()}:{_port}/" : null;

    public bool Start()
    {
        if (IsRunning)
            return true;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            _ = AcceptLoopAsync(_listener);
            return true;
        }
        catch
        {
            // Most likely cause on a non-admin process: no URL ACL reservation
            // for a wildcard prefix. Caller falls back to reporting "couldn't start".
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // best effort
        }
        _listener = null;
    }

    private static async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                return; // listener stopped
            }
            _ = HandleAsync(ctx);
        }
    }

    private static async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(ReadEmbeddedPage());
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
        }
        catch
        {
            // best effort -- a dropped mobile connection isn't worth surfacing
        }
        finally
        {
            ctx.Response.OutputStream.Close();
        }
    }

    private static string? _cachedPage;

    private static string ReadEmbeddedPage()
    {
        if (_cachedPage is not null)
            return _cachedPage;

        string path = Path.Combine(AppContext.BaseDirectory, "Web", "remote.html");
        _cachedPage = File.ReadAllText(path);
        return _cachedPage;
    }

    private static string GetLanIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // no packet actually sent; just resolves the outbound-facing local IP
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
