using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{

        private Border BuildRecentRemoteClipTile(string relativePath, RemoteGalleryFile file)
    {
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        var thumb = new Border { Background = (Brush)FindResource("ThumbnailBg"), Width = 96, Height = 64, Cursor = Cursors.Hand, ClipToBounds = true, Child = thumbImage };
        thumb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenRemoteClipStreaming(relativePath, file);
        };

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 96,
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.IBeam,
            ToolTip = "Double-click to rename",
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                BeginRecentRemoteClipRename(title, relativePath, file);
            }
        };

        DateTime modified = file.Modified.ToLocalTime();
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {FormatFileSize(file.Size)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("Text2"),
            Width = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);

        var tile = new Border { Child = content };
        _ = LoadRemoteThumbnailAsync(relativePath, file, thumbImage);

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var renameItem = new MenuItem { Header = "Rename", Style = (Style)FindResource("DarkMenuItem") };
        renameItem.Click += (_, _) => BeginRecentRemoteClipRename(title, relativePath, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(relativePath);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteRemoteClip(relativePath, file);
        contextMenu.Items.Add(renameItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return tile;
    }


    private void ApplyStorageLimit_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(StorageLimitGbBox.Text.Trim(), out double gb) || gb <= 0)
        {
            MessageBox.Show(this, "Storage limit must be a number of gigabytes greater than 0.", "Backtrack");
            return;
        }
        _settings.StorageLimitGb = gb;
        _settings.Save();
        RefreshStorageLimitStatusText();
    }


    private void ApplyAutoDeleteOldClips_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoDeleteOldClipsDaysBox.Text.Trim(), out int days) || days <= 0)
        {
            MessageBox.Show(this, "Age must be a whole number of days greater than 0.", "Backtrack");
            return;
        }
        _settings.AutoDeleteOldClipsAfterDays = days;
        _settings.Save();
        RestartAutoDeleteOldClipsTimer();
    }


    private async void ApplyRamDiskSettings_Click(object sender, RoutedEventArgs e)
    {
        string driveText = RamDiskDriveBox.Text.Trim().TrimEnd(':');
        if (driveText.Length != 1 || !char.IsLetter(driveText[0]))
        {
            MessageBox.Show(this, "Drive letter must be a single letter, e.g. R.", "Backtrack");
            return;
        }
        if (!int.TryParse(RamDiskSizeBox.Text.Trim(), out int sizeMb) || sizeMb < 256)
        {
            MessageBox.Show(this, "Size must be a number of megabytes, at least 256.", "Backtrack");
            return;
        }

        await ApplyRamDiskConfigAsync(_settings.RamDiskEnabled, char.ToUpperInvariant(driveText[0]), sizeMb);
    }


    private void SuggestRamDiskSize_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RamDiskTargetMinutesBox.Text.Trim(), out int minutes) || minutes <= 0)
        {
            MessageBox.Show(this, "Enter a number of minutes first.", "Backtrack");
            return;
        }

        ReplayBufferSizing.Estimate? estimate = ReplayBufferSizing.TryEstimate(minutes);
        if (estimate is null)
        {
            MessageBox.Show(this, "Couldn't read OBS's config to estimate this -- enter a size manually.", "Backtrack");
            return;
        }

        RamDiskSizeBox.Text = estimate.Value.SuggestedSizeMb.ToString();
        MessageBox.Show(this,
            $"Suggested {estimate.Value.SuggestedSizeMb} MB for a {minutes}-minute buffer, based on {estimate.Value.Source} (~{estimate.Value.AssumedBitrateKbps} kbps).\n\n" +
            "Click \"Save & apply\" to actually use it.",
            "Backtrack");
    }


    private static bool TryParseHotkeyString(string hotkeyStr, out GlobalHotkey.Modifiers modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeyStr) || hotkeyStr.Equals("(unbound)", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = hotkeyStr.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        string mainKeyPart = parts[^1];

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string p = parts[i].ToLowerInvariant();
            if (p is "ctrl" or "control")
                modifiers |= GlobalHotkey.Modifiers.Control;
            else if (p is "alt" or "menu")
                modifiers |= GlobalHotkey.Modifiers.Alt;
            else if (p is "shift")
                modifiers |= GlobalHotkey.Modifiers.Shift;
            else if (p is "win" or "windows" or "super" or "cmd")
                modifiers |= GlobalHotkey.Modifiers.Win;
        }

        string keyClean = mainKeyPart.Trim();
        string keyLower = keyClean.ToLowerInvariant();

        if (keyLower.StartsWith("f") && int.TryParse(keyLower[1..], out int fNum) && fNum >= 1 && fNum <= 24)
        {
            virtualKey = (uint)(0x70 + (fNum - 1));
            return true;
        }

        if (keyClean.Length == 1)
        {
            char c = char.ToUpperInvariant(keyClean[0]);
            if (c >= 'A' && c <= 'Z')
            {
                virtualKey = c;
                return true;
            }
            if (c >= '0' && c <= '9')
            {
                virtualKey = c;
                return true;
            }
            virtualKey = c switch
            {
                ';' => 186,
                '=' or '+' => 187,
                ',' => 188,
                '-' => 189,
                '.' => 190,
                '/' => 191,
                '`' or '~' => 192,
                '[' => 219,
                '\\' => 220,
                ']' => 221,
                '\'' => 222,
                _ => 0
            };
            if (virtualKey != 0)
                return true;
        }

        virtualKey = keyLower switch
        {
            "space" or "spacebar" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" or "back" => 0x08,
            "del" or "delete" => 0x2E,
            "ins" or "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "page up" or "pageup" or "pgup" => 0x21,
            "page down" or "pagedown" or "pgdn" => 0x22,
            "up" or "arrow up" or "arrowup" => 0x26,
            "down" or "arrow down" or "arrowdown" => 0x28,
            "left" or "arrow left" or "arrowleft" => 0x25,
            "right" or "arrow right" or "arrowright" => 0x27,
            "caps lock" or "capslock" or "caps" => 0x14,
            "scroll lock" or "scrolllock" => 0x91,
            "num lock" or "numlock" => 0x90,
            "print screen" or "printscreen" or "prtscn" or "snapshot" => 0x2C,
            "pause" or "break" => 0x13,
            "num 0" or "numpad 0" or "numpad0" => 0x60,
            "num 1" or "numpad 1" or "numpad1" => 0x61,
            "num 2" or "numpad 2" or "numpad2" => 0x62,
            "num 3" or "numpad 3" or "numpad3" => 0x63,
            "num 4" or "numpad 4" or "numpad4" => 0x64,
            "num 5" or "numpad 5" or "numpad5" => 0x65,
            "num 6" or "numpad 6" or "numpad6" => 0x66,
            "num 7" or "numpad 7" or "numpad7" => 0x67,
            "num 8" or "numpad 8" or "numpad8" => 0x68,
            "num 9" or "numpad 9" or "numpad9" => 0x69,
            "period" => 190,
            "comma" => 188,
            "minus" => 189,
            "plus" or "equals" => 187,
            "slash" => 191,
            "backslash" => 220,
            "semicolon" => 186,
            "quote" or "apostrophe" => 222,
            _ => 0
        };

        if (virtualKey != 0)
            return true;

        if (Enum.TryParse<Key>(keyClean, true, out Key parsedKey) && parsedKey != Key.None)
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(parsedKey);
            return virtualKey != 0;
        }

        return false;
    }


    private bool IsCriticalOperationActive()
    {
        bool isRenaming = _isRenamingCard || _isPlayerRenaming;
        bool isTrimming = (TrimPanel != null && TrimPanel.Visibility == Visibility.Visible) || _trimStart.HasValue || _trimEnd.HasValue || _isTrimming;
        bool isSelectingClips = _selectedClipPaths.Count > 0;
        bool isDialogActive = _activeConfirmDialog != null && _activeConfirmDialog.IsLoaded;

        return isRenaming || isTrimming || isSelectingClips || isDialogActive;
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


    private async Task<Border> BuildMainRecordFolderRowAsync()
    {
        string? currentFolder = await _obs.GetMainRecordDirectoryAsync();

        var name = new TextBlock { Text = "Full Scene", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };

        var folderLabel = new TextBlock
        {
            Text = DescribeRecordRowDestDir(currentFolder),
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderButton = BuildFolderIconButton(async (_, _) => await PickMainRecordFolderAsync(folderLabel));

        var bottomGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderLabel, 0);
        Grid.SetColumn(folderButton, 1);
        bottomGrid.Children.Add(folderLabel);
        bottomGrid.Children.Add(folderButton);

        var container = new StackPanel();
        container.Children.Add(name);
        container.Children.Add(bottomGrid);

        return new Border { Style = (Style)FindResource("SettingsRow"), Child = container };
    }


    private async Task<Border> BuildRecordFolderRowAsync(RecordRow row)
    {
        string label = row.Label;
        string? currentFolder = await _obs.GetRecordRowDestinationFolderAsync(row.SourceName, row.FilterName);

        var toggle = new ToggleButton { Style = (Style)FindResource("AppToggle"), VerticalAlignment = VerticalAlignment.Center };
        toggle.IsChecked = !_settings.HiddenBufferLabels.Contains(label);

        var name = new TextBlock { Text = DisplayLabel(label), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition());
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(toggle, 1);
        topGrid.Children.Add(name);
        topGrid.Children.Add(toggle);
        EnableDoubleTapRename(name, label);

        var folderLabel = new TextBlock
        {
            Text = DescribeRecordRowDestDir(currentFolder),
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var folderButton = BuildFolderIconButton(async (_, _) => await PickRecordRowFolderAsync(row.SourceName, row.FilterName, folderLabel));

        var bottomGrid = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
        };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition());
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderLabel, 0);
        Grid.SetColumn(folderButton, 1);
        bottomGrid.Children.Add(folderLabel);
        bottomGrid.Children.Add(folderButton);

        toggle.Click += (_, _) =>
        {
            if (toggle.IsChecked == true)
                _settings.HiddenBufferLabels.Remove(label);
            else
                _settings.HiddenBufferLabels.Add(label);
            _settings.Save();
            bottomGrid.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        };

        var container = new StackPanel();
        container.Children.Add(topGrid);
        container.Children.Add(bottomGrid);

        return new Border { Style = (Style)FindResource("SettingsRow"), Child = container };
    }


    private void SetLocalRowNameOverride(string originalLabel, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, originalLabel, StringComparison.Ordinal))
            _settings.LocalRowNameOverrides.Remove(originalLabel);
        else
            _settings.LocalRowNameOverrides[originalLabel] = newName;
        _settings.Save();
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
                
                
                
                
                
                
                
                
                
                
                
                
                
                .Where(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
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
            GalleryGrid.Children.Add(BuildFolderCard(dir.Name, () => OpenGalleryFolder(dir.FullName), leadsToNewest));
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


    private void GalleryUp_Click(object sender, MouseButtonEventArgs e)
    {
        GalleryFilterBox.Text = string.Empty; 
        if (_galleryIsRemote)
        {
            if (_currentRemoteGalleryFolder is not null)
            {
                int lastSlash = _currentRemoteGalleryFolder.LastIndexOf('/');
                _currentRemoteGalleryFolder = lastSlash < 0 ? null : _currentRemoteGalleryFolder[..lastSlash];
            }
            LoadGallery();
            return;
        }

        string root = Path.GetFullPath(_settings.ClipsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(GalleryFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase) || !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _currentGalleryFolder = null;
        }
        else
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) || !parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                _currentGalleryFolder = null;
            }
            else
            {
                _currentGalleryFolder = parent;
            }
        }
        LoadGallery();
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
            newCards.Add(BuildFolderCard(name, () => OpenRemoteGalleryFolder(name), leadsToNewest));
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


    private Border BuildRemoteClipCard(RemoteGalleryFile file, bool isNewest = false)
    {
        var iconHost = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        
        
        
        
        
        var playGlyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,5.14V19.14L19,12.14L8,5.14Z"),
            Fill = (Brush)FindResource("Text1"),
            Width = 38,
            Height = 38,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        string relativePath = RemoteClipRelativePath(file.Name);
        bool isStarred = _settings.StarredClips.Contains(file.Name) || _settings.StarredClips.Contains(relativePath);
        var starButton = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(isStarred ? (byte)0x80 : (byte)0x40, 0x00, 0x00, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Cursor = Cursors.Hand,
            ToolTip = "Toggle Star / Favorite",
        };
        var starGlyph = new TextBlock
        {
            Text = isStarred ? "★" : "☆",
            FontSize = 13,
            Foreground = isStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        starButton.Child = starGlyph;
        starButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleStarClip(file.Name);
            bool nowStarred = _settings.StarredClips.Contains(file.Name);
            starGlyph.Text = nowStarred ? "★" : "☆";
            starGlyph.Foreground = nowStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1");
            starButton.Background = new SolidColorBrush(Color.FromArgb(nowStarred ? (byte)0x80 : (byte)0x40, 0x00, 0x00, 0x00));
            if (_settings.GalleryStarredOnly)
                _ = LoadRemoteGalleryAsync();
        };

        var iconGrid = new Grid();
        iconGrid.Children.Add(playGlyph);
        iconGrid.Children.Add(thumbImage);
        iconGrid.Children.Add(starButton);
        iconHost.Child = iconGrid;

        var title = new TextBlock
        {
            Text = file.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
            Cursor = Cursors.IBeam,
        };
        double mb = file.Size / (1024.0 * 1024.0);
        
        
        
        
        DateTime modified = file.Modified.ToLocalTime();
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {mb:0.#} MB",
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
        };

        UIElement titleRow = isNewest ? WithNewestDot(title, "Newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(iconHost);
        content.Children.Add(titleRow);
        content.Children.Add(sub);

        var card = new Border { Width = 210, Child = content };

        
        
        
        
        _ = LoadRemoteThumbnailAsync(relativePath, file, thumbImage);
        iconHost.MouseLeftButtonUp += (_, _) => OpenRemoteClipStreaming(relativePath, file);

        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                BeginRenameRemote(card, title, relativePath, file);
        };

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => _ = OpenRemoteClipFileLocationAsync(relativePath, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => _ = CopyRemoteClipPathAsync(relativePath, file);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteRemoteClip(relativePath, file);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        iconHost.ContextMenu = contextMenu;

        return card;
    }


        private void BeginRenameRemote(Border card, TextBlock title, string relativePath, RemoteGalleryFile file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        _isRenamingCard = true;
        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; _ = CommitRenameAsync(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; _isRenamingCard = false; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; _ = CommitRenameAsync(); } };

        async Task CommitRenameAsync()
        {
            _isRenamingCard = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                (bool success, string? error, _) = await _pairing.RenameRemoteClipAsync(relativePath, newName);
                if (!success)
                    MessageBox.Show(this, $"Couldn't rename: {error}", "Backtrack");
            }
            LoadGallery();
        }
    }


        private Border BuildFolderCard(string name, Action onOpen, bool leadsToNewest = false)
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
        card.MouseLeftButtonUp += (_, _) => onOpen();

        return card;
    }


    private Border BuildClipCard(FileInfo file, bool isNewest = false)
    {
        
        
        
        var thumb = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        
        
        
        
        var selectCircle = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
        };

        bool isStarred = _settings.StarredClips.Contains(file.Name) || _settings.StarredClips.Contains(file.FullName);
        var starButton = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(isStarred ? (byte)0x80 : (byte)0x40, 0x00, 0x00, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Cursor = Cursors.Hand,
            ToolTip = "Toggle Star / Favorite",
        };
        var starGlyph = new TextBlock
        {
            Text = isStarred ? "★" : "☆",
            FontSize = 13,
            Foreground = isStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        starButton.Child = starGlyph;
        starButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleStarClip(file.Name);
            bool nowStarred = _settings.StarredClips.Contains(file.Name);
            starGlyph.Text = nowStarred ? "★" : "☆";
            starGlyph.Foreground = nowStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1");
            starButton.Background = new SolidColorBrush(Color.FromArgb(nowStarred ? (byte)0x80 : (byte)0x40, 0x00, 0x00, 0x00));
            if (_settings.GalleryStarredOnly)
                LoadGallery();
        };

        var thumbHost = new Grid();
        thumbHost.Children.Add(thumbImage);
        thumbHost.Children.Add(selectCircle);
        thumbHost.Children.Add(starButton);
        thumb.Child = thumbHost;

        thumb.MouseEnter += (_, _) => selectCircle.Visibility = Visibility.Visible;
        thumb.MouseLeave += (_, _) =>
        {
            if (_selectedClipPaths.Count == 0)
                selectCircle.Visibility = Visibility.Collapsed;
        };
        selectCircle.MouseLeftButtonUp += (_, e) =>
        {
            
            
            
            e.Handled = true;
            ToggleClipSelected(file);
        };

        
        
        
        
        thumb.MouseLeftButtonUp += (_, _) =>
        {
            if (_selectedClipPaths.Count > 0)
                ToggleClipSelected(file);
            else
                OpenInPlayer(file);
        };

        _galleryCardSelection.Add((file, selectCircle, thumb));

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
            Cursor = Cursors.IBeam,
        };

        DateTime modified = file.LastWriteTime;
        string subText = modified.Date == DateTime.Today
            ? modified.ToString("h:mm tt")
            : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock { Text = subText, FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        
        
        
        var durationText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        long? knownDurationMs = TryGetCachedDurationMs(file);
        if (knownDurationMs is long ms)
            durationText.Text = FormatDuration(ms);

        var subRow = new Grid();
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sub, 0);
        Grid.SetColumn(durationText, 1);
        subRow.Children.Add(sub);
        subRow.Children.Add(durationText);

        UIElement titleRow = isNewest ? WithNewestDot(title, "Newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(titleRow);
        content.Children.Add(subRow);

        _ = LoadThumbnailAsync(file, thumbImage, knownDurationMs is null ? durationText : null);

        
        
        
        
        var card = new Border { Width = 210, Child = content };

        
        
        
        
        if (IsNetworkPath(_settings.ClipsFolder))
        {
            var copyBtn = new Button { Content = "Copy here", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0, 6, 0, 0) };
            copyBtn.Click += async (_, _) => await CopyToThisPcAsync(file, copyBtn);
            content.Children.Add(copyBtn);
        }

        
        
        
        
        
        
        
        
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                BeginRename(card, title, file);
        };

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => RevealInExplorerAndClose(file.FullName);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(file.FullName);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteClip(file, card);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return card;
    }


    private void BeginRename(Border card, TextBlock title, FileInfo file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        _isRenamingCard = true;
        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; _isRenamingCard = false; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void CommitRename()
        {
            _isRenamingCard = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                try
                {
                    string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                    File.Move(file.FullName, newPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
                }
            }
            LoadGallery();
        }
    }


    private void UpdatePlayerSeekUi()
    {
        if (_vlcPlayer is null || _isScrubbing)
            return;

        long lengthMs = _vlcPlayer.Length;
        long timeMs = _vlcPlayer.Time;

        if (lengthMs <= 0)
            return;

        
        
        if (_previewLooping && _trimStart is not null && _trimEnd is not null && timeMs >= _trimEnd.Value.TotalMilliseconds)
            CommitSeek((long)_trimStart.Value.TotalMilliseconds);

        PlayerCurrentTime.Text = FormatDuration(Math.Max(timeMs, 0));
        PlayerDurationText.Text = FormatDuration(Math.Max(lengthMs, 0));

        double ratio = Math.Clamp((double)timeMs / lengthMs, 0.0, 1.0);
        double trackWidth = PlayerSeekTrack.ActualWidth;

        if (trackWidth > 0)
        {
            PlayerSeekFill.Width = ratio * trackWidth;
            PlayerSeekThumb.Margin = new Thickness(ratio * trackWidth - 7, 0, 0, 0);
        }

        
        
        
        
        
        if (TrimPanel.Visibility == Visibility.Visible)
            UpdateTrimTimelineUi();
    }


        private void PlayerTrim_Click(object sender, RoutedEventArgs e)
    {
        PlayerMenuPopup.IsOpen = false;
        bool opening = TrimPanel.Visibility != Visibility.Visible;
        TrimPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        PlayerTransportRow.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        MoveTransportControlsForTrim(opening);
        if (opening)
        {
            long lengthMs = _vlcPlayer?.Length ?? 0;
            _trimStart ??= TimeSpan.Zero;
            _trimEnd ??= TimeSpan.FromMilliseconds(Math.Max(0, lengthMs));
            BuildTrimRuler();
            UpdateTrimTimelineUi();
        }
        else
        {
            StopPreviewLoop();
        }
    }


        private void MoveTransportControlsForTrim(bool intoTrimRow)
    {
        PlayPauseButton.Width = PlayPauseButton.Height = intoTrimRow ? PlayPauseButtonTrimSize : PlayPauseButtonNormalSize;
        
        
        
        
        
        PlayPauseButton.Style = (Style)FindResource(intoTrimRow ? "BareIconButton" : "PlayerTransportButton");
        
        
        
        PlayPauseButton.Margin = intoTrimRow ? new Thickness(0, 0, 10, 0) : default;
        Reparent(PlayPauseButton, intoTrimRow ? TrimActionButtons : PlayerTransportRow, intoTrimRow ? null : PlayPauseButtonHomeColumn, insertAtFront: intoTrimRow);
        Reparent(AudioTrackCombo, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : AudioTrackComboHomeColumn);
        Reparent(PlayerSpeedButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerSpeedButtonHomeColumn);
        Reparent(PlayerVolumeButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerVolumeButtonHomeColumn);
        Reparent(PlayerFullscreenButton, intoTrimRow ? TrimTransportExtras : PlayerTransportRow, intoTrimRow ? null : PlayerFullscreenButtonHomeColumn);

        static void Reparent(FrameworkElement element, Panel newParent, int? gridColumn, bool insertAtFront = false)
        {
            if (element.Parent is Panel oldParent && !ReferenceEquals(oldParent, newParent))
                oldParent.Children.Remove(element);
            if (gridColumn is int col)
                Grid.SetColumn(element, col);
            if (!newParent.Children.Contains(element))
            {
                if (insertAtFront)
                    newParent.Children.Insert(0, element);
                else
                    newParent.Children.Add(element);
            }
        }
    }


    private void TrimCancel_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = null;
        _trimEnd = null;
        TrimStartText.Text = "0:00";
        TrimEndText.Text = "0:00";
        TrimStatusText.Text = "";
        TrimPanel.Visibility = Visibility.Collapsed;
        PlayerTransportRow.Visibility = Visibility.Visible;
        MoveTransportControlsForTrim(intoTrimRow: false);
        StopPreviewLoop();
    }


    private void TrimStartHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.Start;
        TrimTimelineTrack.CaptureMouse();
        
        
        e.Handled = true;
    }


    private void TrimEndHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.End;
        TrimTimelineTrack.CaptureMouse();
        e.Handled = true;
    }


        private void TrimTimelineTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _trimDragMode = TrimDragMode.Seek;
        _isScrubbing = true;
        TrimTimelineTrack.CaptureMouse();
        ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack));
    }


    private void TrimTimelineTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trimDragMode == TrimDragMode.None)
            return;
        ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack));
    }


    private void TrimTimelineTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_trimDragMode == TrimDragMode.None)
            return;

        bool wasSeek = _trimDragMode == TrimDragMode.Seek;
        TrimTimelineTrack.ReleaseMouseCapture();
        TrimHandleTooltipPopup.IsOpen = false;
        if (wasSeek)
        {
            ProcessTrimTimelineInput(e.GetPosition(TrimTimelineTrack), immediate: true);
            _isScrubbing = false;
        }
        _trimDragMode = TrimDragMode.None;
    }


    private void TrimTimelineTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildTrimRuler();
        UpdateTrimTimelineUi();
    }


        private void ProcessTrimTimelineInput(Point pos, bool immediate = false)
    {
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        long lengthMs = Math.Max(1, _vlcPlayer.Length);
        double ratio = Math.Clamp(pos.X / trackWidth, 0.0, 1.0);
        long ms = (long)(ratio * lengthMs);

        switch (_trimDragMode)
        {
            case TrimDragMode.Start:
            {
                long endMs = (long)(_trimEnd ?? TimeSpan.FromMilliseconds(lengthMs)).TotalMilliseconds;
                ms = Math.Clamp(ms, 0, Math.Max(0, endMs - 1));
                _trimStart = TimeSpan.FromMilliseconds(ms);
                ShowTrimHandleTooltip(pos.X, ms);
                break;
            }
            case TrimDragMode.End:
            {
                long startMs = (long)(_trimStart ?? TimeSpan.Zero).TotalMilliseconds;
                ms = Math.Clamp(ms, Math.Min(lengthMs, startMs + 1), lengthMs);
                _trimEnd = TimeSpan.FromMilliseconds(ms);
                ShowTrimHandleTooltip(pos.X, ms);
                break;
            }
            case TrimDragMode.Seek:
                _targetSeekMs = ms;
                PlayerCurrentTime.Text = FormatDuration(ms);
                if (immediate)
                {
                    _seekDebounceTimer.Stop();
                    if (_vlcPlayer.IsSeekable)
                        CommitSeek(ms);
                    else
                        _seekDebounceTimer.Start();
                }
                else
                {
                    _seekDebounceTimer.Stop();
                    _seekDebounceTimer.Start();
                }
                break;
        }

        UpdateTrimTimelineUi();
    }


    private void ShowTrimHandleTooltip(double x, long ms)
    {
        TrimHandleTooltipText.Text = FormatDuration(ms);
        TrimHandleTooltipPopup.HorizontalOffset = x - 15;
        TrimHandleTooltipPopup.VerticalOffset = -30;
        TrimHandleTooltipPopup.IsOpen = true;
    }


        private void UpdateTrimTimelineUi()
    {
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        long lengthMs = Math.Max(1, _vlcPlayer.Length);
        long startMs = (long)(_trimStart ?? TimeSpan.Zero).TotalMilliseconds;
        long endMs = (long)(_trimEnd ?? TimeSpan.FromMilliseconds(lengthMs)).TotalMilliseconds;

        double startX = Math.Clamp((double)startMs / lengthMs, 0, 1) * trackWidth;
        double endX = Math.Clamp((double)endMs / lengthMs, 0, 1) * trackWidth;

        TrimSelectedRange.Margin = new Thickness(startX, 0, 0, 0);
        TrimSelectedRange.Width = Math.Max(0, endX - startX);

        
        
        
        
        
        
        const double handleWidth = 10;
        double maxHandleX = Math.Max(0, trackWidth - handleWidth);
        TrimStartHandle.Margin = new Thickness(Math.Clamp(startX - handleWidth / 2, 0, maxHandleX), 0, 0, 0);
        TrimEndHandle.Margin = new Thickness(Math.Clamp(endX - handleWidth / 2, 0, maxHandleX), 0, 0, 0);

        const double playheadWidth = 2;
        double playRatio = Math.Clamp((double)_vlcPlayer.Time / lengthMs, 0, 1);
        double playheadX = Math.Clamp(playRatio * trackWidth - playheadWidth / 2, 0, Math.Max(0, trackWidth - playheadWidth));
        TrimPlayhead.Margin = new Thickness(playheadX, 0, 0, 0);

        TrimStartText.Text = FormatDuration(startMs);
        TrimEndText.Text = FormatDuration(endMs);
    }


        private void BuildTrimRuler()
    {
        TrimRulerCanvas.Children.Clear();
        if (_vlcPlayer is null)
            return;
        double trackWidth = TrimTimelineTrack.ActualWidth;
        if (trackWidth <= 0)
            return;
        long lengthMs = Math.Max(1, _vlcPlayer.Length);

        const int tickCount = 6;
        for (int i = 0; i < tickCount; i++)
        {
            double ratio = i / (double)(tickCount - 1);
            double x = ratio * trackWidth;
            long ms = (long)(ratio * lengthMs);

            var tick = new Border { Width = 1, Height = 5, Background = (Brush)FindResource("Hairline") };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 0);
            TrimRulerCanvas.Children.Add(tick);

            var label = new TextBlock { Text = FormatDuration(ms), FontSize = 9.5, Foreground = (Brush)FindResource("Text2") };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            
            
            
            double labelX = i == 0 ? x : i == tickCount - 1 ? x - label.DesiredSize.Width : x - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, 6);
            TrimRulerCanvas.Children.Add(label);
        }
    }


    private async void TrimReplace_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: true);


    private async void TrimSaveNew_Click(object sender, RoutedEventArgs e) => await RunTrimAsync(replaceOriginal: false);


        private async Task RunTrimAsync(bool replaceOriginal)
    {
        if (_trimStart is null || _trimEnd is null || _trimEnd <= _trimStart)
        {
            MessageBox.Show(this, "Set both a start and end point first (end must be after start).", "Backtrack");
            return;
        }

        
        
        
        
        
        
        if (_currentPlayerFile is null)
        {
            if (_currentPlayerRemoteOrigin is not null)
            {
                await RunRemoteTrimAsync(replaceOriginal);
                return;
            }

            
            
            
            
            
            
            
            
            
            
            AppLog.Write("[trim_clip] RunTrimAsync: both _currentPlayerFile and _currentPlayerRemoteOrigin are null -- nothing to trim, this is the actual failure");
            MessageBox.Show(this, "Nothing to trim -- this clip isn't tracked as either a local file or a remote clip right now. Try reopening it.", "Backtrack");
            return;
        }

        if (_libVlc is null)
            return;

        FileInfo sourceFile = _currentPlayerFile;
        TimeSpan start = _trimStart.Value;
        TimeSpan end = _trimEnd.Value;
        
        
        
        
        
        (string RelativePath, string DeviceId)? remoteOrigin = _currentPlayerRemoteOrigin;

        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{sourceFile.Extension}");

        _isTrimming = true;
        TrimReplaceButton.IsEnabled = false;
        TrimSaveNewButton.IsEnabled = false;
        TrimStatusText.Text = "Trimming...";

        
        
        
        
        
        
        StopPlayerPlayback();

        try
        {
            await Task.Run(() => ExportTrim(sourceFile.FullName, tempOut, start, end));

            if (replaceOriginal)
            {
                bool? userConfirmed = null;
                ShowConfirmDialog("Replace the original clip with this trimmed version? This can't be undone.", "Replace", c => userConfirmed = c);
                while (!userConfirmed.HasValue && IsVisible)
                {
                    await Task.Delay(50);
                }
                if (userConfirmed != true)
                {
                    File.Delete(tempOut);
                    OpenInPlayer(sourceFile);
                    return;
                }
                File.Copy(tempOut, sourceFile.FullName, overwrite: true);
                File.Delete(tempOut);
                _currentPlayerFile = new FileInfo(sourceFile.FullName);
                OpenInPlayer(_currentPlayerFile);
                _toastOverlay.ShowTrimSaved(sourceFile.FullName);

                if (remoteOrigin is (string relPath, _))
                {
                    
                    
                    _currentPlayerRemoteOrigin = remoteOrigin;
                    TrimStatusText.Text = $"Sending to {_settings.PairedPeerName}'s PC...";
                    (bool upSuccess, string? upError, _) = await _pairing.UploadRemoteClipAsync(relPath, _currentPlayerFile.FullName, overwrite: true);
                    if (!upSuccess)
                        MessageBox.Show(this, $"Trimmed locally, but couldn't send it back to {_settings.PairedPeerName}'s PC: {upError}", "Backtrack");
                }
            }
            else
            {
                string destPath = GetTrimmedDestinationPath(sourceFile.DirectoryName!, sourceFile.Name);
                File.Copy(tempOut, destPath, overwrite: false);
                File.Delete(tempOut);
                _ = RefreshGalleryCountAsync();
                var newFileInfo = new FileInfo(destPath);
                _currentPlayerFile = newFileInfo;
                OpenInPlayer(newFileInfo);
                _toastOverlay.ShowTrimSaved(destPath);

                if (remoteOrigin is (string relPath, _))
                {
                    int lastSlash = relPath.LastIndexOf('/');
                    string folderPrefix = lastSlash < 0 ? "" : relPath[..lastSlash];
                    string remoteDestRelPath = folderPrefix.Length == 0 ? Path.GetFileName(destPath) : $"{folderPrefix}/{Path.GetFileName(destPath)}";
                    _currentPlayerRemoteOrigin = (remoteDestRelPath, _settings.PairedPeerDeviceId ?? "");
                    TrimStatusText.Text = $"Sending to {_settings.PairedPeerName}'s PC...";
                    (bool upSuccess, string? upError, string? actualRemotePath) = await _pairing.UploadRemoteClipAsync(remoteDestRelPath, destPath, overwrite: false);
                    if (actualRemotePath is not null)
                        _currentPlayerRemoteOrigin = (actualRemotePath, _settings.PairedPeerDeviceId ?? "");
                    if (!upSuccess)
                        MessageBox.Show(this, $"Trimmed locally, but couldn't send the new clip to {_settings.PairedPeerName}'s PC: {upError}", "Backtrack");
                }
            }

            
            
            
            
            TrimPanel.Visibility = Visibility.Collapsed;
            PlayerTransportRow.Visibility = Visibility.Visible;
            MoveTransportControlsForTrim(intoTrimRow: false);
        }
        catch (Exception ex)
        {
            TrimStatusText.Text = "";
            MessageBox.Show(this, $"Trim failed: {ex.Message}", "Backtrack");
            OpenInPlayer(sourceFile);
        }
        finally
        {
            _isTrimming = false;
            TrimReplaceButton.IsEnabled = true;
            TrimSaveNewButton.IsEnabled = true;
        }
    }


        private async Task<(bool Success, string? Error, string? NewFileName, long Size)> TrimClipForRemoteAsync(string fullPath, double startSeconds, double endSeconds, bool replaceOriginal)
    {
        var file = new FileInfo(fullPath);
        var start = TimeSpan.FromSeconds(startSeconds);
        var end = TimeSpan.FromSeconds(endSeconds);
        string tempOut = Path.Combine(Path.GetTempPath(), $"cc_trim_{Guid.NewGuid():N}{file.Extension}");
        AppLog.Write($"[trim_clip] TrimClipForRemoteAsync: '{fullPath}' {start}-{end} replace={replaceOriginal}, exporting to '{tempOut}'");

        try
        {
            await Task.Run(() => ExportTrim(fullPath, tempOut, start, end));

            
            
            
            
            
            long tempOutSize = File.Exists(tempOut) ? new FileInfo(tempOut).Length : -1;
            AppLog.Write($"[trim_clip] ExportTrim finished -- tempOut {(tempOutSize < 0 ? "does not exist" : $"is {tempOutSize} bytes")}");
            if (tempOutSize <= 0)
                return (false, "The trim produced no output file (libvlc export failed silently) -- check this PC's own log around ExportTrim for details.", null, 0);

            if (replaceOriginal)
            {
                
                
                
                
                
                
                
                
                
                await CopyWithRetryAsync(tempOut, fullPath, overwrite: true);
                File.Delete(tempOut);
                long replacedSize = new FileInfo(fullPath).Length;
                AppLog.Write($"[trim_clip] replaced '{fullPath}' in place (size {replacedSize} bytes)");
                return (true, null, file.Name, replacedSize);
            }

            string newName = GetTrimmedFileName(file.Name, name => File.Exists(Path.Combine(file.DirectoryName!, name)));
            string destPath = Path.Combine(file.DirectoryName!, newName);
            await CopyWithRetryAsync(tempOut, destPath, overwrite: false);
            File.Delete(tempOut);
            long newSize = new FileInfo(destPath).Length;
            AppLog.Write($"[trim_clip] saved as new file '{destPath}' (size {newSize} bytes)");
            return (true, null, newName, newSize);
        }
        catch (Exception ex)
        {
            AppLog.WriteError("[trim_clip] TrimClipForRemoteAsync threw", ex);
            try { File.Delete(tempOut); } catch {  }
            return (false, ex.Message, null, 0);
        }
    }


        private static string GetTrimmedFileName(string originalFileName, Func<string, bool> fileExists)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        string ext = Path.GetExtension(originalFileName);

        string baseName = Regex.Replace(nameWithoutExt, @"(\s*\(trimmed(?:\s+\d+)?\)\s*(\(\d+\))?)+$", "", RegexOptions.IgnoreCase).TrimEnd();
        if (string.IsNullOrEmpty(baseName))
            baseName = nameWithoutExt;

        string candidateName = $"{baseName} (trimmed){ext}";
        if (!fileExists(candidateName))
            return candidateName;

        int i = 1;
        while (true)
        {
            candidateName = $"{baseName} (trimmed) ({i}){ext}";
            if (!fileExists(candidateName))
                return candidateName;
            i++;
        }
    }


    private static string GetTrimmedDestinationPath(string directory, string originalFileName) =>
        Path.Combine(directory, GetTrimmedFileName(originalFileName, name => File.Exists(Path.Combine(directory, name))));


    private void ExportTrim(string sourcePath, string destPath, TimeSpan start, TimeSpan end)
    {
        if (_libVlc is null)
            return;

        using var media = new LibVlc.Media(_libVlc, new Uri(sourcePath));
        media.AddOption($":start-time={start.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        media.AddOption($":stop-time={end.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        media.AddOption($":sout=#std{{access=file,mux=mp4,dst={destPath.Replace("\\", "/")}}}");
        media.AddOption(":sout-keep");

        using var exportPlayer = new LibVlc.MediaPlayer(media);
        using var done = new System.Threading.ManualResetEventSlim(false);
        bool encounteredError = false;

        exportPlayer.EndReached += (_, _) => done.Set();
        
        
        
        
        
        
        
        exportPlayer.EncounteredError += (_, _) =>
        {
            encounteredError = true;
            done.Set();
        };

        exportPlayer.Play();
        if (!done.Wait(TimeSpan.FromMinutes(10)))
            throw new TimeoutException("Trim export took too long.");
        exportPlayer.Stop();

        if (encounteredError)
            throw new InvalidOperationException("LibVLC reported an error during trim export.");

        
        
        
        
        
        if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
            throw new InvalidOperationException("Trim export produced no output file.");
    }


        private async void ManualPairButton_Click(object sender, RoutedEventArgs e)
    {
        string input = ManualPairAddressBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            ManualPairStatusText.Text = "Enter an address first.";
            return;
        }

        string address = input;
        int port = PairingService.DefaultPairingPort;
        int colonIndex = input.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(input[(colonIndex + 1)..], out int parsedPort))
        {
            address = input[..colonIndex];
            port = parsedPort;
        }

        var peer = new DiscoveredPeer(DeviceId: "manual", DeviceName: address, Address: address, PairingPort: port, LastSeen: DateTime.UtcNow);

        ManualPairButton.IsEnabled = false;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        try
        {
            PairingResult result = await _pairing.RequestPairingAsync(peer,
                onCodeReceived: code => Dispatcher.BeginInvoke(() => ManualPairStatusText.Text = $"Code: {code}, waiting for approval..."),
                cts.Token);

            switch (result.Outcome)
            {
                case PairingOutcome.Approved:
                    ManualPairStatusText.Text = "Paired!";
                    RefreshPairingStatusUi();
                    RenderDiscoveredDevices();
                    return;
                case PairingOutcome.Denied:
                    ManualPairStatusText.Text = string.IsNullOrEmpty(result.Error) ? "Request denied." : result.Error;
                    break;
                case PairingOutcome.TimedOut:
                    ManualPairStatusText.Text = "Request timed out. Check the address and that the other PC has \"Share my clips\" on.";
                    break;
                default:
                    ManualPairStatusText.Text = $"Failed: {result.Error}";
                    break;
            }
        }
        finally
        {
            ManualPairButton.IsEnabled = true;
        }
    }


    private void ApplyObsConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool remote = ObsRemoteToggle.IsChecked == true;
            if (remote && string.IsNullOrWhiteSpace(ObsHostBox.Text))
            {
                MessageBox.Show(this, "Enter the stream PC's address first.", "Backtrack");
                return;
            }

            _settings.ObsIsRemote = remote;
            _settings.ObsHost = ObsHostBox.Text.Trim();
            _settings.ObsPort = int.TryParse(ObsPortBox.Text.Trim(), out int p) ? p : 4455;
            _settings.ObsRemotePassword = ObsPasswordBox.Password;
            _settings.Save();

            BuffersSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            RecordingsSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            if (!remote)
            {
                _ = LoadBufferVisibilityUi();
                _ = LoadRecordFolderUi();
            }

            (string url, string? password, _serverEnabledAtStartup) = ResolveObsConnection();
            _obs.Reconfigure(url, password);
            _ = RefreshStatusAsync();
            _ = RefreshRemoteRowHotkeysAsync();
            RefreshRamDiskRemoteGating();
            RefreshPluginStatusRemoteGating();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't apply that OBS connection: {ex.Message}", "Backtrack");
        }
    }


    private async void ApplyRemoteRamDiskSettings_Click(object sender, RoutedEventArgs e)
    {
        string driveText = RemoteRamDiskDriveBox.Text.Trim().TrimEnd(':');
        if (driveText.Length != 1 || !char.IsLetter(driveText[0]))
        {
            MessageBox.Show(this, "Drive letter must be a single letter, e.g. R.", "Backtrack");
            return;
        }
        if (!int.TryParse(RemoteRamDiskSizeBox.Text.Trim(), out int sizeMb) || sizeMb < 256)
        {
            MessageBox.Show(this, "Size must be a number of megabytes, at least 256.", "Backtrack");
            return;
        }

        bool enabled = RemoteRamDiskToggle.IsChecked == true;
        (bool success, string? error) = await _pairing.SetRemoteRamDiskSettingsAsync(enabled, char.ToUpperInvariant(driveText[0]), sizeMb);
        if (!success)
        {
            MessageBox.Show(this, $"Couldn't apply on the transmitter PC: {error}", "Backtrack");
            return;
        }

        await LoadRemoteRamDiskUi();
    }


    private static void CreateOrUpdateStartupTask()
    {
        
        
        
        
        
        
        
        
        
        
        
        
        
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo(SchtasksPath,
            $"/Create /F /SC ONLOGON /RL LIMITED /TN \"{ScheduledTaskName}\" /TR \"\\\"{exePath}\\\"\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "schtasks.exe failed to create the startup task."
                : $"schtasks.exe failed to create the startup task: {stderr.Trim()}");
    }


    private static void DeleteStartupTask()
    {
        
        
        
        
        
        
        
        
        
        
        var psi = new ProcessStartInfo(SchtasksPath, $"/Delete /F /TN \"{ScheduledTaskName}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 && !stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "schtasks.exe failed to remove the startup task."
                : $"schtasks.exe failed to remove the startup task: {stderr.Trim()}");
    }

}
