using System;
using System.Collections.Generic;
using System.Text.Json;
using Backtrack.Core;
using Backtrack.Pairing;
using Xunit;

namespace Backtrack.Tests;

public class BufferRpcTests
{
    [Fact]
    public void BufferPreferencesSnapshot_StoresPropertiesAccurately()
    {
        var hidden = new List<string> { "SCREEN - Source Record", "MIC - Source Record" };
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SCREEN - Source Record"] = "Monitor Display",
            ["ELGATO - Source Record"] = "Game Capture"
        };

        var snapshot = new BufferPreferencesSnapshot(hidden, overrides, PreferredClipLengthSeconds: 120, ReplayBufferMinutes: 10);

        Assert.Equal(2, snapshot.HiddenBuffers.Count);
        Assert.Contains("SCREEN - Source Record", snapshot.HiddenBuffers);
        Assert.Equal("Monitor Display", snapshot.NameOverrides["SCREEN - Source Record"]);
        Assert.Equal("Game Capture", snapshot.NameOverrides["ELGATO - Source Record"]);
        Assert.Equal(120, snapshot.PreferredClipLengthSeconds);
        Assert.Equal(10, snapshot.ReplayBufferMinutes);
    }

    [Fact]
    public void BufferPreferences_JsonRoundtrip_PreservesData()
    {
        var requestObj = new
        {
            type = "set_buffer_preferences",
            secret = "test_secret",
            hiddenBuffers = new[] { "Buffer1", "Buffer2" },
            nameOverrides = new Dictionary<string, string>
            {
                ["Buffer1"] = "Custom Name 1",
                ["Buffer2"] = "Custom Name 2"
            },
            preferredClipLengthSeconds = 45,
            replayBufferMinutes = 5
        };

        string json = JsonSerializer.Serialize(requestObj);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("hiddenBuffers", out var hb));
        Assert.Equal(JsonValueKind.Array, hb.ValueKind);

        var hiddenList = new List<string>();
        foreach (var item in hb.EnumerateArray())
        {
            hiddenList.Add(item.GetString()!);
        }
        Assert.Equal(new[] { "Buffer1", "Buffer2" }, hiddenList);

        Assert.True(doc.RootElement.TryGetProperty("nameOverrides", out var no));
        Assert.Equal(JsonValueKind.Object, no.ValueKind);
        Assert.Equal("Custom Name 1", no.GetProperty("Buffer1").GetString());
        Assert.Equal("Custom Name 2", no.GetProperty("Buffer2").GetString());

        Assert.True(doc.RootElement.TryGetProperty("preferredClipLengthSeconds", out var cl));
        Assert.Equal(45, cl.GetInt32());

        Assert.True(doc.RootElement.TryGetProperty("replayBufferMinutes", out var rbm));
        Assert.Equal(5, rbm.GetInt32());
    }

    [Fact]
    public void LocalRowNameOverrides_CaseInsensitiveLookup()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ELGATO - Source Record"] = "Console Capture"
        };

        Assert.True(dict.TryGetValue("elgato - source record", out string? custom));
        Assert.Equal("Console Capture", custom);
    }

    [Fact]
    public void HiddenBufferLabels_CaseInsensitiveCheck()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SCREEN - Source Record"
        };

        Assert.Contains("screen - source record", set);
    }
}
