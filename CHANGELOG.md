# Changelog

All notable changes to Backtrack are documented in this file.

---

## [v0.3.13] - 2026-08-28

### Native Stream Deck Integration & Zero-Prerequisite Self-Contained Release

- **Official Elgato Stream Deck Plugin (`Backtrack.streamDeckPlugin`)**: Native two-way control integration connecting Stream Deck hardware/app to Backtrack over local WebSocket (`127.0.0.1:44558`).
  - **Clip Replay Buffer**: Flushes and saves instant replay buffer for any selected source with dynamic buffer duration readout.
  - **Record Source**: Starts/stops recording for any source with live stopwatch ticker on keycap.
  - **Cancel Recording**: Instantly purges and cancels active recording with status feedback.
  - **Bookmark Segment**: Drops chapter star marker into clip metadata on the fly.
  - **Toggle Backtrack HUD**: Toggles overlay on screen with live OBS connection dot.
- **Dynamic HTML5 Canvas Keycap Engine**: Custom-rendered keycaps with corner-radius safe insets, high-contrast typography, and automatic desaturated/dimmed offline states when Backtrack or OBS is disconnected.
- **Self-Contained Deployment**: Release binaries and installer are published with `--self-contained true` packaging the .NET 8 desktop runtime, eliminating any missing runtime errors on fresh Windows installations.
- **Modular View Architecture Updates**: Fully hardened modular partial view structure across ReplayRecord, Gallery, Player, Settings, and Styles.

---
## [v0.3.11] - 2026-08-26

### Fixes & Auto-Update Loop Resolution

- **Auto-Update Loop Fix**: Fixed a bug where self-updates would repeatedly download, extract, and relaunch in an infinite loop because `RecordUpdateApplied` was not persisting the applied release digest prior to restart.
- **Normalized Version Comparison**: Standardized `UpdateService.CurrentAppVersion` to always return 3-part version tuples (`Major.Minor.Build`), eliminating revision mismatches (`0.3.10.0` vs `0.3.10`).
- **Downgrade Safeguard**: Added safeguards in `ShouldApplyUpdate` to prevent accidental loops or automated downgrades.

---

## [v0.3.10] - 2026-08-26

### Fixes & Remote Enhancements

- **Remote Recording Cancellation Toast**: Fixed an issue where cancelling recordings over remote OBS/transmitter connections would not display the "Recording Canceled" toast notification.
- **Safe Remote Path Deletion & RPC Cleanup**: Added safe cross-platform path handling and remote clip recycling over `PairingService` when cancelling in-progress remote recordings.
- **Record Row Cancellation Reliability**: Guaranteed instant cancel toast display and label preservation for all active Source Record rows.
- **Zero Compiler Warnings**: Resolved all nullable CS8602 compiler warnings across the entire codebase.

---

## [v0.3.9] - 2026-08-26

### Architecture & Fixes

- **Full Modular Architecture Refactoring**: Cleanly split monolithic files into maintainable, single-responsibility domain modules (`Core/`, `UI/`, `Interop/`, `Obs/`, `Overlays/`, `Pairing/`, `Streaming/`, `Updates/`) with zero files exceeding 600 lines.
- **Overlay Transition & Frame Render Optimization**: Eliminated 1-frame Gallery artifacts and sizing glitches when toggling the overlay via 2-frame DirectX swapchain presentation synchronization and visual tree cleanup.
- **Win32 Layered Window Optimizations**: Integrated `SWP_NOCOPYBITS` handling in `WM_WINDOWPOSCHANGING` to prevent stale frame bit blitting during window bounds changes.
- **Animation Property Hold Release**: Updated window fade animations to use `FillBehavior.Stop` and explicitly clear dependency property animation holds.

---

## [v0.3.8] - 2026-08-25

### Features & Improvements

- **Audio Cue Volume & Delegation**: Added an Audio Cue Volume slider with live scaling and automatic RPC delegation to stream audio cues to the transmitter PC in remote setups.
- **Accurate Bookmark Timing**: Improved bookmark calculation across continuous recordings, replay buffers, and active video player playhead.
- **Gallery Starred Filter & Theming**: Added a dedicated ★ Starred toolbar toggle, Starred First/Only sorting options, and dynamic theme-matched storage progress bars.
- **Video Compression & Bundled FFmpeg**: Packaged standalone FFmpeg binary with optimized MP4 compression presets and faststart flags.
- **OS-Native Drag & Drop**: Implemented Windows Shell drag-image helper for zero-lag clip dragging into folders and external applications.

---

## [v0.3.7] - 2026-08-24

### Features & Improvements

- **Video Player Overhaul**: Removed the player sidebar for a full-width 16:9 player surface with zero letterboxing.
- **Top-Right Context Menu**: Replaced sidebar actions with a compact 3-dot trigger pill opening an action grid (Rename, Trim, Reveal, Delete) and metadata details.
- **Fullscreen Positioning**: Improved fullscreen layout with 20px insets matching the floating transport bar.
- **Inactive Replay Buffer Guard**: Inactive replay buffers are ignored on hotkey triggers and no longer generate stuck toasts.

