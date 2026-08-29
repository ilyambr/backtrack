using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
using Backtrack.Obs;
using Backtrack.Pairing;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void OpenLocalFolderFileLocation(string? folderPath)
    {
        string target = string.IsNullOrEmpty(folderPath) ? _settings.ClipsFolder : folderPath;
        if (!Directory.Exists(target))
            Directory.CreateDirectory(target);
        RevealInExplorerAndClose(target);
    }

    private bool IsWithinClipsFolder(string path, out string relative)
    {
        string clipsFolder = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (full.Equals(clipsFolder, StringComparison.OrdinalIgnoreCase))
        {
            relative = "";
            return true;
        }
        if (full.StartsWith(clipsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            relative = full[(clipsFolder.Length + 1)..];
            return true;
        }
        relative = "";
        return false;
    }

    private void UpdateGalleryPathBar()
    {
        if (_galleryIsRemote)
        {
            bool remoteAtRoot = _currentRemoteGalleryFolder is null;
            GalleryPathBar.Visibility = remoteAtRoot ? Visibility.Collapsed : Visibility.Visible;
            if (!remoteAtRoot)
                GalleryPathText.Text = _currentRemoteGalleryFolder;
            return;
        }

        bool atRoot = _currentGalleryFolder is null;
        GalleryPathBar.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
        if (atRoot)
            return;

        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(GalleryFolder).TrimEnd(Path.DirectorySeparatorChar);
        string relative = full.Length > root.Length ? full[(root.Length + 1)..] : full;
        GalleryPathText.Text = relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private void OpenGalleryFolder(string path)
    {

        GalleryFilterBox.Text = string.Empty;
        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _currentGalleryFolder = null;
        }
        else
        {
            _currentGalleryFolder = full;
        }
        LoadGallery();
    }

    private string GetParentGalleryFolder()
    {
        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(GalleryFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase) || !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        string? parent = Path.GetDirectoryName(current);
        if (parent is null || string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) || !parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return parent;
    }

    internal void GalleryUp_Click(object? sender = null, RoutedEventArgs? e = null)
    {
        GalleryFilterBox.Text = string.Empty;
        if (_galleryIsRemote)
        {
            _currentRemoteGalleryFolder = GetParentRemoteGalleryFolder();
            LoadGallery();
            return;
        }

        string parent = GetParentGalleryFolder();
        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _currentGalleryFolder = string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) ? null : parent;
        LoadGallery();
    }

    private DispatcherTimer? _backButtonHoverTimer;
    private DispatcherTimer? _folderHoverTimer;
    private Border? _activeHoveredFolderCard;
    private Border? _activeHoveredFolderIconHost;

    private void CancelFolderHover()
    {
        _folderHoverTimer?.Stop();
        _folderHoverTimer = null;
        if (_activeHoveredFolderIconHost != null)
        {
            _activeHoveredFolderIconHost.Background = (Brush)FindResource("ThumbnailBg");
            _activeHoveredFolderIconHost = null;
        }
        _activeHoveredFolderCard = null;
    }

    internal void GalleryBackButton_DragEnter(object sender, DragEventArgs e)
    {
        CancelFolderHover();
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("BacktrackRemoteClips"))
        {
            e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
            GalleryBackButtonBg.Fill = (Brush)FindResource("RowHoverBg");
            e.Handled = true;

            _backButtonHoverTimer?.Stop();
            _backButtonHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _backButtonHoverTimer.Tick += (s, ev) =>
            {
                _backButtonHoverTimer?.Stop();
                _backButtonHoverTimer = null;
                GalleryUp_Click(sender, null);
            };
            _backButtonHoverTimer.Start();
        }
    }

    internal void GalleryBackButton_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("BacktrackRemoteClips"))
        {
            e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
            if (GalleryBackButtonBg.Fill != (Brush)FindResource("RowHoverBg"))
                GalleryBackButtonBg.Fill = (Brush)FindResource("RowHoverBg");
            e.Handled = true;
        }
    }

    internal void GalleryBackButton_DragLeave(object sender, DragEventArgs e)
    {
        System.Windows.Point pos = e.GetPosition(GalleryBackButtonHost);
        if (pos.X < 0 || pos.Y < 0 || pos.X >= GalleryBackButtonHost.ActualWidth || pos.Y >= GalleryBackButtonHost.ActualHeight)
        {
            _backButtonHoverTimer?.Stop();
            _backButtonHoverTimer = null;
            GalleryBackButtonBg.Fill = (Brush)FindResource("BorderSubtle");
        }
    }

    internal void GalleryBackButton_Drop(object sender, DragEventArgs e)
    {
        _backButtonHoverTimer?.Stop();
        _backButtonHoverTimer = null;
        GalleryBackButtonBg.Fill = (Brush)FindResource("BorderSubtle");
        CancelFolderHover();

        if (e.Data.GetDataPresent("BacktrackRemoteClips"))
        {
            if (e.Data.GetData("BacktrackRemoteClips") is string[] remotePaths && remotePaths.Length > 0)
            {
                string? parentFolder = GetParentRemoteGalleryFolder();
                MoveRemoteClipsToFolder(remotePaths, parentFolder);
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                string parentFolder = GetParentGalleryFolder();
                MoveClipsToFolder(files, parentFolder);
                e.Handled = true;
            }
        }
    }

    internal void GalleryScrollHost_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("BacktrackRemoteClips"))
        {
            e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    internal void GalleryScrollHost_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("BacktrackRemoteClips"))
        {
            e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    internal void GalleryScrollHost_Drop(object sender, DragEventArgs e)
    {
        CancelFolderHover();
        if (e.Data.GetDataPresent("BacktrackRemoteClips"))
        {
            if (e.Data.GetData("BacktrackRemoteClips") is string[] remotePaths && remotePaths.Length > 0)
            {
                MoveRemoteClipsToFolder(remotePaths, _currentRemoteGalleryFolder ?? "");
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                MoveClipsToFolder(files, GalleryFolder);
                e.Handled = true;
            }
        }
    }
}
