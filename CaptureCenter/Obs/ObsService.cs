using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace CaptureCenter.Obs;

public sealed record ReplayRow(string Key, string Label, string Hotkey, int Status, int LengthSeconds);

public sealed record RecordStatus(bool Active, long DurationMs);

/// <summary>
/// Thin façade over <see cref="ObsClient"/>: owns the connect/retry loop and
/// exposes the handful of calls the overlay UI actually needs, including the
/// two custom requests the patched obs-replay-slider plugin exposes as an
/// obs-websocket vendor (see vendor/obs-replay-slider/src/websocket-bridge.cpp).
///
/// The OBS instance this talks to doesn't have to be on this PC -- e.g. a
/// two-PC setup where OBS runs on a separate stream/broadcast machine and this
/// overlay runs on the PC you actually sit at. <see cref="Reconfigure"/> lets
/// Settings point it at a different host without restarting the app.
/// </summary>
public sealed class ObsService
{
    private ObsClient _client = new();
    private string _url;
    private string? _password;
    private bool _running;
    private int _generation;

    public bool IsConnected => _client.IsConnected;
    public string? LastError { get; private set; }
    public event Action? StateChanged;

    public ObsService(string url, string? password)
    {
        _url = url;
        _password = password;
        _client.Disconnected += () => StateChanged?.Invoke();
    }

    /// <summary>Connects, and keeps retrying every 5s in the background if OBS isn't up yet.</summary>
    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _ = RetryLoopAsync(_client, _generation);
    }

    /// <summary>Points this at a different OBS instance (e.g. switching between "this PC" and a remote stream PC) and reconnects immediately.</summary>
    public void Reconfigure(string url, string? password)
    {
        _url = url;
        _password = password;
        _generation++; // orphans the old retry loop so it stops touching the new client

        var oldClient = _client;
        _client = new ObsClient();
        _client.Disconnected += () => StateChanged?.Invoke();
        LastError = null;
        StateChanged?.Invoke();

        _ = oldClient.DisposeAsync().AsTask();
        _ = RetryLoopAsync(_client, _generation);
    }

    private async Task RetryLoopAsync(ObsClient client, int generation)
    {
        while (_running && generation == _generation)
        {
            if (!client.IsConnected)
            {
                try
                {
                    await client.ConnectAsync(_url, _password);
                    LastError = null;
                    StateChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    public async Task<RecordStatus> GetRecordStatusAsync()
    {
        JsonElement d = await _client.RequestAsync("GetRecordStatus");
        return new RecordStatus(
            d.GetProperty("outputActive").GetBoolean(),
            d.TryGetProperty("outputDuration", out JsonElement od) ? od.GetInt64() : 0);
    }

    public async Task ToggleRecordAsync()
    {
        RecordStatus status = await GetRecordStatusAsync();
        await _client.RequestAsync(status.Active ? "StopRecord" : "StartRecord");
    }

    public async Task<bool> GetReplayBufferActiveAsync()
    {
        JsonElement d = await _client.RequestAsync("GetReplayBufferStatus");
        return d.GetProperty("outputActive").GetBoolean();
    }

    public async Task<List<ReplayRow>> ListReplayRowsAsync()
    {
        JsonElement d = await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "list_rows",
            ["requestData"] = new Dictionary<string, object?>(),
        });

        var rows = new List<ReplayRow>();
        if (d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("responseData", out JsonElement rd) &&
            rd.TryGetProperty("rows", out JsonElement arr))
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                rows.Add(new ReplayRow(
                    item.GetProperty("key").GetString() ?? "",
                    item.GetProperty("label").GetString() ?? "",
                    item.TryGetProperty("hotkey", out JsonElement hk) ? hk.GetString() ?? "" : "",
                    item.TryGetProperty("status", out JsonElement st) ? st.GetInt32() : 0,
                    item.TryGetProperty("length_seconds", out JsonElement ls) ? ls.GetInt32() : 60));
            }
        }
        return rows;
    }

    public async Task SaveReplayRowAsync(string key)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "save_row",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key },
        });
    }

    /// <summary>Needs the set-row-length bridge PR merged into the plugin; older builds will just error.</summary>
    public async Task SetReplayRowLengthAsync(string key, int seconds)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_row_length",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key, ["seconds"] = seconds },
        });
    }
}
