using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Backtrack.UI.Settings.Sections;

public partial class SettingsGeneralSection : UserControl
{
    public SettingsGeneralSection()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void DisplaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Main?.DisplaySelector_SelectionChanged(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseLeftButtonDown(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseMove(object sender, MouseEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseMove(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseLeftButtonUp(sender, e);
    private void ThemeSwatchesScroll_PreviewMouseLeave(object sender, MouseEventArgs e) => Main?.ThemeSwatchesScroll_PreviewMouseLeave(sender, e);
    private void OpenThemesFolderButton_Click(object sender, RoutedEventArgs e) => Main?.OpenThemesFolderButton_Click(sender, e);
    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => Main?.CheckUpdatesButton_Click(sender, e);
    private void DisableBacktrackAutoUpdateToggle_Click(object sender, RoutedEventArgs e) => Main?.DisableBacktrackAutoUpdateToggle_Click(sender, e);
    private void DisablePluginAutoUpdateToggle_Click(object sender, RoutedEventArgs e) => Main?.DisablePluginAutoUpdateToggle_Click(sender, e);
    private void LaunchWithWindowsToggle_Click(object sender, RoutedEventArgs e) => Main?.LaunchWithWindowsToggle_Click(sender, e);
    private void ShowDisclaimerToggle_Click(object sender, RoutedEventArgs e) => Main?.ShowDisclaimerToggle_Click(sender, e);
    private void DisableAudioCuesToggle_Click(object sender, RoutedEventArgs e) => Main?.DisableAudioCuesToggle_Click(sender, e);
    private void DisableAudioCuesRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => Main?.DisableAudioCuesRow_MouseRightButtonUp(sender, e);
    private void AudioCueVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Main?.AudioCueVolumeSlider_ValueChanged(sender, e);
    private void QuitApp_Click(object sender, RoutedEventArgs e) => Main?.QuitApp_Click(sender, e);
}
