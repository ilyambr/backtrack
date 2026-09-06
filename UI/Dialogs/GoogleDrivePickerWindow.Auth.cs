using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Backtrack.Core;

namespace Backtrack.UI.Dialogs;

public partial class GoogleDrivePickerWindow : Window
{
    private void OnObsRecordingStateChanged(bool active, string? path)
    {
        Dispatcher.BeginInvoke(async () => await UpdateUserAccountTextAsync());
    }

    private void OnObsStreamingStateChanged(bool active)
    {
        Dispatcher.BeginInvoke(async () => await UpdateUserAccountTextAsync());
    }

    private async Task UpdateUserAccountTextAsync()
    {
        if (string.IsNullOrEmpty(_rawEmail))
        {
            UserAccountText.Text = "";
            UserAccountText.ToolTip = null;
            return;
        }

        bool isRecordingOrStreaming = false;
        if (Obs != null)
        {
            try
            {
                isRecordingOrStreaming = await Obs.IsRecordingOrStreamingAsync();
            }
            catch { }
        }

        Dispatcher.Invoke(() =>
        {
            bool alwaysRedact = AppSettings.Load().AlwaysRedactDriveEmail;
            if (isRecordingOrStreaming || alwaysRedact)
            {
                string masked = GoogleDriveService.MaskEmail(_rawEmail);
                UserAccountText.Text = masked;
                UserAccountText.ToolTip = $"Signed in as {masked}" + (alwaysRedact ? " (Redacted)" : " (Streamer Mode)");
            }
            else
            {
                UserAccountText.Text = _rawEmail;
                UserAccountText.ToolTip = $"Signed in as {_rawEmail}";
            }
        });
    }

    private void ShowLoginRequired()
    {
        _hasConnected = false;
        _rawEmail = null;
        UserAccountText.Text = "";
        UserAccountText.ToolTip = null;

        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        FolderListBox.Visibility = Visibility.Collapsed;
        FolderListBox.ItemsSource = null;

        NewFolderButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        UploadButton.IsEnabled = false;

        LoginPanel.Visibility = Visibility.Visible;
    }

    private void LoadingCancelButton_Click(object sender, RoutedEventArgs e)
    {
        GoogleDriveService.Instance.CancelAuthentication();
        ShowLoginRequired();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        LoadingMainText.Text = "Connecting to Google Drive...";
        LoadingSubText.Text = "Check your browser to complete sign in.";
        LoadingSubText.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Visible;

        bool authed = await GoogleDriveService.Instance.AuthenticateAsync();
        if (authed)
        {
            _hasConnected = true;
            FolderListBox.Visibility = Visibility.Visible;
            NewFolderButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            _ = LoadUserInfoAsync();
            await NavigateToCurrentAsync();
        }
        else
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (GoogleDriveService.Instance.LastAuthError == "Sign in was cancelled.")
            {
                ShowLoginRequired();
            }
            else
            {
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorMessageText.Text = GoogleDriveService.Instance.LastAuthError ?? "Sign in was cancelled or failed. Please try again.";
            }
        }
    }

    private async Task LoadUserInfoAsync()
    {
        try
        {
            string? email;
            if (RemotePairing != null)
            {
                var result = await RemotePairing.RemoteDriveListFoldersAsync("root");
                email = result.UserEmail;
                if (result.NotLoggedIn)
                    email = null;
            }
            else
            {
                email = await GoogleDriveService.Instance.GetCurrentUserEmailAsync();
            }
            _rawEmail = email;
            await UpdateUserAccountTextAsync();
        }
        catch { }
    }

    private async void ReauthButton_Click(object sender, RoutedEventArgs e)
    {
        await GoogleDriveService.Instance.SignOutAsync();
        _path.Clear();
        _path.Add(new BreadcrumbItem("root", "My Drive"));
        UpdateBreadcrumbsUI();
        ShowLoginRequired();
    }

    private void UserAccountText_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_rawEmail))
            e.Handled = true;
    }

    private void UserAccountText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_rawEmail) && UserAccountText.ContextMenu != null)
        {
            UserAccountText.ContextMenu.PlacementTarget = UserAccountText;
            UserAccountText.ContextMenu.IsOpen = true;
        }
    }

    private async void SignOutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await GoogleDriveService.Instance.SignOutAsync();
        _path.Clear();
        _path.Add(new BreadcrumbItem("root", "My Drive"));
        UpdateBreadcrumbsUI();
        ShowLoginRequired();
    }
}
