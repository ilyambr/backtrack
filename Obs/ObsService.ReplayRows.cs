using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public partial class ObsService
{
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
}
