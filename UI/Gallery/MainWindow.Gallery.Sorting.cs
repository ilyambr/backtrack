using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void SyncGalleryToolbarUi()
    {
        if (_settings is null || GallerySortComboBox is null) return;
        foreach (var item in GallerySortComboBox.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag is string tag && string.Equals(tag, _settings.GallerySortMode, StringComparison.OrdinalIgnoreCase))
            {
                GallerySortComboBox.SelectedItem = cbi;
                break;
            }
        }
    }

    internal void GallerySortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null || GalleryPanel is null) return;
        if (GallerySortComboBox.SelectedItem is ComboBoxItem item && item.Tag is string sortTag)
        {
            _settings.GallerySortMode = sortTag;
            _settings.Save();
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        }
    }

    private IEnumerable<FileInfo> ApplyGallerySort(IEnumerable<FileInfo> files)
    {
        return _settings.GallerySortMode switch
        {
            "StarredOnly" => files.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).OrderByDescending(f => f.LastWriteTime),
            "StarredFirst" => files.OrderByDescending(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).ThenByDescending(f => f.LastWriteTime),
            "DateAsc" => files.OrderBy(f => f.LastWriteTime),
            "SizeDesc" => files.OrderByDescending(f => f.Length),
            "SizeAsc" => files.OrderBy(f => f.Length),
            "NameAsc" => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            "NameDesc" => files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderByDescending(f => f.LastWriteTime),
        };
    }

    private IEnumerable<RemoteGalleryFile> ApplyRemoteGallerySort(IEnumerable<RemoteGalleryFile> files)
    {
        return _settings.GallerySortMode switch
        {
            "StarredOnly" => files.Where(f => _settings.StarredClips.Contains(f.Name)).OrderByDescending(f => f.Modified),
            "StarredFirst" => files.OrderByDescending(f => _settings.StarredClips.Contains(f.Name)).ThenByDescending(f => f.Modified),
            "DateAsc" => files.OrderBy(f => f.Modified),
            "SizeDesc" => files.OrderByDescending(f => f.Size),
            "SizeAsc" => files.OrderBy(f => f.Size),
            "NameAsc" => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            "NameDesc" => files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderByDescending(f => f.Modified),
        };
    }

    internal void GalleryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GalleryFilterPlaceholder.Visibility = GalleryFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _galleryFilterDebounceTimer?.Stop();
        _galleryFilterDebounceTimer?.Start();
    }
}
