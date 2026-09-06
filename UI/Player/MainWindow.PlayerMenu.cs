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
using Backtrack.UI.Dialogs;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    private GoogleDrivePickerWindow? _activeDrivePicker;
    private readonly Dictionary<string, double> _activeUploadingClips = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string, double>? DriveUploadProgressChanged;
    public event Action<string>? DriveUploadCompleted;

    private void SetClipUploading(string clipPath, double progress)
    {
        _activeUploadingClips[clipPath] = progress;
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeUploadingClips[fileName] = progress;

        Dispatcher.BeginInvoke(() => DriveUploadProgressChanged?.Invoke(clipPath, progress));
    }

    private void ClearClipUploading(string clipPath)
    {
        _activeUploadingClips.Remove(clipPath);
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeUploadingClips.Remove(fileName);

        Dispatcher.BeginInvoke(() => DriveUploadCompleted?.Invoke(clipPath));
    }

    internal void PlayerDriveUpload_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;

        string? filePath = _currentPlayerFile?.FullName;
        string? displayName = _currentPlayerFile?.Name;
        string? remoteRelPath = _currentPlayerRemoteOrigin?.RelativePath;

        if (filePath is null && _currentPlayerRemoteOrigin is not null)
        {
            displayName = Path.GetFileName(_currentPlayerRemoteOrigin.Value.RelativePath);
            string cachedPath = Path.Combine(Path.GetTempPath(), "Backtrack", "RemoteCache", displayName);
            if (File.Exists(cachedPath))
            {
                filePath = cachedPath;
            }
            // If not cached locally, we'll do the upload via the Main PC's RPC — don't block here
        }

        // For purely remote clips (no local copy), we route through Main PC — no local file required
        bool isRemoteOnlyUpload = _currentPlayerRemoteOrigin is not null && string.IsNullOrEmpty(filePath);
        if (!isRemoteOnlyUpload && (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)))
        {
            MessageBox.Show(this, "The clip must be available locally to upload to Google Drive.", "Upload to Drive", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_activeDrivePicker != null && _activeDrivePicker.IsLoaded)
        {
            _activeDrivePicker.Activate();
            return;
        }

        var picker = new GoogleDrivePickerWindow
        {
            Owner = this,
            Obs = _obs,
            RemotePairing = isRemoteOnlyUpload ? _pairing : null
        };
        _activeDrivePicker = picker;

        picker.OnDestinationConfirmed = (folderId, folderDisplay) =>
        {
            string clipName = displayName ?? Path.GetFileName(filePath ?? remoteRelPath ?? "clip");

            if (!isRemoteOnlyUpload)
            {
                SetClipUploading(filePath!, 0);
            }
            if (!string.IsNullOrEmpty(remoteRelPath))
                SetClipUploading(remoteRelPath, 0);

            if (_isPlayerFullscreen)
                ExitPlayerFullscreen();

            Screen backTarget = _playerBackTarget;
            StopPlayerPlayback();
            if (backTarget == Screen.Gallery)
            {
                ShowScreen(Screen.Gallery);
                LoadGallery();
            }
            else
            {
                ShowScreen(Screen.Idle);
                RefreshRecentClipsOverlay();
                _recentClipsOverlay.Show();
            }

            if (isRemoteOnlyUpload && !string.IsNullOrEmpty(remoteRelPath))
            {
                // Fire-and-forget: instruct Main PC to upload from its own disk, stream progress back
                _ = Task.Run(async () =>
                {
                    var uploadProgress = new Progress<double>(pct =>
                    {
                        Dispatcher.Invoke(() => SetClipUploading(remoteRelPath, pct));
                    });

                    var result = await _pairing!.RemoteDriveUploadClipAsync(remoteRelPath, folderId, uploadProgress);
                    Dispatcher.Invoke(() =>
                    {
                        ClearClipUploading(remoteRelPath);

                        if (result.Success)
                        {
                            bool copied = false;
                            if (!string.IsNullOrEmpty(result.WebViewLink))
                            {
                                try { Clipboard.SetText(result.WebViewLink); copied = true; } catch { }
                            }
                            _toastOverlay.ShowDriveUploadCompleted(null, clipName, copied);
                        }
                        else
                        {
                            _toastOverlay.ShowDriveUploadFailed(null, result.Error ?? "Upload request failed.");
                        }
                    });
                });
                return;
            }

            var progress = new Progress<double>(pct =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetClipUploading(filePath!, pct / 100.0);
                    if (!string.IsNullOrEmpty(remoteRelPath))
                        SetClipUploading(remoteRelPath, pct / 100.0);
                });
            });

            _ = Task.Run(async () =>
            {
                var result = await GoogleDriveService.Instance.UploadClipAsync(filePath!, folderId, progress);
                Dispatcher.Invoke(() =>
                {
                    ClearClipUploading(filePath!);
                    if (!string.IsNullOrEmpty(remoteRelPath))
                        ClearClipUploading(remoteRelPath);

                    if (result.Success)
                    {
                        bool copied = false;
                        if (!string.IsNullOrEmpty(result.WebViewLink))
                        {
                            try
                            {
                                Clipboard.SetText(result.WebViewLink);
                                copied = true;
                            }
                            catch { }
                        }

                        _toastOverlay.ShowDriveUploadCompleted(null, clipName, copied);
                    }
                    else
                    {
                        _toastOverlay.ShowDriveUploadFailed(null, result.ErrorMessage ?? "Upload failed.");
                    }
                });
            });
        };

        picker.Closed += (_, _) =>
        {
            if (_activeDrivePicker == picker)
                _activeDrivePicker = null;
        };

        picker.Show();
    }

    internal void PlayerFolder_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        if (_currentPlayerFile is null)
            return;
        RevealInExplorer(_currentPlayerFile.FullName);
        ShowScreen(Screen.Gallery);
        LoadGallery();
        CloseOverlay();
    }

    internal void PlayerTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            PlayerRename_Click(sender, e);
    }

    internal void PlayerRename_Click(object sender, RoutedEventArgs e)
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

    internal void PlayerDelete_Click(object sender, RoutedEventArgs e)
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
                        try { File.Delete(file.FullName); } catch { }
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
