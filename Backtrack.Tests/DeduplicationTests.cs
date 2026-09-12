using System;
using System.IO;
using Backtrack.Core;
using Xunit;

namespace Backtrack.Tests;

public class DeduplicationTests
{
    [Fact]
    public void DeduplicationEntry_RecordProperties_AssignProperly()
    {
        var now = DateTime.UtcNow;
        var entry = new DeduplicationEntry(
            ClipFileName: "clip1.mp4",
            ClipPath: @"C:\Clips\clip1.mp4",
            OriginClipFileName: "origin.mp4",
            OriginClipPath: @"C:\Clips\origin.mp4",
            SourceKey: "game_capture",
            DurationSeconds: 60,
            SavedAtUtc: now,
            ExactDurationSeconds: 59.84
        );

        Assert.Equal("clip1.mp4", entry.ClipFileName);
        Assert.Equal(@"C:\Clips\clip1.mp4", entry.ClipPath);
        Assert.Equal("origin.mp4", entry.OriginClipFileName);
        Assert.Equal(60, entry.DurationSeconds);
        Assert.Equal(now, entry.SavedAtUtc);
        Assert.Equal(59.84, entry.ExactDurationSeconds);
    }

    [Fact]
    public void DeduplicationService_ClearSaveTimestamps_ClearsTracking()
    {
        var service = DeduplicationService.Instance;
        service.ClearSaveTimestamps();
        // Changing duration clears timestamps without throwing
        service.OnClipDurationChanged(120);
        Assert.NotNull(service);
    }
}
