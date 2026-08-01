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
using System.Windows.Threading;
using CaptureCenter.Interop;
using CaptureCenter.Obs;
using CaptureCenter.Remote;
using Microsoft.Win32;

namespace CaptureCenter;

public partial class MainWindow : Window
{
    private enum Screen { Idle, SaveReplay, Gallery, Settings }

    private const double CompactWidth = 460;
    private const double WideWidth = 680;
    private const string RunKeyName = "CaptureCenter";

    private readonly ObsService _obs;
    private bool _serverEnabledAtStartup;
    private readonly DispatcherTimer _pollTimer;
    private readonly StatusOverlay _statusOverlay;
    private readonly AppSettings _settings;
    private readonly RemoteServer _remoteServer = new();
    private GlobalHotkey? _hotkey;

    public MainWindow(StatusOverlay statusOverlay)
    {
        InitializeComponent();
        _statusOverlay = statusOverlay;
        _settings = AppSettings.Load();

        string url;
        string? password;
        (url, password, _serverEnabledAtStartup) = ResolveObsConnection();
        _obs = new ObsService(url, password);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();

        // The window needs a real HWND immediately for RegisterHotKey and the
        // acrylic blur, but must never actually appear until the hotkey is
        // pressed -- EnsureHandle() creates it without calling Show().
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 40;
        Acrylic.TryEnableBlurBehind(hwnd, 16, 17, 19, 205);

        try
        {
            _hotkey = new GlobalHotkey(this, GlobalHotkey.Modifiers.Control | GlobalHotkey.Modifiers.Alt, (uint)'G');
            _hotkey.Pressed += () => Dispatcher.Invoke(ToggleVisible);
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Hotkey registration failed: {ex.Message}");
        }

        _obs.Start();
        _pollTimer.Start();
        _ = RefreshStatusAsync();
        ShowScreen(Screen.Idle);
        _ = RefreshGalleryCountAsync();

        if (_settings.RemoteControlEnabled)
            _remoteServer.Start();
    }

    /// <summary>
    /// Returns (url, password, serverEnabledAtStartup). Local mode reads this
    /// PC's own obs-websocket config so the password never needs typing;
    /// remote mode (OBS on a different, e.g. dedicated stream, PC) has no way
    /// to see that machine's config, so host/port/password all come from
    /// Settings instead, and "serverEnabledAtStartup" is just assumed true
    /// since we can't check it up front.
    /// </summary>
    private (string Url, string? Password, bool ServerEnabledAtStartup) ResolveObsConnection()
    {
        if (_settings.ObsIsRemote)
            return ($"ws://{_settings.ObsHost}:{_settings.ObsPort}", _settings.ObsRemotePassword, true);

        (bool enabled, string? password) = ObsConfigReader.ReadLocalConfig();
        return ("ws://127.0.0.1:4455", password, enabled);
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

    // ---------------------------------------------------------------- screens

    private void ShowScreen(Screen screen)
    {
        IdlePanel.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;
        SaveReplayPanel.Visibility = screen == Screen.SaveReplay ? Visibility.Visible : Visibility.Collapsed;
        GalleryPanel.Visibility = screen == Screen.Gallery ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = screen == Screen.Settings ? Visibility.Visible : Visibility.Collapsed;

        // The gear only makes sense on the idle screen -- it isn't a fourth tile,
        // so it shouldn't linger once you've navigated away from the row it sits above.
        SettingsButton.Visibility = screen == Screen.Idle ? Visibility.Visible : Visibility.Collapsed;

        Width = screen is Screen.Gallery or Screen.Settings ? WideWidth : CompactWidth;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
    }

    private void BackToIdle_Click(object sender, MouseButtonEventArgs e) => ShowScreen(Screen.Idle);

    // ------------------------------------------------------------- idle tiles

    private async Task RefreshStatusAsync()
    {
        if (!_obs.IsConnected)
        {
            ConnDot.Fill = (Brush)FindResource("Rec");
            ConnDot.ToolTip = !_serverEnabledAtStartup
                ? "OBS's WebSocket server is disabled -- enable it in OBS: Tools > WebSocket Server Settings"
                : _obs.LastError is null ? "Not connected to OBS" : $"OBS: {_obs.LastError}";
            RecordLabel.Text = "Start Recording";
            RecordStatusText.Text = "OBS offline";
            RecordDot.Fill = (Brush)FindResource("Text1");
            ReplayStatus.Text = " ";
            _statusOverlay.SetRecording(false);
            _statusOverlay.SetReplayOnline(false);
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
            _statusOverlay.SetRecording(recStatus.Active);

            bool replayActive = await _obs.GetReplayBufferActiveAsync();
            ReplayStatus.Text = replayActive ? "On" : "Off";
            ReplayStatus.Foreground = (Brush)FindResource(replayActive ? "Green" : "Text2");
            _statusOverlay.SetReplayOnline(replayActive);
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
        ShowScreen(Screen.SaveReplay);
        _ = LoadReplayRowsAsync();
    }

    private void GalleryTile_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Gallery);
        LoadGallery();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowScreen(Screen.Settings);
        LoadSettingsUi();
    }

