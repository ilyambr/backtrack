using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public sealed record ReplayRow(string Key, string Label, string Hotkey, int Status, int LengthSeconds, string DestDir);

/// <summary>
/// One Source Record filter tracked by obs-replay-slider's ControlPanelDock -- see ListRecordRowsAsync.
/// Status: 0 Inactive (the underlying source isn't actively capturing anything,
/// e.g. a Window Capture with no window selected), 1 Stopped (capturing fine,
/// just not recording), 2 Recording, 3 Error (was recording, the output
/// stopped with a failure). SourceName/FilterName (not Label, which is the
/// "{source} - {filter}" display string) are for looking this filter's own
/// settings up via GetRecordRowDestinationFolderAsync.
/// </summary>
public sealed record RecordRow(string Key, string Label, int Status, string SourceName, string FilterName, string Path = "");
public sealed record RecordStatus(bool Active, long DurationMs, bool Paused);

public sealed record ObsStats(long RenderTotalFrames, long RenderSkippedFrames, long OutputTotalFrames, long OutputSkippedFrames);

/// <summary>
/// One or more of these is true whenever obs-source-record's own
/// check_encoder_overload (0.4.19+) detects a real recent dropped-frame rate
/// on that specific output -- see ObsService.EncoderOverloadDetected.
/// MainStream can also mean network/bandwidth congestion, not necessarily
/// the encoder itself; the plugin can't tell those apart for a streaming
/// output, only file outputs (recording/replay buffer/this filter) reliably
/// mean encoder-side lag.
/// </summary>
public sealed record EncoderOverloadInfo(bool ThisFilter, bool MainRecording, bool MainStream, bool MainReplayBuffer, string Source, string Filter);

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

    /// <summary>Fires the moment OBS's own stream output actually starts/stops -- event-driven, same idea as RecordingStateChanged.</summary>
    public event Action<bool>? StreamingStateChanged;

    /// <summary>Fires when a Replay Slider row's buffer actually finishes saving: (rowKey, path). This is the real "yes, the clip exists now" confirmation.</summary>
    public event Action<string, string>? ReplaySaved;

    /// <summary>Fires roughly every ~2s while obs-source-record detects a real recent dropped-frame rate somewhere (this filter, main recording, main stream, or main replay buffer) -- see EncoderOverloadInfo.</summary>
    public event Action<EncoderOverloadInfo>? EncoderOverloadDetected;

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
        else if (eventType == "StreamStateChanged" && data.TryGetProperty("outputState", out JsonElement streamStateEl))
        {
            // Same double-fire-per-transition reasoning as RecordStateChanged above.
            string? state = streamStateEl.GetString();
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED")
                StreamingStateChanged?.Invoke(true);
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                StreamingStateChanged?.Invoke(false);
        }
        else if (eventType == "VendorEvent" &&
                 data.TryGetProperty("vendorName", out JsonElement overloadVn) && overloadVn.GetString() == "source-record" &&
                 data.TryGetProperty("eventType", out JsonElement overloadEt) && overloadEt.GetString() == "encoder_overload" &&
                 data.TryGetProperty("eventData", out JsonElement overloadEd))
        {
            EncoderOverloadDetected?.Invoke(new EncoderOverloadInfo(
                ThisFilter: overloadEd.TryGetProperty("this_filter", out JsonElement tf) && tf.GetBoolean(),
                MainRecording: overloadEd.TryGetProperty("main_recording", out JsonElement mr) && mr.GetBoolean(),
                MainStream: overloadEd.TryGetProperty("main_stream", out JsonElement ms) && ms.GetBoolean(),
                MainReplayBuffer: overloadEd.TryGetProperty("main_replay_buffer", out JsonElement mrb) && mrb.GetBoolean(),
                Source: overloadEd.TryGetProperty("source", out JsonElement os) ? os.GetString() ?? "" : "",
                Filter: overloadEd.TryGetProperty("filter", out JsonElement of) ? of.GetString() ?? "" : ""));
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
            d.TryGetProperty("outputDuration", out JsonElement od) ? od.GetInt64() : 0,
            // OBS legitimately freezes outputDuration while paused (paused time
            // doesn't count toward recording length) -- correct on OBS's end, but
            // Backtrack was never reading this flag at all, so a paused recording
            // just looked exactly like a broken/stuck timer with no explanation.
            d.TryGetProperty("outputPaused", out JsonElement op) && op.GetBoolean());
    }

    public async Task ToggleRecordAsync()
    {
        RecordStatus status = await GetRecordStatusAsync();
        await _client.RequestAsync(status.Active ? "StopRecord" : "StartRecord");
    }

    /// <summary>Explicit start/stop of OBS's own single global recording -- used by the
    /// "main" row on the Start Recording menu (see LoadRecordRowsAsync), where the
    /// current state is already known so a toggle would be redundant/racy.</summary>
    public async Task StartMainRecordAsync() => await _client.RequestAsync("StartRecord");
    public async Task StopMainRecordAsync() => await _client.RequestAsync("StopRecord");

    /// <summary>
    /// OBS's own single global recording output directory (Settings > Output >
    /// Recording Path) -- native obs-websocket requests, same idea as
    /// GetRecordRowDestinationFolderAsync/SetRecordRowDestinationFolderAsync
    /// for a Source Record filter's own path, just for the "Full Scene" row
    /// instead of a per-filter one. No plugin bridge involved.
    /// </summary>
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
    /// <summary>
    /// Raw counters behind OBS's own status-bar-style overload warnings --
    /// there's no request/event that hands over the literal status bar text
    /// or its timing (that's internal Qt UI logic with no public hook), but
    /// this is the same underlying data it's computed from, and it works over
    /// the same websocket connection regardless of whether OBS is local or on
    /// a remote transmitter PC (unlike, say, tailing OBS's own log file,
    /// which only a local install even has). Callers diff consecutive polls
    /// to get a recent/current rate rather than a lifetime average.
    /// </summary>
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
        JsonElement d = await _client.RequestAsync("GetStreamStatus");
        return d.GetProperty("outputActive").GetBoolean();
    }

    /// <summary>
    /// True if OBS is actually recording or streaming right now (main output,
    /// or a Source Record filter's own per-source recording) -- used to defer
    /// auto-updates rather than yank OBS out from under something genuinely
    /// being captured. Deliberately does NOT count a replay buffer just being
    /// armed (main or per-row, Status == 1) -- an armed-but-not-saving buffer
    /// isn't writing anything to disk that an update would interrupt, and
    /// buffers being armed is the normal resting state most of the time, so
    /// counting that here meant updates almost never applied automatically.
    /// </summary>
    public async Task<bool> IsRecordingOrStreamingAsync()
    {
        if (!IsConnected)
            return false;

        try
        {
            if ((await GetRecordStatusAsync()).Active || await GetStreamActiveAsync())
                return true;

            List<RecordRow> recordRows = await ListRecordRowsAsync();
            return recordRows.Any(r => r.Status == 2); // 2 == Recording, see RecordRow's own doc comment
        }
        catch
        {
            // Can't confirm nothing's active (bridge unreachable, request failed,
            // etc.) -- treat as active so a transient hiccup errs toward NOT
            // updating rather than risking an update mid-recording.
            return true;
        }
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
                    item.TryGetProperty("length_seconds", out JsonElement ls) ? ls.GetInt32() : 60,
                    item.TryGetProperty("dest_dir", out JsonElement dd) ? dd.GetString() ?? "" : ""));
            }
        }
        return rows;
    }

    /// <summary>
    /// One row per Source Record filter obs-replay-slider's ControlPanelDock is
    /// currently tracking -- distinct from ListReplayRowsAsync's rows: no "main"
    /// entry here (ControlPanelDock doesn't track OBS's own global recording),
    /// and each row is a start/stop toggle rather than a one-shot save. Needs
    /// the list_record_rows bridge PR merged into the plugin; older builds just
    /// return an empty list harmlessly.
    /// </summary>
    public async Task<List<RecordRow>> ListRecordRowsAsync()
    {
        JsonElement d = await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "list_record_rows",
            ["requestData"] = new Dictionary<string, object?>(),
        });

        var rows = new List<RecordRow>();
        if (d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("responseData", out JsonElement rd) &&
            rd.TryGetProperty("rows", out JsonElement arr))
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                rows.Add(new RecordRow(
                    item.GetProperty("key").GetString() ?? "",
                    item.GetProperty("label").GetString() ?? "",
                    item.TryGetProperty("status", out JsonElement st) ? st.GetInt32() : 0,
                    item.TryGetProperty("source", out JsonElement sn) ? sn.GetString() ?? "" : "",
                    item.TryGetProperty("filter", out JsonElement fn) ? fn.GetString() ?? "" : "",
                    item.TryGetProperty("path", out JsonElement pt) ? pt.GetString() ?? "" : ""));
            }
        }
        return rows;
    }

    /// <summary>
    /// Reads a Source Record filter's own configured output folder ("path" in
    /// its settings) via the plain obs-websocket GetSourceFilterList request --
    /// no plugin bridge involved, this is regular filter-settings data any
    /// obs-websocket client can already read. Returns null if the source/filter
    /// can't be found or has no path set (recordings stay wherever the filter's
    /// own default/relative location resolves to).
    /// </summary>
    public async Task<string?> GetRecordRowDestinationFolderAsync(string sourceName, string filterName)
    {
        try
        {
            JsonElement d = await _client.RequestAsync("GetSourceFilterList", new Dictionary<string, object?> { ["sourceName"] = sourceName });
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("filters", out JsonElement filters))
                return null;

            foreach (JsonElement filter in filters.EnumerateArray())
            {
                if (!filter.TryGetProperty("filterName", out JsonElement fn) || fn.GetString() != filterName)
                    continue;
                if (filter.TryGetProperty("filterSettings", out JsonElement settings) &&
                    settings.TryGetProperty("path", out JsonElement path) &&
                    path.ValueKind == JsonValueKind.String)
                {
                    string? value = path.GetString();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
                return null;
            }
        }
        catch
        {
            // Bridge/request unreachable -- just means the toast won't show a
            // folder this one time, not worth surfacing as an error.
        }
        return null;
    }

    public async Task SetRecordRowDestinationFolderAsync(string sourceName, string filterName, string newPath)
    {
        await _client.RequestAsync("SetSourceFilterSettings", new Dictionary<string, object?>
        {
            ["sourceName"] = sourceName,
            ["filterName"] = filterName,
            ["filterSettings"] = new Dictionary<string, object?>
            {
                ["path"] = newPath,
                ["directory"] = newPath
            },
            ["overlay"] = true
        });
    }

    public async Task StartRecordRowAsync(string key)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "start_record_row",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key },
        });
    }

    public async Task StopRecordRowAsync(string key)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "stop_record_row",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key },
        });
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

    /// <summary>
    /// Per-row override of SetReplayDestDirAsync below -- lets one specific
    /// buffer's clips land in their own subfolder instead of the shared clips
    /// folder. Empty path clears the override. Needs the set-row-dest-dir
    /// bridge PR merged into the plugin; older builds just error here harmlessly.
    /// </summary>
    public async Task SetReplayRowDestDirAsync(string key, string path)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_row_dest_dir",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key, ["path"] = path },
        });
    }

    /// <summary>
    /// Points the dock's "Move clips to:" destination folder at a path -- used to
    /// tell it to move trimmed clips off the RAM disk (see Interop/RamDisk.cs)
    /// onto persistent storage the moment trimming finishes. Needs the
    /// set-dest-dir bridge PR merged into the plugin; older builds just error
    /// here harmlessly. There's no manual fallback UI for this in the dock
    /// itself (removed) since this app is the only thing that ever sets it.
    /// </summary>
    public async Task SetReplayDestDirAsync(string path)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_dest_dir",
            ["requestData"] = new Dictionary<string, object?> { ["path"] = path },
        });
    }

    /// <summary>
    /// Pushes a new replay_duration (seconds) onto every Source Record filter
    /// the plugin is currently tracking -- this is the buffer that gets
    /// flushed to disk in full on every save, not the trimmed clip length (see
    /// SetReplayRowLengthAsync for that). Needs the set-buffer-duration bridge
    /// PR merged into the plugin; older builds just error here harmlessly.
    /// </summary>
    public async Task SetReplayBufferDurationAsync(int seconds)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_buffer_duration",
            ["requestData"] = new Dictionary<string, object?> { ["seconds"] = seconds },
        });
    }

    /// <summary>
    /// Queries all OBS sources for Source Record filters and updates any whose output path
    /// is set to the RAM disk drive back to the specified target folder via obs-websocket.
    /// </summary>
    public async Task RevertSourceRecordFilterPathsAsync(char driveLetter, string targetFolder)
    {
        if (!IsConnected)
            return;

        string ramDiskPrefix = $"{char.ToUpperInvariant(driveLetter)}:";
        try
        {
            var sourceNames = new List<string>();

            try
            {
                JsonElement inputsResponse = await _client.RequestAsync("GetInputList");
                if (inputsResponse.ValueKind == JsonValueKind.Object &&
                    inputsResponse.TryGetProperty("inputs", out JsonElement inputsArr))
                {
                    foreach (JsonElement input in inputsArr.EnumerateArray())
                    {
                        if (input.TryGetProperty("inputName", out JsonElement nameEl))
                        {
                            string name = nameEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(name))
                                sourceNames.Add(name);
                        }
                    }
                }
            }
            catch { }

            try
            {
                JsonElement scenesResponse = await _client.RequestAsync("GetSceneList");
                if (scenesResponse.ValueKind == JsonValueKind.Object &&
                    scenesResponse.TryGetProperty("scenes", out JsonElement scenesArr))
                {
                    foreach (JsonElement scene in scenesArr.EnumerateArray())
                    {
                        if (scene.TryGetProperty("sceneName", out JsonElement sNameEl))
                        {
                            string sName = sNameEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(sName) && !sourceNames.Contains(sName))
                                sourceNames.Add(sName);
                        }
                    }
                }
            }
            catch { }

            foreach (string sourceName in sourceNames)
            {
                try
                {
                    JsonElement filterListResponse = await _client.RequestAsync("GetSourceFilterList", new Dictionary<string, object?>
                    {
                        ["sourceName"] = sourceName
                    });

                    if (filterListResponse.ValueKind == JsonValueKind.Object &&
                        filterListResponse.TryGetProperty("filters", out JsonElement filtersArr))
                    {
                        foreach (JsonElement filter in filtersArr.EnumerateArray())
                        {
                            string filterKind = filter.TryGetProperty("filterKind", out JsonElement fk) ? fk.GetString() ?? "" : "";
                            string filterName = filter.TryGetProperty("filterName", out JsonElement fn) ? fn.GetString() ?? "" : "";

                            if (filterKind.Contains("source_record", StringComparison.OrdinalIgnoreCase) ||
                                filterKind.Contains("sourcerecord", StringComparison.OrdinalIgnoreCase))
                            {
                                if (filter.TryGetProperty("filterSettings", out JsonElement settings))
                                {
                                    bool needsUpdate = false;
                                    var updatedSettings = new Dictionary<string, object?>();

                                    foreach (JsonProperty prop in settings.EnumerateObject())
                                    {
                                        string val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
                                        if (val.StartsWith(ramDiskPrefix, StringComparison.OrdinalIgnoreCase))
                                        {
                                            needsUpdate = true;
                                            updatedSettings[prop.Name] = targetFolder;
                                        }
                                        else
                                        {
                                            if (prop.Value.ValueKind == JsonValueKind.String)
                                                updatedSettings[prop.Name] = prop.Value.GetString();
                                            else if (prop.Value.ValueKind == JsonValueKind.Number)
                                                updatedSettings[prop.Name] = prop.Value.GetDouble();
                                            else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                                                updatedSettings[prop.Name] = prop.Value.GetBoolean();
                                        }
                                    }

                                    if (needsUpdate)
                                    {
                                        updatedSettings["directory"] = targetFolder;
                                        updatedSettings["path"] = targetFolder;

                                        await _client.RequestAsync("SetSourceFilterSettings", new Dictionary<string, object?>
                                        {
                                            ["sourceName"] = sourceName,
                                            ["filterName"] = filterName,
                                            ["filterSettings"] = updatedSettings,
                                            ["overlay"] = true
                                        });
                                        AppLog.Write($"Reverted Source Record filter '{filterName}' path on '{sourceName}' to '{targetFolder}'");
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RevertSourceRecordFilterPathsAsync failed: {ex.Message}");
        }
    }
}
