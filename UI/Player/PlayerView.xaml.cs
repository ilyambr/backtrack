using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Backtrack.UI.Player;

public partial class PlayerView : UserControl
{
    public PlayerView()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void BackToGallery_Click(object sender, RoutedEventArgs e) => Main?.BackToGallery_Click(sender, e);
    private void PlayerTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Main?.PlayerTitle_MouseLeftButtonDown(sender, e);
    private void PlayerMenuButton_Click(object sender, RoutedEventArgs e) => Main?.PlayerMenuButton_Click(sender, e as MouseButtonEventArgs ?? new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => Main?.PlayPauseButton_Click(sender, e);
    private void PlayerSeekTrack_MouseDown(object sender, MouseButtonEventArgs e) => Main?.PlayerSeekTrack_MouseDown(sender, e);
    private void PlayerSeekTrack_MouseUp(object sender, MouseButtonEventArgs e) => Main?.PlayerSeekTrack_MouseUp(sender, e);
    private void PlayerSeekTrack_MouseMove(object sender, MouseEventArgs e) => Main?.PlayerSeekTrack_MouseMove(sender, e);
    private void PlayerSeekTrack_MouseEnter(object sender, MouseEventArgs e) => Main?.PlayerSeekTrack_MouseEnter(sender, e);
    private void PlayerSeekTrack_MouseLeave(object sender, MouseEventArgs e) => Main?.PlayerSeekTrack_MouseLeave(sender, e);
    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.AudioTrackCombo_SelectionChanged(sender, e);
    private void PlayerSpeedButton_Click(object sender, RoutedEventArgs e) => Main?.PlayerSpeedButton_Click(sender, e);
    private void PlayerVolumeButton_Click(object sender, RoutedEventArgs e) => Main?.PlayerVolumeButton_Click(sender, e);
    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Main?.PlayerVolumeSlider_ValueChanged(sender, e);
    private void PlayerVolumeArea_MouseEnter(object sender, MouseEventArgs e) => Main?.PlayerVolumeArea_MouseEnter(sender, e);
    private void PlayerVolumeArea_MouseLeave(object sender, MouseEventArgs e) => Main?.PlayerVolumeArea_MouseLeave(sender, e);
    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => Main?.ToggleFullscreen_Click(sender, e);
    private void PlayerStarButton_Click(object sender, RoutedEventArgs e) => Main?.PlayerStarButton_Click(sender, e);
    private void PlayerFolder_Click(object sender, RoutedEventArgs e) => Main?.PlayerFolder_Click(sender, e);
    private void PlayerRename_Click(object sender, RoutedEventArgs e) => Main?.PlayerRename_Click(sender, e);
    private void PlayerTrim_Click(object sender, RoutedEventArgs e) => Main?.PlayerTrim_Click(sender, e);
    private void PlayerCompress_Click(object sender, RoutedEventArgs e) => Main?.PlayerCompress_Click(sender, e);
    private void PlayerBookmarks_Click(object sender, RoutedEventArgs e) => Main?.PlayerBookmarks_Click(sender, e);
    private void PlayerDelete_Click(object sender, RoutedEventArgs e) => Main?.PlayerDelete_Click(sender, e);
    private void TrimTimelineTrack_MouseDown(object sender, MouseButtonEventArgs e) => Main?.TrimTimelineTrack_MouseDown(sender, e);
    private void TrimTimelineTrack_MouseMove(object sender, MouseEventArgs e) => Main?.TrimTimelineTrack_MouseMove(sender, e);
    private void TrimTimelineTrack_MouseUp(object sender, MouseButtonEventArgs e) => Main?.TrimTimelineTrack_MouseUp(sender, e);
    private void TrimTimelineTrack_SizeChanged(object sender, SizeChangedEventArgs e) => Main?.TrimTimelineTrack_SizeChanged(sender, e);
    private void TrimStartHandle_MouseDown(object sender, MouseButtonEventArgs e) => Main?.TrimStartHandle_MouseDown(sender, e);
    private void TrimEndHandle_MouseDown(object sender, MouseButtonEventArgs e) => Main?.TrimEndHandle_MouseDown(sender, e);
    private void PreviewLoopButton_Click(object sender, RoutedEventArgs e) => Main?.PreviewLoopButton_Click(sender, e);
    private void TrimReplace_Click(object sender, RoutedEventArgs e) => Main?.TrimReplace_Click(sender, e);
    private void TrimSaveNew_Click(object sender, RoutedEventArgs e) => Main?.TrimSaveNew_Click(sender, e);
    private void TrimCancel_Click(object sender, RoutedEventArgs e) => Main?.TrimCancel_Click(sender, e);
    private void CloseCompressPopup_Click(object sender, RoutedEventArgs e) => Main?.CloseCompressPopup_Click(sender, e);
    private void CompressPreset_Click(object sender, RoutedEventArgs e) => Main?.CompressPreset_Click(sender, e);
    private void CompressCustomButton_Click(object sender, RoutedEventArgs e) => Main?.CompressCustomButton_Click(sender, e);
    private void CompressReplace_Click(object sender, RoutedEventArgs e) => Main?.CompressReplace_Click(sender, e);
    private void CompressSaveNew_Click(object sender, RoutedEventArgs e) => Main?.CompressSaveNew_Click(sender, e);
    private void CloseBookmarkPopup_Click(object sender, RoutedEventArgs e) => Main?.CloseBookmarkPopup_Click(sender, e);
    private void AddBookmarkDialogButton_Click(object sender, RoutedEventArgs e) => Main?.AddBookmarkDialogButton_Click(sender, e);
    private void PlayerControl_MouseMove(object sender, MouseEventArgs e) => Main?.NotifyFullscreenActivity();
    private void PlayerControl_MouseDown(object sender, MouseButtonEventArgs e) => Main?.NotifyFullscreenActivity();
}