    // ------------------------------------------------------------ save replay

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

        // Online (armed) buffers first -- everything else keeps its original order after them.
        foreach (ReplayRow row in rows.OrderBy(r => r.Status == 1 ? 0 : 1))
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

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(hkPanel, 1);
        headerGrid.Children.Add(name);
        headerGrid.Children.Add(hkPanel);

        var slider = new Slider { Style = (Style)FindResource("RowLengthSlider"), Value = row.LengthSeconds };
        var lengthText = new TextBlock
        {
            Text = FormatDuration(row.LengthSeconds * 1000L),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Accent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 6, 0, 0),
            MinWidth = 34,
        };
        slider.ValueChanged += (_, e) => lengthText.Text = FormatDuration((long)e.NewValue * 1000L);
        slider.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            try
            {
                await _obs.SetReplayRowLengthAsync(row.Key, (int)slider.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not set clip length: {ex.Message}\n\n(Needs the set-row-length bridge update in obs-replay-slider.)", "Capture Center");
            }
        };

        var sliderRow = new Grid();
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition());
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(lengthText, 1);
        sliderRow.Children.Add(slider);
        sliderRow.Children.Add(lengthText);

        var content = new StackPanel();
        content.Children.Add(headerGrid);
        content.Children.Add(sliderRow);

        string styleKey = row.Status == 1 ? "BufRowButton" : "BufRowButtonNoHover";
        var button = new Button { Style = (Style)FindResource(styleKey), Content = content, Tag = row.Key };
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

    private void AddInfoLine(Panel container, string text)
    {
        container.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text2"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4),
        });
    }

    // ---------------------------------------------------------------- gallery

    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".flv", ".mov" };

    private async Task RefreshGalleryCountAsync()
    {
        int count = await Task.Run(CountClips);
        GalleryStatus.Text = count == 1 ? "1 clip" : $"{count} clips";
    }

    private int CountClips()
    {
        try
        {
            return Directory.Exists(_settings.ClipsFolder)
                ? Directory.EnumerateFiles(_settings.ClipsFolder)
                    .Count(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void LoadGallery()
    {
        GalleryGrid.Children.Clear();

        if (!Directory.Exists(_settings.ClipsFolder))
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Folder doesn't exist yet: {_settings.ClipsFolder}\n\nSet a folder that actually has your clips in Settings.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
                Width = WideWidth - 40,
            });
            return;
        }

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(_settings.ClipsFolder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch (Exception ex)
        {
            GalleryGrid.Children.Add(new TextBlock { Text = $"Couldn't read that folder: {ex.Message}", Foreground = (Brush)FindResource("Rec"), TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (files.Count == 0)
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = "No clips in this folder yet.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            return;
        }

        foreach (FileInfo file in files)
            GalleryGrid.Children.Add(BuildClipCard(file));

        GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }

    private Border BuildClipCard(FileInfo file)
    {
        // Deterministic placeholder color per file -- there's no ffmpeg available
        // to pull a real video frame, so this is an honest stand-in, not a fake thumbnail.
        int hash = file.Name.GetHashCode();
        var thumbColor = Color.FromRgb(
            (byte)(40 + Math.Abs(hash) % 60),
            (byte)(40 + Math.Abs(hash / 7) % 60),
            (byte)(50 + Math.Abs(hash / 13) % 70));

        var thumb = new Border
        {
            Background = new SolidColorBrush(thumbColor),
            Height = 84,
        };
        thumb.Child = new TextBlock
        {
            Text = "▶",
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
        };

        DateTime modified = file.LastWriteTime;
        string subText = modified.Date == DateTime.Today
            ? modified.ToString("h:mm tt")
            : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock { Text = subText, FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        var playBtn = new Button { Content = "Play", Style = (Style)FindResource("IconButton") };
        playBtn.Click += (_, _) => Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true });

        var folderBtn = new Button { Content = "Folder", Style = (Style)FindResource("IconButton") };
        folderBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullName}\"") { UseShellExecute = true });

        var renameBtn = new Button { Content = "Rename", Style = (Style)FindResource("IconButton") };
        var deleteBtn = new Button { Content = "Delete", Style = (Style)FindResource("IconButton") };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(playBtn);
        actions.Children.Add(renameBtn);
        actions.Children.Add(folderBtn);
        actions.Children.Add(deleteBtn);

        // Only worth showing when the clip isn't already local -- this is the
        // "bring it from the stream PC to this one" action.
        if (IsNetworkPath(_settings.ClipsFolder))
        {
            var copyBtn = new Button { Content = "Copy here", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0) };
            copyBtn.Click += async (_, _) => await CopyToThisPcAsync(file, copyBtn);
            actions.Children.Add(copyBtn);
        }
        else
        {
            deleteBtn.Margin = new Thickness(0);
        }

        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);
        content.Children.Add(actions);

        var card = new Border { Width = 190, Margin = new Thickness(0, 0, 14, 14), Child = content };

        renameBtn.Click += (_, _) => BeginRename(card, title, file);
        deleteBtn.Click += (_, _) => DeleteClip(file);

        return card;
    }

    private void BeginRename(Border card, TextBlock title, FileInfo file)
    {
        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
        };

        var stack = (StackPanel)card.Child;
        int index = stack.Children.IndexOf(title);
        stack.Children.RemoveAt(index);
        stack.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitRename();
            else if (e.Key == Key.Escape) LoadGallery();
        };
        box.LostFocus += (_, _) => CommitRename();

        void CommitRename()
        {
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                try
                {
                    string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                    File.Move(file.FullName, newPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Capture Center");
                }
            }
            LoadGallery();
        }
    }

    private static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private async Task CopyToThisPcAsync(FileInfo file, Button triggerButton)
    {
        triggerButton.IsEnabled = false;
        string originalText = (string)triggerButton.Content;
        triggerButton.Content = "Copying...";
        try
        {
            Directory.CreateDirectory(_settings.LocalCopyFolder);
            string dest = Path.Combine(_settings.LocalCopyFolder, file.Name);
            await Task.Run(() => File.Copy(file.FullName, dest, overwrite: true));
            triggerButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't copy that clip: {ex.Message}", "Capture Center");
            triggerButton.Content = originalText;
            triggerButton.IsEnabled = true;
        }
    }

    private void DeleteClip(FileInfo file)
    {
        var result = MessageBox.Show(this, $"Send \"{file.Name}\" to the Recycle Bin?", "Capture Center", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes)
            return;

        if (RecycleBin.Delete(file.FullName))
            LoadGallery();
        else
            MessageBox.Show(this, "Couldn't delete that file.", "Capture Center");
    }

    // --------------------------------------------------------------- settings

    private void LoadSettingsUi()
    {
        LaunchWithWindowsToggle.IsChecked = IsLaunchWithWindowsEnabled();
        ClipsFolderText.Text = _settings.ClipsFolder;
        RemoteControlToggle.IsChecked = _settings.RemoteControlEnabled;
        UpdateRemoteControlUrlText();

        ObsRemoteToggle.IsChecked = _settings.ObsIsRemote;
        ObsRemoteFields.Visibility = _settings.ObsIsRemote ? Visibility.Visible : Visibility.Collapsed;
        ObsHostBox.Text = _settings.ObsHost;
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsPasswordBox.Password = _settings.ObsRemotePassword;
    }

    private void ObsRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        ObsRemoteFields.Visibility = ObsRemoteToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyObsConnection_Click(object sender, RoutedEventArgs e)
    {
        bool remote = ObsRemoteToggle.IsChecked == true;
        if (remote && string.IsNullOrWhiteSpace(ObsHostBox.Text))
        {
            MessageBox.Show(this, "Enter the stream PC's address first.", "Capture Center");
            return;
        }

        _settings.ObsIsRemote = remote;
        _settings.ObsHost = ObsHostBox.Text.Trim();
        _settings.ObsPort = int.TryParse(ObsPortBox.Text.Trim(), out int p) ? p : 4455;
        _settings.ObsRemotePassword = ObsPasswordBox.Password;
        _settings.Save();

        (string url, string? password, _serverEnabledAtStartup) = ResolveObsConnection();
        _obs.Reconfigure(url, password);
        _ = RefreshStatusAsync();
    }

    private void UpdateRemoteControlUrlText()
    {
        if (_remoteServer.IsRunning)
        {
            RemoteControlUrlText.Text = $"Open {_remoteServer.LocalUrl} on your phone (same WiFi/LAN).";
            RemoteControlUrlText.Visibility = Visibility.Visible;
        }
        else
        {
            RemoteControlUrlText.Visibility = Visibility.Collapsed;
        }
    }

    private void RemoteControlToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = RemoteControlToggle.IsChecked == true;
        if (enabled)
        {
            if (!_remoteServer.Start())
            {
                MessageBox.Show(this,
                    "Couldn't start the remote page's local server. Try running Capture Center as Administrator once " +
                    "(needed the first time to reserve the port), or check whether something else is already using it.",
                    "Capture Center");
                RemoteControlToggle.IsChecked = false;
                return;
            }
        }
        else
        {
            _remoteServer.Stop();
        }

        _settings.RemoteControlEnabled = enabled;
        _settings.Save();
        UpdateRemoteControlUrlText();
    }

    private static string RunKeyPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private bool IsLaunchWithWindowsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunKeyName) is not null;
    }

    private void LaunchWithWindowsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = LaunchWithWindowsToggle.IsChecked == true;
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)!;
            if (enabled)
                key.SetValue(RunKeyName, Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName);
            else
                key.DeleteValue(RunKeyName, throwOnMissingValue: false);

            _settings.LaunchWithWindows = enabled;
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update the startup registry key: {ex.Message}", "Capture Center");
            LaunchWithWindowsToggle.IsChecked = !enabled;
        }
    }

    private void ChangeClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _settings.ClipsFolder };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.ClipsFolder = dialog.FolderName;
            _settings.Save();
            ClipsFolderText.Text = _settings.ClipsFolder;
            _ = RefreshGalleryCountAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkey?.Dispose();
        _remoteServer.Stop();
        base.OnClosed(e);
    }
}
