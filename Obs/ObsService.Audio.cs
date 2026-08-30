using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public partial class ObsService
{
    private string? _micInputName;
    private DateTime _micLastAudioUtc = DateTime.UtcNow;
    private bool _micMuted;
    private float _micVolumeDb;

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
}
