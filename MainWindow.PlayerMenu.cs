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

    private void EnterPlayerFullscreen()
    {
        _isPlayerFullscreen = true;
        _preFullscreenWidth = Width;
        _preFullscreenLeft = Left;

        
        
        
        
        double transportBarHeight = PlayerTransportBar.ActualHeight;

        Rect targetBounds = TargetScreenBounds;

        
        
        
        
        
        
        double videoWidth = targetBounds.Width;
        double videoHeight = videoWidth * 9.0 / 16.0;
        if (videoHeight > targetBounds.Height)
        {
            videoHeight = targetBounds.Height;
            videoWidth = videoHeight * 16.0 / 9.0;
        }

        Width = videoWidth;
        PlayerVideoHost.Height = videoHeight;
        Left = targetBounds.X + (targetBounds.Width - Width) / 2;
        Top = targetBounds.Y + Math.Max((targetBounds.Height - videoHeight) / 2, 0);

        
        
        
        
        PlayerVideoColumnDock.Children.Remove(PlayerTransportBar);
        
        
        
        
        
        PlayerTransportBar.Background = Brushes.Transparent;
        PlayerFullscreenTransportBorder.Child = PlayerTransportBar;

        
        
        
        
        
        
        
        
        const double transportPillSideInset = 40;
        const double transportPillBottomGap = 16;
        const double transportPillVerticalPadding = 12; 
        const double transportPillHorizontalPadding = 40; 
        PlayerFullscreenTransportBorder.Width = videoWidth - transportPillSideInset;
        PlayerTransportBar.Width = PlayerFullscreenTransportBorder.Width - transportPillHorizontalPadding;
        PlayerFullscreenTransportPopup.HorizontalOffset = transportPillSideInset / 2;
        PlayerFullscreenTransportPopup.VerticalOffset =
            videoHeight - (transportBarHeight + transportPillVerticalPadding) - transportPillBottomGap;

        
        
        
        PlayerTitlePill.Margin = new Thickness(10, 10, 0, 0);
        PlayerMenuPill.Margin = new Thickness(0, 10, 30, 0);
        PlayerTitleBarHost.Height = 56;

        
        
        _scrim.SetExitButtonVisible(false);

        
        
        
        
        RootBorder.BorderThickness = new Thickness(0);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenExitIcon);
        PlayerFullscreenButton.ToolTip = "Exit fullscreen";
        ReopenPlayerOverlayPopup();
        ReopenPlayerFullscreenTransportPopup();
    }


    private void ExitPlayerFullscreen()
    {
        _isPlayerFullscreen = false;

        RootBorder.BorderThickness = new Thickness(1);

        PlayerFullscreenTransportPopup.IsOpen = false;
        PlayerFullscreenTransportBorder.Child = null;
        PlayerTransportBar.ClearValue(BackgroundProperty);
        PlayerTransportBar.ClearValue(WidthProperty);
        DockPanel.SetDock(PlayerTransportBar, Dock.Bottom);
        PlayerVideoColumnDock.Children.Insert(0, PlayerTransportBar);

        Width = _preFullscreenWidth;
        ApplyBigScreenSize(); 
        Left = _preFullscreenLeft;

        PlayerTitlePill.Margin = new Thickness(0);
        PlayerMenuPill.Margin = new Thickness(0, 0, 20, 0);
        PlayerTitleBarHost.Height = 46;
        _scrim.SetExitButtonVisible(true);

        PlayerFullscreenIcon.Data = Geometry.Parse(FullscreenEnterIcon);
        PlayerFullscreenButton.ToolTip = "Fullscreen";
        ReopenPlayerOverlayPopup();
    }


        private void ReopenPlayerOverlayPopup()
    {
        PlayerOverlayPopup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayerOverlayPopup.IsOpen = true;
            IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
            if (toastHwnd != IntPtr.Zero)
                WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
        }), DispatcherPriority.Loaded);
    }


        private void OpenRemoteClipStreaming(string relativePath, RemoteGalleryFile file)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerDeviceId))
            return;

        
        
        
        
        
        
        
        
        
        long myToken = ++_clipOpenToken;
        string? thumbnailCachePath = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
        ShowPlayerLoadingUi(file.Name, thumbnailCachePath);
        
        
        
        
        
        
        
        _currentPlayerRemoteOrigin = (relativePath, _settings.PairedPeerDeviceId);

        
        
        
        _remoteStreamTotalBytes = file.Size;
        StatSize.Text = file.Size > 0 ? $"{file.Size / 1024.0 / 1024.0:0.#} MB" : "";
        StatDate.Text = $"{file.Modified.ToLocalTime():MMM d, yyyy h:mm tt}";
        StatBitrate.Text = "";

        string streamUrl = _remoteStreamServer.PrepareStream(relativePath);
        _currentStreamToken = streamUrl[(streamUrl.LastIndexOf('/') + 1)..];
        var mediaUri = new Uri(streamUrl);
        Dispatcher.BeginInvoke(new Action(() => StartPlayerPlayback(mediaUri, myToken, hideFreezeFrameOnFirstPlay: true)), DispatcherPriority.Loaded);
    }


        private void ShowPlayerLoadingUi(string title, string? thumbnailCachePath)
    {
        _currentPlayerFile = null;
        _trimStart = null;
        _trimEnd = null;
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
        ResetPlaybackSpeed();
        PlayerVideoView.Visibility = Visibility.Visible;

        ShowScreen(Screen.Player);
        PlayerTitle.Text = title;
        UpdatePlayerStarUi();
        RenderPlayerMarkers();
        ReopenPlayerOverlayPopup();

        StatSize.Text = "";
        StatDate.Text = "";
        StatResolution.Text = "";
        StatFps.Text = "";
        StatBitrate.Text = "";

        StopPlayerPlayback();

        if (thumbnailCachePath is not null && File.Exists(thumbnailCachePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(thumbnailCachePath);
                bitmap.EndInit();
                bitmap.Freeze();
                PlayerFreezeFrame.Source = bitmap;
            }
            catch
            {
                
            }
        }

        PlayerFreezeFramePopup.IsOpen = false;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayerFreezeFramePopup.IsOpen = true;
            
            
            
            
            
            
            _freezeFrameTimer.Stop();
            ReopenPlayerOverlayPopup();
        }), DispatcherPriority.Loaded);
    }


        private async void ShowPlayerFreezeFrame(FileInfo file)
    {
        await LoadThumbnailAsync(file, PlayerFreezeFrame);
        PlayerFreezeFramePopup.IsOpen = false;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayerFreezeFramePopup.IsOpen = true;
            _freezeFrameTimer.Stop();
            _freezeFrameTimer.Start();
            ReopenPlayerOverlayPopup();
            IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
            if (toastHwnd != IntPtr.Zero)
                WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
        }), DispatcherPriority.Loaded);
    }


    private void OpenInPlayer(FileInfo file)
    {
        if (_libVlc is null)
        {
            MessageBox.Show(this, "The video player failed to initialize (LibVLC).", "Backtrack");
            return;
        }

        
        
        
        
        
        
        _playerBackTarget = Screen.Gallery;

        
        
        
        
        
        
        _clipOpenToken++;
        
        
        
        
        _currentPlayerRemoteOrigin = null;

        _currentPlayerFile = file;
        _trimStart = null;
        _trimEnd = null;
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
        ResetPlaybackSpeed();

        
        
        
        
        PlayerVideoView.Visibility = Visibility.Visible;

        ShowScreen(Screen.Player);
        PlayerTitle.Text = Path.GetFileNameWithoutExtension(file.Name);
        UpdatePlayerStarUi();
        RenderPlayerMarkers();

        
        
        
        
        
        
        
        
        
        
        
        
        ReopenPlayerOverlayPopup();
        ShowPlayerFreezeFrame(file);

        StatSize.Text = $"{file.Length / 1024.0 / 1024.0:0.#} MB";
        StatDate.Text = $"{file.LastWriteTime:MMM d, yyyy h:mm tt}";
        StatResolution.Text = "";
        StatFps.Text = "";
        StatBitrate.Text = "";

        StopPlayerPlayback();

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        long myToken = _clipOpenToken;
        var mediaUri = new Uri(ResolveLocalClipPath(file));
        Dispatcher.BeginInvoke(new Action(() => StartPlayerPlayback(mediaUri, myToken)), DispatcherPriority.Loaded);
    }


        private async void StartPlayerPlayback(Uri mediaUri, long myToken, bool hideFreezeFrameOnFirstPlay = false)
    {
        
        
        
        
        if (_libVlc is null)
            return;

        
        
        
        
        if (myToken != _clipOpenToken)
            return;

        if (_pendingVlcDisposeTask is Task pending)
        {
            await pending;
            _pendingVlcDisposeTask = null;
        }

        
        
        if (myToken != _clipOpenToken)
            return;

        _vlcPlayer = new LibVlc.MediaPlayer(_libVlc);
        PlayerVideoView.MediaPlayer = _vlcPlayer;
        _playerHasEnded = false;

        using var media = new LibVlc.Media(_libVlc, mediaUri);
        _vlcPlayer.Play(media);

        IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
        if (toastHwnd != IntPtr.Zero)
            WindowZOrder.BringToFrontWithoutActivating(toastHwnd);

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        _isMuted = false;
        _vlcPlayer.Volume = 100;
        _vlcPlayer.Mute = false;
        PlayerVolumeSlider.Value = 100;
        UpdateVolumeIcon();

        
        
        
        
        
        

        bool tracksLoaded = false;
        bool volumeConfirmed = false;
        bool freezeFrameHidden = false;
        _vlcPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Collapsed;
            PauseIcon.Visibility = Visibility.Visible;

            if (hideFreezeFrameOnFirstPlay && !freezeFrameHidden)
            {
                freezeFrameHidden = true;
                _freezeFrameTimer.Stop();
                _freezeFrameTimer.Start();
            }

            if (_vlcPlayer.Media is not null)
            {
                var videoTrack = _vlcPlayer.Media.Tracks.FirstOrDefault(t => t.TrackType == LibVlc.TrackType.Video).Data.Video;
                if (videoTrack.Width > 0 && videoTrack.Height > 0)
                    StatResolution.Text = $"{videoTrack.Width} x {videoTrack.Height}";
                if (videoTrack.FrameRateDen > 0)
                    StatFps.Text = $"{(double)videoTrack.FrameRateNum / videoTrack.FrameRateDen:0.##} fps";

                long durMs = _vlcPlayer.Length;
                long fileBytes = _currentPlayerFile?.Length ?? (_remoteStreamTotalBytes > 0 ? _remoteStreamTotalBytes : 0);
                if (durMs > 0 && fileBytes > 0)
                {
                    long kbps = (long)((fileBytes * 8.0) / (durMs / 1000.0) / 1000.0);
                    StatBitrate.Text = $"{kbps:N0} kbps";
                }
            }

            
            
            
            
            
            if (!tracksLoaded)
            {
                tracksLoaded = true;
                LoadAudioTracks();
            }

            
            
            
            
            
            
            
            
            
            
            
            if (!volumeConfirmed)
            {
                volumeConfirmed = true;
                _vlcPlayer.Volume = (int)PlayerVolumeSlider.Value;
                _vlcPlayer.Mute = _isMuted;
                UpdateVolumeIcon();
            }
        });
        _vlcPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Visible;
            PauseIcon.Visibility = Visibility.Collapsed;
        });
        _vlcPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Visible;
            PauseIcon.Visibility = Visibility.Collapsed;
            _playerHasEnded = true;

            
            
            
            
            
            
            
            
            
            
            
            _seekTimer.Stop();
            PlayerSeekFill.Width = PlayerSeekTrack.ActualWidth;
            PlayerSeekThumb.Margin = new Thickness(PlayerSeekTrack.ActualWidth - 7, 0, 0, 0);
            PlayerCurrentTime.Text = PlayerDurationText.Text;
        });

        _seekTimer.Start();
    }


        private void TogglePlayerMenu() => PlayerMenuPopup.IsOpen = !PlayerMenuPopup.IsOpen;

    private void PlayerMenuButton_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu();

    private void PlayerMenuButton_Click(object sender, MouseButtonEventArgs e) => TogglePlayerMenu();

    private string? GetCurrentClipKey()
    {
        if (_currentPlayerFile is not null)
            return _currentPlayerFile.Name;
        if (_currentPlayerRemoteOrigin is not null)
            return _currentPlayerRemoteOrigin.Value.RelativePath;
        return null;
    }

    private void UpdatePlayerStarUi()
    {
        string? key = GetCurrentClipKey();
        bool isStarred = key is not null && (_settings.StarredClips.Contains(key) || (_currentPlayerFile is not null && _settings.StarredClips.Contains(_currentPlayerFile.FullName)));
        PlayerStarGlyph.Text = isStarred ? "★" : "☆";
        PlayerStarGlyph.Foreground = isStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1");
    }

    private void PlayerStarButton_Click(object sender, RoutedEventArgs e)
    {
        string? key = GetCurrentClipKey();
        if (key is null) return;

        if (!_settings.StarredClips.Add(key))
            _settings.StarredClips.Remove(key);
        _settings.Save();
        UpdatePlayerStarUi();
    }

    private void PlayerAddBookmark_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        AddPlayerBookmark();
    }

    private void AddPlayerBookmark()
    {
        string? key = GetCurrentClipKey();
        if (key is null || _vlcPlayer is null) return;

        double posSec = Math.Max(0, (double)_vlcPlayer.Time / 1000.0);
        if (!_settings.ClipMarkers.TryGetValue(key, out var markers))
        {
            markers = new List<double>();
            _settings.ClipMarkers[key] = markers;
        }

        if (!markers.Any(m => Math.Abs(m - posSec) < 1.0))
        {
            markers.Add(posSec);
            markers.Sort();
            _settings.Save();
        }

        RenderPlayerMarkers();
        TimeSpan ts = TimeSpan.FromSeconds(posSec);
        _toastOverlay.ShowBookmarkAdded($"At {ts:mm\\:ss}");
    }


    private void PlayerFolder_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        if (_currentPlayerFile is null)
            return;
        RevealInExplorer(_currentPlayerFile.FullName);
        ShowScreen(Screen.Gallery);
        LoadGallery();
        CloseOverlay(preserveScreen: true);
    }


        private void PlayerTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            PlayerRename_Click(sender, e);
    }


    private void PlayerRename_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        
        
        
        if (_currentPlayerFile is null && _currentPlayerRemoteOrigin is null)
            return;
        _isPlayerRenaming = true;
        FileInfo? file = _currentPlayerFile;
        string currentName = file?.Name ?? Path.GetFileName(_currentPlayerRemoteOrigin!.Value.RelativePath);
        bool finished = false;

        if (PlayerTitle.Parent is not Panel stack)
            return;
        int index = stack.Children.IndexOf(PlayerTitle);
        if (index < 0)
            return;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(currentName),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Brushes.White,
        };

        stack.Children.RemoveAt(index);
        stack.Children.Insert(index, box);

        _cancelPlayerRename = () => { if (!finished) { finished = true; RevertBox(); } };

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (ke.Key == Key.Escape) { ke.Handled = true; if (!finished) { finished = true; RevertBox(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void RevertBox()
        {
            _isPlayerRenaming = false;
            _cancelPlayerRename = null;
            stack.Children.Remove(box);
            stack.Children.Insert(index, PlayerTitle);
        }

        async void CommitRename()
        {
            _isPlayerRenaming = false;
            _cancelPlayerRename = null;
            string newName = box.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == Path.GetFileNameWithoutExtension(currentName))
            {
                RevertBox();
                return;
            }

            if (file is null)
            {
                
                
                
                
                
                
                
                (string relPath, string deviceId) = _currentPlayerRemoteOrigin!.Value;
                stack.Children.Remove(box);
                stack.Children.Insert(index, PlayerTitle);
                (bool success, string? error, string? newRelPath) = await _pairing.RenameRemoteClipAsync(relPath, newName);
                if (success)
                {
                    string finalRelPath = newRelPath ?? relPath;
                    _currentPlayerRemoteOrigin = (finalRelPath, deviceId);
                    PlayerTitle.Text = newName;
                    if (_currentStreamToken is not null)
                        _remoteStreamServer.UpdateSessionPath(_currentStreamToken, finalRelPath);
                }
                else
                {
                    MessageBox.Show(this, $"Couldn't rename on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                }
                return;
            }

            
            
            (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;
            try
            {
                StopPlayerPlayback();
                string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                File.Move(file.FullName, newPath);
                _currentPlayerFile = new FileInfo(newPath);
                PlayerTitle.Text = Path.GetFileNameWithoutExtension(_currentPlayerFile.Name);
                stack.Children.Remove(box);
                stack.Children.Insert(index, PlayerTitle);
                OpenInPlayer(_currentPlayerFile);

                if (remoteOrigin is (string relPath2, string deviceId2))
                {
                    
                    
                    
                    _currentPlayerRemoteOrigin = remoteOrigin;
                    (bool success, string? error, string? newRelPath) = await _pairing.RenameRemoteClipAsync(relPath2, newName);
                    if (success)
                        _currentPlayerRemoteOrigin = (newRelPath ?? relPath2, deviceId2);
                    else
                        MessageBox.Show(this, $"Renamed locally, but couldn't rename on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                }
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
            }
            RevertBox();
        }
    }


    private void PlayerDelete_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        
        
        
        
        
        
        if (_currentPlayerFile is null && _currentPlayerRemoteOrigin is null)
            return;

        FileInfo? file = _currentPlayerFile;
        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;
        string displayName = file?.Name ?? Path.GetFileName(remoteOrigin!.Value.RelativePath);

        string message = remoteOrigin is null
            ? $"Are you sure you want to delete \"{displayName}\"? This will send it to your recycle bin."
            : $"Delete \"{displayName}\"? This deletes the original clip on {_settings.PairedPeerName}'s PC (sent to its Recycle Bin there){(file is null ? "." : ", and the cached copy here.")}";

        ShowConfirmDialog(
            message,
            "Delete",
            confirmed =>
            {
                if (!confirmed)
                    return;

                _currentPlayerFile = null;
                _currentPlayerRemoteOrigin = null;
                StopPlayerPlayback();
                ShowScreen(Screen.Gallery);

                if (remoteOrigin is (string relPath, _))
                {
                    
                    
                    
                    
                    
                    
                    
                    
                    
                    if (file is not null)
                    {
                        try { File.Delete(file.FullName); } catch {  }
                    }
                    QueueRemoteDeleteWithUndo(relPath, displayName, file: null);
                }
                else
                {
                    QueueDeleteWithUndo(file!); 
                }
            });
    }

}
