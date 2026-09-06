using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Backtrack.Core;
using Backtrack.Obs;
using Backtrack.Pairing;

namespace Backtrack.UI.Dialogs;

public partial class GoogleDrivePickerWindow : Window
{
    private sealed record BreadcrumbItem(string Id, string Name);

    private readonly List<BreadcrumbItem> _path = new();
    private List<DriveFolderItem> _currentFolders = new();
    private CancellationTokenSource? _loadCts;
    private bool _hasConnected = false;
    private string? _rawEmail;

    public ObsService? Obs { get; set; }

    // When set, all Drive operations are routed through this pairing service to the Main PC
    public PairingService? RemotePairing { get; set; }

    public string? SelectedFolderId { get; private set; }
    public string SelectedFolderPath { get; private set; } = "My Drive";
    public bool DestinationConfirmed { get; private set; }

    public Action<string?, string>? OnDestinationConfirmed { get; set; }
    public Action? OnPickerClosed { get; set; }

    public GoogleDrivePickerWindow()
    {
        InitializeComponent();
        _path.Add(new BreadcrumbItem("root", "My Drive"));

        // Only check local credentials if not using remote Drive
        if (RemotePairing == null && !GoogleDriveService.Instance.HasStoredCredentials())
        {
            ShowLoginRequired();
        }

        Loaded += async (_, _) =>
        {
            if (Obs != null)
            {
                Obs.RecordingStateChanged += OnObsRecordingStateChanged;
                Obs.StreamingStateChanged += OnObsStreamingStateChanged;
            }
            await LoadInitialAsync();
        };
        Closed += (_, _) =>
        {
            if (Obs != null)
            {
                Obs.RecordingStateChanged -= OnObsRecordingStateChanged;
                Obs.StreamingStateChanged -= OnObsStreamingStateChanged;
            }
            if (!_hasConnected)
            {
                GoogleDriveService.Instance.CancelAuthentication();
            }
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (NewFolderRow.Visibility == Visibility.Visible)
            {
                CreateFolderCancel_Click(this, new RoutedEventArgs());
            }
            else
            {
                CloseButton_Click(this, new RoutedEventArgs());
            }
        }
    }

    private async Task LoadInitialAsync()
    {
        UpdateBreadcrumbsUI();

        if (RemotePairing != null)
        {
            // Remote mode: just navigate, credentials check happens inside NavigateToCurrentAsync
            _ = LoadUserInfoAsync();
            await NavigateToCurrentAsync();
            return;
        }

        if (!GoogleDriveService.Instance.HasStoredCredentials())
        {
            ShowLoginRequired();
            return;
        }

        _ = LoadUserInfoAsync();
        await NavigateToCurrentAsync();
    }

    private void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderListBox.SelectedItem is DriveFolderItem item)
        {
            SelectedFolderId = item.Id;
            SelectedFolderPath = $"{SelectedFolderPath} / {item.Name}";
        }

        DestinationConfirmed = true;
        OnDestinationConfirmed?.Invoke(SelectedFolderId, SelectedFolderPath);
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DestinationConfirmed = false;
        OnPickerClosed?.Invoke();
        Close();
    }
}
