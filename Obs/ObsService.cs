using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public sealed record ReplayRow(string Key, string Label, string Hotkey, int Status, int LengthSeconds);

public sealed record RecordStatus(bool Active, long DurationMs);

public enum MicStatus { Hidden, Silent, MutedOrQuiet }

/// <summary>
/// Thin faÃ§ade over <see cref="ObsClient"/>: owns the connect/retry loop and
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

    // Auto-detected on connect (first WASAPI mic-capture input OBS reports) --
    // no Settings UI to pick one, since "the" global mic is what's being asked for.
    private string? _micInputName;
    private DateTime _micLastAudioUtc = DateTime.UtcNow;
    private bool _micMuted;
    private float _micVolumeDb;

    public bool IsConnected => _client.IsConnected;
    public string? LastError { get; private set; }
    public event Action? StateChanged;

    /// <summary>Fires the moment OBS's own recording output actually starts/stops -- event-driven, not the 1s poll. Path is only populated on stop.</summary>
    public event Action<bool, string?>? RecordingStateChanged;

    /// <summary>Fires when a Replay Slider row's buffer actually finishes saving: (rowKey, path). This is the real "yes, the clip exists now" confirmation.</summary>
    public event Action<string, string>? ReplaySaved;

    public ObsService(string url, string? password)
    {
        _url = url;
        _password = password;
        HookClientEvents(_client);
    }

    private void HookClientEvents(ObsClient client)
    {
        client.Disconnected += () =>
        {
            // Can't verify mic state without a connection -- hide the badge
            // rather than show a stale reading from before the disconnect.
            _micInputName = null;
            StateChanged?.Invoke();
        };
        client.EventReceived += HandleEvent;
    }

    private void HandleEvent(string eventType, JsonElement data)
    {
        if (eventType == "RecordStateChanged" && data.TryGetProperty("outputState", out JsonElement stateEl))
        {
            // obs-websocket fires this twice per transition (STARTING then STARTED,
            // STOPPING then STOPPED) -- both carry the same outputActive value, so
            // reacting to outputActive directly fired the toast twice per action.
            // outputPath is only populated once the state is definitively STOPPED.
            string? state = stateEl.GetString();
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED")
                RecordingStateChanged?.Invoke(true, null);
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                RecordingStateChanged?.Invoke(false, data.TryGetProperty("outputPath", out JsonElement pathEl) ? pathEl.GetString() : null);
        }
        else if (eventType == "VendorEvent" &&
                 data.TryGetProperty("vendorName", out JsonElement vn) && vn.GetString() == "replay-buffer-slider" &&
                 data.TryGetProperty("eventType", out JsonElement et) && et.GetString() == "row_saved" &&
                 data.TryGetProperty("eventData", out JsonElement ed))
        {
            string key = ed.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? "" : "";
            string path = ed.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
            ReplaySaved?.Invoke(key, path);
        }
        else if (_micInputName is not null && eventType == "InputMuteStateChanged" &&
                 data.TryGetProperty("inputName", out JsonElement muteName) && muteName.GetString() == _micInputName &&
                 data.TryGetProperty("inputMuted", out JsonElement mutedEl))
        {
            _micMuted = mutedEl.GetBoolean();
        }
        else if (_micInputName is not null && eventType == "InputVolumeChanged" &&
                 data.TryGetProperty("inputName", out JsonElement volName) && volName.GetString() == _micInputName &&
                 data.TryGetProperty("inputVolumeDb", out JsonElement volDbEl))
        {
            _micVolumeDb = volDbEl.GetSingle();
        }
        else if (_micInputName is not null && eventType == "InputVolumeMeters" &&
                 data.TryGetProperty("inputs", out JsonElement meterInputs))
        {
            foreach (JsonElement inp in meterInputs.EnumerateArray())
            {
                if (!inp.TryGetProperty("inputName", out JsonElement nameEl) || nameEl.GetString() != _micInputName)
                    continue;
                if (inp.TryGetProperty("inputLevelsMul", out JsonElement levels) && HasSignal(levels))
                    _micLastAudioUtc = DateTime.UtcNow;
                break;
            }
        }
    }

    /// <summary>
    /// inputLevelsMul is an array of per-channel [peak, magnitude, inputPeak]
    /// linear-multiplier readings -- rather than depend on the exact index
    /// meaning, "any of these numbers above a near-silence floor" is enough to
    /// call it "there's signal right now".
    /// </summary>
    private static bool HasSignal(JsonElement levelsArray)
    {
        const double SilenceFloor = 0.0005;
        foreach (JsonElement channel in levelsArray.EnumerateArray())
        {
            foreach (JsonElement v in channel.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.Number && v.GetDouble() > SilenceFloor)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Hidden when no mic is detected or things look fine; MutedOrQuiet takes
    /// priority over Silent since a deliberately muted/low mic isn't "dead",
    /// it's configured that way -- distinct icon for that case.
    /// </summary>
    public MicStatus GetMicStatus()
    {
        if (_micInputName is null)
            return MicStatus.Hidden;
        if (_micMuted || _micVolumeDb < -40)
            return MicStatus.MutedOrQuiet;
        if (DateTime.UtcNow - _micLastAudioUtc >= TimeSpan.FromSeconds(10))
            return MicStatus.Silent;
        return MicStatus.Hidden;
    }

    private async Task DetectMicInputAsync()
    {
        try
        {
            JsonElement d = await _client.RequestAsync("GetInputList", new Dictionary<string, object?> { ["inputKind"] = "wasapi_input_capture" });
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("inputs", out JsonElement inputs) || inputs.GetArrayLength() == 0)
            {
                _micInputName = null;
                return;
            }

            string? name = inputs[0].GetProperty("inputName").GetString();
            _micInputName = name;
            _micLastAudioUtc = DateTime.UtcNow;

            JsonElement muteData = await _client.RequestAsync("GetInputMute", new Dictionary<string, object?> { ["inputName"] = name });
            _micMuted = muteData.GetProperty("inputMuted").GetBoolean();

            JsonElement volData = await _client.RequestAsync("GetInputVolume", new Dictionary<string, object?> { ["inputName"] = name });
            _micVolumeDb = volData.GetProperty("inputVolumeDb").GetSingle();
        }
        catch
        {
            // No mic input, or a request failed mid-detection -- just hide the badge.
            _micInputName = null;
        }
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
                    // Just not there yet -- expected whenever OBS is closed, not a real error.
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
