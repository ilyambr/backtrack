using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;

namespace Backtrack.UI.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void BackToIdle_Click(object sender, RoutedEventArgs e) => Main?.BackToIdle_Click(sender, e);
    private void SettingsScrollHost_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Main?.SettingsScrollHost_PreviewMouseDown(sender, e);
    private void SettingsScrollHost_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Main?.SettingsScrollHost_PreviewMouseUp(sender, e);

    // --- Forwarded General Section Controls ---
    public ComboBox DisplaySelector => GeneralSection.DisplaySelector;
    public ScrollViewer ThemeSwatchesScroll => GeneralSection.ThemeSwatchesScroll;
    public StackPanel ThemeSwatchesPanel => GeneralSection.ThemeSwatchesPanel;
    public StackPanel ThemeSwatchLabelsPanel => GeneralSection.ThemeSwatchLabelsPanel;
    public Button CheckUpdatesButton => GeneralSection.CheckUpdatesButton;
    public Ellipse BacktrackStatusDot => GeneralSection.BacktrackStatusDot;
    public TextBlock BacktrackVersionText => GeneralSection.BacktrackVersionText;
    public StackPanel LocalPluginStatusRows => GeneralSection.LocalPluginStatusRows;
    public Ellipse ReplaySliderStatusDot => GeneralSection.ReplaySliderStatusDot;
    public TextBlock ReplaySliderVersionText => GeneralSection.ReplaySliderVersionText;
    public Ellipse SourceRecordStatusDot => GeneralSection.SourceRecordStatusDot;
    public TextBlock SourceRecordVersionText => GeneralSection.SourceRecordVersionText;
    public TextBlock PluginStatusRemoteNotice => GeneralSection.PluginStatusRemoteNotice;
    public ToggleButton DisableBacktrackAutoUpdateToggle => GeneralSection.DisableBacktrackAutoUpdateToggle;
    public ToggleButton DisablePluginAutoUpdateToggle => GeneralSection.DisablePluginAutoUpdateToggle;
    public ToggleButton LaunchWithWindowsToggle => GeneralSection.LaunchWithWindowsToggle;
    public ToggleButton ShowDisclaimerToggle => GeneralSection.ShowDisclaimerToggle;
    public Border DisableAudioCuesRow => GeneralSection.DisableAudioCuesRow;
    public TextBlock DisableAudioCuesSubtext => GeneralSection.DisableAudioCuesSubtext;
    public ToggleButton DisableAudioCuesToggle => GeneralSection.DisableAudioCuesToggle;
    public Border AudioCueVolumeRow => GeneralSection.AudioCueVolumeRow;
    public TextBlock AudioCueVolumeText => GeneralSection.AudioCueVolumeText;
    public Slider AudioCueVolumeSlider => GeneralSection.AudioCueVolumeSlider;

    // --- Forwarded Clips Section Controls ---
    public TextBlock ClipsFolderText => ClipsSection.ClipsFolderText;
    public TextBlock StorageLimitStatusText => ClipsSection.StorageLimitStatusText;
    public ToggleButton StorageLimitToggle => ClipsSection.StorageLimitToggle;
    public StackPanel StorageLimitFields => ClipsSection.StorageLimitFields;
    public TextBox StorageLimitGbBox => ClipsSection.StorageLimitGbBox;
    public ToggleButton AutoDeleteOldClipsToggle => ClipsSection.AutoDeleteOldClipsToggle;
    public StackPanel AutoDeleteOldClipsFields => ClipsSection.AutoDeleteOldClipsFields;
    public TextBox AutoDeleteOldClipsDaysBox => ClipsSection.AutoDeleteOldClipsDaysBox;
    public TextBlock BufferDurationValueText => ClipsSection.BufferDurationValueText;
    public Slider BufferDurationSlider => ClipsSection.BufferDurationSlider;
    public TextBlock BufferDurationWarningText => ClipsSection.BufferDurationWarningText;
    public ComboBox DefaultAudioTrackSelector => ClipsSection.DefaultAudioTrackSelector;

    // --- Forwarded Overlay Section Controls ---
    public Button HotkeyCaptureButton => OverlaySection.HotkeyCaptureButton;
    public Button CancelRecordHotkeyCaptureButton => OverlaySection.CancelRecordHotkeyCaptureButton;
    public Button BookmarkHotkeyCaptureButton => OverlaySection.BookmarkHotkeyCaptureButton;
    public ToggleButton ShowRecentClipsToggle => OverlaySection.ShowRecentClipsToggle;
    public ToggleButton ShowStatusIndicatorToggle => OverlaySection.ShowStatusIndicatorToggle;
    public ComboBox StatusIndicatorOrientationSelector => OverlaySection.StatusIndicatorOrientationSelector;
    public ComboBox StatusIndicatorLocationSelector => OverlaySection.StatusIndicatorLocationSelector;
    public Border StatusIndicatorPreviewBorder => OverlaySection.StatusIndicatorPreviewBorder;
    public StackPanel StatusIndicatorPreviewPanel => OverlaySection.StatusIndicatorPreviewPanel;

    // --- Forwarded OBS Section Controls ---
    public ToggleButton ObsRemoteToggle => ObsSection.ObsRemoteToggle;
    public StackPanel ObsRemoteFields => ObsSection.ObsRemoteFields;
    public TextBox ObsHostBox => ObsSection.ObsHostBox;
    public TextBox ObsPortBox => ObsSection.ObsPortBox;
    public PasswordBox ObsPasswordBox => ObsSection.ObsPasswordBox;
    public StackPanel RemotePluginSection => ObsSection.RemotePluginSection;
    public TextBlock RemotePluginStatusText => ObsSection.RemotePluginStatusText;
    public Button CheckRemotePluginsButton => ObsSection.CheckRemotePluginsButton;
    public StackPanel RemotePluginRows => ObsSection.RemotePluginRows;
    public Ellipse RemoteReplaySliderStatusDot => ObsSection.RemoteReplaySliderStatusDot;
    public TextBlock RemoteReplaySliderVersionText => ObsSection.RemoteReplaySliderVersionText;
    public Ellipse RemoteSourceRecordStatusDot => ObsSection.RemoteSourceRecordStatusDot;
    public TextBlock RemoteSourceRecordVersionText => ObsSection.RemoteSourceRecordVersionText;
    public StackPanel BuffersSection => ObsSection.BuffersSection;
    public StackPanel BufferVisibilityPanel => ObsSection.BufferVisibilityPanel;
    public StackPanel RecordingsSection => ObsSection.RecordingsSection;
    public StackPanel RecordFolderPanel => ObsSection.RecordFolderPanel;
    public TextBlock ShareClipsStatusText => ObsSection.ShareClipsStatusText;
    public ToggleButton ShareClipsToggle => ObsSection.ShareClipsToggle;
    public Border AuthorizedDeviceRow => ObsSection.AuthorizedDeviceRow;
    public TextBlock AuthorizedDeviceNameText => ObsSection.AuthorizedDeviceNameText;
    public Button DeauthorizeButton => ObsSection.DeauthorizeButton;
    public TextBlock PairingStatusText => ObsSection.PairingStatusText;
    public Button UnpairButton => ObsSection.UnpairButton;
    public StackPanel DiscoveredDevicesPanel => ObsSection.DiscoveredDevicesPanel;
    public TextBox ManualPairAddressBox => ObsSection.ManualPairAddressBox;
    public Button ManualPairButton => ObsSection.ManualPairButton;
    public TextBlock ManualPairStatusText => ObsSection.ManualPairStatusText;
    public Border RefreshRemoteThumbnailsRow => ObsSection.RefreshRemoteThumbnailsRow;
    public Button RefreshRemoteThumbnailsButton => ObsSection.RefreshRemoteThumbnailsButton;

    // --- Forwarded Advanced Section Controls ---
    public Border ExperimentalHeader => AdvancedSection.ExperimentalHeader;
    public TextBlock ExperimentalHeaderText => AdvancedSection.ExperimentalHeaderText;
    public StackPanel ExperimentalContent => AdvancedSection.ExperimentalContent;
    public StackPanel LocalRamDiskSection => AdvancedSection.LocalRamDiskSection;
    public TextBlock RamDiskStatusText => AdvancedSection.RamDiskStatusText;
    public ToggleButton RamDiskToggle => AdvancedSection.RamDiskToggle;
    public StackPanel RamDiskFields => AdvancedSection.RamDiskFields;
    public TextBox RamDiskDriveBox => AdvancedSection.RamDiskDriveBox;
    public TextBox RamDiskSizeBox => AdvancedSection.RamDiskSizeBox;
    public TextBox RamDiskTargetMinutesBox => AdvancedSection.RamDiskTargetMinutesBox;
    public StackPanel RemoteRamDiskSection => AdvancedSection.RemoteRamDiskSection;
    public TextBlock RemoteRamDiskStatusText => AdvancedSection.RemoteRamDiskStatusText;
    public StackPanel RemoteRamDiskFields => AdvancedSection.RemoteRamDiskFields;
    public ToggleButton RemoteRamDiskToggle => AdvancedSection.RemoteRamDiskToggle;
    public TextBox RemoteRamDiskDriveBox => AdvancedSection.RemoteRamDiskDriveBox;
    public TextBox RemoteRamDiskSizeBox => AdvancedSection.RemoteRamDiskSizeBox;
    public ToggleButton OverlayLogToggle => AdvancedSection.OverlayLogToggle;
    public StackPanel OverlayLogModeFields => AdvancedSection.OverlayLogModeFields;
    public ComboBox OverlayLogModeSelector => AdvancedSection.OverlayLogModeSelector;
    public ToggleButton EnableAnimationsToggle => AdvancedSection.EnableAnimationsToggle;
    public ToggleButton DiagnosticLogToggle => AdvancedSection.DiagnosticLogToggle;
    public Button OpenDiagnosticLogButton => AdvancedSection.OpenDiagnosticLogButton;
    public ToggleButton DeveloperModeToggle => AdvancedSection.DeveloperModeToggle;
    public TextBlock DeveloperModeLockedNoteText => AdvancedSection.DeveloperModeLockedNoteText;
    public ToggleButton DisableHardwareAccelToggle => AdvancedSection.DisableHardwareAccelToggle;
    public Border DestructiveHeader => AdvancedSection.DestructiveHeader;
    public TextBlock DestructiveHeaderText => AdvancedSection.DestructiveHeaderText;
    public StackPanel DestructiveContent => AdvancedSection.DestructiveContent;
    public Button ClearSettingsCacheButton => AdvancedSection.ClearSettingsCacheButton;
    public Button ClearClipsDirectoryButton => AdvancedSection.ClearClipsDirectoryButton;
    public Button UninstallBacktrackButton => AdvancedSection.UninstallBacktrackButton;
    public Button UninstallSourceRecordButton => AdvancedSection.UninstallSourceRecordButton;
    public Button UninstallReplaySliderButton => AdvancedSection.UninstallReplaySliderButton;
}
