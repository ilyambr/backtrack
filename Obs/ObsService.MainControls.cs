using System;
using System.Collections.Generic;
using System.Text.Json;
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
        while (_running && generation == _generation)
        {
            if (!client.IsConnected)
            {
                try
                {
                    await client.ConnectAsync(_url, _password);
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
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    public async Task<RecordStatus> GetRecordStatusAsync()
    {
        JsonElement d = await _client.RequestAsync("GetRecordStatus");
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
        JsonElement d = await _client.RequestAsync("GetStats");
        return new ObsStats(
            d.GetProperty("renderTotalFrames").GetInt64(),
            d.GetProperty("renderSkippedFrames").GetInt64(),
            d.GetProperty("outputTotalFrames").GetInt64(),
            d.GetProperty("outputSkippedFrames").GetInt64());
    }

    public async Task<bool> GetReplayBufferActiveAsync()
    {
        JsonElement d = await _client.RequestAsync("GetReplayBufferStatus");
        return d.GetProperty("outputActive").GetBoolean();
    }

    public async Task<bool> GetStreamActiveAsync()
    {
        if (!IsConnected)
            return false;
        JsonElement d = await _client.RequestAsync("GetStreamStatus");
        return d.GetProperty("outputActive").GetBoolean();
    }

    public async Task<bool> GetVirtualCamActiveAsync()
    {
        if (!IsConnected)
            return false;
        JsonElement d = await _client.RequestAsync("GetVirtualCamStatus");
        return d.GetProperty("outputActive").GetBoolean();
    }
}
