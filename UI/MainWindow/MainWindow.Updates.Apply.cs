using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
        private async Task<(bool Available, string InstalledVersion)> CheckSelfAvailabilityAsync()
    {
        Version installed = UpdateService.CurrentAppVersion;
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", "backtrack",
                name => name.Contains("win", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (release?.DownloadUrl is null)
                return (false, installed.ToString(3));

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            
            
            
            
            bool available = ShouldApplyUpdate(release, versionBumped, installedFileMissing: false,
                () => _settings.LastAppliedBacktrackReleaseAt, v => _settings.LastAppliedBacktrackReleaseAt = v,
                () => _settings.LastAppliedBacktrackDigest, v => _settings.LastAppliedBacktrackDigest = v);
            return (available, installed.ToString(3));
        }
        catch
        {
            return (false, installed.ToString(3));
        }
    }


    private bool ShouldApplyUpdate(ReleaseInfo release, bool versionBumped, bool installedFileMissing,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied,
        Func<string?> getLastDigest, Action<string?> setLastDigest)
    {
        // 1. Missing file: definitely update!
        if (installedFileMissing)
            return true;

        // 2. Version bumped (candidate > installed): definitely update!
        // A previous failed update or cached digest must NEVER block updating to a newer version.
        if (versionBumped)
            return true;

        // 3. For same-version checks (candidate <= installed), only update if GitHub release was re-published in-place with a new digest/timestamp.
        DateTimeOffset? lastApplied = getLastApplied();
        string? lastDigest = getLastDigest();

        if (lastApplied is null && lastDigest is null)
        {
            setLastApplied(release.PublishedAt);
            setLastDigest(release.Digest);
            _settings.Save();
            return false;
        }

        // Never auto-downgrade if candidate version is strictly older than installed
        if (UpdateService.IsNewer(UpdateService.CurrentAppVersion.ToString(3), release.Version))
            return false;

        bool digestKnownBothSides = release.Digest is not null && lastDigest is not null;
        if (digestKnownBothSides)
        {
            bool digestChanged = !string.Equals(release.Digest, lastDigest, StringComparison.OrdinalIgnoreCase);
            return digestChanged;
        }

        bool republishedByTimestamp = release.PublishedAt is not null && lastApplied is not null && release.PublishedAt > lastApplied;
        return republishedByTimestamp;
    }


    private void RecordUpdateApplied(ReleaseInfo release, Action<DateTimeOffset?> setLastApplied, Action<string?> setLastDigest)
    {
        setLastApplied(release.PublishedAt ?? DateTimeOffset.UtcNow);
        setLastDigest(release.Digest);
        _settings.Save();
    }


    
    
    
    
    
    
    
    private bool _obsReopenPendingFromPluginUpdates;



    private void RefreshRemotePluginStatusText()
    {
        RemotePluginStatusText.Text = string.IsNullOrEmpty(_settings.PairedPeerSecret)
            ? "Not paired with a transmitter PC yet -- pair with it first (below, in OBS section)."
            : $"Paired with {_settings.PairedPeerName}. Click \"Check & update\" to check its plugin versions.";
    }


    private readonly Dictionary<string, Border> _themeSwatches = new(StringComparer.OrdinalIgnoreCase);


    

    
    
    
    private Point? _themeSwatchesDragStart;

    private double _themeSwatchesDragStartOffset;

    
    
    
    
    
    
    private const double ThemeSwatchesDragThreshold = 4;

    private bool _themeSwatchesDragged;


    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private bool _settingsAutoscrollActive;

    private double _settingsAutoscrollStartY;


    
    
    
    
    private const double AutoscrollSensitivity = 0.06;

    private const double AutoscrollDeadZone = 4;
}
