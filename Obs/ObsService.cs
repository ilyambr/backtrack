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

public sealed partial class ObsService
{
    private ObsClient _client = new();
    private string _url;
    private string? _password;
    private bool _running;
    private int _generation;

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
}
