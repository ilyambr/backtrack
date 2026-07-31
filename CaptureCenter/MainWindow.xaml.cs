using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CaptureCenter.Interop;
using CaptureCenter.Obs;

namespace CaptureCenter;

public partial class MainWindow : Window
{
    private readonly ObsService _obs = new("ws://127.0.0.1:4455", password: null);
    private readonly DispatcherTimer _pollTimer;
    private GlobalHotkey? _hotkey;

    public MainWindow()
    {
        InitializeComponent();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Top-center of the primary screen, same placement as the design mockup.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 40;

        try
        {
            _hotkey = new GlobalHotkey(this, GlobalHotkey.Modifiers.Control | GlobalHotkey.Modifiers.Alt, (uint)'G');
            _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hotkey registration failed: {ex.Message}");
        }

        _obs.Start();
        _pollTimer.Start();
        _ = RefreshStatusAsync();
    }

    private void ToggleVisible()
    {
        if (IsVisible)
            Hide();
        else
        {
            Show();
            Activate();
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (!_obs.IsConnected)
        {
            ConnDot.Fill = (Brush)FindResource("Rec");
            ConnDot.ToolTip = _obs.LastError is null ? "Not connected to OBS" : $"OBS: {_obs.LastError}";
            RecordLabel.Text = "Start Recording";
            RecordStatusText.Text = "OBS offline";
            RecordDot.Fill = (Brush)FindResource("Text1");
            ReplayStatus.Text = " ";
            return;
        }

        ConnDot.Fill = (Brush)FindResource("Green");
        ConnDot.ToolTip = "Connected to OBS";

        try
        {
            RecordStatus recStatus = await _obs.GetRecordStatusAsync();
            RecordLabel.Text = recStatus.Active ? "Stop Recording" : "Start Recording";
            RecordDot.Fill = (Brush)FindResource(recStatus.Active ? "Rec" : "Text1");
            RecordStatusText.Text = recStatus.Active ? FormatDuration(recStatus.DurationMs) : " ";

            bool replayActive = await _obs.GetReplayBufferActiveAsync();
            ReplayStatus.Text = replayActive ? "On" : "Off";
            ReplayStatus.Foreground = (Brush)FindResource(replayActive ? "Green" : "Text2");
        }
        catch
        {
            // A request failing mid-poll (e.g. OBS closing right now) just means
            // we show stale values for one tick; the next Disconnected event fixes it.
        }
    }

    private static string FormatDuration(long ms)
    {
        int totalSeconds = (int)(ms / 1000);
        int m = totalSeconds / 60;
        int s = totalSeconds % 60;
        return $"{m}:{s:D2}";
    }

    private async void RecordTile_Click(object sender, RoutedEventArgs e)
    {
        if (!_obs.IsConnected)
            return;
        await _obs.ToggleRecordAsync();
        await RefreshStatusAsync();
    }

    private void SaveReplayTile_Click(object sender, RoutedEventArgs e)
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        SaveReplayPanel.Visibility = Visibility.Visible;
        _ = LoadReplayRowsAsync();
    }

    private void BackFromSaveReplay_Click(object sender, MouseButtonEventArgs e)
    {
        SaveReplayPanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Visible;
    }

    private void GalleryTile_Click(object sender, RoutedEventArgs e)
    {
        // Gallery/Player/Settings aren't built yet -- see the design mockup for
        // what they're meant to look like once the clip-library work lands.
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private async Task LoadReplayRowsAsync()
    {
        BufRowsPanel.Children.Clear();

        if (!_obs.IsConnected)
        {
            AddInfoLine("Not connected to OBS.");
            return;
        }

        List<ReplayRow> rows;
        try
        {
            rows = await _obs.ListReplayRowsAsync();
        }
        catch (Exception ex)
        {
            AddInfoLine($"Could not reach the Replay Slider bridge: {ex.Message}");
            AddInfoLine("Needs the patched obs-replay-slider build (see vendor/obs-replay-slider).");
            return;
        }

        if (rows.Count == 0)
        {
            AddInfoLine("No replay buffers found.");
            return;
        }

        foreach (ReplayRow row in rows)
            BufRowsPanel.Children.Add(BuildRowButton(row));
    }

    private Button BuildRowButton(ReplayRow row)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = (Brush)FindResource(row.Status switch { 1 => "Green", 2 => "Rec", _ => "Text2" }),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var name = new TextBlock { Text = row.Label, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = (Brush)FindResource("Text0") };
        var hotkey = new TextBlock
        {
            Text = string.IsNullOrEmpty(row.Hotkey) ? "(unbound)" : row.Hotkey,
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hkPanel = new StackPanel { Orientation = Orientation.Horizontal };
        hkPanel.Children.Add(dot);
        hkPanel.Children.Add(hotkey);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(hkPanel, 1);
        grid.Children.Add(name);
        grid.Children.Add(hkPanel);

        var button = new Button { Style = (Style)FindResource("BufRowButton"), Content = grid, Tag = row.Key };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await _obs.SaveReplayRowAsync(row.Key);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Save failed: {ex.Message}", "Capture Center");
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private void AddInfoLine(string text)
    {
        BufRowsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4),
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkey?.Dispose();
        base.OnClosed(e);
    }
}
