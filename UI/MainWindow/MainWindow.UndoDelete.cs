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
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Updates;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private async Task DeleteOrRecycleCancelledFileAsync(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            fullPath = path;
        }

        _pendingDeletePaths.Add(fullPath);
        Dispatcher.Invoke(() =>
        {
            RefreshRecentClipsOverlay();
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            else
                _ = RefreshGalleryCountAsync();
        });

        try
        {
            if (!_settings.ObsIsRemote)
            {
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    if (!File.Exists(path))
                        break;
                    try
                    {
                        if (RecycleBin.Delete(path))
                            break;
                        File.Delete(path);
                        break;
                    }
                    catch
                    {
                        await Task.Delay(100);
                    }
                }
            }
            else
            {
                try
                {
                    string fileName = Path.GetFileName(path.Replace('/', '\\'));
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        _ = _pairing.DeleteRemoteClipAsync(fileName);
                    }
                }
                catch { }
            }
        }
        finally
        {
            _pendingDeletePaths.Remove(fullPath);
            Dispatcher.Invoke(() =>
            {
                RefreshRecentClipsOverlay();
                if (GalleryPanel.Visibility == Visibility.Visible)
                    LoadGallery();
                else
                    _ = RefreshGalleryCountAsync();
            });
        }
    }


        private void QueueRemoteDeleteWithUndo(string relativePath, string displayName, RemoteGalleryFile? file)
    {
        _pendingRemoteDeletePaths.Add(relativePath);
        if (GalleryPanel.Visibility == Visibility.Visible)
            LoadGallery();
        
        
        
        
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowDeleteUndo(displayName,
            onExpire: () =>
            {
                _pendingRemoteDeletePaths.Remove(relativePath);
                _ = FinishRemoteDeleteAsync(relativePath, displayName, file);
            },
            onUndo: () =>
            {
                _pendingRemoteDeletePaths.Remove(relativePath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }


    private void QueueDeleteWithUndo(FileInfo file)
    {
        string fullPath = Path.GetFullPath(file.FullName);
        _pendingDeletePaths.Add(fullPath);
        LoadGallery();
        
        
        
        
        
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowDeleteUndo(file.Name,
            onExpire: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                if (!RecycleBin.Delete(fullPath))
                {
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"Couldn't delete \"{file.Name}\".", "Backtrack"));
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    else
                        _ = RefreshGalleryCountAsync();
                    RefreshRecentClipsOverlay();
                });
            },
            onUndo: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }


        private void QueueMultiDeleteWithUndo(List<FileInfo> files)
    {
        var fullPaths = files.Select(f => Path.GetFullPath(f.FullName)).ToList();
        foreach (string fullPath in fullPaths)
            _pendingDeletePaths.Add(fullPath);
        LoadGallery();
        
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowMultiDeleteUndo(files.Count,
            onExpire: () =>
            {
                var failed = new List<string>();
                foreach (string fullPath in fullPaths)
                {
                    _pendingDeletePaths.Remove(fullPath);
                    if (!RecycleBin.Delete(fullPath))
                        failed.Add(Path.GetFileName(fullPath));
                }
                if (failed.Count > 0)
                {
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                        $"Couldn't delete {failed.Count} clip(s): {string.Join(", ", failed)}.", "Backtrack"));
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    else
                        _ = RefreshGalleryCountAsync();
                    RefreshRecentClipsOverlay();
                });
            },
            onUndo: () =>
            {
                foreach (string fullPath in fullPaths)
                    _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }
}
