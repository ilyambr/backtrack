using System;
using System.Collections.Generic;
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
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
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

        PlayerFreezeFrame.Effect = null;
        if (PlayerFreezeFrameDimmer != null)
            PlayerFreezeFrameDimmer.Visibility = Visibility.Collapsed;

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

            _freezeFrameTimer?.Stop();
            ReopenPlayerOverlayPopup();
        }), DispatcherPriority.Loaded);
    }

    private async void ShowPlayerFreezeFrame(FileInfo file)
    {
        PlayerFreezeFrame.Effect = null;
        if (PlayerFreezeFrameDimmer != null)
            PlayerFreezeFrameDimmer.Visibility = Visibility.Collapsed;
        await LoadThumbnailAsync(file, PlayerFreezeFrame);
        PlayerFreezeFramePopup.IsOpen = false;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayerFreezeFramePopup.IsOpen = true;
            _freezeFrameTimer?.Stop();
            _freezeFrameTimer?.Start();
            ReopenPlayerOverlayPopup();
            IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
            if (toastHwnd != IntPtr.Zero)
                WindowZOrder.BringToFrontWithoutActivating(toastHwnd);
        }), DispatcherPriority.Loaded);
    }

    private void OpenInPlayer(FileInfo file, bool keepCurrentFreezeFrame = false)
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
        if (!keepCurrentFreezeFrame)
        {
            ShowPlayerFreezeFrame(file);
        }

        StatSize.Text = $"{file.Length / 1024.0 / 1024.0:0.#} MB";
        StatDate.Text = $"{file.LastWriteTime:MMM d, yyyy h:mm tt}";
        StatResolution.Text = "";
        StatFps.Text = "";
        StatBitrate.Text = "";

        StopPlayerPlayback(keepFreezeFrame: keepCurrentFreezeFrame);

        long myToken = _clipOpenToken;
        var mediaUri = new Uri(ResolveLocalClipPath(file));
        Dispatcher.BeginInvoke(new Action(() => StartPlayerPlayback(mediaUri, myToken, hideFreezeFrameOnFirstPlay: keepCurrentFreezeFrame)), DispatcherPriority.Loaded);
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

        _currentPlayerMedia?.Dispose();
        _currentPlayerMedia = new LibVlc.Media(_libVlc, mediaUri);
        _vlcPlayer.Play(_currentPlayerMedia);

        IntPtr toastHwnd = new WindowInteropHelper(_toastOverlay).Handle;
        if (toastHwnd != IntPtr.Zero)
            WindowZOrder.BringToFrontWithoutActivating(toastHwnd);

        _isMuted = false;
        _vlcPlayer.Volume = 100;
        _vlcPlayer.Mute = false;
        PlayerVolumeSlider.Value = 100;
        UpdateVolumeIcon();

        bool tracksLoaded = false;
        bool freezeFrameHidden = false;
        _vlcPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayIcon.Visibility = Visibility.Collapsed;
            PauseIcon.Visibility = Visibility.Visible;

            if (hideFreezeFrameOnFirstPlay && !freezeFrameHidden)
            {
                freezeFrameHidden = true;
                _freezeFrameTimer?.Stop();
                _freezeFrameTimer?.Start();
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

            _seekTimer?.Stop();
            PlayerSeekFill.Width = PlayerSeekTrack.ActualWidth;
            PlayerSeekThumb.Margin = new Thickness(PlayerSeekTrack.ActualWidth - 7, 0, 0, 0);
            PlayerCurrentTime.Text = PlayerDurationText.Text;
        });

        _seekTimer?.Start();
    }

    private void TogglePlayerMenu() => PlayerMenuPopup.IsOpen = !PlayerMenuPopup.IsOpen;

    internal void PlayerMenuButton_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu();

    internal void PlayerMenuButton_Click(object sender, MouseButtonEventArgs e) => TogglePlayerMenu();
}
