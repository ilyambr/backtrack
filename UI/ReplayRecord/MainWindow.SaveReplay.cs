using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void SaveReplayTile_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.SaveReplay);
        _ = LoadReplayRowsAsync();
    }


    

    private async Task LoadReplayRowsAsync()
    {
        BufRowsPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine(BufRowsPanel, !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings."
                : "Not connected to OBS.");
            return;
        }

        List<ReplayRow> rows;
        try
        {
            rows = await _obs.ListReplayRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine(BufRowsPanel, $"Could not reach the Replay Slider bridge: {ex.Message}");
            AddInfoLine(BufRowsPanel, "Needs the patched obs-replay-slider build (see vendor/obs-replay-slider).");
            return;
        }

        if (rows.Count == 0)
        {
            AddInfoLine(BufRowsPanel, "No replay buffers found.");
            return;
        }

        foreach (ReplayRow row in rows)
            _rowLabels[row.Key] = row.Label;

        
        
        
        
        List<ReplayRow> visibleRows = rows.Where(r => !_settings.HiddenBufferLabels.Contains(r.Label)).ToList();
        _lastReplayRows = visibleRows;

        if (visibleRows.Count == 0)
        {
            AddInfoLine(BufRowsPanel, "All buffers are hidden -- unhide one in Settings > Buffers.");
            return;
        }

        
        foreach (ReplayRow row in visibleRows.OrderBy(r => r.Status == 1 ? 0 : 1))
            BufRowsPanel.Children.Add(BuildRowButton(row));

        BufRowsPanel.Children.Add(BuildSharedClipLengthControl(visibleRows));
    }
}
