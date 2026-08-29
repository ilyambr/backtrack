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

    private async void RecordTile_Click(object sender, RoutedEventArgs e)
    {

        try
        {

            if (!_obs.IsConnected)
            {
                ShowScreen(Screen.StartRecord);
                _ = LoadRecordRowsAsync();
                return;
            }

            RecordStatus mainStatus = await _obs.GetRecordStatusAsync();
            List<RecordRow> activeRows = (await _obs.ListRecordRowsAsync()).Where(r => r.Status == RecordStatusRecording).ToList();

            if (mainStatus.Active && activeRows.Count == 0)
            {
                await _obs.StopMainRecordAsync();
                await RefreshStatusAsync();
            }
            else if (!mainStatus.Active && activeRows.Count == 1)
            {
                RecordRow row = activeRows[0];
                await _obs.StopRecordRowAsync(row.Key);

                await RefreshStatusAsync();
            }
            else
            {
                ShowScreen(Screen.StartRecord);
                _ = LoadRecordRowsAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't toggle recording: {ex.Message}", "Backtrack");
        }
    }

    private async Task LoadRecordRowsAsync()
    {
        RecRowsPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine(RecRowsPanel, !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings."
                : "Not connected to OBS.");
            return;
        }

        try
        {
            RecordStatus mainStatus = await _obs.GetRecordStatusAsync();
            RecRowsPanel.Children.Add(BuildRecordRowButton("Full Scene", mainStatus.Active ? RecordStatusRecording : RecordStatusStopped,
                start: _obs.StartMainRecordAsync,
                stop: _obs.StopMainRecordAsync,
                cancel: async () =>
                {
                    _cancellingMainRecording = true;
                    try
                    {
                        RecordStatus s = await _obs.GetRecordStatusAsync();
                        _cancellingMainRecordingDuration = s.Active ? FormatDuration(s.DurationMs) : null;
                    }
                    catch { }
                    await _obs.StopMainRecordAsync();
                }));
        }
        catch (Exception ex)
        {
            AddInfoLine(RecRowsPanel, $"Couldn't read OBS's recording status: {ex.Message}");
        }

        List<RecordRow> rows;
        try
        {
            rows = await _obs.ListRecordRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine(RecRowsPanel, $"Could not reach the Replay Slider bridge: {ex.Message}");
            AddInfoLine(RecRowsPanel, "Needs the patched obs-replay-slider build (see vendor/obs-replay-slider).");
            return;
        }

        List<RecordRow> visibleRows = rows.Where(r => !_settings.HiddenBufferLabels.Contains(r.Label))
            .OrderBy(r => r.Status is RecordStatusStopped or RecordStatusRecording ? 0 : 1)
            .ToList();
        foreach (RecordRow row in visibleRows)
        {
            string key = row.Key;
            RecRowsPanel.Children.Add(BuildRecordRowButton(DisplayLabel(row.Label), row.Status,
                start: () => _obs.StartRecordRowAsync(key),
                stop: () => _obs.StopRecordRowAsync(key),
                cancel: async () => { _cancelledRecordRows.Add(key); await _obs.CancelRecordRowAsync(key); },
                hotkey: row.Hotkey));
        }
    }

}
