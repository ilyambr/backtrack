using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backtrack.Core;

namespace Backtrack.UI.Dialogs;

public partial class GoogleDrivePickerWindow : Window
{
    private async Task NavigateToCurrentAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var current = _path.Last();
        SelectedFolderId = current.Id == "root" ? null : current.Id;
        SelectedFolderPath = string.Join(" / ", _path.Select(p => p.Name));
        CurrentTargetPathText.Text = SelectedFolderPath;

        if (!_hasConnected)
        {
            LoadingMainText.Text = "Connecting to Google Drive...";
            LoadingSubText.Text = "Check your browser if signing in for the first time.";
            LoadingSubText.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Visible;
            FolderListBox.ItemsSource = null;
        }
        else
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            FolderListBox.Opacity = 0.5;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        UploadButton.IsEnabled = false;

        try
        {
            List<DriveFolderItem> folders;

            if (RemotePairing != null)
            {
                string? folderId = current.Id == "root" ? null : current.Id;
                var remoteResult = await RemotePairing.RemoteDriveListFoldersAsync(folderId);
                if (ct.IsCancellationRequested)
                    return;

                if (remoteResult.NotLoggedIn)
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    FolderListBox.Opacity = 1.0;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorMessageText.Text = "The Main PC is not logged into Google Drive.";
                    UploadButton.IsEnabled = false;
                    return;
                }

                if (!remoteResult.Success)
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    FolderListBox.Opacity = 1.0;
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorMessageText.Text = $"Error: {remoteResult.Error ?? "Could not connect to Main PC."}";
                    UploadButton.IsEnabled = false;
                    return;
                }

                if (remoteResult.UserEmail != null && string.IsNullOrEmpty(_rawEmail))
                {
                    _rawEmail = remoteResult.UserEmail;
                    _ = UpdateUserAccountTextAsync();
                }

