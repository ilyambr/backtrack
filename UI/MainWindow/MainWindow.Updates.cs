using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        private async Task CheckForUpdatesAsync(bool isManualTrigger = false)
    {
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var obsConnectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!_obs.IsConnected && DateTime.UtcNow < obsConnectDeadline)
            await Task.Delay(100);

        
        
        
        
        await CheckAndApplyPluginUpdateAsync("obs-replay-slider", "Replay Slider", "replay-slider.dll", ReplaySliderStatusDot, ReplaySliderVersionText,
            name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
            () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v, isManualTrigger, deferObsReopen: true);
        await CheckAndApplyPluginUpdateAsync("obs-source-record", "Source Record", "source-record.dll", SourceRecordStatusDot, SourceRecordVersionText,
            name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
            () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v, isManualTrigger, deferObsReopen: true);
        ReopenObsIfPendingFromPluginUpdates();

        
        
        
        
        
        
        
        
        
        if (!UpdateService.IsDevBuild)
            await CheckAndApplySelfUpdateAsync(isManualTrigger);
    }


        private void SetUpdateStatus(System.Windows.Shapes.Ellipse dot, TextBlock versionText, string version, bool? ok)
    {
        dot.Fill = (Brush)FindResource(ok switch { true => "Green", false => "Rec", null => "Text2" });
        versionText.Text = version;
    }


        private void ClearPendingUpdateIfMatches(string componentDisplayName)
    {
        if (_pendingUpdateName == componentDisplayName)
            SetPendingUpdate(null, null);
    }


    private async Task<PluginVersionInfo> CheckAndApplyPluginUpdateAsync(string repo, string displayName, string dllFileName, System.Windows.Shapes.Ellipse dot, TextBlock versionText, Func<string, bool> assetPredicate,
        Func<DateTimeOffset?> getLastApplied, Action<DateTimeOffset?> setLastApplied, Func<string?> getLastDigest, Action<string?> setLastDigest, bool isManualTrigger = false, bool deferObsReopen = false)
    {
        
        
        
        
        if (!_updates.IsObsInstalled)
        {
            SetUpdateStatus(dot, versionText, "OBS not installed", ok: null);
            return new PluginVersionInfo("OBS not installed", null);
        }

        Version installed = _updates.GetInstalledPluginVersion(dllFileName);
        try
        {
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", repo, assetPredicate);
            if (release?.DownloadUrl is null)
            {
                SetUpdateStatus(dot, versionText, installed.ToString(3), ok: false);
                return new PluginVersionInfo(installed.ToString(3), false);
            }

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            if (!ShouldApplyUpdate(release, versionBumped, installed == UpdateService.MissingPluginVersion, getLastApplied, setLastApplied, getLastDigest, setLastDigest))
            {
                SetUpdateStatus(dot, versionText, installed.ToString(3), ok: true);
                ClearPendingUpdateIfMatches(displayName);
                return new PluginVersionInfo(installed.ToString(3), true);
            }

            async Task ApplyAsync()
            {
                
                
                
                
                
                
                
                
                
                if (await _obs.GetStreamActiveAsync())
                {
                    MessageBox.Show(this, $"You're currently livestreaming. End your stream before updating {displayName}.", "Backtrack");
                    SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (blocked -- you're livestreaming)", ok: null);
                    return;
                }
                _toastOverlay.ShowUpdateInProgress(displayName);
                (bool obsWasRunning, bool installSuccess) = await _updates.InstallPluginUpdateAsync(release.DownloadUrl, release.Digest, reopenAfterInstall: !deferObsReopen);
                if (deferObsReopen && obsWasRunning)
                    _obsReopenPendingFromPluginUpdates = true;

                Version newInstalled = _updates.GetInstalledPluginVersion(dllFileName);
                if (!installSuccess || (newInstalled < installed && newInstalled == UpdateService.MissingPluginVersion))
                {
                    AppLog.Write($"[Updates] {displayName} installer failed or was aborted.");
                    _toastOverlay.ClearUpdateInProgress(displayName);
                    SetUpdateStatus(dot, versionText, installed.ToString(3), ok: false);
                    return;
                }

                RecordUpdateApplied(release, setLastApplied, setLastDigest);
                AppLog.Write($"{displayName} updated to {release.Version}");
                _toastOverlay.ShowUpdateApplied(displayName, release.Version);
                SetUpdateStatus(dot, versionText, release.Version, ok: true);
                ClearPendingUpdateIfMatches(displayName);
            }

            
            
            
            
            
            
            
            
            async Task ApplyAndReopenAsync()
            {
                await ApplyAsync();
                ReopenObsIfPendingFromPluginUpdates();
            }

            
            
            
            
            
            
            
            
            
            if (await _obs.GetStreamActiveAsync())
            {
                if (isManualTrigger)
                {
                    MessageBox.Show(this, $"You're currently livestreaming. End your stream before updating {displayName}.", "Backtrack");
                }
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (blocked -- you're livestreaming)", ok: null);
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            
            
            
            
            
            
            
            
            if (await _obs.IsRecordingOrStreamingAsync())
            {
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (waiting for OBS)", ok: null);
                SetPendingUpdate(displayName, () => _ = ApplyAndReopenAsync());
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            
            
            
            
            
            
            
            if (!isManualTrigger && _settings.DisablePluginAutoUpdate)
            {
                SetUpdateStatus(dot, versionText, $"{installed.ToString(3)} (update available)", ok: null);
                SetPendingUpdate(displayName, () => _ = ApplyAndReopenAsync());
                return new PluginVersionInfo(installed.ToString(3), null);
            }

            await ApplyAsync();
            return new PluginVersionInfo(release.Version, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check/apply failed for {repo}: {ex.Message}");
            AppLog.WriteError($"Update check/apply failed for {repo}", ex);
            
            
            
            
            
            _toastOverlay.ClearUpdateInProgress(displayName);
            SetUpdateStatus(dot, versionText, installed.ToString(3), ok: false);
            return new PluginVersionInfo(installed.ToString(3), false);
        }
    }


        private void ReopenObsIfPendingFromPluginUpdates()
    {
        if (!_obsReopenPendingFromPluginUpdates)
            return;
        _obsReopenPendingFromPluginUpdates = false;
        _updates.RelaunchObsIfInstalled();
    }


    private async Task CheckAndApplySelfUpdateAsync(bool isManualTrigger = false)
    {
        Version installed = UpdateService.CurrentAppVersion;
        try
        {
            
            
            
            
            
            ReleaseInfo? release = await _updates.GetLatestReleaseAsync("ilyambr", "backtrack",
                name => name.Contains("win", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (release?.DownloadUrl is null)
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, installed.ToString(3), ok: false);
                return;
            }

            bool versionBumped = UpdateService.IsNewer(release.Version, installed);
            
            
            if (!ShouldApplyUpdate(release, versionBumped, installedFileMissing: false,
                    () => _settings.LastAppliedBacktrackReleaseAt, v => _settings.LastAppliedBacktrackReleaseAt = v,
                    () => _settings.LastAppliedBacktrackDigest, v => _settings.LastAppliedBacktrackDigest = v))
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, installed.ToString(3), ok: true);
                ClearPendingUpdateIfMatches("Backtrack");
                return;
            }

            async Task ApplyAsync()
            {
                _toastOverlay.ShowUpdateInProgress("Backtrack");
                AppLog.Write($"Backtrack updating to {release.Version} (relaunching)");
                await _updates.ApplySelfUpdateAsync(release.DownloadUrl, release.Version, release.Digest);
                Application.Current.Shutdown();
            }

            
            
            
            
            
            if (await _obs.IsRecordingOrStreamingAsync())
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, $"{installed.ToString(3)} (waiting for OBS)", ok: null);
                SetPendingUpdate("Backtrack", () => _ = ApplyAsync());
                return;
            }

            
            
            
            if (!isManualTrigger && _settings.DisableBacktrackAutoUpdate)
            {
                SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, $"{installed.ToString(3)} (update available)", ok: null);
                SetPendingUpdate("Backtrack", () => _ = ApplyAsync());
                return;
            }

            await ApplyAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Self-update check/apply failed: {ex.Message}");
            AppLog.WriteError("Self-update check/apply failed", ex);
            
            
            
            _toastOverlay.ClearUpdateInProgress("Backtrack");
            SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, installed.ToString(3), ok: false);
        }
    }


        private void SetPendingUpdate(string? componentDisplayName, Action? install)
    {
        _pendingUpdateName = componentDisplayName;
        _pendingUpdateInstall = install;
        RefreshUpdatePromptVisibility();
    }


    private async void CheckRemotePluginsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RefreshRemotePluginStatusText();
            return;
        }

        CheckRemotePluginsButton.IsEnabled = false;
        RemotePluginStatusText.Text = $"Checking on {_settings.PairedPeerName}...";
        try
        {
            PluginVersionsSnapshot? snapshot = await _pairing.CheckRemotePluginUpdatesAsync();
            if (snapshot is null)
            {
                RemotePluginStatusText.Text = $"Couldn't reach {_settings.PairedPeerName}'s Backtrack -- make sure it's running.";
                RemotePluginRows.Visibility = Visibility.Collapsed;
                return;
            }

            RemotePluginStatusText.Text = $"Checked on {_settings.PairedPeerName}.";
            RemotePluginRows.Visibility = Visibility.Visible;
            SetUpdateStatus(RemoteReplaySliderStatusDot, RemoteReplaySliderVersionText, snapshot.ReplaySlider.InstalledVersion, snapshot.ReplaySlider.Ok);
            SetUpdateStatus(RemoteSourceRecordStatusDot, RemoteSourceRecordVersionText, snapshot.SourceRecord.InstalledVersion, snapshot.SourceRecord.Ok);
        }
        finally
        {
            CheckRemotePluginsButton.IsEnabled = true;
        }
    }


        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try
        {
            if (_manualUpdateReady)
            {
                _manualUpdateReady = false;
                CheckUpdatesButton.Content = "Applying...";
                await CheckForUpdatesAsync(isManualTrigger: true);
                CheckUpdatesButton.Content = "Check now";
                return;
            }

            CheckUpdatesButton.Content = "Checking...";

            (bool backtrackAvail, string backtrackVer) = UpdateService.IsDevBuild
                ? (false, $"{UpdateService.CurrentAppVersion.ToString(3)} (dev build)")
                : await CheckSelfAvailabilityAsync();
            (bool replayAvail, string replayVer) = await CheckPluginAvailabilityAsync("obs-replay-slider", "replay-slider.dll",
                name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                () => _settings.LastAppliedReplaySliderReleaseAt, v => _settings.LastAppliedReplaySliderReleaseAt = v,
                () => _settings.LastAppliedReplaySliderDigest, v => _settings.LastAppliedReplaySliderDigest = v);
            (bool sourceAvail, string sourceVer) = await CheckPluginAvailabilityAsync("obs-source-record", "source-record.dll",
                name => name.Contains("windows-installer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                () => _settings.LastAppliedSourceRecordReleaseAt, v => _settings.LastAppliedSourceRecordReleaseAt = v,
                () => _settings.LastAppliedSourceRecordDigest, v => _settings.LastAppliedSourceRecordDigest = v);

            SetUpdateStatus(BacktrackStatusDot, BacktrackVersionText, backtrackAvail ? $"{backtrackVer} (update available)" : backtrackVer, ok: backtrackAvail ? null : true);
            SetUpdateStatus(ReplaySliderStatusDot, ReplaySliderVersionText, replayAvail ? $"{replayVer} (update available)" : replayVer, ok: replayAvail ? null : true);
            SetUpdateStatus(SourceRecordStatusDot, SourceRecordVersionText, sourceAvail ? $"{sourceVer} (update available)" : sourceVer, ok: sourceAvail ? null : true);

            _manualUpdateReady = backtrackAvail || replayAvail || sourceAvail;
            CheckUpdatesButton.Content = _manualUpdateReady ? "Apply" : "Check now";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

}
