using System.Collections.Generic;
using System.Windows;
using Backtrack.Core;
using Backtrack.Interop;
using Xunit;

namespace Backtrack.Tests;

public class DisplayResolutionTests
{
    [Fact]
    public void DisplayResolution_PreservesPreferencesAcrossDisconnection()
    {
        var settings = new AppSettings
        {
            DisplayDeviceName = @"\\.\DISPLAY2",
            DisplayDeviceId = @"\\?\DISPLAY#VSC7836#5&9bd5ad0&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            DisplayFriendlyName = "VG2248"
        };

        // When resolving settings, if device is present, it returns a valid display info
        DisplayInfo info = DisplayMonitors.Resolve(settings);
        Assert.False(string.IsNullOrEmpty(info.DeviceName));

        // Settings object properties must not be erased or corrupted during resolution
        Assert.Equal(@"\\.\DISPLAY2", settings.DisplayDeviceName);
        Assert.Equal("VG2248", settings.DisplayFriendlyName);
        Assert.Contains("VSC7836", settings.DisplayDeviceId);
    }

    [Fact]
    public void DisplayResolution_FallsBackToPrimaryWhenUnknown()
    {
        var settings = new AppSettings
        {
            DisplayDeviceName = @"\\.\NON_EXISTENT_DISPLAY_99",
            DisplayDeviceId = @"NON_EXISTENT_DEVICE_ID",
            DisplayFriendlyName = "NonExistentModel"
        };

        DisplayInfo info = DisplayMonitors.Resolve(settings);
        Assert.False(string.IsNullOrEmpty(info.DeviceName));

        // If multiple displays exist, fallback must be the primary display
        var all = DisplayMonitors.GetAll();
        if (all.Count > 0)
        {
            DisplayInfo primary = all.Find(d => d.IsPrimary);
            if (!string.IsNullOrEmpty(primary.DeviceName))
            {
                Assert.Equal(primary.DeviceName, info.DeviceName);
            }
        }
    }
}