---

## [v0.3.6] - 2026-08-24

### Features & Improvements

- **Restructured Settings UI**: Reorganized settings into 7 clear categories (General, Clips, Overlay & Hotkeys, OBS, Sharing, Experimental, Maintenance) with card-style groupings.
- **Context Menu Positioning**: "Cancel recording" context menu now opens directly at the cursor position.
- **Player Mute Feedback Fix**: Fixed inverted mute state and icon feedback in the built-in video player.
- **Quick Gallery Renaming**: Added title renaming support by double-tapping the title in Quick Gallery.
- **Audio Cues**: Added audio chime feedback on clip and recording saves with a dedicated mute toggle in Settings.
- **Remote OBS Gating**: Automatically locked plugin auto-updates when OBS runs on a remote transmitter PC.
- **Overlay Z-Order & Fullscreen Detection**: Improved window layering and topmost window detection across games and fullscreen apps.

---

## [v0.3.5] - 2026-08-21

### Features & Improvements

- **Remote Clip Streaming**: Opening remote clips now streams on-demand via a local relay without downloading full files first.
- **Remote Trim & File Management**: Trimming, renaming, and deleting streaming clips now execute directly on the transmitter PC with automatic file-lock retries.
- **Player Teardown Fix**: Prevented UI thread lockups and black-screen playback errors when trimming remote streaming clips.
- **Toast & Folder Picker Fixes**: Fixed duplicated 'Processing clip...' notifications and disabled irrelevant local folder pickers when OBS is remote.
- **Remote Trim Logging**: Added comprehensive diagnostic logging across both PCs for remote trim operations.

---

## [v0.3.4] - 2026-08-20

### Features & Improvements

- **Streaming Video Playback**: Remote clips now stream instantly over the network using an HTTP relay rather than downloading in advance.
- **Instant Seeking Support**: Added dynamic seek buffering that fetches video bytes from the active seek point during remote playback.

---

## [v0.3.3] - 2026-08-20

### Features & Improvements

- **Plugin Auto-Reinstall**: Automatically detects missing OBS plugin files and reinstalls them reliably even after OBS reinstallation.
- **Remote Quick Gallery & Sync**: Added remote clip support to the floating Recent Clips overlay, background sync every 20 minutes, and visual sync progress readouts.
- **Source Record Hotkey Feedback**: Start/stop toasts now trigger for hotkeys pressed directly in OBS.
- **Audio Track Selection**: Added a "Default audio track" selector in Settings and fixed LibVLC initial audio track muting quirks.
- **Resilient Self-Updates**: Added automatic retry logic for transient network failures during application updates.

---

## [v0.3.2] - 2026-08-19

### Features & Improvements

- **Destructive Settings Controls**: Added confirmed actions in Settings to uninstall Backtrack/plugins, reset cache, and clear clip files safely.
- **Storage Limits & Auto-Cleanup**: Added configurable storage quotas (GB) and automatic aged-clip recycling (days) in Settings.
- **Smooth Settings Scrolling**: Fixed middle-click autoscrolling to stop cleanly on release.
- **Toast Text Wrapping**: Improved notification layout so long text wraps cleanly instead of truncating.

---

## [v0.3.1] - 2026-08-16

### Features & Improvements

- **Automatic WebSocket Recovery**: Automatically enables OBS WebSocket server if disabled without requiring manual configuration.
- **New Status Badges**: Added real-time status badges for Encoder Overload and Virtual Camera.
- **Independent Record Timers**: Accurate elapsed timers for multi-source recording when main and filter records start/stop independently.
- **No-Signal Filter Detection**: Displays clear "No Signal" feedback when a recording source or device is disconnected.
- **Newest Clip Indicator**: Added visual indicator dots marking newest clips and parent folders in the Gallery.
- **Yami Acri Theme**: Added a new dark theme ported from OBS Studio.

---

## [v0.3.0] - 2026-08-15

### Features & Improvements

- **Configurable Status Indicators**: Added customizable corner positioning, horizontal/vertical orientation, and live preview for status badges.
- **Redesigned Timeline Trim**: Editor-style timeline with interactive range handles, live playhead, and time ruler replacing legacy start/end buttons.
- **Quick Gallery Polish**: Instant deletion updates and automatic repositioning across monitor resolution/display changes.

---

## [v0.2.12] - 2026-08-15

### Features & Improvements

- **Idle-Scoped Recent Clips**: Quick Gallery overlay now only displays on the Idle screen and hides during settings, player, or gallery screens.
- **Position Reset**: Disabling and re-enabling Recent Clips overlay resets its screen position back to default.
- **Quick Navigation**: Opening clips from Recent Clips overlay now navigates directly back to Idle.

---

## [v0.2.11] - 2026-08-12

### Features & Improvements

- **SHA256 Integrity Verification**: Validates downloaded update binaries and OBS plugin installers against official GitHub release digests before installation.

---

## [v0.2.10] - 2026-08-12

### Features & Improvements