                folders = remoteResult.Folders ?? new List<DriveFolderItem>();
            }
            else
            {
                folders = await GoogleDriveService.Instance.ListFoldersAsync(current.Id, ct);
                if (ct.IsCancellationRequested)
                    return;
            }

            if (current.Id == "root" && RemotePairing == null)
            {
                var settings = AppSettings.Load();
                string? activeEmail = _rawEmail;
                if (string.IsNullOrEmpty(activeEmail))
                {
                    try { activeEmail = await GoogleDriveService.Instance.GetCurrentUserEmailAsync(ct); } catch { }
                    _rawEmail = activeEmail;
                }

                // Associate legacy pinned folders with active account so they are not lost
                if (!string.IsNullOrEmpty(activeEmail))
                {
                    bool migrated = false;
                    foreach (var pinned in settings.PinnedDriveFolders)
                    {
                        if (string.IsNullOrEmpty(pinned.AccountEmail))
                        {
                            pinned.AccountEmail = activeEmail;
                            migrated = true;
                        }
                    }
                    if (migrated)
                    {
                        settings.Save();
                    }
                }

                // Only show pinned folders belonging to the active account
                var visiblePinned = settings.PinnedDriveFolders.Where(p =>
                    string.IsNullOrEmpty(activeEmail) ||
                    string.IsNullOrEmpty(p.AccountEmail) ||
                    string.Equals(p.AccountEmail, activeEmail, StringComparison.OrdinalIgnoreCase));

                foreach (var pinned in visiblePinned)
                {
                    if (!folders.Any(f => f.Id == pinned.Id))
                    {
                        folders.Add(new DriveFolderItem(pinned.Id, pinned.Name, "root", IsLinked: true));
                    }
                }
            }

            _hasConnected = true;
            _currentFolders = folders;
            LoginPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            FolderListBox.Visibility = Visibility.Visible;
            FolderListBox.Opacity = 1.0;
            NewFolderButton.IsEnabled = true;
            LinkFolderButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;

            if (folders.Count == 0)
            {
                FolderListBox.ItemsSource = null;
                EmptyPanel.Visibility = Visibility.Visible;
            }
            else
            {
                FolderListBox.ItemsSource = folders;
            }
            UploadButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            FolderListBox.Opacity = 1.0;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = $"Error: {ex.Message}";
            UploadButton.IsEnabled = false;
        }
    }

    private void UpdateBreadcrumbsUI()
    {
        BreadcrumbsPanel.Children.Clear();

        for (int i = 0; i < _path.Count; i++)
        {
            int index = i;
            var item = _path[i];

            if (i > 0)
            {
                var sep = new TextBlock
                {
                    Text = " / ",
                    Foreground = (Brush)FindResource("Text2"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                BreadcrumbsPanel.Children.Add(sep);
            }

            var btn = new Button
            {
                Content = item.Name,
                Style = TryFindResource("BareButton") as Style ?? TryFindResource("BareIconButton") as Style,
                Foreground = (i == _path.Count - 1) ? (Brush)FindResource("Text0") : (Brush)FindResource("Text1"),
                FontWeight = (i == _path.Count - 1) ? FontWeights.SemiBold : FontWeights.Normal,
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };

            btn.Click += async (_, _) =>
            {
                if (index < _path.Count - 1)
                {
                    _path.RemoveRange(index + 1, _path.Count - (index + 1));
                    UpdateBreadcrumbsUI();
                    await NavigateToCurrentAsync();
                }
            };

            BreadcrumbsPanel.Children.Add(btn);
        }
    }

    private async void FolderListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderListBox.SelectedItem is DriveFolderItem folder)
        {
            _path.Add(new BreadcrumbItem(folder.Id, folder.Name));
            UpdateBreadcrumbsUI();
            await NavigateToCurrentAsync();
        }
    }

    private void FolderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderListBox.SelectedItem is DriveFolderItem folder)
        {
            CurrentTargetPathText.Text = $"{SelectedFolderPath} / {folder.Name}";
        }
        else
        {
            CurrentTargetPathText.Text = SelectedFolderPath;
        }
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationBar.Visibility = Visibility.Collapsed;
        LinkFolderRow.Visibility = Visibility.Collapsed;
        RenameFolderRow.Visibility = Visibility.Collapsed;
        NewFolderRow.Visibility = Visibility.Visible;
        NewFolderInput.Text = "";
        NewFolderInput.Focus();
    }

    private void CreateFolderCancel_Click(object sender, RoutedEventArgs e)
    {
        NewFolderRow.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;
        NewFolderInput.Text = "";
    }

    private async void CreateFolderCommit_Click(object sender, RoutedEventArgs e)
    {
        await CommitCreateFolderAsync();
    }

    private async void NewFolderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitCreateFolderAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CreateFolderCancel_Click(sender, e);
        }
    }

    private async Task CommitCreateFolderAsync()
    {
        string name = NewFolderInput.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            CreateFolderCancel_Click(this, new RoutedEventArgs());
            return;
        }

        NewFolderRow.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;

        try
        {
            var current = _path.Last();
            string? parentId = current.Id == "root" ? null : current.Id;

            if (RemotePairing != null)
            {
                var result = await RemotePairing.RemoteDriveCreateFolderAsync(name, parentId);
                if (!result.Success)
                {
                    MessageBox.Show(this, $"Failed to create folder: {result.Error}", "Google Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                var created = await GoogleDriveService.Instance.CreateFolderAsync(name, current.Id);
                if (created == null)
                {
                    MessageBox.Show(this, "Failed to create folder.", "Google Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            await NavigateToCurrentAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to create folder: {ex.Message}", "Google Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
            await NavigateToCurrentAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (RemotePairing == null && !GoogleDriveService.Instance.HasStoredCredentials())
        {
            ShowLoginRequired();
            return;
        }
        await NavigateToCurrentAsync();
    }

    private static void OpenFolderUrlInBrowser(string? folderId)
    {
        string url = string.IsNullOrEmpty(folderId) || folderId == "root"
            ? "https://drive.google.com/drive/my-drive"
            : $"https://drive.google.com/drive/folders/{folderId}";

        AppLog.Write($"[GoogleDrive] Opening URL in browser: {url}");

        bool opened = false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            opened = true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[GoogleDrive] Process.Start with UseShellExecute failed: {ex.Message}");
        }

        if (!opened)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{url}\"") { CreateNoWindow = true, UseShellExecute = false });
                opened = true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"[GoogleDrive] cmd start fallback failed: {ex.Message}");
            }
        }

        if (!opened)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Start-Process '{url}'\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                AppLog.Write($"[GoogleDrive] powershell Start-Process fallback failed: {ex.Message}");
            }
        }
    }

    private void FolderListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != FolderListBox)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem lbi && lbi.DataContext is DriveFolderItem item)
        {
            FolderListBox.SelectedItem = item;
        }
    }

    private void FolderItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DriveFolderItem item)
        {
            FolderListBox.SelectedItem = item;
        }
    }

    private void OpenFolderInBrowser_Click(object sender, RoutedEventArgs e)
    {
        DriveFolderItem? targetFolder = null;

        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is DriveFolderItem item)
            {
                targetFolder = item;
            }
            else if (menuItem.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is DriveFolderItem feItem)
            {
                targetFolder = feItem;
            }
        }

        targetFolder ??= FolderListBox.SelectedItem as DriveFolderItem;

        if (targetFolder != null)
        {
            OpenFolderUrlInBrowser(targetFolder.Id);
        }
    }

    private void LinkFolderButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationBar.Visibility = Visibility.Collapsed;
        NewFolderRow.Visibility = Visibility.Collapsed;
        RenameFolderRow.Visibility = Visibility.Collapsed;
        LinkFolderRow.Visibility = Visibility.Visible;
        LinkFolderNameInput.Text = "";
        LinkFolderInput.Text = "";
        LinkFolderInput.Focus();
    }

    private void LinkFolderCancel_Click(object sender, RoutedEventArgs e)
    {
        LinkFolderRow.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;
        LinkFolderNameInput.Text = "";
        LinkFolderInput.Text = "";
    }

    private async void LinkFolderCommit_Click(object sender, RoutedEventArgs e)
    {
        await CommitLinkFolderAsync();
    }

    private async void LinkFolderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitLinkFolderAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            LinkFolderCancel_Click(sender, e);
        }
    }

    private async Task CommitLinkFolderAsync()
    {
        string customName = LinkFolderNameInput.Text.Trim();
        string input = LinkFolderInput.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            LinkFolderCancel_Click(this, new RoutedEventArgs());
            return;
        }

        string? folderId = ExtractDriveFolderId(input);
        if (string.IsNullOrEmpty(folderId))
        {
            MessageBox.Show(this, "Please enter a valid Google Drive folder URL or ID.", "Link Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LinkFolderRow.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;

        string folderName = !string.IsNullOrEmpty(customName) ? customName : "Linked Folder";
        if (string.IsNullOrEmpty(customName))
        {
            try
            {
                var fetched = await GoogleDriveService.Instance.GetFolderNameAsync(folderId);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    folderName = fetched;
                }
                else
                {
                    string preview = folderId.Length > 8 ? folderId.Substring(0, 8) : folderId;
                    folderName = $"Linked Folder ({preview}...)";
                }
            }
            catch
            {
                string preview = folderId.Length > 8 ? folderId.Substring(0, 8) : folderId;
                folderName = $"Linked Folder ({preview}...)";
            }
        }

        string? activeEmail = _rawEmail;
        if (string.IsNullOrEmpty(activeEmail) && RemotePairing == null)
        {
            try { activeEmail = await GoogleDriveService.Instance.GetCurrentUserEmailAsync(); } catch { }
            _rawEmail = activeEmail;
        }

        var settings = AppSettings.Load();
        var existing = settings.PinnedDriveFolders.FirstOrDefault(p =>
            p.Id == folderId &&
            (string.IsNullOrEmpty(p.AccountEmail) || string.IsNullOrEmpty(activeEmail) || string.Equals(p.AccountEmail, activeEmail, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            existing.Name = folderName;
            if (!string.IsNullOrEmpty(activeEmail))
                existing.AccountEmail = activeEmail;
        }
        else
        {
            settings.PinnedDriveFolders.Add(new PinnedDriveFolder
            {
                Id = folderId,
                Name = folderName,
                AccountEmail = activeEmail
            });
        }
        settings.Save();

        await NavigateToCurrentAsync();
    }

    private string? _renamingFolderId;

    private void RenameLinkedFolder_Click(object sender, RoutedEventArgs e)
    {
        DriveFolderItem? targetFolder = null;
        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is DriveFolderItem item)
                targetFolder = item;
            else if (menuItem.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is DriveFolderItem feItem)
                targetFolder = feItem;
        }
        targetFolder ??= FolderListBox.SelectedItem as DriveFolderItem;

        if (targetFolder != null && targetFolder.IsLinked)
        {
            _renamingFolderId = targetFolder.Id;
            NavigationBar.Visibility = Visibility.Collapsed;
            NewFolderRow.Visibility = Visibility.Collapsed;
            LinkFolderRow.Visibility = Visibility.Collapsed;
            RenameFolderRow.Visibility = Visibility.Visible;
            RenameFolderInput.Text = targetFolder.Name;
            RenameFolderInput.Focus();
            RenameFolderInput.SelectAll();
        }
    }

    private void RenameFolderCancel_Click(object sender, RoutedEventArgs e)
    {
        RenameFolderRow.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;
        _renamingFolderId = null;
        RenameFolderInput.Text = "";
    }

    private void RenameFolderCommit_Click(object sender, RoutedEventArgs e)
    {
        CommitRenameFolder();
    }

    private void RenameFolderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRenameFolder();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            RenameFolderCancel_Click(sender, e);
        }
    }

    private void CommitRenameFolder()
    {
        string newName = RenameFolderInput.Text.Trim();
        if (string.IsNullOrEmpty(newName) || string.IsNullOrEmpty(_renamingFolderId))
        {
            RenameFolderCancel_Click(this, new RoutedEventArgs());
            return;
        }

        var settings = AppSettings.Load();
        var target = settings.PinnedDriveFolders.FirstOrDefault(p =>
            p.Id == _renamingFolderId &&
            (string.IsNullOrEmpty(p.AccountEmail) || string.IsNullOrEmpty(_rawEmail) || string.Equals(p.AccountEmail, _rawEmail, StringComparison.OrdinalIgnoreCase)));

        if (target != null)
        {
            target.Name = newName;
            settings.Save();
        }

        RenameFolderRow.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Visible;
        _renamingFolderId = null;
        _ = NavigateToCurrentAsync();
    }

    public static string? ExtractDriveFolderId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(input, @"/folders/([a-zA-Z0-9_-]+)");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(input, @"[?&]id=([a-zA-Z0-9_-]+)");
        if (m.Success) return m.Groups[1].Value;
        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{15,}$"))
            return input;
        return null;
    }

    private async void UnlinkFolder_Click(object sender, RoutedEventArgs e)
    {
        DriveFolderItem? targetFolder = null;

        if (sender is MenuItem menuItem)
        {
            if (menuItem.DataContext is DriveFolderItem item)
            {
                targetFolder = item;
            }
            else if (menuItem.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is DriveFolderItem feItem)
            {
                targetFolder = feItem;
            }
        }

        targetFolder ??= FolderListBox.SelectedItem as DriveFolderItem;

        if (targetFolder != null && targetFolder.IsLinked)
        {
            var settings = AppSettings.Load();
            settings.PinnedDriveFolders.RemoveAll(p =>
                p.Id == targetFolder.Id &&
                (string.IsNullOrEmpty(p.AccountEmail) || string.IsNullOrEmpty(_rawEmail) || string.Equals(p.AccountEmail, _rawEmail, StringComparison.OrdinalIgnoreCase)));
            settings.Save();

            await NavigateToCurrentAsync();
        }
    }
}
