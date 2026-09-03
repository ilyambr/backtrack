using System.Windows;
using System.Windows.Controls;

namespace Backtrack.UI.Settings.Sections;

public partial class SettingsClipsSection : UserControl
{
    public SettingsClipsSection()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void ChangeClipsFolder_Click(object sender, RoutedEventArgs e) => Main?.ChangeClipsFolder_Click(sender, e);
    private void StorageLimitToggle_Click(object sender, RoutedEventArgs e) => Main?.StorageLimitToggle_Click(sender, e);
    private void ApplyStorageLimit_Click(object sender, RoutedEventArgs e) => Main?.ApplyStorageLimit_Click(sender, e);
    private void AutoDeleteOldClipsToggle_Click(object sender, RoutedEventArgs e) => Main?.AutoDeleteOldClipsToggle_Click(sender, e);
    private void ApplyAutoDeleteOldClips_Click(object sender, RoutedEventArgs e) => Main?.ApplyAutoDeleteOldClips_Click(sender, e);
    private void BufferDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Main?.BufferDurationSlider_ValueChanged(sender, e);
    private void ApplyBufferDuration_Click(object sender, RoutedEventArgs e) => Main?.ApplyBufferDuration_Click(sender, e);
    private void DefaultAudioTrackSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.DefaultAudioTrackSelector_SelectionChanged(sender, e);
}