- **Remote Clip Context Menu**: Added right-click context menu to reveal or delete remote clips directly on the stream PC.
- **In-Place Remote Rename**: Double-clicking remote clip titles renames the clip on the stream PC in real time.
- **Remote Player Operations**: Deleting, renaming, or trimming remote clips in the player syncs changes back to the stream PC.

---

## [v0.2.9] - 2026-08-12

### Features & Improvements

- **OBS Hotkey Display**: Recording and replay rows now display their bound OBS hotkeys or an explicit unbound indicator.

---

## [v0.2.8] - 2026-08-12

### Features & Improvements

- **Automatic Firewall Configuration**: Prompts for one-time firewall rule creation on first launch with an explanatory toast.
- **Independent Update Controls**: Added separate toggle switches in Settings for Backtrack and OBS plugin updates.
- **Theme-Aware Cards**: Gallery thumbnails and folder cards now adapt background colors to active themes.

---

## [v0.2.7] - 2026-08-10

### Features & Improvements

- **Smooth UI Animations**: Added optional fade and scale window transition animations in Settings.
- **Dark Theme Refinements**: Restored clean neutral backgrounds and text contrast in Dark Mode.
- **Player Layout Improvements**: Adjusted player navigation insets and fixed clipping artifacts during screen transitions.

---

## [v0.2.6] - 2026-08-08

### Features & Improvements

- **Per-Source Recording Folders**: Added custom destination folder controls for individual recording sources in Settings.
- **Vector Icons**: Replaced raster assets with crisp vector folder and action icons.
- **2-Step Update Workflow**: Explicit check and apply update flow with real-time progress notifications.

---

## [v0.2.5] - 2026-08-08

### Features & Improvements

- **Remote Gallery Browsing**: Paired PCs can browse remote clip folders, listings, and cached thumbnails directly.
- **Thumbnail Cache Sharing**: Receiver PCs reuse transmitter thumbnail caches without re-encoding.
- **Corrupt Clip Filtering**: Gallery automatically hides broken and sub-2-second video clips.
- **Update Loop Protection**: Digest-based release validation prevents false-positive update loops.

---

## [v0.2.4] - 2026-08-07

### Features & Improvements

- **Gallery Counter Synchronization**: Fixed clip count staying accurate when navigating between gallery folders and main screens.
- **Dev Build Protection**: Disabled self-updater execution on local development builds.
- **Pairing Handshake Reliability**: Improved pairing secret negotiation and explicit UI feedback on connection denial.

---

## [v0.2.3] - 2026-08-07

### Features & Improvements

- **Corner Overlay Log**: Added customizable OBS and Backtrack activity log in the bottom-right corner.
- **Single-Instance Guard**: Relaunching Backtrack focuses the active overlay instead of spawning duplicate processes.
- **Hotkey Notification**: Startup toast displays the configured global overlay hotkey.
- **Branded Application Icon**: Added high-resolution application icon for executable, shortcuts, and installer.

---

## [v0.2.2] - 2026-08-06

### Features & Improvements

- **EDID Monitor Names**: Display selector in Settings detects and shows real monitor model names.
- **RAM Disk Configuration**: Moved RAM disk options into a dedicated Experimental settings card.
- **Safe Auto-Update Engine**: Explicit update prompts that prevent installing during active recordings, streams, or replay saves.
- **Timer & Status Accuracy**: Recording timers accurately reflect paused states, and replay pills avoid false error flickers.

---

## [v0.2.1] - 2026-08-03

### Features & Improvements

- **Custom Replay Length Slider**: Interactive slider (1–60 minutes) pushing live buffer lengths to Source Record filters.
- **Buffer Visibility Controls**: Added options in Settings to hide specific replay buffers from the save screen.
- **Recording State Indicators**: Record button switches to an active square icon matching standard recording conventions.
- **Remote Machine Handling**: Gracefully disables local-only RAM disk settings when connected to a remote OBS host.

---

## [v0.2.0] - 2026-08-02

### Features & Improvements

- **OBS RAM Disk Support**: Optional ImDisk integration for zero-wear high-speed replay buffering.
- **Folder Browsing & Multi-Select**: Gallery supports subfolder navigation, mass clip selection, move, and batch deletion.
- **Global Escape Hotkey**: Pressing Escape closes the overlay from any screen.
- **Installer & Update Fixes**: Fixed Setup installer bundling and custom OBS installation path detection.

---

## [v0.1.3] - 2026-08-02

### Features & Improvements

- **System Tray Integration**: Added dedicated tray icon with quick controls.
- **Delete Confirmations**: Added confirmation dialogs for clip deletions.
- **Repository Optimization**: Streamlined project layout and build output structure.

---

## [0.1.2] - 2026-08-02

### Features & Improvements

- **Updater Test Build**: Verified automatic self-updater delivery pipeline and patch validation.

---

## [0.1.1] - 2026-08-02

### Features & Improvements

- **Updater Verification Build**: Validation build for self-update mechanism testing.

---

## [0.1.0] - 2026-08-02

### Features & Improvements

- **Initial Alpha Release**: Shadowplay-style hotkey-summoned overlay for OBS Studio with replay buffer and Source Record integration.
- **Self-Contained Deployment**: Standalone Windows x64 build with bundled .NET runtime.
