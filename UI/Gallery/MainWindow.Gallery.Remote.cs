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
    private void GalleryRemoteTab_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryIsRemote || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return;
        GalleryFilterBox.Text = string.Empty; 
        _galleryIsRemote = true;
        _currentRemoteGalleryFolder = null;
        RefreshGallerySourceTabsVisibility();
        LoadGallery();
    }


        private async Task<int> CountRemoteClipsRecursiveAsync(string relativePath)
    {
        RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(relativePath);
        if (listing is null)
            return 0;

        int count = listing.Files.Count;
        foreach (string folder in listing.Folders)
            count += await CountRemoteClipsRecursiveAsync($"{relativePath}/{folder}");
        return count;
    }


        private void OpenRemoteGalleryFolder(string name)
    {
        GalleryFilterBox.Text = string.Empty; 
        _currentRemoteGalleryFolder = _currentRemoteGalleryFolder is null ? name : $"{_currentRemoteGalleryFolder}/{name}";
        LoadGallery();
    }


    private string RemoteClipRelativePath(string fileName) =>
        _currentRemoteGalleryFolder is null ? fileName : $"{_currentRemoteGalleryFolder}/{fileName}";


        private async Task<List<(string RelativePath, RemoteGalleryFile File)>?> ListAllRemoteClipsAsync()
    {
        var foldersToWalk = new Queue<string?>();
        foldersToWalk.Enqueue(null); 
        var all = new List<(string RelativePath, RemoteGalleryFile File)>();

        while (foldersToWalk.Count > 0)
        {
            string? folder = foldersToWalk.Dequeue();
            RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(folder ?? "");
            if (listing is null)
                return null;

            foreach (string subfolder in listing.Folders)
                foldersToWalk.Enqueue(folder is null ? subfolder : $"{folder}/{subfolder}");

            foreach (RemoteGalleryFile file in listing.Files)
                all.Add((folder is null ? file.Name : $"{folder}/{file.Name}", file));
        }

        return all;
    }


        private async Task SyncRemoteClipsAsync(IProgress<double>? progress = null)
    {
        List<(string RelativePath, RemoteGalleryFile File)>? all = await ListAllRemoteClipsAsync();
        
        
        if (all is null)
            return;

        var toDownload = new List<(string RelativePath, RemoteGalleryFile File, string DestPath)>();
        foreach ((string relativePath, RemoteGalleryFile file) in all)
        {
            if (_pendingRemoteDeletePaths.Contains(relativePath))
                continue;

            string destPath = GetRemoteClipCachePath(relativePath, file.Name);
            if (File.Exists(destPath))
                continue;

            toDownload.Add((relativePath, file, destPath));
        }

        if (toDownload.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        for (int i = 0; i < toDownload.Count; i++)
        {
            (string relativePath, RemoteGalleryFile file, string destPath) = toDownload[i];
            int completed = i; 
            
            
            
            
            
            var fileProgress = progress is null ? null : new Progress<double>(p => progress.Report((completed + p) / toDownload.Count));

            
            
            
            
            await _pairing.DownloadRemoteClipAsync(relativePath, destPath, fileProgress);
            progress?.Report((double)(i + 1) / toDownload.Count);
        }
    }


        private async Task OpenRemoteClipFileLocationAsync(string relativePath, RemoteGalleryFile file)
    {
        string destPath = GetRemoteClipCachePath(relativePath, file.Name);
        if (!File.Exists(destPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            (bool success, string? error) = await _pairing.DownloadRemoteClipAsync(relativePath, destPath);
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't download that clip: {error}", "Backtrack");
                return;
            }
        }
        RevealInExplorerAndClose(destPath);
    }


        private async Task CopyRemoteClipPathAsync(string relativePath, RemoteGalleryFile file)
    {
        string destPath = GetRemoteClipCachePath(relativePath, file.Name);
        if (!File.Exists(destPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            (bool success, string? error) = await _pairing.DownloadRemoteClipAsync(relativePath, destPath);
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't download that clip: {error}", "Backtrack");
                return;
            }
        }
        Clipboard.SetText(destPath);
    }


        private void DeleteRemoteClip(string relativePath, RemoteGalleryFile file)
    {
        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This deletes the real clip on {_settings.PairedPeerName}'s PC (sent to its recycle bin there), not just this view.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                    QueueRemoteDeleteWithUndo(relativePath, file.Name, file);
            });
    }


    private async Task FinishRemoteDeleteAsync(string relativePath, string displayName, RemoteGalleryFile? file)
    {
        (bool success, string? error) = await _pairing.DeleteRemoteClipAsync(relativePath);
        if (!success)
        {
            
            
            
            
            
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"Couldn't delete \"{displayName}\": {error}", "Backtrack"));
        }
        else if (file is not null)
        {
            
            
            
            try { File.Delete(GetRemoteClipCachePath(relativePath, file.Name)); } catch {  }
            string? thumbCache = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
            if (thumbCache is not null)
            {
                try { File.Delete(thumbCache); } catch {  }
            }
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            else
                _ = RefreshGalleryCountAsync();
        });
    }


        private string? GetRemoteThumbnailCachePath(string relativePath, DateTime modified, long size)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerDeviceId))
            return null;

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Backtrack", "RemoteThumbnails", _settings.PairedPeerDeviceId);
        string key = $"{relativePath}|{modified.Ticks}|{size}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, $"{hash}.jpg");
    }


    private async Task LoadRemoteThumbnailAsync(string relativePath, RemoteGalleryFile file, Image target)
    {
        string? cachePath = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
        if (cachePath is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        if (!File.Exists(cachePath))
        {
            (bool success, _) = await _pairing.DownloadRemoteThumbnailAsync(relativePath, cachePath);
            if (!success)
                return;
        }

        BitmapImage? bitmap = null;
        if (File.Exists(cachePath))
        {
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(cachePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch
            {
                bitmap = null;
            }
        }

        if (bitmap is not null)
        {
            await target.Dispatcher.InvokeAsync(() => target.Source = bitmap);
        }
    }


    private void OpenRemoteFolderFileLocation(string? relativeFolderPath)
    {
        string baseCache = Path.Combine(_settings.ClipsFolder, "RemoteClips");
        string localFolder = string.IsNullOrEmpty(relativeFolderPath)
            ? baseCache
            : Path.Combine(baseCache, relativeFolderPath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(localFolder))
            Directory.CreateDirectory(localFolder);
        RevealInExplorerAndClose(localFolder);
    }

    private string? GetParentRemoteGalleryFolder()
    {
        if (string.IsNullOrEmpty(_currentRemoteGalleryFolder))
            return null;
        int lastSlash = _currentRemoteGalleryFolder.LastIndexOf('/');
        return lastSlash < 0 ? null : _currentRemoteGalleryFolder[..lastSlash];
    }

        private async Task LoadRemoteGalleryAsync()
    {
        _galleryCardSelection.Clear();
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
        UpdateGalleryPathBar();
        GalleryStatus.Text = "Loading...";

        RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(_currentRemoteGalleryFolder ?? "");
        if (listing is null)
        {
            
            
            
            if (_remotePcWasConnected)
            {
                _remotePcWasConnected = false;
                _toastOverlay.ShowRemotePcDisconnected(_settings.PairedPeerHost ?? _settings.PairedPeerName ?? "The remote PC");
            }
            GalleryGrid.Children.Clear();
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Couldn't reach {_settings.PairedPeerName}'s Backtrack -- make sure it's running and paired.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Rec"),
                TextWrapping = TextWrapping.Wrap,
                Width = BigWidth() - 40,
            });
            GalleryStatus.Text = "";
            return;
        }
        _remotePcWasConnected = true;
        _lastRemoteStorageInfo = listing.Storage;

        
        
        
        string? newestRemotePath = await _pairing.GetRemoteNewestClipPathAsync();

        
        string filter = GalleryFilterBox.Text.Trim();

        
        
        
        
        List<RemoteGalleryFile> files = listing.Files
            .Where(f => !_pendingRemoteDeletePaths.Contains(RemoteClipRelativePath(f.Name)))
            .Where(f => filter.Length == 0 || Path.GetFileNameWithoutExtension(f.Name).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_settings.GalleryStarredOnly)
        {
            files = files.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(RemoteClipRelativePath(f.Name))).ToList();
        }

        files = ApplyRemoteGallerySort(files).ToList();

        List<string> folders = listing.Folders
            .Where(name => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (folders.Count == 0 && files.Count == 0)
        {
            GalleryGrid.Children.Clear();
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = filter.Length == 0 ? "No clips in this folder yet." : $"Nothing here matches \"{filter}\".",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            GalleryStatus.Text = "0 clips";
            UpdateGalleryFooterStats(0, 0, _currentRemoteGalleryFolder);
            return;
        }

        var newCards = new List<UIElement>();
        foreach (string name in folders)
        {
            string folderRelPath = RemoteClipRelativePath(name);
            bool leadsToNewest = newestRemotePath is not null
                && (string.Equals(newestRemotePath, folderRelPath, StringComparison.OrdinalIgnoreCase)
                    || newestRemotePath.StartsWith(folderRelPath + "/", StringComparison.OrdinalIgnoreCase));
            newCards.Add(BuildFolderCard(name, () => OpenRemoteGalleryFolder(name), leadsToNewest, folderRelPath));
        }

        foreach (RemoteGalleryFile file in files)
            newCards.Add(BuildRemoteClipCard(file,
                isNewest: newestRemotePath is not null && string.Equals(RemoteClipRelativePath(file.Name), newestRemotePath, StringComparison.OrdinalIgnoreCase)));

        GalleryGrid.Children.Clear();
        foreach (UIElement card in newCards)
            GalleryGrid.Children.Add(card);

        long remoteTotalBytes = files.Sum(f => f.Size);
        UpdateGalleryFooterStats(files.Count, remoteTotalBytes, _currentRemoteGalleryFolder);

        GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }
}
