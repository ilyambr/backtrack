using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public partial class ObsService
{
    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _ = RetryLoopAsync(_client, _generation);
    }

    public void Reconfigure(string url, string? password)
    {
        _url = url;
        _password = password;
        _generation++;

        var oldClient = _client;
        _client = new ObsClient();
        HookClientEvents(_client);
        LastError = null;
        StateChanged?.Invoke();

        _ = oldClient.DisposeAsync().AsTask();
        _ = RetryLoopAsync(_client, _generation);
    }

    private async Task RetryLoopAsync(ObsClient client, int generation)
    {
        int retryCount = 0;
        while (_running && generation == _generation)
        {
            if (!client.IsConnected)
            {
                try
                {
                    await client.ConnectAsync(_url, _password);
                    retryCount = 0;
                    LastError = null;
                    StateChanged?.Invoke();
                    _ = DetectMicInputAsync();
                }
                catch (ObsUnreachableException)
                {
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
            int delay = Math.Min(5 * (1 << Math.Min(retryCount, 3)), 30);
            retryCount++;
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
    }

    private static CancellationTokenSource TimeoutCts(int ms = 10000)
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(ms));
        return cts;
    }

    public async Task<RecordStatus> GetRecordStatusAsync()
    {
        using var cts = TimeoutCts();
        JsonElement d = await _client.RequestAsync("GetRecordStatus", ct: cts.Token);
        return new RecordStatus(
            d.GetProperty("outputActive").GetBoolean(),
            d.TryGetProperty("outputDuration", out JsonElement od) ? od.GetInt64() : 0,
            d.TryGetProperty("outputPaused", out JsonElement op) && op.GetBoolean());
    }

    public async Task ToggleRecordAsync()
    {
        RecordStatus status = await GetRecordStatusAsync();
        await _client.RequestAsync(status.Active ? "StopRecord" : "StartRecord");
    }

    public async Task StartMainRecordAsync() => await _client.RequestAsync("StartRecord");
    public async Task StopMainRecordAsync() => await _client.RequestAsync("StopRecord");

    public async Task<string?> GetMainRecordDirectoryAsync()
    {
        try
        {
            JsonElement d = await _client.RequestAsync("GetRecordDirectory");
            string? value = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("recordDirectory", out JsonElement dir)
                ? dir.GetString() : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public async Task SetMainRecordDirectoryAsync(string newPath) =>
        await _client.RequestAsync("SetRecordDirectory", new Dictionary<string, object?> { ["recordDirectory"] = newPath });

    public async Task<ObsStats> GetStatsAsync()
    {
        using var cts = TimeoutCts();
        JsonElement d = await _client.RequestAsync("GetStats", ct: cts.Token);
        return new ObsStats(
            d.GetProperty("renderTotalFrames").GetInt64(),
            d.GetProperty("renderSkippedFrames").GetInt64(),
            d.GetProperty("outputTotalFrames").GetInt64(),
            d.GetProperty("outputSkippedFrames").GetInt64());
    }

    public async Task<bool> GetReplayBufferActiveAsync()
    {
        using var cts = TimeoutCts();
        JsonElement d = await _client.RequestAsync("GetReplayBufferStatus", ct: cts.Token);
        return d.GetProperty("outputActive").GetBoolean();
    }

    public async Task<bool> GetStreamActiveAsync()
    {
        if (!IsConnected)
            return false;
        using var cts = TimeoutCts();
        JsonElement d = await _client.RequestAsync("GetStreamStatus", ct: cts.Token);
        return d.GetProperty("outputActive").GetBoolean();
    }

    public async Task<bool> GetVirtualCamActiveAsync()
    {
        if (!IsConnected)
            return false;
        using var cts = TimeoutCts();
        JsonElement d = await _client.RequestAsync("GetVirtualCamStatus", ct: cts.Token);
        return d.GetProperty("outputActive").GetBoolean();
    }
}
