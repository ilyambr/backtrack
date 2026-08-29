using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public sealed record ReplayRow(string Key, string Label, string Hotkey, int Status, int LengthSeconds, string DestDir);

public sealed record RecordRow(string Key, string Label, int Status, string SourceName, string FilterName, string Path = "", string Hotkey = "");
public sealed record RecordStatus(bool Active, long DurationMs, bool Paused);

public sealed record ObsStats(long RenderTotalFrames, long RenderSkippedFrames, long OutputTotalFrames, long OutputSkippedFrames);

public sealed record EncoderOverloadInfo(bool ThisFilter, bool MainRecording, bool MainStream, bool MainReplayBuffer, string Source, string Filter);

public enum MicStatus { Hidden, Silent, MutedOrQuiet }

public sealed class ObsService
{
    private ObsClient _client = new();
    private string _url;
    private string? _password;
    private bool _running;
    private int _generation;

    private string? _micInputName;
    private DateTime _micLastAudioUtc = DateTime.UtcNow;
    private bool _micMuted;
    private float _micVolumeDb;

    public bool IsConnected => _client.IsConnected;
    public string? LastError { get; private set; }
    public event Action? StateChanged;

    public event Action<bool, string?>? RecordingStateChanged;

    public event Action<bool>? StreamingStateChanged;

    public event Action<bool>? VirtualCamStateChanged;

    public event Action<string, string>? ReplaySaved;

    public event Action<string>? ReplaySaving;

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
            _micInputName = null;
            StateChanged?.Invoke();
        };
        client.EventReceived += HandleEvent;
    }

    private void HandleEvent(string eventType, JsonElement data)
    {
        if (eventType == "RecordStateChanged" && data.TryGetProperty("outputState", out JsonElement stateEl))
        {
            string? state = stateEl.GetString();
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED")
                RecordingStateChanged?.Invoke(true, null);
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                RecordingStateChanged?.Invoke(false, data.TryGetProperty("outputPath", out JsonElement pathEl) ? pathEl.GetString() : null);
        }
        else if (eventType == "StreamStateChanged" && data.TryGetProperty("outputState", out JsonElement streamStateEl))
        {
            string? state = streamStateEl.GetString();
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED")
                StreamingStateChanged?.Invoke(true);
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                StreamingStateChanged?.Invoke(false);
        }
        else if (eventType == "VirtualcamStateChanged" && data.TryGetProperty("outputState", out JsonElement vcamStateEl))
        {
            string? state = vcamStateEl.GetString();
            if (state == "OBS_WEBSOCKET_OUTPUT_STARTED")
                VirtualCamStateChanged?.Invoke(true);
            else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                VirtualCamStateChanged?.Invoke(false);
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
        else if (eventType == "VendorEvent" &&
                 data.TryGetProperty("vendorName", out JsonElement savingVn) && savingVn.GetString() == "replay-buffer-slider" &&
                 data.TryGetProperty("eventType", out JsonElement savingEt) && savingEt.GetString() == "row_saving" &&
                 data.TryGetProperty("eventData", out JsonElement savingEd))
        {
            string key = savingEd.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? "" : "";
            ReplaySaving?.Invoke(key);
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
            _micInputName = null;
        }
    }

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

    public async Task<bool> IsRecordingOrStreamingAsync()
    {
        if (!IsConnected)
            return false;

        try
        {
            if ((await GetRecordStatusAsync()).Active || await GetStreamActiveAsync())
                return true;

            List<RecordRow> recordRows = await ListRecordRowsAsync();
            return recordRows.Any(r => r.Status == 2);
        }
        catch
        {
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
                    item.TryGetProperty("path", out JsonElement pt) ? pt.GetString() ?? "" : "",
                    item.TryGetProperty("hotkey", out JsonElement hk) ? hk.GetString() ?? "" : ""));
            }
        }
        return rows;
    }

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

    public async Task CancelRecordRowAsync(string key)
    {
        try
        {
            await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
            {
                ["vendorName"] = "replay-buffer-slider",
                ["requestType"] = "cancel_record_row",
                ["requestData"] = new Dictionary<string, object?> { ["key"] = key },
            });
        }
        catch
        {
            await StopRecordRowAsync(key);
        }
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

    public async Task CancelReplayRowSaveAsync(string key)
    {
        try
        {
            await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
            {
                ["vendorName"] = "replay-buffer-slider",
                ["requestType"] = "cancel_save",
                ["requestData"] = new Dictionary<string, object?> { ["key"] = key },
            });
        }
        catch { }
    }

    public async Task SetReplayRowLengthAsync(string key, int seconds)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_row_length",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key, ["seconds"] = seconds },
        });
    }

    public async Task SetReplayRowDestDirAsync(string key, string path)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_row_dest_dir",
            ["requestData"] = new Dictionary<string, object?> { ["key"] = key, ["path"] = path },
        });
    }

    public async Task SetReplayDestDirAsync(string path)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_dest_dir",
            ["requestData"] = new Dictionary<string, object?> { ["path"] = path },
        });
    }

    public async Task SetReplayBufferDurationAsync(int seconds)
    {
        await _client.RequestAsync("CallVendorRequest", new Dictionary<string, object?>
        {
            ["vendorName"] = "replay-buffer-slider",
            ["requestType"] = "set_buffer_duration",
            ["requestData"] = new Dictionary<string, object?> { ["seconds"] = seconds },
        });
    }

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
