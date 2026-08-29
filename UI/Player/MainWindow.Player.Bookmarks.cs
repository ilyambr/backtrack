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

        ToggleStarClip(key);
        UpdatePlayerStarUi();
    }

    private void PlayerBookmarks_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        CompressPopup.IsOpen = false;
        if (BookmarkPopup.IsOpen)
        {
            BookmarkPopup.IsOpen = false;
        }
        else
        {
            PopulateBookmarkList();
            BookmarkPopup.IsOpen = true;
        }
    }

    private void CloseBookmarkPopup_Click(object sender, RoutedEventArgs e)
    {
        BookmarkPopup.IsOpen = false;
    }

    private void RepositionPlayerPopups()
    {
        if (BookmarkPopup?.IsOpen == true)
        {
            var offset = BookmarkPopup.HorizontalOffset;
            BookmarkPopup.HorizontalOffset = offset + 0.0001;
            BookmarkPopup.HorizontalOffset = offset;
        }
        if (CompressPopup?.IsOpen == true)
        {
            var offset = CompressPopup.HorizontalOffset;
            CompressPopup.HorizontalOffset = offset + 0.0001;
            CompressPopup.HorizontalOffset = offset;
        }
    }

    private void AddBookmarkDialogButton_Click(object sender, RoutedEventArgs e)
    {
        AddPlayerBookmark();
        PopulateBookmarkList();
    }

    private void PopulateBookmarkList()
    {
        BookmarkListContainer.Children.Clear();
        if (!TryGetMarkersForCurrentClip(out var markers) || markers.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "No bookmarks added yet",
                FontSize = 11,
                Foreground = (Brush)FindResource("Text2"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 12)
            };
            BookmarkListContainer.Children.Add(emptyText);
            return;
        }

        markers = markers.OrderBy(m => m).ToList();
        for (int i = 0; i < markers.Count; i++)
        {
            double markerSec = markers[i];
            int bookmarkNum = i + 1;

            var rowBorder = new Border
            {
                Background = (Brush)FindResource("RowBg"),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(8, 6, 6, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left side: Clickable bookmark item to seek
            var jumpPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = $"Jump to {TimeSpan.FromSeconds(markerSec):mm\\:ss}"
            };
            jumpPanel.MouseLeftButtonDown += (_, _) =>
            {
                CommitSeek((long)(markerSec * 1000.0));
                ShowPlayerActionFeedback(PlayerFeedbackIcon.SeekForward, TimeSpan.FromSeconds(markerSec).ToString(@"mm\:ss"));
            };

            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M17,3H7c-1.1,0 -1.99,0.9 -1.99,2L5,21l7,-3 7,3V5c0,-1.1 -0.9,-2 -2,-2z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleText = new TextBlock
            {
                Text = $"Bookmark {bookmarkNum}",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text0"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var timeText = new TextBlock
            {
                Text = $"  ({TimeSpan.FromSeconds(markerSec):mm\\:ss})",
                FontSize = 11,
                Foreground = (Brush)FindResource("Text2"),
                VerticalAlignment = VerticalAlignment.Center
            };

            jumpPanel.Children.Add(icon);
            jumpPanel.Children.Add(titleText);
            jumpPanel.Children.Add(timeText);
            Grid.SetColumn(jumpPanel, 0);
            grid.Children.Add(jumpPanel);

            // Right side: Trash / Delete button
            var trashBtn = new Button
            {
                Style = (Style)FindResource("BareIconButton"),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Delete bookmark"
            };

            var trashIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M6,19c0,1.1 0.9,2 2,2h8c1.1,0 2,-0.9 2,-2V7H6v12zM19,4h-3.5l-1,-1h-5l-1,1H5v2h14V4z"),
                Fill = (Brush)FindResource("Text2"),
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform
            };
            trashBtn.Content = trashIcon;

            trashBtn.Click += (_, _) =>
            {
                DeletePlayerBookmark(markerSec);
            };

            Grid.SetColumn(trashBtn, 1);
            grid.Children.Add(trashBtn);

            rowBorder.Child = grid;
            BookmarkListContainer.Children.Add(rowBorder);
        }
    }

    private void DeletePlayerBookmark(double targetSec)
    {
        string? key = GetCurrentClipKey();
        if (key is null) return;
        string saveKey = _currentPlayerFile?.Name ?? key;

        if (TryGetMarkersForCurrentClip(out var markers))
        {
            markers.RemoveAll(m => Math.Abs(m - targetSec) < 0.25);
            SaveClipMarkers(saveKey, markers);
            _lastRenderedMarkerDurationMs = -1;
            RenderPlayerMarkers();
            PopulateBookmarkList();
        }
    }

    private void AddPlayerBookmark()
    {
        string? key = GetCurrentClipKey();
        if (key is null || _vlcPlayer is null) return;

        double posSec = Math.Max(0, (double)_vlcPlayer.Time / 1000.0);
        string saveKey = _currentPlayerFile?.Name ?? key;

        if (!TryGetMarkersForCurrentClip(out var markers))
        {
            markers = new List<double>();
        }

        if (!markers.Any(m => Math.Abs(m - posSec) < 0.5))
        {
            markers.Add(posSec);
            SaveClipMarkers(saveKey, markers);
        }

        _lastRenderedMarkerDurationMs = -1;
        RenderPlayerMarkers();
        TimeSpan ts = TimeSpan.FromSeconds(posSec);
        _toastOverlay.ShowBookmarkAdded($"At {ts:mm\\:ss}");

        if (BookmarkPopup.IsOpen)
        {
            PopulateBookmarkList();
        }
    }
}
