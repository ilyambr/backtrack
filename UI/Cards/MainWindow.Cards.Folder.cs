using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
        private Border BuildFolderCard(string name, Action onOpen, bool leadsToNewest = false, string? targetFolderPath = null)
    {
        var iconHost = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
        };

        var folderGlyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z"),
            Fill = (Brush)FindResource("Text1"),
            Width = 46,
            Height = 38,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconHost.Child = folderGlyph;

        var title = new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
        };

        var sub = new TextBlock { Text = "Folder", FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        UIElement titleRow = leadsToNewest ? WithNewestDot(title, "Contains the newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(iconHost);
        content.Children.Add(titleRow);
        content.Children.Add(sub);

        var card = new Border { Width = 210, Child = content, Cursor = Cursors.Hand };

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) =>
        {
            if (_galleryIsRemote)
                OpenRemoteFolderFileLocation(targetFolderPath);
            else
                OpenLocalFolderFileLocation(targetFolderPath);
        };
        contextMenu.Items.Add(openFolderItem);
        card.ContextMenu = contextMenu;

        Point? folderDragStart = null;
        bool isFolderDragging = false;

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            folderDragStart = e.GetPosition(null);
            isFolderDragging = false;
        };

        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && folderDragStart.HasValue && !isFolderDragging && !string.IsNullOrEmpty(targetFolderPath))
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = folderDragStart.Value - currentPos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    isFolderDragging = true;
                    try
                    {
                        if (_galleryIsRemote)
                        {
                            var data = new DataObject();
                            data.SetData("BacktrackRemoteClips", new[] { targetFolderPath });
                            DragDrop.DoDragDrop(card, data, DragDropEffects.Move | DragDropEffects.Copy);
                        }
                        else if (Directory.Exists(targetFolderPath))
                        {
                            ShellDragHelper.DoFileDragDrop(card, new[] { targetFolderPath }, null, name);
                        }
                    }
                    finally
                    {
                        isFolderDragging = false;
                        folderDragStart = null;
                    }
                }
            }
        };

        card.PreviewMouseLeftButtonUp += (_, _) =>
        {
            folderDragStart = null;
        };

        card.MouseLeftButtonUp += (_, _) =>
        {
            if (isFolderDragging) return;
            onOpen();
        };

        if (!string.IsNullOrEmpty(targetFolderPath))
        {
            card.AllowDrop = true;
            card.DragEnter += (_, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("BacktrackRemoteClips"))
                {
                    e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
                    if (_activeHoveredFolderCard != card)
                    {
                        CancelFolderHover();
                        _activeHoveredFolderCard = card;
                        _activeHoveredFolderIconHost = iconHost;
                        iconHost.Background = (Brush)FindResource("RowHoverBg");

                        _folderHoverTimer?.Stop();
                        _folderHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        _folderHoverTimer.Tick += (s, ev) =>
                        {
                            CancelFolderHover();
                            onOpen();
                        };
                        _folderHoverTimer.Start();
                    }
                    e.Handled = true;
                }
            };
            card.DragOver += (_, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("BacktrackRemoteClips"))
                {
                    e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
                    if (iconHost.Background != (Brush)FindResource("RowHoverBg"))
                        iconHost.Background = (Brush)FindResource("RowHoverBg");
                    e.Handled = true;
                }
            };
            card.DragLeave += (_, e) =>
            {
                System.Windows.Point pos = e.GetPosition(card);
                if (pos.X < 0 || pos.Y < 0 || pos.X >= card.ActualWidth || pos.Y >= card.ActualHeight)
                {
                    if (_activeHoveredFolderCard == card)
                    {
                        CancelFolderHover();
                    }
                    else
                    {
                        iconHost.Background = (Brush)FindResource("ThumbnailBg");
                    }
                }
            };
            card.Drop += (_, e) =>
            {
                CancelFolderHover();
                if (e.Data.GetDataPresent("BacktrackRemoteClips"))
                {
                    if (e.Data.GetData("BacktrackRemoteClips") is string[] remotePaths && remotePaths.Length > 0)
                    {
                        MoveRemoteClipsToFolder(remotePaths, targetFolderPath);
                        e.Handled = true;
                    }
                }
                else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    {
                        MoveClipsToFolder(files, targetFolderPath);
                        e.Handled = true;
                    }
                }
            };
        }

        return card;
    }
}
