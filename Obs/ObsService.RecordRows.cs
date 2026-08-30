using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Backtrack.Obs;

public partial class ObsService
{
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
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
