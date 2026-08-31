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
    private string GalleryFolder => _currentGalleryFolder ?? _settings.ClipsFolder;

    internal void ClearClipsDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        string clipsFolder = _settings.ClipsFolder;
        if (string.IsNullOrWhiteSpace(clipsFolder) || !Directory.Exists(clipsFolder))
        {
            MessageBox.Show(this, "Your clips folder isn't set or doesn't exist.", "Backtrack");
            return;
        }

        List<string> clipFiles;
        try
        {

            clipFiles = Directory.EnumerateFiles(clipsFolder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read the clips folder: {ex.Message}", "Backtrack");
            return;
        }

        if (clipFiles.Count == 0)
        {
            MessageBox.Show(this, "No clips found in your clips folder.", "Backtrack");
            return;
        }

        ShowConfirmDialog(
            $"Permanently delete {clipFiles.Count} clip(s) from \"{clipsFolder}\"? " +
            "Only the clip files will be deleted. Folders, other file types, and subfolders will not be affected.",
            "Delete clips",
            confirmed =>
            {
                if (!confirmed) return;
                int failed = 0;
                foreach (string f in clipFiles)
                {
                    try { File.Delete(f); }
                    catch { failed++; }
                }
                LoadGallery();
                if (failed > 0)
                    MessageBox.Show(this, $"{failed} clip(s) couldn't be deleted (in use, or permissions). The rest were removed.", "Backtrack");
            });
    }

    private static void RevealInExplorer(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

    private void GalleryTile_Click(object sender, RoutedEventArgs e)
    {
        _currentGalleryFolder = null;
        _currentRemoteGalleryFolder = null;

        _galleryIsRemote = !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        RefreshGallerySourceTabsVisibility();
        SyncGalleryToolbarUi();

        ShowScreen(Screen.Gallery);
        LoadGallery();
    }

    private void RefreshGallerySourceTabsVisibility()
    {
        GallerySourceTabs.Visibility = Visibility.Collapsed;
        bool paired = !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        if (!paired && _galleryIsRemote)
        {

            _galleryIsRemote = false;
            _currentRemoteGalleryFolder = null;
        }
    }

    internal void GalleryLocalTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_galleryIsRemote)
            return;
        GalleryFilterBox.Text = string.Empty;
        _galleryIsRemote = false;
        RefreshGallerySourceTabsVisibility();
        LoadGallery();
    }

    private void RunAutoDeleteOldClips()
    {
        if (!_settings.AutoDeleteOldClipsEnabled)
            return;

        string folder = _settings.ClipsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        DateTime cutoff = DateTime.Now.AddDays(-_settings.AutoDeleteOldClipsAfterDays);
        List<string> oldClips;
        try
        {
            oldClips = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.GetLastWriteTime(f) < cutoff)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Auto-delete old clips: couldn't scan clips folder: {ex.Message}");
            return;
        }

        if (oldClips.Count == 0)
            return;

        int deleted = 0;
        foreach (string f in oldClips)
        {
            string fileName = Path.GetFileName(f);
            if (_settings.StarredClips.Contains(fileName) || _settings.StarredClips.Contains(f))
                continue;

            if (RecycleBin.Delete(f))
                deleted++;
        }

        AppLog.Write($"Auto-delete old clips: removed {deleted}/{oldClips.Count} clip(s) older than {_settings.AutoDeleteOldClipsAfterDays} day(s).");
        if (deleted > 0)
        {
            _toastOverlay.ShowOldClipsAutoDeleted(deleted, _settings.AutoDeleteOldClipsAfterDays);
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        }
    }

    private async Task RefreshGalleryCountAsync()
    {

        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RemoteGalleryListing? rootListing = await _pairing.ListRemoteGalleryAsync("");
            if (rootListing is not null)
            {
                int total = rootListing.Files.Count;
                foreach (string folder in rootListing.Folders)
                    total += await CountRemoteClipsRecursiveAsync(folder);
                GalleryStatus.Text = total == 1 ? "1 clip" : $"{total} clips";
                return;
            }

        }

        int count = await Task.Run(CountClips);
        GalleryStatus.Text = count == 1 ? "1 clip" : $"{count} clips";
    }

    private static string GetThumbnailCachePath(FileInfo file)
    {
        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backtrack", "thumbnails");
        Directory.CreateDirectory(cacheDir);

        string key = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, $"{hash}.jpg");
    }

    private static string GetDurationCachePath(FileInfo file) => Path.ChangeExtension(GetThumbnailCachePath(file), ".duration");

    private async Task LoadThumbnailAsync(FileInfo file, Image target, TextBlock? durationTarget = null)
    {
        string? cachePath = await EnsureThumbnailCachedAsync(file);

        long? durationMs = TryGetCachedDurationMs(file);

        if (cachePath is null && durationMs is null)
            return;

        BitmapImage? bitmap = null;
        if (cachePath is not null && File.Exists(cachePath))
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

        await target.Dispatcher.InvokeAsync(() =>
        {
            if (bitmap is not null)
                target.Source = bitmap;
            if (durationTarget is not null && durationMs is not null)
                durationTarget.Text = FormatDuration(durationMs.Value);
        });
    }

    private void RefreshPairingStatusUi()
    {
        if (!string.IsNullOrEmpty(_settings.PairedPeerName))
        {
            PairingStatusText.Text = $"Paired with \"{_settings.PairedPeerName}\"";
            UnpairButton.Visibility = Visibility.Visible;
            RefreshRemoteThumbnailsRow.Visibility = Visibility.Visible;
        }
        else
        {
            PairingStatusText.Text = "Not paired";
            UnpairButton.Visibility = Visibility.Collapsed;
            RefreshRemoteThumbnailsRow.Visibility = Visibility.Collapsed;
        }
        RefreshGallerySourceTabsVisibility();
    }

    internal void RefreshRemoteThumbnailsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Backtrack", "RemoteThumbnails");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
            _toastOverlay.ShowMergeSaved("Remote thumbnails refreshed");
            if (GalleryPanel.Visibility == Visibility.Visible)
            {
                _ = LoadRemoteGalleryAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Failed to refresh remote thumbnails: {ex.Message}");
        }
    }

    internal void ChangeClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { InitialDirectory = _settings.ClipsFolder };
            if (dialog.ShowDialog(this) == true)
            {
                _settings.ClipsFolder = dialog.FolderName;
                _settings.Save();
                ClipsFolderText.Text = _settings.ClipsFolder;
                _ = RefreshGalleryCountAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't change the clips folder: {ex.Message}", "Backtrack");
        }
    }

    private void LoadGallery()
    {
        if (_galleryIsRemote)
        {
            _ = LoadRemoteGalleryAsync();
            return;
        }

        GalleryGrid.Children.Clear();
        _galleryCardSelection.Clear();
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
        UpdateGalleryPathBar();

        string folder = GalleryFolder;

        if (!Directory.Exists(folder))
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Folder doesn't exist yet: {folder}\n\nSet a folder that actually has your clips in Settings.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
                Width = BigWidth() - 40,
            });
            return;
        }

        string filter = GalleryFilterBox.Text.Trim();

        PurgeCorruptedStubs(folder);

        List<DirectoryInfo> subfolders;
        List<FileInfo> files;
        try
        {
            subfolders = Directory.GetDirectories(folder)
                .Select(d => new DirectoryInfo(d))
                .Where(d => filter.Length == 0 || d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            files = Directory.EnumerateFiles(folder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Where(f => filter.Length == 0 || Path.GetFileNameWithoutExtension(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length >= 10240 && TryGetCachedDurationMs(f) is not < 2000)
                .ToList();

            if (_settings.GalleryStarredOnly)
            {
                files = files.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).ToList();
            }

            files = ApplyGallerySort(files).ToList();
        }
        catch (Exception ex)
        {
            GalleryGrid.Children.Add(new TextBlock { Text = $"Couldn't read that folder: {ex.Message}", Foreground = (Brush)FindResource("Rec"), TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (subfolders.Count == 0 && files.Count == 0)
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = filter.Length == 0 ? "No clips in this folder yet." : $"Nothing here matches \"{filter}\".",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            GalleryStatus.Text = "0 clips";
            UpdateGalleryFooterStats(0, 0, _currentGalleryFolder is null ? null : Path.GetFileName(_currentGalleryFolder));
            return;
        }

        string? newestClipPath = GetNewestClipPath();

        foreach (DirectoryInfo dir in subfolders)
        {
            string dirFull = Path.GetFullPath(dir.FullName);
            bool leadsToNewest = newestClipPath is not null
                && newestClipPath.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            GalleryGrid.Children.Add(BuildFolderCard(dir.Name, () => OpenGalleryFolder(dir.FullName), leadsToNewest, dir.FullName));
        }

        foreach (FileInfo file in files)
            GalleryGrid.Children.Add(BuildClipCard(file,
                isNewest: newestClipPath is not null && string.Equals(Path.GetFullPath(file.FullName), newestClipPath, StringComparison.OrdinalIgnoreCase)));

        int totalClipsCount;
        long totalFolderBytes;
        try
        {
            var allFilesInTree = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length > 0)
                .ToList();

            if (_settings.GalleryStarredOnly)
            {
                allFilesInTree = allFilesInTree.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).ToList();
            }

            totalClipsCount = allFilesInTree.Count;
            totalFolderBytes = allFilesInTree.Sum(f => f.Length);
        }
        catch
        {
            totalClipsCount = files.Count;
            totalFolderBytes = files.Sum(f => f.Length);
        }

        UpdateGalleryFooterStats(totalClipsCount, totalFolderBytes, _currentGalleryFolder is null ? null : Path.GetFileName(_currentGalleryFolder));

        if (_currentGalleryFolder is null && subfolders.Count > 0)
            _ = RefreshGalleryCountAsync();
        else
            GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";

        AdjustGalleryGridItemWidth();
    }

    private double GetGalleryCardWidth()
    {
        if (GalleryGrid is not null && GalleryGrid.ItemWidth > 50)
            return Math.Max(180, GalleryGrid.ItemWidth - 14);
        return 216;
    }

    private void AdjustGalleryGridItemWidth()
    {
        if (GalleryGrid is null) return;
        double targetWidth = Width > 0 ? Width : 1430;
        double innerWidth = targetWidth - 50;
        if (innerWidth < 300) return;

        int cols = Math.Max(3, (int)Math.Round(innerWidth / 232.0));
        double itemW = Math.Floor(innerWidth / cols);
        GalleryGrid.ItemWidth = itemW;
    }

    private static void PurgeCorruptedStubs(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 0 && fi.Length < 10240)
                    {
                        try { fi.Delete(); } catch { }
                    }
                }
            }
        }
        catch { }
    }
}
