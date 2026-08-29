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
    private void PlayerFolder_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        if (_currentPlayerFile is null)
            return;
        RevealInExplorer(_currentPlayerFile.FullName);
        ShowScreen(Screen.Gallery);
        LoadGallery();
        CloseOverlay();
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
