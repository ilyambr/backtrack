using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace CaptureCenter.Obs;

public sealed record ReplayRow(string Key, string Label, string Hotkey, int Status);

public sealed record RecordStatus(bool Active, long DurationMs);

/// <summary>
/// Thin façade over <see cref="ObsClient"/>: owns the connect/retry loop and
/// exposes the handful of calls the overlay UI actually needs, including the
/// two custom requests the patched obs-replay-slider plugin exposes as an
/// obs-websocket vendor (see vendor/obs-replay-slider/src/websocket-bridge.cpp).
/// </summary>
public sealed class ObsService
{
    private readonly ObsClient _client = new();
    private readonly string _url;
    private readonly string? _password;
    private bool _running;

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
        _ = RetryLoopAsync();
    }

    private async Task RetryLoopAsync()
    {
        while (_running)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await _client.ConnectAsync(_url, _password);
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
                    item.TryGetProperty("status", out JsonElement st) ? st.GetInt32() : 0));
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
}
