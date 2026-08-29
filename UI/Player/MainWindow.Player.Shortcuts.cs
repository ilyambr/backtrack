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
using Backtrack.Pairing;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
        private void ShowMainWindowAndOpenInPlayer(FileInfo file)
    {
        if (DateTime.UtcNow - _lastQuickOpenUtc < TimeSpan.FromMilliseconds(400))
            return;
        _lastQuickOpenUtc = DateTime.UtcNow;

        _scrim.ArmDismissCooldown(400);

        if (!IsVisible)
            ToggleVisible();

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        Activate();

        OpenInPlayer(file);
        
        
        _playerBackTarget = Screen.Idle;
    }


    private void RevealInExplorerAndClose(string filePath)
    {
        RevealInExplorer(filePath);
        StopPlayerPlayback();
        CloseOverlay();
    }


        private void HandlePlayerKeyboardShortcut(KeyEventArgs e)
    {
        if (PlayerPanel.Visibility != Visibility.Visible || _isPlayerRenaming || _vlcPlayer is null)
            return;
        if (Keyboard.FocusedElement is TextBox)
            return;

        long currentMs = _vlcPlayer.Time;
        long lengthMs = _vlcPlayer.Length;
        long shortSeekMs = Math.Clamp((long)(lengthMs * 0.05), 1000, 15000);
        long longSeekMs = Math.Clamp((long)(lengthMs * 0.10), 2000, 30000);

        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                bool wasPlaying = _vlcPlayer.IsPlaying;
                PlayPauseButton_Click(this, e);
                ShowPlayerActionFeedback(wasPlaying ? PlayerFeedbackIcon.Pause : PlayerFeedbackIcon.Play);
                break;
            case Key.Left:
                CommitSeek(Math.Max(0, currentMs - shortSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, $"-{shortSeekMs / 1000.0:0.#}s");
                break;
            case Key.Right:
                CommitSeek(Math.Min(lengthMs, currentMs + shortSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, $"+{shortSeekMs / 1000.0:0.#}s");
                break;
            case Key.J:
                CommitSeek(Math.Max(0, currentMs - longSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, $"-{longSeekMs / 1000.0:0.#}s");
                break;
            case Key.L:
                CommitSeek(Math.Min(lengthMs, currentMs + longSeekMs));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, $"+{longSeekMs / 1000.0:0.#}s");
                break;
            case Key.Home:
                CommitSeek(0);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekBack, "Start");
                break;
            case Key.End:
                CommitSeek(Math.Max(0, lengthMs - 1));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, "End");
                break;
            case Key.M:
                TogglePlayerMute();
                break;
            case Key.Up:
                
                
                
                
                
                
                PlayerVolumeSlider.Value = Math.Min(100, PlayerVolumeSlider.Value + 5);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.Volume, $"{(int)PlayerVolumeSlider.Value}%");
                break;
            case Key.Down:
                PlayerVolumeSlider.Value = Math.Max(0, PlayerVolumeSlider.Value - 5);
                ShowPlayerActionFeedback(PlayerFeedbackIcon.Volume, $"{(int)PlayerVolumeSlider.Value}%");
                break;
            case Key.F:
                ToggleFullscreen_Click(this, e);
                break;
            case Key.OemOpenBrackets:
                JumpToPreviousMarker();
                break;
            case Key.OemCloseBrackets:
                JumpToNextMarker();
                break;
            case Key.B:
                AddPlayerBookmark();
                break;
            case Key.S:
                PlayerStarButton_Click(this, e);
                break;
            case Key.C:
                PlayerCompress_Click(this, e);
                break;
            case Key.D0 or Key.NumPad0: CommitSeek(0); break;
            case Key.D1 or Key.NumPad1: CommitSeek(lengthMs * 1 / 10); break;
            case Key.D2 or Key.NumPad2: CommitSeek(lengthMs * 2 / 10); break;
            case Key.D3 or Key.NumPad3: CommitSeek(lengthMs * 3 / 10); break;
            case Key.D4 or Key.NumPad4: CommitSeek(lengthMs * 4 / 10); break;
            case Key.D5 or Key.NumPad5: CommitSeek(lengthMs * 5 / 10); break;
            case Key.D6 or Key.NumPad6: CommitSeek(lengthMs * 6 / 10); break;
            case Key.D7 or Key.NumPad7: CommitSeek(lengthMs * 7 / 10); break;
            case Key.D8 or Key.NumPad8: CommitSeek(lengthMs * 8 / 10); break;
            case Key.D9 or Key.NumPad9: CommitSeek(lengthMs * 9 / 10); break;
            default:
                return; 
        }

        e.Handled = true;
    }


    private void BackToGallery_Click(object sender, MouseButtonEventArgs e)
    {
        
        
        
        
        
        
        
        
        _cancelPlayerRename?.Invoke();

        
        
        
        
        
        
        
        if (TrimPanel.Visibility == Visibility.Visible)
        {
            TrimCancel_Click(sender, e);
            return;
        }

        
        
        
        
        
        if (_isPlayerFullscreen)
        {
            ExitPlayerFullscreen();
            return;
        }

        
        
        
        ShowScreen(_playerBackTarget);
        if (_playerBackTarget == Screen.Gallery)
            LoadGallery();
    }
}
