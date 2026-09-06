using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Backtrack;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, double> _activeCompressingClips = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string, double>? ClipCompressionProgressChanged;
    public event Action<string>? ClipCompressionCompleted;

    private double _selectedCompressMb = 10.0;
    private bool _isCustomCompressSelected = false;

    private void SetClipCompressing(string clipPath, double progress)
    {
        _activeCompressingClips[clipPath] = progress;
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeCompressingClips[fileName] = progress;

        Dispatcher.BeginInvoke(() => ClipCompressionProgressChanged?.Invoke(clipPath, progress));
    }

    private void ClearClipCompressing(string clipPath)
    {
        _activeCompressingClips.Remove(clipPath);
        string fileName = Path.GetFileName(clipPath);
        if (!string.IsNullOrEmpty(fileName))
            _activeCompressingClips.Remove(fileName);

        Dispatcher.BeginInvoke(() => ClipCompressionCompleted?.Invoke(clipPath));
    }

    internal void PlayerCompress_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        CompressStatusText.Text = string.Empty;
        UpdateCompressPresetButtonsUi();
        CompressPopup.IsOpen = true;
        RepositionPlayerPopups();
    }

    internal void CloseCompressPopup_Click(object sender, RoutedEventArgs e)
    {
        CompressPopup.IsOpen = false;
    }

    private void UpdateCompressPresetButtonsUi()
    {
        var accentBrush = (Brush)FindResource("Accent");
        var regularBrush = (Brush)FindResource("RowBg");
        var text0 = (Brush)FindResource("Text0");
        var panelBg = (Brush)FindResource("PanelBg");

        Brush selectedTextBrush;
        if (accentBrush is SolidColorBrush scb)
        {
            double luminance = (0.299 * scb.Color.R + 0.587 * scb.Color.G + 0.114 * scb.Color.B) / 255.0;
            selectedTextBrush = luminance > 0.5
                ? new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x13))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        }
        else
        {
            selectedTextBrush = panelBg;
        }

        Button[] buttons = new[] { CompressPreset10, CompressPreset25, CompressPreset50, CompressPreset100, CompressPreset250 };
        foreach (var btn in buttons)
        {
            if (btn is null) continue;
            bool isSel = !_isCustomCompressSelected && btn.Tag is string tag && double.TryParse(tag, out double mb) && Math.Abs(mb - _selectedCompressMb) < 0.1;
            btn.Background = isSel ? accentBrush : regularBrush;
            btn.Foreground = isSel ? selectedTextBrush : text0;
        }

        if (CompressCustomButton is not null)
        {
            CompressCustomButton.Background = _isCustomCompressSelected ? accentBrush : regularBrush;
            CompressCustomButton.Foreground = _isCustomCompressSelected ? selectedTextBrush : text0;
        }
    }

    internal void CompressPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, out double targetMb))
        {
            _selectedCompressMb = targetMb;
            _isCustomCompressSelected = false;
            CompressCustomRow.Visibility = Visibility.Collapsed;
            UpdateCompressPresetButtonsUi();
        }
    }

    internal void CompressCustomButton_Click(object sender, RoutedEventArgs e)
    {
        _isCustomCompressSelected = true;
        CompressCustomRow.Visibility = Visibility.Visible;
        UpdateCompressPresetButtonsUi();
        CompressCustomMbBox.Focus();
        CompressCustomMbBox.SelectAll();
    }

    internal void CompressReplace_Click(object sender, RoutedEventArgs e) =>
        RunCompressAction(replaceOriginal: true);

    internal void CompressSaveNew_Click(object sender, RoutedEventArgs e) =>
        RunCompressAction(replaceOriginal: false);

    private void RunCompressAction(bool replaceOriginal)
    {
        double targetMb = _selectedCompressMb;
        if (_isCustomCompressSelected)
        {
            if (!double.TryParse(CompressCustomMbBox.Text.Trim(), out targetMb) || targetMb <= 0)
            {
                CompressStatusText.Text = "Please enter a valid size in MB.";
                return;
            }
        }

        CompressPopup.IsOpen = false;

        FileInfo? localFile = _currentPlayerFile;
        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;
        Screen backTarget = _playerBackTarget;

        if (localFile is not null)
        {
            SetClipCompressing(localFile.FullName, 0.0);
        }
        else if (remoteOrigin is not null)
        {
            SetClipCompressing(remoteOrigin.Value.RelativePath, 0.0);
        }

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

        if (localFile is not null)
        {
            _ = CompressLocalClipAsync(localFile.FullName, targetMb, replaceOriginal);
        }
        else if (remoteOrigin is not null)
        {
            _ = CompressRemoteStreamingClipAsync(remoteOrigin.Value.RelativePath, targetMb, replaceOriginal);
        }
    }
}
