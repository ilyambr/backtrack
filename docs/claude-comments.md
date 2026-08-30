# Backtrack Codebase — Comments & Architectural Rationale Archive

> **Total source files indexed**: 74  
> **Total comment blocks archived**: 478  
> **Last updated**: 2026-08-30

This document is the comprehensive archive of all original architectural comments, design decisions, Win32 interop notes, and XAML explanations extracted from the Backtrack codebase. Each section provides the exact file, line range, code context, and full explanatory notes.

---

## Table of Contents

- [`App.xaml.cs`](#appxamlcs) (15 comment blocks)
- [`Backtrack.csproj`](#backtrackcsproj) (4 comment blocks)
- [`Core/AppLog.cs`](#coreapplogcs) (9 comment blocks)
- [`Core/AppSettings.cs`](#coreappsettingscs) (38 comment blocks)
- [`Core/AudioCues.cs`](#coreaudiocuescs) (9 comment blocks)
- [`Core/GalleryFormats.cs`](#coregalleryformatscs) (1 comment blocks)
- [`Core/SliderFillConverter.cs`](#coresliderfillconvertercs) (1 comment blocks)
- [`Core/SystemTrayManager.cs`](#coresystemtraymanagercs) (7 comment blocks)
- [`Interop/Acrylic.cs`](#interopacryliccs) (3 comment blocks)
- [`Interop/ClickThrough.cs`](#interopclickthroughcs) (2 comment blocks)
- [`Interop/CursorPos.cs`](#interopcursorposcs) (1 comment blocks)
- [`Interop/DisplayMonitors.cs`](#interopdisplaymonitorscs) (6 comment blocks)
- [`Interop/FirewallRules.cs`](#interopfirewallrulescs) (7 comment blocks)
- [`Interop/FullscreenDetector.cs`](#interopfullscreendetectorcs) (13 comment blocks)
- [`Interop/GlobalHotkey.cs`](#interopglobalhotkeycs) (2 comment blocks)
- [`Interop/RamDisk.cs`](#interopramdiskcs) (18 comment blocks)
- [`Interop/RecycleBin.cs`](#interoprecyclebincs) (2 comment blocks)
- [`Interop/SelfUninstall.cs`](#interopselfuninstallcs) (3 comment blocks)
- [`Interop/ShellDragHelper.cs`](#interopshelldraghelpercs) (15 comment blocks)
- [`Interop/ToolWindow.cs`](#interoptoolwindowcs) (1 comment blocks)
- [`Interop/WindowZOrder.cs`](#interopwindowzordercs) (1 comment blocks)
- [`Obs/ObsClient.cs`](#obsobsclientcs) (8 comment blocks)
- [`Obs/ObsConfigReader.cs`](#obsobsconfigreadercs) (3 comment blocks)
- [`Obs/ObsService.cs`](#obsobsservicecs) (36 comment blocks)
- [`Obs/ReplayBufferSizing.cs`](#obsreplaybuffersizingcs) (3 comment blocks)
- [`Overlays/DisclaimerOverlay.xaml`](#overlaysdisclaimeroverlayxaml) (2 comment blocks)
- [`Overlays/DisclaimerOverlay.xaml.cs`](#overlaysdisclaimeroverlayxamlcs) (2 comment blocks)
- [`Overlays/LogoOverlay.xaml`](#overlayslogooverlayxaml) (2 comment blocks)
- [`Overlays/LogoOverlay.xaml.cs`](#overlayslogooverlayxamlcs) (4 comment blocks)
- [`Overlays/OverlayLogWindow.xaml`](#overlaysoverlaylogwindowxaml) (3 comment blocks)
- [`Overlays/OverlayLogWindow.xaml.cs`](#overlaysoverlaylogwindowxamlcs) (1 comment blocks)
- [`Overlays/PairingRequestOverlay.xaml`](#overlayspairingrequestoverlayxaml) (1 comment blocks)
- [`Overlays/PairingRequestOverlay.xaml.cs`](#overlayspairingrequestoverlayxamlcs) (1 comment blocks)
- [`Overlays/RecentClipsOverlay.xaml`](#overlaysrecentclipsoverlayxaml) (2 comment blocks)
- [`Overlays/RecentClipsOverlay.xaml.cs`](#overlaysrecentclipsoverlayxamlcs) (3 comment blocks)
- [`Overlays/ScrimOverlay.xaml`](#overlaysscrimoverlayxaml) (2 comment blocks)
- [`Overlays/ScrimOverlay.xaml.cs`](#overlaysscrimoverlayxamlcs) (5 comment blocks)
- [`Overlays/StatusOverlay.xaml`](#overlaysstatusoverlayxaml) (6 comment blocks)
- [`Overlays/StatusOverlay.xaml.cs`](#overlaysstatusoverlayxamlcs) (16 comment blocks)
- [`Overlays/StreamingStatusOverlay.xaml`](#overlaysstreamingstatusoverlayxaml) (1 comment blocks)
- [`Overlays/StreamingStatusOverlay.xaml.cs`](#overlaysstreamingstatusoverlayxamlcs) (1 comment blocks)
- [`Overlays/ToastOverlay.xaml`](#overlaystoastoverlayxaml) (1 comment blocks)
- [`Overlays/ToastOverlay.xaml.cs`](#overlaystoastoverlayxamlcs) (23 comment blocks)
- [`Overlays/UpdatePromptOverlay.xaml`](#overlaysupdatepromptoverlayxaml) (3 comment blocks)
- [`Overlays/UpdatePromptOverlay.xaml.cs`](#overlaysupdatepromptoverlayxamlcs) (2 comment blocks)
- [`Pairing/PairingService.Client.cs`](#pairingpairingserviceclientcs) (4 comment blocks)
- [`Pairing/PairingService.Discovery.cs`](#pairingpairingservicediscoverycs) (5 comment blocks)
- [`Streaming/RemoteClipStreamServer.cs`](#streamingremoteclipstreamservercs) (14 comment blocks)
- [`Themes/Theme.Amoled.xaml`](#themesthemeamoledxaml) (3 comment blocks)
- [`Themes/Theme.Dark.xaml`](#themesthemedarkxaml) (6 comment blocks)
- [`Themes/Theme.Light.xaml`](#themesthemelightxaml) (4 comment blocks)
- [`Themes/Theme.Yami.xaml`](#themesthemeyamixaml) (3 comment blocks)
- [`Themes/Theme.YamiAcri.xaml`](#themesthemeyamiacrixaml) (4 comment blocks)
- [`Themes/ThemeManager.cs`](#themesthememanagercs) (11 comment blocks)
- [`UI/Cards/MainWindow.Cards.Remote.cs`](#uicardsmainwindowcardsremotecs) (2 comment blocks)
- [`UI/Cards/MainWindow.Cards.cs`](#uicardsmainwindowcardscs) (1 comment blocks)
- [`UI/MainWindow/MainWindow.Updates.Apply.cs`](#uimainwindowmainwindowupdatesapplycs) (4 comment blocks)
- [`UI/MainWindow/MainWindow.WindowChrome.cs`](#uimainwindowmainwindowwindowchromecs) (2 comment blocks)
- [`UI/MainWindow/MainWindow.xaml`](#uimainwindowmainwindowxaml) (53 comment blocks)
- [`UI/Player/MainWindow.Compress.cs`](#uiplayermainwindowcompresscs) (3 comment blocks)
- [`UI/Player/MainWindow.Player.AudioVolume.cs`](#uiplayermainwindowplayeraudiovolumecs) (4 comment blocks)
- [`UI/Player/MainWindow.Player.Bookmarks.cs`](#uiplayermainwindowplayerbookmarkscs) (2 comment blocks)
- [`UI/Player/MainWindow.Player.Fullscreen.cs`](#uiplayermainwindowplayerfullscreencs) (1 comment blocks)
- [`UI/Player/MainWindow.Player.cs`](#uiplayermainwindowplayercs) (1 comment blocks)
- [`UI/RecentClips/MainWindow.RecentClipsOverlay.Tiles.cs`](#uirecentclipsmainwindowrecentclipsoverlaytilescs) (1 comment blocks)
- [`UI/RecentClips/MainWindow.RecentClipsOverlay.cs`](#uirecentclipsmainwindowrecentclipsoverlaycs) (1 comment blocks)
- [`UI/ReplayRecord/MainWindow.Obs.cs`](#uireplayrecordmainwindowobscs) (1 comment blocks)
- [`UI/ReplayRecord/MainWindow.SaveReplay.Status.cs`](#uireplayrecordmainwindowsavereplaystatuscs) (1 comment blocks)
- [`Updates/UpdateService.cs`](#updatesupdateservicecs) (33 comment blocks)
- [`installer/BacktrackSetup.csproj`](#installerbacktracksetupcsproj) (2 comment blocks)
- [`installer/Program.cs`](#installerprogramcs) (6 comment blocks)

---


## `App.xaml.cs`

*Total comments: 15*

### Lines 12-14
**Context**: `private StatusOverlay? _status;`

```csharp
    // All three windows must live for the whole app lifetime -- Application doesn't
    // otherwise hold a reference to any of them once OnStartup returns, and MainWindow
    // in particular is never registered as the "main" window (it starts hidden).
```

### Lines 34-36
**Context**: `ThemeManager.Apply(startupSettings.Theme);`

```csharp
        // Before any window is created -- DynamicResource lookups on a
        // window's very first Loaded pass should already see the right
        // theme, not flash the default one then swap.
```

### Lines 39-46
**Context**: `if (startupSettings.DisableHardwareAcceleration)`

```csharp
        // Also before any window is created -- WPF's rendering pipeline picks
        // hardware vs. software once per process and isn't something that
        // can be flipped cleanly mid-session (see AppSettings.
        // DisableHardwareAcceleration's own comment). Settings > Experimental
        // > Diagnostics; a troubleshooting escape hatch for actual visual
        // corruption in Backtrack's own overlay rendering, not a
        // capture/recording setting -- Backtrack has no capture pipeline of
        // its own, OBS owns that entirely.
```

### Lines 50-55
**Context**: `string crashLogPath = System.IO.Path.Combine(`

```csharp
        // %AppData%\Backtrack, same folder settings.json already lives in --
        // not a bare relative "crash.log", which lands wherever the process
        // happens to think its working directory is (inconsistent depending
        // on how it's launched: double-click, a shortcut with a different
        // "Start in" folder, Task Scheduler, etc.) rather than somewhere
        // reliably findable afterward.
```

### Lines 121-122
**Context**: `_status = new StatusOverlay();`

```csharp
        // The status overlay and toast notifications are always visible,
        // independent of the hotkey-summoned HUD -- create and show them first.
```

### Lines 124-126
**Context**: `if (startupSettings.ShowStatusIndicator)`

```csharp
        // Off at startup if the user last had it hidden (via the tray icon's
        // own toggle or Settings' "Show status indicator") -- see
        // AppSettings.ShowStatusIndicator's own comment.
```

### Lines 133-134
**Context**: `_scrim = new ScrimOverlay();`

```csharp
        // Stays hidden until the HUD is summoned -- unlike the two above, this
        // one is never shown on its own.
```

### Lines 137-139
**Context**: `_disclaimer = new DisclaimerOverlay();`

```csharp
        // Also only shown/hidden in lockstep with the HUD (see MainWindow.ToggleVisible),
        // not an always-on fixture -- unlike Status/Toast, this one only makes
        // sense while the overlay itself is actually open.
```

### Lines 142-144
**Context**: `_logo = new LogoOverlay();`

```csharp
        // Also only shown/hidden in lockstep with the HUD -- lives at a fixed
        // screen position, independent of MainWindow's own size, which changes a
        // lot between the compact pill and the big Gallery/Player panel.
```

### Lines 147-150
**Context**: `_streamingStatus = new StreamingStatusOverlay();`

```csharp
        // Unlike the logo, this one DOES need to track MainWindow's own
        // position/size (it's meant to read as attached directly underneath
        // the main pill) -- see MainWindow.UpdateStreamingBoxVisibility and
        // StreamingStatusOverlay.Reposition.
```

### Lines 153-156
**Context**: `_pairingRequest = new PairingRequestOverlay();`

```csharp
        // A pairing request can arrive at any time (whoever's on the other PC
        // decides when to click "pair"), independent of whether the HUD is open --
        // created up front like the other always-available overlays, but only
        // actually shown when a request comes in.
```

### Lines 159-161
**Context**: `_recentClips = new RecentClipsOverlay();`

```csharp
        // Also created up front, own visibility/position controlled entirely
        // by MainWindow from AppSettings.ShowRecentClipsOverlay -- off by
        // default (see that setting's own comment).
```

### Lines 164-166
**Context**: `_main = new MainWindow(_status, _toasts, _scrim, _disclaimer, _logo, _streamingStatus, _pairingRequest, _recentClips);`

```csharp
        // MainWindow creates its own HWND immediately (for the global hotkey) but
        // is never Shown until the hotkey is pressed -- it's a summonable overlay,
        // not a normal always-visible window.
```

### Lines 178-185
**Context**: `if (!AppSettings.Load().FirewallRulesAttempted)`

```csharp
        // Once, ever, per install (gated by FirewallRulesAttempted, set
        // after the first try regardless of outcome) -- adds the inbound/
        // outbound firewall rules Pairing/PairingService.cs's discovery
        // listener and pairing server need. Off the UI thread: the elevated
        // netsh call blocks on Process.WaitForExit() until the user
        // responds to the UAC prompt and it actually finishes, which would
        // freeze the whole app (including the hotkey/tray) if awaited
        // inline here on startup.
```

### Lines 192-202
**Context**: `_main.MarkFirewallRulesAttempted();`

```csharp
                // NOT a fresh AppSettings.Load()/Save() here (that's what this
                // used to do) -- MainWindow already loaded its OWN AppSettings
                // instance above and saves from it constantly (dozens of call
                // sites), and AppSettings.Save() is a whole-object overwrite,
                // not a merge. A separate copy saved here would win the race
                // for a moment, but the next unrelated _settings.Save() over on
                // MainWindow's older in-memory copy (loaded before this flag
                // existed) would clobber it straight back to false -- which is
                // exactly why this was firing the UAC prompt on every single
                // launch instead of once ever. Mutating MainWindow's own
                // instance means every later save already carries this true.
```


## `Backtrack.csproj`

*Total comments: 4*

### Lines 11-16
**Context**: `<Version>0.3.11</Version>`

```xml
    <!-- This is specifically the Windows build's version; see VERSIONING.md.
         A Linux build (WPF/net8.0-windows only runs on Windows; a real Linux
         port would need a separate UI project, likely Avalonia, plus
         replacements for every Win32-only feature) doesn't exist yet, but
         when it does it versions independently, starting fresh, not from
         this number. -->
```

### Lines 19-24
**Context**: `</PropertyGroup>`

```xml
    <!-- The OBS link itself is still a hand-rolled obs-websocket v5 client over
         System.Net.WebSockets, no package needed. LibVLC below is the one
         exception, since WPF's own MediaElement is built on the legacy Windows
         Media Player COM component, which isn't present/registered on every
         Windows install (this one included); LibVLC ships its own decoder and
         doesn't depend on WMP or Media Foundation being there at all. -->
```

### Lines 53-57
**Context**: `<None Update="ThirdParty\ImDisk\**">`

```xml
    <!-- ImDisk Virtual Disk Driver (ltr-data.se), redistributed unmodified per its
         license: used to install/mount the optional RAM disk. Only the amd64
         binaries are actually run (this app only ships x64), but the whole
         official package tree is kept intact rather than cherry-picked, so
         install.cmd/imdisk.inf still find files exactly where they expect them. -->
```

### Lines 67-75
**Context**: `<Page Remove="Themes\Theme.*.xaml" />`

```xml
    <!-- Theme.*.xaml files are loose files at runtime (Themes\ next to the
         .exe), not embedded Page/BAML resources: SDK-style projects
         auto-include every .xaml as a compiled Page by default, which is
         exactly what this Remove+None pair opts these specific files out
         of. That's what makes ThemeManager.DiscoverThemes' whole point
         possible: adding a new theme (or a user making their own) is
         "drop a file named Theme.Whatever.xaml in that folder", no code
         change and no rebuild required, since the app reads them off disk
         at runtime instead of needing them baked into the binary. -->
```


## `Core/AppLog.cs`

*Total comments: 9*

### Lines 8-19
**Context**: `public static class AppLog`

```csharp
/// <summary>
/// A small in-memory ring buffer of recent app-level events (OBS connect/
/// disconnect, clips saved, update checks/installs, RAM disk mount/unmount,
/// errors) -- backs the bottom-right overlay log's "Backtrack" mode. Nothing
/// here existed before; previously this kind of thing only ever went to
/// Debug.WriteLine, invisible outside an attached debugger. The ring buffer
/// itself is still deliberately not persisted -- FileLoggingEnabled below
/// (Settings > Experimental > Diagnostics > "Diagnostic log file") is the
/// opt-in for that, added after a real debugging session needed hand-added
/// temporary file logging repeatedly, precisely because nothing here
/// survived a restart to look back at afterward.
/// </summary>
```

### Line 28
**Context**: `public static event Action? Changed;`

```csharp
    /// <summary>Fires after every Write, on whatever thread called Write -- subscribers hop to the UI thread themselves.</summary>
```

### Line 31
**Context**: `public static bool FileLoggingEnabled { get; set; }`

```csharp
    /// <summary>Set once at startup from AppSettings.DiagnosticLogEnabled, and again whenever that Settings toggle changes.</summary>
```

### Line 34
**Context**: `public static bool DeveloperModeEnabled { get; set; }`

```csharp
    /// <summary>Set once at startup from AppSettings.DeveloperModeEnabled, and again whenever that Settings toggle changes -- see WriteError.</summary>
```

### Lines 40-43
**Context**: `private const long MaxFileBytes = 5 * 1024 * 1024;`

```csharp
    // Rotated (not appended past) at this size -- an always-on file log
    // across many sessions would otherwise grow forever. One rollover file
    // kept, not a numbered series: this is a recent-activity diagnostic aid,
    // not a long-term audit trail.
```

### Lines 60-66
**Context**: `public static void WriteError(string context, Exception ex) =>`

```csharp
    /// <summary>
    /// context/ex.Message normally, or the full exception (stack trace and
    /// all) when DeveloperModeEnabled -- see its own comment on why that's
    /// gated separately from FileLoggingEnabled rather than always-verbose.
    /// Still goes through the same Write above either way, so it shows in
    /// the overlay log too, not just the file.
    /// </summary>
```

### Line 81
**Context**: `File.Move(LogFilePath, rolledPath);`

```csharp
                try { File.Delete(rolledPath); } catch { /* best effort */ }
```

### Lines 89-91
**Context**: `}`

```csharp
            // Best effort -- a locked/missing/permission-denied log file is
            // never worth surfacing an error over; the in-memory ring buffer
            // above already has this entry regardless.
```

### Line 95
**Context**: `public static List<Entry> Snapshot()`

```csharp
    /// <summary>Oldest first, matching how the log panel displays them (newest at the bottom, like a terminal).</summary>
```


## `Core/AppSettings.cs`

*Total comments: 38*

### Lines 9-23
**Context**: `public sealed class ThemeIdConverter : JsonConverter<string>`

```csharp
/// <summary>
/// Theme is now a free-form string Id (a theme file's name, see
/// ThemeManager), not the old fixed AppTheme enum -- an open-ended,
/// drop-a-file set of themes can't be represented by a fixed enum. An
/// existing settings.json from before this change stored the OLD enum's
/// raw numeric ordinal instead of a string, which a plain string property
/// would fail to deserialize entirely -- and AppSettings.Load's own
/// catch-all around the WHOLE file would then silently reset EVERY other
/// setting (hotkey, clips folder, OBS pairing, ...) back to defaults too,
/// not just the theme. This converter accepts either shape: a JSON number
/// is mapped through the old enum's own historical append order
/// (Dark/Light/Yami/Amoled/YamiAcri -- never reordered, per that enum's
/// own comment while it still existed) to the matching theme Id string; a
/// JSON string (the new, ongoing format) passes through unchanged.
/// </summary>
```

### Lines 46-47
**Context**: `public string ClipsFolder { get; set; } = DefaultClipsFolder;`

```csharp
    // Where clips live -- can be a local folder or a UNC network path
    // (\\STREAM-PC\Clips) when OBS runs on a different machine than this overlay.
```

### Lines 53-54
**Context**: `public string LocalCopyFolder { get; set; } = Path.Combine(`

```csharp
    // Where "Copy to this PC" drops a local copy of a clip that's actually sitting
    // on a remote stream PC's share.
```

### Lines 58-61
**Context**: `public bool ObsIsRemote { get; set; }`

```csharp
    // OBS connection target: defaults to "OBS is on this PC" (auto-detect the
    // password from this machine's own obs-websocket config). Set ObsIsRemote
    // to talk to OBS on a different PC instead (e.g. a dedicated stream PC) --
    // that machine's own config isn't reachable, so host/port/password are manual.
```

### Lines 67-68
**Context**: `public int HotkeyModifiers { get; set; } = 0x1 | 0x2; // Alt | Control`

```csharp
    // Stored as raw values (not the GlobalHotkey.Modifiers enum) so this class
    // doesn't need to reference the Interop namespace. Defaults to Ctrl+Alt+G.
```

### Line 72
**Context**: `public int CancelRecordHotkeyModifiers { get; set; } = 0;`

```csharp
    // Global hotkey to cancel in-progress recording and discard the recorded file.
```

### Lines 78-82
**Context**: `public bool ShowStatusIndicator { get; set; } = true;`

```csharp
    // Mirrors the tray icon's own "Hide Status Overlay"/"Show Status Overlay"
    // menu item -- either toggling this in Settings or from the tray flips
    // the same underlying window and this same setting, so both stay in
    // sync and the choice persists across restarts (the tray toggle alone
    // used to be runtime-only, resetting to visible on every launch).
```

### Lines 85-87
**Context**: `public StatusIndicatorOrientation StatusIndicatorOrientation { get; set; } = StatusIndicatorOrientation.Horizontal;`

```csharp
    // Defaults match the status indicator's original hardcoded shape/position
    // (horizontal strip, top-right) so upgrading doesn't move it. See the
    // enums' own comments (StatusOverlay.xaml.cs) for what each value means.
```

### Lines 91-96
**Context**: `public bool DisableBacktrackAutoUpdate { get; set; }`

```csharp
    // Both off by default (auto-update stays on unless explicitly turned off).
    // Only gate the automatic startup check in CheckForUpdatesAsync -- the
    // "Check now" button always still works regardless of either, same as
    // Windows Update's own "notify but don't auto-install" pattern still
    // lets you manually check/install; these two are about NOT wanting it
    // to happen unattended, not about losing the ability to trigger it.
```

### Line 100
**Context**: `public bool DisableAudioCues { get; set; }`

```csharp
    // Audio chimes played when recordings or clips are saved (opt-out, enabled by default).
```

### Line 103
**Context**: `public int AudioCueVolume { get; set; } = 100;`

```csharp
    // Volume for audio chimes (0 to 100, default 100).
```

### Lines 106-108
**Context**: `[JsonConverter(typeof(ThemeIdConverter))]`

```csharp
    // See ThemeIdConverter's own comment for why this needs a custom
    // converter: an existing settings.json's stored value predates this
    // being a string at all.
```

### Lines 112-121
**Context**: `public bool EnableAnimations { get; set; } = false;`

```csharp
    // Off by default -- see MainWindow's ShowScreen/ToggleVisible/CloseOverlay
    // for the AllowsTransparency="True" experiment these gate. That change
    // enabled a genuine screen-transition fade/scale and a real window-level
    // open/close fade, but on a layered window ANY animation means a full
    // software re-composite of the whole window on every frame, which reads
    // as smooth on some setups and janky/frame-dropped on others depending
    // on hardware, drivers, and what's on screen underneath (a game with its
    // own heavy GPU load in particular). Instant show/hide is the safe,
    // guaranteed-smooth-because-there's-nothing-to-drop-frames-on default;
    // this is the escape hatch for turning the animated version back on.
```

### Lines 124-126
**Context**: `public bool DiagnosticLogEnabled { get; set; } = false;`

```csharp
    // Off by default -- see AppLog's own comment on why this exists (a real
    // debugging session repeatedly needed hand-added temporary file logging
    // because AppLog's ring buffer is in-memory only, gone on restart).
```

### Lines 129-134
**Context**: `public bool DeveloperModeEnabled { get; set; } = false;`

```csharp
    // Makes logged/shown errors include the full exception (stack trace and
    // all) instead of just its Message -- only meaningfully useful alongside
    // DiagnosticLogEnabled above, since that's where the extra detail
    // actually ends up persisted anywhere. Also the sole authority for
    // UpdateService.IsDevBuild (see its own comment) -- this is now the MAIN
    // way to declare a dev build, not an add-on to a path guess.
```

### Lines 137-141
**Context**: `public bool DeveloperModeAutoSuggested { get; set; } = false;`

```csharp
    // Set true the one time MainWindow.LoadSettingsUi auto-suggests
    // DeveloperModeEnabled based on UpdateService.IsRunningFromDevLocation,
    // so that suggestion only ever happens once -- after that, the toggle is
    // fully user-controlled, including turning it back off in a location
    // that would still trip the auto-suggestion.
```

### Lines 144-151
**Context**: `public bool DisableHardwareAcceleration { get; set; } = false;`

```csharp
    // RenderOptions.ProcessRenderMode -- applied once at startup (App.xaml.cs),
    // before any window is created, since WPF's rendering pipeline isn't
    // something that can be flipped cleanly mid-session. A troubleshooting
    // escape hatch for actual visual corruption/glitches in Backtrack's own
    // overlay on unusual GPU/driver combos, same category of issue this
    // repo's CLAUDE.md already documents extensively for the layered-window
    // rendering path -- not a capture/recording setting (OBS owns that
    // entirely; Backtrack has no capture pipeline of its own).
```

### Lines 154-159
**Context**: `public bool ShowRecentClipsOverlay { get; set; } = false;`

```csharp
    // Off by default -- a floating draggable window is a bigger ask on
    // someone's screen than anything else this app adds unprompted. Null
    // X/Y means "never been positioned yet"; RecentClipsOverlay.Show picks a
    // sensible default (bottom-right of the active display) the first time,
    // then MainWindow persists wherever it actually gets dragged to via
    // RecentClipsOverlay.PositionChanged.
```

### Lines 164-168
**Context**: `public string? DisplayDeviceName { get; set; }`

```csharp
    // Which monitor the overlay and all its auxiliary windows appear on --
    // Win32's own per-monitor device name (e.g. "\\.\DISPLAY1"), not an index,
    // since indices can silently renumber when a monitor is plugged/unplugged
    // but a still-connected monitor's device name doesn't change. Null/empty
    // means "whichever one Windows currently calls primary."
```

### Lines 171-173
**Context**: `public string DeviceId { get; set; } = Guid.NewGuid().ToString();`

```csharp
    // A stable identity for this install, shown to (and shown by) other Backtrack
    // instances during pairing -- generated once, not tied to the Windows machine
    // name alone since that can change.
```

### Line 176
**Context**: `public bool ShareClipsEnabled { get; set; }`

```csharp
    // Host side: broadcasts this machine as pairable and answers pairing requests.
```

### Lines 179-182
**Context**: `public string? PairedPeerDeviceId { get; set; }`

```csharp
    // Client side: the one peer this install is currently paired with. Only one at
    // a time -- this mirrors the existing single "OBS is on a different PC" model
    // rather than supporting a whole paired-device list, since that's the actual
    // use case (two of your own PCs), not a general file-sharing platform.
```

### Lines 189-191
**Context**: `public string? AuthorizedClientDeviceId { get; set; }`

```csharp
    // Host side: who was actually approved to pull from this PC's own share.
    // Kept separate from PairedPeer* above since a single install could in theory
    // both share its own clips and pull from someone else's at once.
```

### Lines 196-199
**Context**: `public bool FirewallRulesAttempted { get; set; }`

```csharp
    // Attempted exactly once, ever -- see Interop/FirewallRules.cs. Set after the
    // first try regardless of outcome (elevation approved, denied, or the netsh
    // calls themselves failed), not just on success, so a user who dismisses the
    // one UAC prompt doesn't get re-prompted on every subsequent launch.
```

### Lines 202-208
**Context**: `public bool RamDiskEnabled { get; set; }`

```csharp
    // RAM disk for OBS's replay buffer output (via ImDisk): mounted on Backtrack
    // startup and unmounted on exit, not left mounted independent of this app --
    // opt-in and off by default since it installs a kernel driver the first time
    // it's turned on. Size starts at a conservative flat default rather than
    // whatever a full buffer-duration estimate would compute to (that number can
    // get large -- see Obs/ReplayBufferSizing.cs), and is meant to be raised by
    // hand once the feature is confirmed working.
```

### Lines 213-216
**Context**: `public bool RamDiskInstructionShown { get; set; }`

```csharp
    // Shown once, the first time the RAM disk is actually mounted -- OBS has no
    // API to set the Replay Buffer output path, so this is a one-time manual
    // step in OBS's own Settings > Output > Replay Buffer, not something
    // Backtrack can do for the user on every launch.
```

### Lines 219-228
**Context**: `public DateTimeOffset? LastAppliedBacktrackReleaseAt { get; set; }`

```csharp
    // Sha256 digest (preferred, when GitHub provides one -- see ReleaseInfo.Digest)
    // and updated_at timestamp (fallback for the rare asset without a digest) of
    // the release ASSET, not just its version tag, that Backtrack last actually
    // applied for itself and each companion plugin -- lets UpdateService catch a
    // same-version-tag re-upload (this project's own release workflow sometimes
    // reuses the version number for a small fix) that plain version-number
    // comparison would otherwise miss entirely. Both null until the first check
    // after this tracking was added; see
    // CheckAndApplyPluginUpdateAsync/CheckAndApplySelfUpdateAsync for how that
    // gets seeded without forcing an unnecessary reinstall.
```

### Lines 236-241
**Context**: `public bool StorageLimitEnabled { get; set; }`

```csharp
    // Settings > Clips > Storage limit / Auto-delete old clips. StorageLimitEnabled
    // false means no limit at all, regardless of StorageLimitGb's stored value --
    // a hard "stop letting you make new clips" gate once ClipsFolder's total size
    // reaches the limit, not an auto-cleanup (that's the separate setting below).
    // Neither one deletes anything by itself; see TryBlockForStorageLimit /
    // RunAutoDeleteOldClips in MainWindow.xaml.cs.
```

### Lines 247-249
**Context**: `public bool OverlayLogEnabled { get; set; } = true;`

```csharp
    // Bottom-right corner log window (see OverlayLogWindow). "Obs" mirrors
    // OBS's own status-bar-style warnings (encoding overload, saves) one line
    // at a time; "Backtrack" shows a scrollable window into AppLog instead.
```

### Lines 253-258
**Context**: `public int ReplayBufferMinutes { get; set; } = 30;`

```csharp
    // Pushed to every Source Record filter's own replay_duration via the
    // replay-slider bridge (set_buffer_duration) -- this is the buffer that
    // actually gets flushed to disk (the RAM disk, if enabled) on every save,
    // not the trimmed clip length. 30 min default; UI allows up to 60 min
    // with a RAM allocation warning since a full flush at that length can
    // exceed a modest RAM disk's size.
```

### Lines 261-266
**Context**: `public HashSet<string> HiddenBufferLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);`

```csharp
    // Buffers hidden from the "Save which buffer?" screen -- keyed by the row's
    // Label (e.g. "Replay Buffer", "elgato - Source Record"), not its Key. A
    // row's Key is that filter object's in-memory address for this OBS session
    // (see the plugin's RefreshAll -- there's no stable UUID to use instead),
    // so it changes every OBS restart; the Label is what's actually stable
    // across restarts as long as the source/filter itself isn't renamed.
```

### Lines 269-278
**Context**: `public Dictionary<string, string> LocalRowNameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);`

```csharp
    // Local-only display name override for a buffer/recording-source row,
    // double-tapped in Settings -- keyed by the row's real OBS-reported Label
    // (same stability reasoning as HiddenBufferLabels just above: Key isn't
    // stable across an OBS restart, Label is). Purely cosmetic on Backtrack's
    // end; every OBS-facing call still uses the real Label/SourceName/
    // FilterName throughout, never this override. A buffer and a recording
    // source backed by the SAME Source Record filter share the exact same
    // Label string (both obs-replay-slider docks derive it from one shared
    // FilterRowLabel helper), so keying by Label naturally links renaming
    // across both lists instead of needing separate tracking for each.
```

### Lines 281-292
**Context**: `public int DefaultPlayerAudioTrackIndex { get; set; }`

```csharp
    // Settings > Player > "Default audio track". 0 = automatic (whichever
    // track LibVLC/the clip lists first, the old unconditional behavior).
    // 1-6 = OBS's own fixed Track 1-6 output numbering -- a clip's audio
    // tracks don't come back from LibVLC pre-labeled with which OBS track
    // number they actually are, only in whatever order the file lists them
    // (usually the same order, but not something to rely on), so this is
    // matched positionally (Nth audio track in the file = Track N) the same
    // way LoadAudioTracks' own "Track {i+1}" fallback naming already
    // assumes. Exists because which OBS track actually carries real audio
    // depends entirely on this user's own Advanced Audio Properties routing
    // (e.g. desktop audio on Track 2, mic on Track 1) -- defaulting to
    // "whichever came first" can default to a genuinely silent track for a
```

### Lines 308-311
**Context**: `private static string LegacyFilePath => Path.Combine(`

```csharp
    // The app used to be called Capture Center, with settings stored under that
    // folder name. A one-time copy on first run under the new name means the
    // rename doesn't silently reset the hotkey, clips folder, OBS connection,
    // etc. back to defaults for anyone upgrading from that version.
```

### Line 337
**Context**: `}`

```csharp
            // Corrupt or unreadable settings file -- fall back to defaults rather than crash.
```

### Lines 342-353
**Context**: `private static string ResolveClipsFolderForThisMachine(string loadedClipsFolder)`

```csharp
    // settings.json lives under %AppData%, so it doesn't normally follow anyone
    // between machines -- but it's a plain file, and people do sometimes copy it
    // over by hand to carry pairing/hotkey/etc config to a second PC quickly.
    // If it carries ClipsFolder along too, the copied value bakes in the
    // ORIGINAL machine's Windows username (MyVideos resolves through the
    // account name), which is very likely wrong on the new machine -- e.g.
    // "...\Administrator\Videos\Backtrack" landing on a PC whose actual account
    // isn't named Administrator. Only kicks in when the loaded path (a) doesn't
    // exist on this machine and (b) matches our own generated default's shape
    // for a different user, so a deliberately chosen custom folder -- or a
    // legitimate UNC path to a stream PC's share, see the property doc above --
    // is never touched.
```

### Lines 372-379
**Context**: `public static void ClearSavedFile()`

```csharp
    /// <summary>
    /// Settings > Destructive > Clear settings cache. Deletes settings.json
    /// itself (not just an in-memory reset) so the NEXT Load() -- after the
    /// caller restarts the app, since a live reset would mean manually
    /// re-syncing dozens of already-bound Settings UI controls by hand -- has
    /// nothing to read and falls through to a plain `new AppSettings()`,
    /// same as a genuinely first-ever run.
    /// </summary>
```

### Line 383
**Context**: `}`

```csharp
        catch { /* best effort -- e.g. file briefly locked; caller still restarts either way */ }
```


## `Core/AudioCues.cs`

*Total comments: 9*

### Lines 9-13
**Context**: `public static class AudioCues`

```csharp
/// <summary>
/// Instant, zero-latency, zero-codec-dependency audio cue playback using Win32 PlaySound from memory.
/// Preloads PCM WAV bytes into memory at startup for true zero-latency sound effects across all Windows versions.
/// Supports dynamic volume scaling and seamless remote execution over PairingService.
/// </summary>
```

### Line 64
**Context**: `Assembly asm = typeof(AudioCues).Assembly;`

```csharp
            // Fallback: try embedded resource from assembly
```

### Line 84
**Context**: `}`

```csharp
        // Sound removed as requested
```

### Line 132
**Context**: `if (IsRemoteModeActive?.Invoke() == true && RemoteCuePlayer != null)`

```csharp
            // If OBS is remote AND Backtrack is paired with the remote PC, delegate audio cue to remote PC
```

### Line 168
**Context**: `if (memoryBuffer != null && memoryBuffer.Length > 0)`

```csharp
            // 1. Play scaled directly from memory buffer (0ms latency, zero disk I/O)
```

### Line 180
**Context**: `string wavPath = Path.Combine(AssetsAudioDir, wavFileName);`

```csharp
            // 2. Fallback to WAV file on disk
```

### Line 194
**Context**: `string mp3Path = Path.Combine(AssetsAudioDir, mp3FallbackFileName);`

```csharp
            // 3. Fallback to MP3 file on disk using MediaPlayer
```

### Line 222
**Context**: `int fmtIndex = -1;`

```csharp
            // Scan for "fmt " chunk to verify 16-bit uncompressed PCM
```

### Line 245
**Context**: `int dataIndex = -1;`

```csharp
            // Scan for "data" chunk
```


## `Core/GalleryFormats.cs`

*Total comments: 1*

### Lines 3-4
**Context**: `public static class GalleryFormats`

```csharp
/// <summary>Shared between MainWindow's local Gallery and PairingService's remote
/// gallery listing/download, so both sides agree on what counts as a clip.</summary>
```


## `Core/SliderFillConverter.cs`

*Total comments: 1*

### Lines 8-11
**Context**: `public sealed class SliderFillConverter : IMultiValueConverter`

```csharp
/// <summary>
/// Splits a Slider's track into two Star-weighted grid columns (played / remaining)
/// so the "filled up to the thumb" look doesn't need the track's rendered width.
/// </summary>
```


## `Core/SystemTrayManager.cs`

*Total comments: 7*

### Line 131
**Context**: `int dotSize = 10;`

```csharp
            // Green / Red dot in bottom-right corner
```

### Line 200
**Context**: `var obsHeader = new System.Windows.Controls.MenuItem`

```csharp
        // OBS Status Header Item
```

### Line 211
**Context**: `var openHudItem = new System.Windows.Controls.MenuItem { Header = "Open Backtrack HUD", Style = menuItemStyle };`

```csharp
        // Open Backtrack HUD
```

### Line 216
**Context**: `var toggleOverlayItem = new System.Windows.Controls.MenuItem`

```csharp
        // Toggle Status Overlay
```

### Line 225
**Context**: `var openClipsItem = new System.Windows.Controls.MenuItem { Header = "Open Clips Folder", Style = menuItemStyle };`

```csharp
        // Open Clips Folder
```

### Line 230
**Context**: `var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings...", Style = menuItemStyle };`

```csharp
        // Settings
```

### Line 237
**Context**: `var quitItem = new System.Windows.Controls.MenuItem { Header = "Quit Backtrack", Style = menuItemStyle };`

```csharp
        // Quit
```


## `Interop/Acrylic.cs`

*Total comments: 3*

### Lines 6-12
**Context**: `public static class Acrylic`

```csharp
/// <summary>
/// Real blur-behind (Windows' undocumented-but-widely-used SetWindowCompositionAttribute),
/// so the panel actually looks like a translucent dimmed sheet over the desktop/game
/// instead of a flat, non-blurred rectangle. Best-effort: if it fails on some
/// system/driver combination, the window still has its plain semi-transparent
/// background as a fallback -- this never throws out to the caller.
/// </summary>
```

### Lines 55-62
**Context**: `public static void TryEnableBlurBehind(IntPtr hwnd, byte r, byte g, byte b, byte a)`

```csharp
    /// <summary>
    /// Real acrylic blur-behind needs two things together: telling DWM the whole
    /// client area is "glass" (so a plain WPF Background="Transparent" -- with
    /// AllowsTransparency left OFF -- actually shows DWM's composited blur
    /// through it instead of rendering opaque black), then the accent policy
    /// itself. AllowsTransparency="True" uses GDI layered windows instead of DWM
    /// composition and silently defeats this -- the window must not use it.
    /// </summary>
```

### Line 96
**Context**: `}`

```csharp
            // Best effort only -- the flat semi-transparent Border is the fallback.
```


## `Interop/ClickThrough.cs`

*Total comments: 2*

### Line 6
**Context**: `public static class ClickThrough`

```csharp
/// <summary>Makes a layered window pass mouse clicks through to whatever is behind it -- for the always-on status badges, which are informational only.</summary>
```

### Line 25
**Context**: `public static void Disable(IntPtr hwnd)`

```csharp
    /// <summary>Turns click-through back off -- needed while an Undo toast with a real, clickable button is showing.</summary>
```


## `Interop/CursorPos.cs`

*Total comments: 1*

### Line 6
**Context**: `public static class CursorPos`

```csharp
/// <summary>Wraps GetCursorPos -- used to detect when the mouse has moved away from a window that hid itself on hover (a hidden window can't raise its own MouseLeave).</summary>
```


## `Interop/DisplayMonitors.cs`

*Total comments: 6*

### Line 10
**Context**: `public readonly record struct DisplayInfo(string DeviceName, bool IsPrimary, Rect BoundsDiu, Rect WorkAreaDiu, string? FriendlyName);`

```csharp
/// <summary>WorkAreaDiu excludes the taskbar (and any other appbar docked to an edge) -- BoundsDiu is the full physical monitor rect, taskbar included.</summary>
```

### Lines 13-25
**Context**: `public static class DisplayMonitors`

```csharp
/// <summary>
/// Raw Win32 monitor enumeration, not System.Windows.Forms.Screen -- this app
/// has no other reason to reference WinForms (the tray icon uses raw
/// Shell_NotifyIcon, not NotifyIcon; see SystemTrayManager.cs), so adding that
/// whole assembly just for Screen would be a bigger footprint than a handful
/// of P/Invoke calls.
///
/// Bounds are converted from the physical pixels Win32 reports into WPF's
/// device-independent units (96 DPI) using THAT monitor's own actual DPI
/// (GetDpiForMonitor), not an assumption borrowed from the primary screen --
/// this is what makes multi-monitor placement come out correct even when
/// monitors run at different Windows scaling percentages.
/// </summary>
```

### Lines 107-124
**Context**: `private static string? TryGetMonitorFriendlyName(string gdiDeviceName)`

```csharp
    /// <summary>
    /// Real monitor model name (e.g. "AG276QZD"), the same thing OBS's own log
    /// shows -- not exposed by GetMonitorInfo/EnumDisplayMonitors at all, and
    /// EnumDisplayDevices' own DeviceString is usually just the generic driver
    /// name ("Generic PnP Monitor"), not the actual model. Getting the real
    /// name means walking one level further: EnumDisplayDevices with
    /// EDD_GET_DEVICE_INTERFACE_NAME turns the GDI device name into a device
    /// interface path (\\?\DISPLAY#AOCA601#5&amp;...#{GUID}), whose middle two
    /// segments are exactly the registry path components under
    /// Enum\DISPLAY\...\Device Parameters where Windows stores that monitor's
    /// raw EDID -- and the EDID itself (not the registry, not any Win32 call)
    /// is where the actual model name lives, as one of its descriptor blocks.
    /// Deliberately not using WMI's WmiMonitorID for this (same underlying
    /// data) to avoid pulling in System.Management as a dependency just for
    /// something raw P/Invoke + a registry read already gets us.
    /// Returns null (never throws) if anything along the way doesn't pan out --
    /// callers fall back to a generic "Display N" label in that case.
    /// </summary>
```

### Lines 133-135
**Context**: `string[] parts = monitorDevice.DeviceID.Split('#');`

```csharp
            // DeviceID looks like \\?\DISPLAY#AOCA601#5&2d8cb812&0&UID4353#{GUID} --
            // segments [1] and [2] are the hardware ID and instance ID Windows
            // uses as registry key names under Enum\DISPLAY.
```

### Lines 153-160
**Context**: `private static string? ParseEdidMonitorName(byte[] edid)`

```csharp
    /// <summary>
    /// EDID descriptor blocks live at fixed 18-byte offsets 54/72/90/108. A
    /// block that isn't a detailed timing descriptor starts with 00 00 00,
    /// followed by a tag byte -- 0xFC specifically means "Monitor Name",
    /// with up to 13 ASCII bytes after that, LF-terminated (0x0A) and
    /// space-padded. Not every monitor includes one (some only have a
    /// serial number or range-limits descriptor instead), hence nullable.
    /// </summary>
```

### Line 183
**Context**: `public static DisplayInfo Resolve(string? deviceName)`

```csharp
    /// <summary>Falls back to the primary display if deviceName is empty/null, or no longer matches a connected monitor (e.g. it was unplugged).</summary>
```


## `Interop/FirewallRules.cs`

*Total comments: 7*

### Lines 8-24
**Context**: `public static class FirewallRules`

```csharp
/// <summary>
/// Adds Windows Firewall allow rules for Backtrack's peer-to-peer clip
/// sharing (Pairing/PairingService.cs): UDP BroadcastPort for discovery
/// (StartDiscoveryListener runs unconditionally on every launch, whether or
/// not "Share my clips" is on -- the Settings screen's discovered-devices
/// list needs to keep hearing announcements regardless), and TCP
/// DefaultPairingPort for the actual pairing/data connection (only
/// listened on when sharing is enabled, but allowed either way so turning
/// sharing on later doesn't need a second elevation).
///
/// Same "one UAC prompt for everything, not a whole elevated app" shape as
/// RamDisk.InstallDriverElevated -- see that method's own comment for the
/// fuller reasoning on why a cmd wrapper script instead of running netsh
/// directly (RedirectStandardOutput/Error can't be combined with
/// UseShellExecute+runas, so a wrapper redirecting to a log file is the
/// only way to see WHY a failure failed instead of just an exit code).
/// </summary>
```

### Lines 27-31
**Context**: `private const string InboundUdpRuleName = "Backtrack Discovery (UDP-In)";`

```csharp
    // Both directions for both ports, per port, named individually rather
    // than one combined rule -- Windows Firewall rules are inbound XOR
    // outbound (dir=in/out can't be combined in one rule), and separate
    // names make each one individually visible/removable in Windows
    // Defender Firewall's own UI if a user ever goes looking.
```

### Lines 37-46
**Context**: `public static (bool Success, string? Error) AddRulesElevated()`

```csharp
    /// <summary>
    /// Runs all four `netsh advfirewall firewall add rule` calls elevated,
    /// in one UAC prompt. Scoped to this exe's own path (program="..."),
    /// not a bare port-based rule that would open the port for any process,
    /// so the rules stay specific to Backtrack. Safe to call more than
    /// once -- netsh doesn't error on a duplicate rule name, it just adds
    /// another one -- but the caller (App.xaml.cs) only ever calls this
    /// once, gated by AppSettings.FirewallRulesAttempted, precisely to
    /// avoid that duplication in the normal case.
    /// </summary>
```

### Line 86
**Context**: `if (proc is null || proc.ExitCode != 0)`

```csharp
            catch { /* best effort -- still report the exit code below either way */ }
```

### Line 100
**Context**: `return (false, "Admin permission was declined, so the firewall rules weren't added. Clip sharing with another PC may be blocked until they're added manually or Backtrack is allowed to try again.");`

```csharp
            // ERROR_CANCELLED -- user said no to the UAC prompt.
```

### Line 109
**Context**: `try { File.Delete(logPath); } catch { /* best effort cleanup */ }`

```csharp
            try { File.Delete(wrapperPath); } catch { /* best effort cleanup */ }
```

### Line 110
**Context**: `}`

```csharp
            try { File.Delete(logPath); } catch { /* best effort cleanup */ }
```


## `Interop/FullscreenDetector.cs`

*Total comments: 13*

### Lines 8-42
**Context**: `public static class FullscreenDetector`

```csharp
/// <summary>
/// Answers the two questions StatusOverlay.Reposition() needs to decide
/// whether to avoid the taskbar or drop to the true screen edge: "is a
/// fullscreen app currently occupying the monitor the indicator itself
/// lives on" and "is the taskbar set to auto-hide at all".
///
/// This is the THIRD design. The first (enumerate every top-level window,
/// anywhere) kept false-positiving on invisible, monitor-sized windows
/// that never actually take focus: Windows 11's own shell-infrastructure
/// host windows (explorer.exe's XamlExplorerHostIslandWindow, TextInputHost's
/// CoreWindow), and other apps' own click-through in-game overlays
/// (Discord's "Discord Overlay" window, and the same trick Steam/GeForce
/// Experience/RTSS/Xbox Game Bar all use). The second (just
/// GetForegroundWindow()) fixed that, but broke the moment a SECOND
/// monitor had real OS focus: a still-fullscreen game sitting unfocused
/// on the indicator's own monitor isn't "foreground" anymore once the user
/// clicks over to a window on a different monitor, even though it's still
/// sitting there fullscreen, taskbar still hidden underneath it.
///
/// This version walks EnumWindows' own natural Z-order (top-to-bottom,
/// same visitation order as the first design) but stops at the FIRST real,
/// non-excluded window that's actually on the TARGET monitor -- i.e.
/// "whatever's currently showing on top of that specific monitor",
/// independent of which monitor real OS focus happens to be on right now.
/// This also naturally covers "Backtrack's own HUD is focused" for free,
/// without a separate special case: Backtrack's own windows are excluded
/// from counting either way, so the walk just continues past them to
/// whatever real window is underneath.
///
/// Also deliberately NOT SHQueryUserNotificationState for the fullscreen
/// check specifically -- it only reports true D3D EXCLUSIVE fullscreen,
/// which most modern games don't actually use; "borderless fullscreen" (a
/// plain chrome-less window sized exactly to the monitor) is invisible to
/// it entirely, and is what actually needed catching here.
/// </summary>
```

### Lines 78-82
**Context**: `private const int WS_EX_TRANSPARENT = 0x00000020;`

```csharp
    // Marks a window click-through -- input passes straight to whatever's
    // behind it. Discord/Steam/GeForce Experience/RTSS/Xbox Game Bar's own
    // in-game overlays all use exactly this, which is how "Discord Overlay"
    // ended up ahead of the real game in Z-order and got picked up as the
    // topmost window on the monitor before this filter existed.
```

### Lines 131-149
**Context**: `private static readonly HashSet<string> ExplorerShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)`

```csharp
    // Explorer and a few of its satellite host processes represent the
    // plain desktop itself while nothing else has focus -- their own
    // top-level window is legitimately monitor-sized (Progman/WorkerW,
    // excluded by class name below), so without this the ordinary "nothing
    // is focused, I'm looking at my desktop" state would itself register
    // as fullscreen.
    // explorer.exe is ambiguous on its own -- it's ALSO the process behind
    // every ordinary File Explorer folder window (class "CabinetWClass")
    // the user might open to browse files, which obviously isn't "the
    // taskbar is up" just because it happens to be focused, even on a
    // completely different monitor than the indicator's own. Used only by
    // IsShellSurfaceActive (IsFullscreenAppOnMonitor's own Z-order walk
    // never needed this distinction -- a folder window isn't monitor-sized
    // either way, so it was never going to false-positive as "fullscreen"
    // there in the first place). Real shell surfaces confirmed live via the
    // diagnostic log (idle desktop, Start menu/Search/Task View/Alt+Tab
    // switcher all sharing "XamlExplorerHostIslandWindow", plus the
    // taskbar's own window classes never actually seen directly foreground
    // but included for completeness).
```

### Lines 171-172
**Context**: `private static string? _lastLoggedShellState;`

```csharp
    // Edge-triggered ("only when it actually changes") diagnostic logging,
    // separated by detector method so they don't overwrite each other's cache key on every tick.
```

### Lines 176-182
**Context**: `public static bool IsTaskbarAutoHideEnabled()`

```csharp
    /// <summary>
    /// True when the taskbar is set to auto-hide at all (a static setting,
    /// not "is it visible on screen at this exact instant") -- an
    /// auto-hidden taskbar never reserves WorkArea space regardless of
    /// whether it's momentarily revealed by a hover, so there's nothing
    /// meaningful to avoid either way.
    /// </summary>
```

### Lines 197-210
**Context**: `public static bool IsShellSurfaceActive()`

```csharp
    /// <summary>
    /// True when the REAL foreground/active window belongs to Windows shell
    /// infrastructure right now -- opening the Start menu, Search, or the
    /// taskbar itself over a fullscreen game genuinely moves real OS focus
    /// to one of these, which is a reliable signal nothing else gives:
    /// unlike IsFullscreenAppOnMonitor's own Z-order walk (which
    /// deliberately skips PAST these same processes, since they can sit
    /// ahead of a normal game in Z-order even during completely ordinary
    /// play, not just when actually summoned), real Windows focus only
    /// ever lands on one of these when the user is genuinely interacting
    /// with it. This is what actually answers "is the taskbar visibly up
    /// right now", separate from "is a fullscreen app still sitting there
    /// underneath everything".
    /// </summary>
```

### Lines 232-235
**Context**: `if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && !ExplorerShellWindowClasses.Contains(className))`

```csharp
            // explorer.exe specifically needs a class check too -- see
            // ExplorerShellWindowClasses' own comment. A regular File
            // Explorer folder window (anywhere, including a completely
            // different monitor than this one) isn't the taskbar.
```

### Lines 242-249
**Context**: `if (title == "Task Switching")`

```csharp
            // The Alt+Tab switcher is hosted by the exact same generic
            // explorer.exe XAML-island class the real Start menu/Search/
            // Task View surfaces use ("XamlExplorerHostIslandWindow"), so
            // it can't be told apart by process or class alone -- found via
            // the diagnostic log flagging its own internal title, "Task
            // Switching", while alt-tabbing kept incorrectly reading as
            // "the taskbar is up" the same way genuinely opening the Start
            // menu does.
```

### Lines 265-277
**Context**: `public static bool IsFullscreenAppOnMonitor(string? deviceName)`

```csharp
    /// <summary>
    /// True when the topmost real window actually sitting on the given
    /// monitor (matched by DisplayMonitors' own device-name format;
    /// null/empty matches the first real window found on any monitor) is
    /// fullscreen-sized -- independent of whether that window currently has
    /// real OS focus. "Real" excludes Backtrack's own windows, Windows
    /// shell infrastructure, click-through overlays, and the desktop itself
    /// (see their own comments); the walk continues past a window on a
    /// DIFFERENT monitor rather than stopping there, since a window being
    /// earlier in Z-order globally doesn't mean it's the topmost thing on
    /// THIS monitor specifically. Never throws; defaults to "no" on any
    /// failure or if nothing real is found on the monitor at all.
    /// </summary>
```

### Lines 290-292
**Context**: `if (GetWindow(hWnd, GW_OWNER) != 0)`

```csharp
                // Only real top-level windows -- owned windows (tooltips,
                // combo dropdowns, etc.) can report odd/stale bounds and
                // were never "what's showing on this monitor" anyway.
```

### Lines 300-305
**Context**: `nint hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);`

```csharp
                // Monitor resolved BEFORE the shell/desktop checks below, on
                // purpose: those checks now STOP the walk (not skip past
                // it) once we know the window is genuinely topmost on OUR
                // monitor specifically -- a shell window sitting topmost on
                // some OTHER monitor is irrelevant and must keep the walk
                // going, not stop it early.
```

### Lines 327-330
**Context**: `bool gotDwmBounds = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT windowRect, Marshal.SizeOf<RECT>()) == 0;`

```csharp
                // First real, non-excluded window actually on the target
                // monitor -- since EnumWindows visits in Z-order (topmost
                // first), this IS whatever's currently showing on top of
                // that monitor, regardless of where real OS focus is.
```

### Line 383
**Context**: `return "unknown";`

```csharp
            // Process may have exited between the enumeration and this lookup, or access denied -- not worth failing the whole check over.
```


## `Interop/GlobalHotkey.cs`

*Total comments: 2*

### Line 8
**Context**: `public sealed class GlobalHotkey : IDisposable`

```csharp
/// <summary>Registers a real OS-level hotkey (RegisterHotKey/WM_HOTKEY), not a Chromium accelerator.</summary>
```

### Line 47
**Context**: `public void Rebind(Modifiers modifiers, uint virtualKey)`

```csharp
    /// <summary>Switches to a different key combo without needing to recreate the window hook -- restores the previous combo if the new one is already taken elsewhere, rather than leaving no hotkey registered at all.</summary>
```


## `Interop/RamDisk.cs`

*Total comments: 18*

### Lines 10-24
**Context**: `public static class RamDisk`

```csharp
/// <summary>
/// Installs (once) and mounts/unmounts an ImDisk-backed RAM disk, so OBS's
/// Replay Buffer can write its (large, constantly-rewritten) buffer file to
/// memory instead of a real drive -- writing a multi-GB buffer file to RAM is
/// near-instant vs. several seconds to even a fast SSD. The small final
/// trimmed clip still lands on persistent storage afterward; see
/// obs-replay-slider's "Move clips to:" destination folder, which MainWindow
/// points at ClipsFolder once this disk is mounted.
///
/// ImDisk (ltr-data.se), not a custom driver: free, open source, and its
/// driver ships signed by a certificate Windows already trusts, so it works
/// under Secure Boot without this app needing its own signing setup. The
/// install package is bundled unmodified under ThirdParty/ImDisk per its
/// license (see the LICENSE.md/README.md alongside it).
/// </summary>
```

### Lines 27-29
**Context**: `private const string ServiceName = "ImDisk";`

```csharp
    // The actual kernel driver service is "ImDisk" (confirmed against
    // imdisk.inf's [DefaultInstall.ntamd64.Services] -- "ImDskSvc" alongside it
    // is a separate Win32 helper service, not the driver itself).
```

### Lines 32-35
**Context**: `public static bool IsDriverInstalled()`

```csharp
    // ServiceController.GetServices() only enumerates Win32-type services, not
    // kernel/file-system drivers -- it silently omits "ImDisk" (a
    // SERVICE_KERNEL_DRIVER) regardless of name, so checking the registry
    // directly is the only reliable way to see whether the driver is present.
```

### Lines 42-51
**Context**: `public static (bool Success, string? Error) InstallDriverElevated()`

```csharp
    /// <summary>
    /// Runs the vendor's own install.cmd elevated, with IMDISK_SILENT_SETUP=1 so
    /// it skips every message box. That env var is set inside the elevated shell
    /// itself (via "cmd /c set X=1&amp;&amp; script"), not passed in through
    /// ProcessStartInfo.EnvironmentVariables -- environment variables set there
    /// are not reliably delivered when UseShellExecute+runas hands the launch off
    /// to the elevation broker instead of this process spawning the child
    /// directly. This is the one and only UAC prompt the whole feature needs;
    /// everything else here runs unelevated.
    /// </summary>
```

### Lines 59-66
**Context**: `string logPath = Path.Combine(Path.GetTempPath(), $"backtrack-imdisk-install-{Guid.NewGuid():N}.log");`

```csharp
        // UseShellExecute+"runas" (needed to actually trigger the UAC prompt) can't
        // be combined with RedirectStandardOutput/Error -- .NET flatly disallows
        // that combination -- so there was no way to see WHY a failed install
        // failed beyond a bare exit code, ever. Routing through a temp wrapper
        // script that redirects install.cmd's own output to a log file (then
        // reading that file back afterward) is the standard workaround: it lets
        // the elevated process run exactly as before while still capturing what
        // it actually printed.
```

### Lines 75-81
**Context**: `$"call .\\install.cmd > \"{logPath}\" 2>&1\r\n" +`

```csharp
                // ".\install.cmd", not the bare filename -- cmd.exe's own command
                // resolution does NOT search the current directory for a plain
                // relative name (that's only how *interactive* typing behaves);
                // without the explicit ".\" prefix this fails with
                // "'install.cmd' is not recognized..." even though the file is
                // right there, and cmd.exe still exits 0, making the failure
                // invisible unless the log is actually checked.
```

### Line 102
**Context**: `if (proc is null || proc.ExitCode != 0)`

```csharp
            catch { /* best effort -- still report the exit code below either way */ }
```

### Line 114
**Context**: `return (false, "Admin permission was declined, so the ImDisk driver wasn't installed.");`

```csharp
            // ERROR_CANCELLED -- user said no to the UAC prompt.
```

### Line 123
**Context**: `try { File.Delete(logPath); } catch { /* best effort cleanup */ }`

```csharp
            try { File.Delete(wrapperPath); } catch { /* best effort cleanup */ }
```

### Line 124
**Context**: `}`

```csharp
            try { File.Delete(logPath); } catch { /* best effort cleanup */ }
```

### Lines 130-135
**Context**: `public static (bool Success, string? Error) Mount(char driveLetter, int sizeMb)`

```csharp
    /// <summary>
    /// Mounts (or re-mounts, if a stale one is already sitting on that drive
    /// letter) an NTFS RAM disk. No elevation needed -- imdisk.exe talks to the
    /// already-installed, already-running driver/service, which is configured
    /// to allow ordinary authenticated users to create/manage its own devices.
    /// </summary>
```

### Lines 144-151
**Context**: `RunImDisk($"-D -m {driveLetter}:");`

```csharp
            // A failed attempt (e.g. it ran out of memory partway through
            // formatting -- this is a real, size-dependent failure mode for a
            // memory-backed disk, not a bug) can still leave a half-created
            // device claiming this drive letter even though the volume itself
            // never came up, which would collide with the next attempt. Force
            // it off; -D (capital) works even when the plain -d unmount above
            // can't (the volume was never fully live, so there's nothing for a
            // graceful dismount to release).
```

### Lines 174-184
**Context**: `private static string TranslateMountError(string reason, int sizeMb)`

```csharp
    /// <summary>
    /// ImDisk's default RAM disk type is backed by the system's overall memory
    /// headroom (physical RAM + page file, tracked as Windows' "commit charge"),
    /// not a dedicated pool -- so "Not enough memory resources are available"
    /// can fire even with plenty of free physical RAM showing in Task Manager,
    /// if OTHER running apps have that headroom pinned down at the moment. It's
    /// also often transient: closing something memory-heavy (or just trying
    /// again a moment later) can be all it takes. Confirmed directly against
    /// this exact ImDisk error text, not a guess -- leads with the plain-English
    /// explanation but keeps ImDisk's own line for anyone who wants it.
    /// </summary>
```

### Lines 200-202
**Context**: `var psi = new ProcessStartInfo`

```csharp
        // Falls back to a bare PATH lookup if somehow not bundled (e.g. the driver
        // was already installed system-wide by something else) -- a real install
        // always registers imdisk.exe under System32, which is already on PATH.
```

### Lines 213-221
**Context**: `Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();`

```csharp
        // Both reads must be started before WaitForExit, and drained
        // concurrently, not one-after-the-other -- imdisk.exe writes to both
        // stdout and stderr, and reading either stream fully before starting the
        // other risks the classic redirect deadlock: the child blocks trying to
        // write to the stream nobody's draining yet, while this thread blocks
        // waiting for the stream it IS reading to hit EOF, which never happens
        // because the child is stuck. That's exactly what was hanging Mount/
        // Unmount (and freezing the whole UI, since these used to be called
        // directly on the UI thread) on anything past a trivially small disk.
```

### Lines 225-228
**Context**: `if (!proc.WaitForExit(timeoutMs))`

```csharp
        // A hard ceiling, not a normal-case expectation -- a RAM-backed quick
        // format should never actually take anywhere near this long. This is
        // just so a stuck imdisk.exe (for whatever reason) can't hang the whole
        // app indefinitely the way it did before the deadlock fix above.
```

### Line 231
**Context**: `return (-1, $"imdisk.exe didn't finish within {timeoutMs / 1000}s and was killed. Try a smaller size.");`

```csharp
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
```

### Lines 238-244
**Context**: `string combined = string.Join("\n", new[] { stdout, stderr }`

```csharp
        // Both, not "stdout, falling back to stderr only if stdout is empty" --
        // imdisk.exe prints its ordinary progress lines ("Creating device...")
        // to stdout and then the actual failure reason (e.g. "Error creating
        // virtual disk: Not enough memory resources are available...") to
        // stderr, so preferring stdout whenever it's non-empty was showing the
        // harmless progress line and silently dropping the one line that
        // actually explained what went wrong.
```


## `Interop/RecycleBin.cs`

*Total comments: 2*

### Line 5
**Context**: `public static class RecycleBin`

```csharp
/// <summary>Sends a file to the Recycle Bin instead of permanently deleting it -- free, real "undo" via Windows' own trash, no custom toast/undo system needed.</summary>
```

### Line 29
**Context**: `public static bool Delete(string path)`

```csharp
    /// <returns>true if the file was moved to the Recycle Bin successfully.</returns>
```


## `Interop/SelfUninstall.cs`

*Total comments: 3*

### Lines 8-14
**Context**: `public static class SelfUninstall`

```csharp
/// <summary>
/// Uninstalls Backtrack itself, from inside Backtrack -- Settings &gt;
/// Destructive &gt; Uninstall Backtrack. Reuses the exact UninstallString the
/// installer (installer/Program.cs) already wrote to the registry rather than
/// re-deriving the install dir/shortcut path here a second time, so this
/// can't drift out of sync with whatever the installer actually did.
/// </summary>
```

### Lines 25-32
**Context**: `public static (bool Success, string? Error) BeginUninstall()`

```csharp
    /// <summary>
    /// Launches a detached wrapper that waits for THIS process to actually
    /// exit before running the real uninstall command, then returns
    /// immediately so the caller can shut the app down. The uninstall
    /// command deletes this very exe's own folder -- Windows won't allow
    /// that while it's still loaded and running, so the ordering here isn't
    /// optional: start the wrapper, then quit, in that order, every time.
    /// </summary>
```

### Lines 42-44
**Context**: `string script =`

```csharp
        // The self-deleting-batch-file trick ("del %~f0" as the last line) --
        // standard cmd.exe idiom, works because cmd has already read the rest
        // of the file into memory before that line executes.
```


## `Interop/ShellDragHelper.cs`

*Total comments: 15*

### Lines 12-17
**Context**: `internal static class ShellDragHelper`

```csharp
/// <summary>
/// Provides OS-native drag-and-drop with custom drag image previews via the
/// Windows Shell's IDragSourceHelper and shell IDataObject.
/// The Windows OS handles rendering the drag preview natively at the cursor
/// with zero lag across all applications.
/// </summary>
```

### Line 20
**Context**: `[ComImport]`

```csharp
    // ── COM Interfaces & CoClasses ────────────────────────────────────────────
```

### Line 128
**Context**: `private const int GhostWidth  = 180;`

```csharp
    // ── Preview Dimensions ───────────────────────────────────────────────────
```

### Line 135
**Context**: `public static void DoFileDragDrop(`

```csharp
    // ── Public Drag API ──────────────────────────────────────────────────────
```

### Lines 137-139
**Context**: `public static void DoFileDragDrop(`

```csharp
    /// <summary>
    /// Initiates a drag-and-drop operation with a native OS drag preview image.
    /// </summary>
```

### Line 162
**Context**: `AttachDragImage(shellDataObj, thumbnail, label);`

```csharp
            // Attach drag image to the native shell IDataObject
```

### Line 164
**Context**: `dataObjectToUse = new System.Windows.DataObject(shellDataObj);`

```csharp
            // Wrap in WPF DataObject so WPF DragDrop pipeline can handle it
```

### Line 169
**Context**: `dataObjectToUse = new System.Windows.DataObject(DataFormats.FileDrop, filePaths);`

```csharp
            // Fallback to standard WPF FileDrop DataObject if shell interop fails
```

### Lines 187-189
**Context**: `public static void ResetDropHelper()`

```csharp
    /// <summary>
    /// Forces DropTargetHelper to leave and resets drop state, preventing stuck ghost images when overlay closes.
    /// </summary>
```

### Lines 201-203
**Context**: `public static void EnableDropPreview(UIElement element, Window window)`

```csharp
    /// <summary>
    /// Enables Windows Shell drag image display over a WPF window during drag-and-drop.
    /// </summary>
```

### Line 377
**Context**: `private static Bitmap? RenderGhostBitmap(ImageSource? thumbnail, string label)`

```csharp
    // ── GDI+ Preview Rendering ───────────────────────────────────────────────
```

### Line 390
**Context**: `bool drewThumb = false;`

```csharp
        // ── Thumbnail ─────────────────────────────────────────────────────────
```

### Line 408
**Context**: `using var labelBg = new SolidBrush(`

```csharp
        // ── Label background strip ─────────────────────────────────────────────
```

### Line 413
**Context**: `using var font = new Font(`

```csharp
        // ── Label text ────────────────────────────────────────────────────────
```

### Line 429
**Context**: `using var borderPen = new System.Drawing.Pen(`

```csharp
        // ── 1-px hard border ──────────────────────────────────────────────────
```


## `Interop/ToolWindow.cs`

*Total comments: 1*

### Lines 6-12
**Context**: `public static class ToolWindow`

```csharp
/// <summary>
/// Keeps a window out of Alt+Tab. ShowInTaskbar="False" alone only hides a window
/// from the taskbar -- Alt+Tab decides inclusion from the WS_EX_TOOLWINDOW/
/// WS_EX_APPWINDOW extended styles instead, which is why every one of this app's
/// windows (MainWindow plus the Status/Toast/Scrim/Disclaimer/Logo overlays) was
/// showing up as five or six separate entries despite ShowInTaskbar being off.
/// </summary>
```


## `Interop/WindowZOrder.cs`

*Total comments: 1*

### Lines 6-12
**Context**: `public static class WindowZOrder`

```csharp
/// <summary>
/// Brings a topmost window back to the front of the topmost band without giving
/// it keyboard focus. Needed because among several Topmost="True" windows, Windows
/// puts whichever one was most recently shown/activated at the front -- so showing
/// the Scrim after StatusOverlay/ToastOverlay were first shown at startup would
/// otherwise leave it covering them.
/// </summary>
```


## `Obs/ObsClient.cs`

*Total comments: 8*

### Lines 13-19
**Context**: `public sealed class ObsUnreachableException : Exception`

```csharp
/// <summary>
/// A hand-rolled obs-websocket v5 client over <see cref="ClientWebSocket"/> --
/// no third-party package, just the wire protocol: Hello -> Identify ->
/// Identified, then Request/RequestResponse pairs matched by requestId, plus
/// an Event stream for anything OBS pushes unprompted.
/// </summary>
/// <summary>Thrown when there's simply nothing to connect to (OBS closed, or its WebSocket server disabled) -- as opposed to a real protocol/auth error once a connection is actually made.</summary>
```

### Lines 44-46
**Context**: `throw new ObsUnreachableException("OBS isn't running, or its WebSocket server is off", ex);`

```csharp
            // Nothing there to connect to at all (OBS closed, or its WebSocket
            // server disabled) -- distinct from a real protocol/auth failure
            // below, which only happens once a connection is actually made.
```

### Lines 50-56
**Context**: `try`

```csharp
        // Everything below assumes the WebSocket-level connect above already
        // succeeded (_ws is open) -- if the obs-websocket handshake itself
        // fails (unexpected Hello, wrong password on Identified, etc.), that
        // open socket was previously left dangling: nothing closed or
        // disposed it, and RetryLoopAsync calls ConnectAsync again every 5s
        // forever while disconnected, leaking one real OS socket handle per
        // attempt. Catch and clean up here instead of leaving _ws open.
```

### Lines 69-72
**Context**: `["eventSubscriptions"] = 1023 | (1 << 16),`

```csharp
                // General|Config|Scenes|Inputs|Transitions|Filters|Outputs|SceneItems|MediaInputs|Vendors
                // ((1<<10)-1 = 1023) plus InputVolumeMeters (1<<16 = 65536) -- that one's a
                // separate "high-volume" category not included in the low bits at all,
                // needed for the mic status badge's live audio-level monitoring.
```

### Lines 99-104
**Context**: `private bool CleanupFailedConnectWebSocket()`

```csharp
    /// <summary>
    /// Disposes and clears a WebSocket left over from a failed post-connect
    /// handshake. Run from an exception filter (always returns false, so the
    /// original exception's stack trace is preserved) rather than a catch
    /// block, purely so cleanup happens without needing a separate rethrow.
    /// </summary>
```

### Lines 112-114
**Context**: `private static string ComputeAuthString(string password, string salt, string challenge)`

```csharp
    // Per the obs-websocket v5 auth spec:
    //   secret = base64(sha256(password + salt))
    //   authentication = base64(sha256(secret + challenge))
```

### Line 207
**Context**: `}`

```csharp
            // Connection dropped -- ObsService owns the reconnect policy.
```

### Line 230
**Context**: `}`

```csharp
                // best effort
```


## `Obs/ObsConfigReader.cs`

*Total comments: 3*

### Lines 7-11
**Context**: `public static class ObsConfigReader`

```csharp
/// <summary>
/// Reads (and, in one narrow case, writes) obs-websocket's own config file so
/// the user never has to copy/paste the password OBS generated for itself
/// into this app separately.
/// </summary>
```

### Lines 43-55
**Context**: `public static bool TryEnableServer()`

```csharp
    /// <summary>
    /// Local-mode only: silently flips server_enabled to true in this file
    /// when it's off, so the NEXT time OBS launches it just connects, no
    /// manual trip to Tools > WebSocket Server Settings required. Meant to be
    /// called only while OBS itself isn't running (see MainWindow's own
    /// gate) -- obs-websocket only reads this file at its own startup, so
    /// rewriting it while OBS is already open with the server off wouldn't
    /// take effect until a restart anyway, and risks a lost-update race if
    /// OBS happens to save its own settings back to this same file in
    /// between. Every other field (password, port, auth_required, ...) is
    /// passed through untouched. Returns true only if it actually changed
    /// something, so the caller can log/react once instead of every poll tick.
    /// </summary>
```

### Lines 85-91
**Context**: `if (!wroteServerEnabled)`

```csharp
                // The key not existing at all (not just being false) is the
                // same "off" as far as the caller's check above is concerned
                // (TryGetProperty returns false either way) -- rewriting the
                // loop above alone would silently never actually add it,
                // still returning true below, which looked "fixed" but
                // wasn't: the next poll would read it as off again and retry
                // forever, logging every tick.
```


## `Obs/ObsService.cs`

*Total comments: 36*

### Lines 11-22
**Context**: `public sealed record RecordRow(string Key, string Label, int Status, string SourceName, string FilterName, string Path = "", string Hotkey = "");`

```csharp
/// <summary>
/// One Source Record filter tracked by obs-replay-slider's ControlPanelDock -- see ListRecordRowsAsync.
/// Status: 0 Inactive (the underlying source isn't actively capturing anything,
/// e.g. a Window Capture with no window selected), 1 Stopped (capturing fine,
/// just not recording), 2 Recording, 3 Error (was recording, the output
/// stopped with a failure). SourceName/FilterName (not Label, which is the
/// "{source} - {filter}" display string) are for looking this filter's own
/// settings up via GetRecordRowDestinationFolderAsync. Hotkey is this
/// filter's own "Source Record Start Recording" OBS hotkey (set via OBS's
/// Settings > Hotkeys, same as any other), empty if unbound -- same idea as
/// ReplayRow.Hotkey above.
/// </summary>
```

### Lines 28-36
**Context**: `public sealed record EncoderOverloadInfo(bool ThisFilter, bool MainRecording, bool MainStream, bool MainReplayBuffer, string Source, string Filter);`

```csharp
/// <summary>
/// One or more of these is true whenever obs-source-record's own
/// check_encoder_overload (0.4.19+) detects a real recent dropped-frame rate
/// on that specific output -- see ObsService.EncoderOverloadDetected.
/// MainStream can also mean network/bandwidth congestion, not necessarily
/// the encoder itself; the plugin can't tell those apart for a streaming
/// output, only file outputs (recording/replay buffer/this filter) reliably
/// mean encoder-side lag.
/// </summary>
```

### Lines 41-51
**Context**: `public sealed class ObsService`

```csharp
/// <summary>
/// Thin faÃ§ade over <see cref="ObsClient"/>: owns the connect/retry loop and
/// exposes the handful of calls the overlay UI actually needs, including the
/// two custom requests the patched obs-replay-slider plugin exposes as an
/// obs-websocket vendor (see vendor/obs-replay-slider/src/websocket-bridge.cpp).
///
/// The OBS instance this talks to doesn't have to be on this PC -- e.g. a
/// two-PC setup where OBS runs on a separate stream/broadcast machine and this
/// overlay runs on the PC you actually sit at. <see cref="Reconfigure"/> lets
/// Settings point it at a different host without restarting the app.
/// </summary>
```

### Lines 60-61
**Context**: `private string? _micInputName;`

```csharp
    // Auto-detected on connect (first WASAPI mic-capture input OBS reports) --
    // no Settings UI to pick one, since "the" global mic is what's being asked for.
```

### Line 71
**Context**: `public event Action<bool, string?>? RecordingStateChanged;`

```csharp
    /// <summary>Fires the moment OBS's own recording output actually starts/stops -- event-driven, not the 1s poll. Path is only populated on stop.</summary>
```

### Line 74
**Context**: `public event Action<bool>? StreamingStateChanged;`

```csharp
    /// <summary>Fires the moment OBS's own stream output actually starts/stops -- event-driven, same idea as RecordingStateChanged.</summary>
```

### Line 77
**Context**: `public event Action<bool>? VirtualCamStateChanged;`

```csharp
    /// <summary>Fires the moment OBS's own Virtual Camera output actually starts/stops -- event-driven, same idea as StreamingStateChanged.</summary>
```

### Line 80
**Context**: `public event Action<string, string>? ReplaySaved;`

```csharp
    /// <summary>Fires when a Replay Slider row's buffer actually finishes saving: (rowKey, path). This is the real "yes, the clip exists now" confirmation.</summary>
```

### Lines 83-95
**Context**: `public event Action<string>? ReplaySaving;`

```csharp
    /// <summary>
    /// Fires the instant a row's save genuinely starts -- OBS itself just
    /// reported the raw (untrimmed) file landed, right before obs-replay-
    /// slider's own trim thread starts. Needs obs-replay-slider 0.2.20+;
    /// older builds simply never emit this, so a Processing toast still
    /// silently never shows on those the same way it always didn't (not a
    /// regression, no version check needed here -- ReplaySaved alone still
    /// covers the eventual "done" toast regardless of this event existing).
    /// Unlike ReplaySaved, fires for EVERY trigger (this dock's own Save
    /// button, a hotkey bound directly in OBS, or a save_row request) --
    /// previously the only "processing" signal Backtrack had was its own UI
    /// click handler, which obviously never fired for the other two.
    /// </summary>
```

### Line 98
**Context**: `public event Action<EncoderOverloadInfo>? EncoderOverloadDetected;`

```csharp
    /// <summary>Fires roughly every ~2s while obs-source-record detects a real recent dropped-frame rate somewhere (this filter, main recording, main stream, or main replay buffer) -- see EncoderOverloadInfo.</summary>
```

### Lines 112-113
**Context**: `_micInputName = null;`

```csharp
            // Can't verify mic state without a connection -- hide the badge
            // rather than show a stale reading from before the disconnect.
```

### Lines 124-127
**Context**: `string? state = stateEl.GetString();`

```csharp
            // obs-websocket fires this twice per transition (STARTING then STARTED,
            // STOPPING then STOPPED) -- both carry the same outputActive value, so
            // reacting to outputActive directly fired the toast twice per action.
            // outputPath is only populated once the state is definitively STOPPED.
```

### Line 136
**Context**: `string? state = streamStateEl.GetString();`

```csharp
            // Same double-fire-per-transition reasoning as RecordStateChanged above.
```

### Lines 145-147
**Context**: `string? state = vcamStateEl.GetString();`

```csharp
            // Same double-fire-per-transition reasoning as RecordStateChanged above.
            // Event name really is lowercase-c "Virtualcam" -- that's obs-websocket's
            // own spelling for this one, unlike "VirtualCam" everywhere else in its API.
```

### Lines 210-215
**Context**: `private static bool HasSignal(JsonElement levelsArray)`

```csharp
    /// <summary>
    /// inputLevelsMul is an array of per-channel [peak, magnitude, inputPeak]
    /// linear-multiplier readings -- rather than depend on the exact index
    /// meaning, "any of these numbers above a near-silence floor" is enough to
    /// call it "there's signal right now".
    /// </summary>
```

### Lines 230-234
**Context**: `public MicStatus GetMicStatus()`

```csharp
    /// <summary>
    /// Hidden when no mic is detected or things look fine; MutedOrQuiet takes
    /// priority over Silent since a deliberately muted/low mic isn't "dead",
    /// it's configured that way -- distinct icon for that case.
    /// </summary>
```

### Line 269
**Context**: `_micInputName = null;`

```csharp
            // No mic input, or a request failed mid-detection -- just hide the badge.
```

### Line 274
**Context**: `public void Start()`

```csharp
    /// <summary>Connects, and keeps retrying every 5s in the background if OBS isn't up yet.</summary>
```

### Line 283
**Context**: `public void Reconfigure(string url, string? password)`

```csharp
    /// <summary>Points this at a different OBS instance (e.g. switching between "this PC" and a remote stream PC) and reconnects immediately.</summary>
```

### Line 315
**Context**: `LastError = null;`

```csharp
                    // Just not there yet -- expected whenever OBS is closed, not a real error.
```

### Lines 333-336
**Context**: `d.TryGetProperty("outputPaused", out JsonElement op) && op.GetBoolean());`

```csharp
            // OBS legitimately freezes outputDuration while paused (paused time
            // doesn't count toward recording length) -- correct on OBS's end, but
            // Backtrack was never reading this flag at all, so a paused recording
            // just looked exactly like a broken/stuck timer with no explanation.
```

### Lines 346-348
**Context**: `public async Task StartMainRecordAsync() => await _client.RequestAsync("StartRecord");`

```csharp
    /// <summary>Explicit start/stop of OBS's own single global recording -- used by the
    /// "main" row on the Start Recording menu (see LoadRecordRowsAsync), where the
    /// current state is already known so a toggle would be redundant/racy.</summary>
```

### Lines 352-358
**Context**: `public async Task<string?> GetMainRecordDirectoryAsync()`

```csharp
    /// <summary>
    /// OBS's own single global recording output directory (Settings > Output >
    /// Recording Path) -- native obs-websocket requests, same idea as
    /// GetRecordRowDestinationFolderAsync/SetRecordRowDestinationFolderAsync
    /// for a Source Record filter's own path, just for the "Full Scene" row
    /// instead of a per-filter one. No plugin bridge involved.
    /// </summary>
```

### Lines 376-385
**Context**: `public async Task<ObsStats> GetStatsAsync()`

```csharp
    /// <summary>
    /// Raw counters behind OBS's own status-bar-style overload warnings --
    /// there's no request/event that hands over the literal status bar text
    /// or its timing (that's internal Qt UI logic with no public hook), but
    /// this is the same underlying data it's computed from, and it works over
    /// the same websocket connection regardless of whether OBS is local or on
    /// a remote transmitter PC (unlike, say, tailing OBS's own log file,
    /// which only a local install even has). Callers diff consecutive polls
    /// to get a recent/current rate rather than a lifetime average.
    /// </summary>
```

### Lines 404-411
**Context**: `if (!IsConnected)`

```csharp
        // IsRecordingOrStreamingAsync already guards its OWN call to this with
        // the same check, but two other callers (CheckAndApplyPluginUpdateAsync's
        // livestream-block, both inside and outside ApplyAsync) call this
        // directly -- OBS simply not running at all (very much the normal case
        // right before installing a plugin update) meant _client.RequestAsync
        // threw "Not connected to OBS" straight through, which those callers'
        // generic catch block then misread as an update failure (red status
        // dot) instead of "OBS isn't running, obviously not streaming".
```

### Line 418
**Context**: `public async Task<bool> GetVirtualCamActiveAsync()`

```csharp
    /// <summary>Same shape/reasoning as GetStreamActiveAsync above -- OBS's Virtual Camera output, a plain obs-websocket request with no plugin dependency.</summary>
```

### Lines 427-436
**Context**: `public async Task<bool> IsRecordingOrStreamingAsync()`

```csharp
    /// <summary>
    /// True if OBS is actually recording or streaming right now (main output,
    /// or a Source Record filter's own per-source recording) -- used to defer
    /// auto-updates rather than yank OBS out from under something genuinely
    /// being captured. Deliberately does NOT count a replay buffer just being
    /// armed (main or per-row, Status == 1) -- an armed-but-not-saving buffer
    /// isn't writing anything to disk that an update would interrupt, and
    /// buffers being armed is the normal resting state most of the time, so
    /// counting that here meant updates almost never applied automatically.
    /// </summary>
```

### Lines 452-454
**Context**: `return true;`

```csharp
            // Can't confirm nothing's active (bridge unreachable, request failed,
            // etc.) -- treat as active so a transient hiccup errs toward NOT
            // updating rather than risking an update mid-recording.
```

### Lines 487-494
**Context**: `public async Task<List<RecordRow>> ListRecordRowsAsync()`

```csharp
    /// <summary>
    /// One row per Source Record filter obs-replay-slider's ControlPanelDock is
    /// currently tracking -- distinct from ListReplayRowsAsync's rows: no "main"
    /// entry here (ControlPanelDock doesn't track OBS's own global recording),
    /// and each row is a start/stop toggle rather than a one-shot save. Needs
    /// the list_record_rows bridge PR merged into the plugin; older builds just
    /// return an empty list harmlessly.
    /// </summary>
```

### Lines 524-531
**Context**: `public async Task<string?> GetRecordRowDestinationFolderAsync(string sourceName, string filterName)`

```csharp
    /// <summary>
    /// Reads a Source Record filter's own configured output folder ("path" in
    /// its settings) via the plain obs-websocket GetSourceFilterList request --
    /// no plugin bridge involved, this is regular filter-settings data any
    /// obs-websocket client can already read. Returns null if the source/filter
    /// can't be found or has no path set (recordings stay wherever the filter's
    /// own default/relative location resolves to).
    /// </summary>
```

### Lines 556-557
**Context**: `}`

```csharp
            // Bridge/request unreachable -- just means the toast won't show a
            // folder this one time, not worth surfacing as an error.
```

### Line 638
**Context**: `public async Task SetReplayRowLengthAsync(string key, int seconds)`

```csharp
    /// <summary>Needs the set-row-length bridge PR merged into the plugin; older builds will just error.</summary>
```

### Lines 649-654
**Context**: `public async Task SetReplayRowDestDirAsync(string key, string path)`

```csharp
    /// <summary>
    /// Per-row override of SetReplayDestDirAsync below -- lets one specific
    /// buffer's clips land in their own subfolder instead of the shared clips
    /// folder. Empty path clears the override. Needs the set-row-dest-dir
    /// bridge PR merged into the plugin; older builds just error here harmlessly.
    /// </summary>
```

### Lines 665-672
**Context**: `public async Task SetReplayDestDirAsync(string path)`

```csharp
    /// <summary>
    /// Points the dock's "Move clips to:" destination folder at a path -- used to
    /// tell it to move trimmed clips off the RAM disk (see Interop/RamDisk.cs)
    /// onto persistent storage the moment trimming finishes. Needs the
    /// set-dest-dir bridge PR merged into the plugin; older builds just error
    /// here harmlessly. There's no manual fallback UI for this in the dock
    /// itself (removed) since this app is the only thing that ever sets it.
    /// </summary>
```

### Lines 683-689
**Context**: `public async Task SetReplayBufferDurationAsync(int seconds)`

```csharp
    /// <summary>
    /// Pushes a new replay_duration (seconds) onto every Source Record filter
    /// the plugin is currently tracking -- this is the buffer that gets
    /// flushed to disk in full on every save, not the trimmed clip length (see
    /// SetReplayRowLengthAsync for that). Needs the set-buffer-duration bridge
    /// PR merged into the plugin; older builds just error here harmlessly.
    /// </summary>
```

### Lines 700-703
**Context**: `public async Task RevertSourceRecordFilterPathsAsync(char driveLetter, string targetFolder)`

```csharp
    /// <summary>
    /// Queries all OBS sources for Source Record filters and updates any whose output path
    /// is set to the RAM disk drive back to the specified target folder via obs-websocket.
    /// </summary>
```


## `Obs/ReplayBufferSizing.cs`

*Total comments: 3*

### Lines 7-13
**Context**: `public static class ReplayBufferSizing`

```csharp
/// <summary>
/// Estimates a RAM disk size to safely hold OBS's replay buffer for a given
/// target duration, using OBS's own config on disk rather than a hardcoded
/// guess. Advisory only -- Backtrack never writes to any of these files, and
/// the result is just a suggested value for the user to review and apply by
/// hand in Settings, never auto-applied.
/// </summary>
```

### Lines 36-39
**Context**: `if (isSimple &&`

```csharp
            // Advanced mode, and Simple mode with multitrack video's auto bitrate,
            // both leave no single fixed bitrate number sitting in basic.ini --
            // only plain Simple output has one, so that's the only case read
            // directly rather than falling back to the resolution guess below.
```

### Lines 62-63
**Context**: `int suggestedMb = (int)Math.Ceiling(totalBytes * 1.3 / 1024 / 1024 / 256) * 256;`

```csharp
            // +30% headroom for NTFS overhead and the fact this is an estimate,
            // not a measurement; rounded up to a clean 256MB step.
```


## `Overlays/DisclaimerOverlay.xaml`

*Total comments: 2*

### Lines 14-26
**Context**: `size instead of wrapping mid-sentence; the explicit LineBreaks`

```xml
    <!--
      Bottom-center of the screen, but shown/hidden by MainWindow in lockstep
      with the HUD itself; only visible while the overlay is open, not an
      always-on fixture like StatusOverlay/ToastOverlay. No background, just
      centered text; the GitHub link needs real clicks so this isn't click-through.

      Deliberately NOT themed: this floats directly over arbitrary desktop/
      game content, not an app panel, so it needs to stay light/readable
      regardless of light/dark mode; same reasoning as the Player popup's
      back-button/title and seek thumb in MainWindow.xaml. Themed briefly
      during the theming pass, which turned it dark-on-dark-desktop in light
      mode; reverted back to fixed literal colors.
    -->
```

### Lines 27-29
**Context**: `<TextBlock TextAlignment="Center" TextWrapping="Wrap" FontSize="10.5" Foreground="#AEB4BD" MaxWidth="720">`

```xml
    <!-- Wide enough that the middle sentence fits on one line at this font
         size instead of wrapping mid-sentence; the explicit LineBreaks
         below are what control where it actually splits, not this. -->
```


## `Overlays/DisclaimerOverlay.xaml.cs`

*Total comments: 2*

### Lines 20-23
**Context**: `SizeChanged += (_, _) => Reposition();`

```csharp
        // SizeChanged, not Loaded -- a window that starts Visibility="Hidden" can
        // fire Loaded before its first real layout pass finishes, leaving
        // ActualWidth/Height at 0 and positioning this off-screen. SizeChanged
        // fires again once the real (wrapped-text) size is known, self-correcting.
```

### Line 28
**Context**: `public void Reposition()`

```csharp
    /// <summary>Re-reads the configured display -- called again from Settings if the user changes which monitor Backtrack shows on mid-session.</summary>
```


## `Overlays/LogoOverlay.xaml`

*Total comments: 2*

### Lines 12-20
**Context**: `briefly, then hands off to Backtrack's, which stays visible afterward.`

```xml
    <!--
      A fixed screen position, independent of MainWindow's own size/position:
      MainWindow resizes and moves a lot (compact pill vs. the big Gallery/Player
      panel), and the logo used to live inside it, so it dragged around and
      resized along with every screen change instead of staying put. Shown/hidden
      in lockstep with the HUD's own open/close state (see MainWindow.ToggleVisible/
      CloseOverlay), same as the Scrim and Disclaimer, not an always-on fixture
      like the Status/Toast overlays.
    -->
```

### Lines 21-34
**Context**: `<Grid>`

```xml
    <!-- Two logos, crossfading on each reveal: ilyambr's own brand mark holds
         briefly, then hands off to Backtrack's, which stays visible afterward.
         Same beats as ilyambr/bams' own brandBug intro animation (see
         PlayIntro in the code-behind for the exact keyframe timing), ported to
         a WPF Storyboard rather than CSS keyframes/JS class toggles.

         Opacity-only, no scale/zoom: animating a ScaleTransform on top of a
         Stretch="Uniform" brush sourced from a large image (ilyambr.png is
         1747x710) forces WPF to re-rasterize the bitmap every single frame,
         which was the actual cause of the animation looking laggy rather than
         smooth. A pure opacity crossfade is just alpha blending, much
         cheaper to render, and reads just as smooth for a logo handoff.
         CachingHint tells WPF to cache each logo's rendered tile instead of
         re-rendering it every frame purely because Opacity is animating. -->
```


## `Overlays/LogoOverlay.xaml.cs`

*Total comments: 4*

### Line 27
**Context**: `public void Reposition()`

```csharp
    /// <summary>Re-reads the configured display -- called again from Settings if the user changes which monitor Backtrack shows on mid-session.</summary>
```

### Lines 35-39
**Context**: `public void ShowWithIntro()`

```csharp
    /// <summary>
    /// Shows the window, playing the ilyambr -> Backtrack crossfade only the
    /// first time this is called since the app launched -- every later HUD open
    /// this session just shows the Backtrack logo directly, already settled.
    /// </summary>
```

### Lines 59-62
**Context**: `IlyambrLogo.BeginAnimation(UIElement.OpacityProperty, BuildTimeline(ease,`

```csharp
        // ilyambr: fades in by 12%, holds fully visible through 62%, then fades
        // back out to hand off to the Backtrack logo. Opacity only -- no scale/
        // zoom, since animating that on top of a large source image forced a
        // re-rasterize every frame and was the actual cause of the choppiness.
```

### Lines 66-68
**Context**: `BacktrackLogo.BeginAnimation(UIElement.OpacityProperty, BuildTimeline(ease,`

```csharp
        // Backtrack: stays hidden until ilyambr starts its exit (58%), then fades
        // in the rest of the way and holds there (BeginAnimation's default
        // FillBehavior keeps the final keyframe's value once it finishes).
```


## `Overlays/OverlayLogWindow.xaml`

*Total comments: 3*

### Lines 15-17
**Context**: `<Border Background="{DynamicResource PanelBgOpaque}" BorderBrush="{DynamicResource Hairline}" BorderThickness="1">`

```xml
    <!-- PanelBg/Hairline/Text0/Text2/Rec used to be defined locally here
         (PanelBg with its own opaque-ish alpha, #EC101113); now DynamicResource
         into the shared theme (PanelBgOpaque specifically, same alpha level). -->
```

### Lines 20-21
**Context**: `<TextBlock x:Name="ObsModeText" Text="" FontFamily="Consolas" FontSize="11" Foreground="{DynamicResource Text0}"`

```xml
            <!-- OBS mode: exactly one line, no scrolling; mirrors OBS's own
                 status bar showing one current condition at a time, not a history. -->
```

### Line 25
**Context**: `<ScrollViewer x:Name="BacktrackModeScroll" MaxHeight="62" VerticalScrollBarVisibility="Auto" Visibility="Collapsed">`

```xml
            <!-- Backtrack mode: fixed to ~3 lines tall, scrolls for anything older. -->
```


## `Overlays/OverlayLogWindow.xaml.cs`

*Total comments: 1*

### Line 93
**Context**: `public void SetObsLine(string text)`

```csharp
    /// <summary>Empty text triggers smooth fade out to hidden -- matches OBS's status bar clearing when idle.</summary>
```


## `Overlays/PairingRequestOverlay.xaml`

*Total comments: 1*

### Lines 14-19
**Context**: `<Border Background="{DynamicResource PanelBg}" BorderBrush="{DynamicResource Hairline}" BorderThickness="1" Padding="22,20">`

```xml
    <!--
      Not modal (no ShowDialog/Owner): a pairing request can arrive at any time,
      whether or not the HUD happens to be open, same reasoning as the other
      always-available overlays. Centered on screen and Topmost, but still a
      normal click-through-disabled window since Allow/Deny need real clicks.
    -->
```


## `Overlays/PairingRequestOverlay.xaml.cs`

*Total comments: 1*

### Line 23
**Context**: `public void ShowRequest(string deviceName, string code, Action onAllow, Action onDeny)`

```csharp
    /// <summary>Pops up centered and focused (unlike the passive overlays, this one needs a real decision), auto-denying after 60s so an ignored request doesn't sit there forever.</summary>
```


## `Overlays/RecentClipsOverlay.xaml`

*Total comments: 2*

### Lines 13-21
**Context**: `<Border Background="{DynamicResource PanelBg}" BorderBrush="{DynamicResource Hairline}" BorderThickness="1" Padding="8">`

```xml
    <!--
      Same "own top-level window" reasoning as StreamingStatusOverlay: MainWindow
      itself is AllowsTransparency="False" (needed for the VLC video surface),
      so anything wanting a genuinely separate floating box has to live here
      instead. Unlike StreamingStatusOverlay/LogoOverlay (passive, click-through,
      auto-positioned), this one is interactive on purpose: draggable via the
      grip on the left, and each tile is clickable/right-clickable, so it does
      NOT get ClickThrough.Enable in the code-behind.
    -->
```

### Lines 24-26
**Context**: `<Border x:Name="DragHandle" Width="18" Cursor="SizeAll" Background="Transparent"`

```xml
            <!-- Six-dot grip, the standard "this is draggable" affordance. MouseLeftButtonDown
                 here calls DragMove() in the code-behind; nothing else on this window drags,
                 only the grip, so accidentally clicking a tile never also moves the window. -->
```


## `Overlays/RecentClipsOverlay.xaml.cs`

*Total comments: 3*

### Line 12
**Context**: `public event Action<double, double>? PositionChanged;`

```csharp
    /// <summary>Fired once a drag actually completes (DragMove returns), not on every intermediate mouse-move -- MainWindow persists Left/Top from this into settings.</summary>
```

### Lines 19-20
**Context**: `Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);`

```csharp
        // No ClickThrough.Enable here -- see the XAML's own comment on why
        // this one, unlike StreamingStatusOverlay, needs to stay interactive.
```

### Lines 24-29
**Context**: `private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)`

```csharp
    /// <summary>
    /// DragMove() blocks until the mouse button is released (it runs its own
    /// message loop), so firing PositionChanged right after it returns is
    /// exactly "the drag just finished" -- no separate LocationChanged
    /// debounce needed the way a continuously-updating position would.
    /// </summary>
```


## `Overlays/ScrimOverlay.xaml`

*Total comments: 2*

### Lines 16-27
**Context**: `<Grid>`

```xml
    <!--
      Sits behind MainWindow, in front of everything else, whenever the HUD is
      summoned, dimming the desktop; and since it isn't click-through, unlike
      StatusOverlay/ToastOverlay) actually blocks clicks from reaching whatever
      is behind it. Clicking the dimmed area itself dismisses the HUD.

      Deliberately NOT themed: this dims whatever's behind it (desktop/game
      content), not an app panel, so it stays black regardless of light/dark
      mode; same reasoning for the exit button's own fixed dark chip and
      white X glyph, which floats over that dimmed backdrop, not over a
      themed surface.
    -->
```

### Lines 29-30
**Context**: `<Button x:Name="ExitButton" Width="34" Height="34" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="16,16,0,0"`

```xml
        <!-- A real, discoverable exit control at the screen's actual top-left
             corner (not just inside MainWindow's own small panel). -->
```


## `Overlays/ScrimOverlay.xaml.cs`

*Total comments: 5*

### Line 48
**Context**: `if (msg == WM_MOUSEACTIVATE)`

```csharp
        // Prevent Windows from ever promoting ScrimOverlay above MainWindow when clicked
```

### Line 57
**Context**: `public void Reposition()`

```csharp
    /// <summary>Re-reads the configured display and re-covers it -- called again from Settings if the user changes which monitor Backtrack shows on mid-session.</summary>
```

### Lines 69-72
**Context**: `public void ArmDismissCooldown(int ms = 400)`

```csharp
    /// <summary>
    /// Temporarily ignores background/scrim dismiss clicks for the specified duration (default 400ms).
    /// Used when opening or switching tabs/screens so fast double-clicks don't immediately exit or crash the player.
    /// </summary>
```

### Line 78
**Context**: `private void Scrim_MouseDown(object sender, MouseButtonEventArgs e)`

```csharp
    // Any click on the dim area dismisses -- not just left.
```

### Lines 107-115
**Context**: `public void SetExitButtonVisible(bool visible) =>`

```csharp
    /// <summary>
    /// Player fullscreen deliberately covers this window's own top-left
    /// corner with the video, but this button sits in a separate Topmost
    /// window from MainWindow -- whether it actually ends up hidden behind
    /// the video depends on exact window bounds/z-order lining up, which
    /// fullscreen's letterboxing can't always guarantee. Collapsing it
    /// outright removes that dependency entirely; Escape and the in-video
    /// fullscreen-exit button both still reach the same close path.
    /// </summary>
```


## `Overlays/StatusOverlay.xaml`

*Total comments: 6*

### Lines 13-20
**Context**: `Orientation, and which edge(s) the strip anchors to, are both set from`

```xml
    <!--
      Always visible, independent of MainWindow's hidden/shown state; this is
      the "always-on-screen indicator" from the design, separate from the
      hotkey-summoned HUD on purpose. Click-through (see ClickThrough.Enable in
      the code-behind) so it never blocks clicks to the game underneath it;
      the code-behind also fades its own Opacity down the closer the real
      cursor gets, purely cosmetic since clicks already pass through regardless.
    -->
```

### Lines 21-30
**Context**: `<StackPanel x:Name="BadgesPanel" Orientation="Horizontal">`

```xml
    <!--
      Orientation, and which edge(s) the strip anchors to, are both set from
      code (StatusOverlay.xaml.cs's ApplyLayout, driven by AppSettings'
      StatusIndicatorOrientation/StatusIndicatorLocation) rather than fixed
      here: the window itself stays a fixed size (room for all four
      badges) specifically so that alignment alone, not a resize, is what
      keeps however many badges are actually showing anchored to the
      corner the user picked; see ApplyLayout's own comment for why a
      resize-based approach was rejected.
    -->
```

### Lines 32-44
**Context**: `<Border x:Name="ObsDisconnectedBadge" Width="27" Height="27" Background="{DynamicResource BadgeBg}" BorderBrush="{DynamicResource BadgeBorder}" BorderThickness="1"`

```xml
        <!-- Leads the strip (most urgent status first): red warning-triangle
             glyph. Font Awesome's SOLID "triangle-exclamation" (CC BY 4.0),
             not Google Material's outline one originally used here: the
             outline version's exclamation mark was a hairline stroke that
             disappeared at this badge's actual 27px size; this one cuts the
             "!" as solid negative space out of a filled triangle, bold at
             any size. "F1" prefix forces Nonzero fill rule, which is what
             that negative-space cutout actually depends on to render
             correctly (WPF's own path-mini-language default is EvenOdd).
             RefreshBadgeMargins handles actual spacing at runtime regardless
             of declaration order here (see its own comment), so this only
             needs to be first for the visual PRIORITY, not for any
             margin-math reason. -->
```

### Lines 50-56
**Context**: `<Border x:Name="EncoderOverloadBadge" Width="27" Height="27" Background="{DynamicResource BadgeBg}" BorderBrush="{DynamicResource BadgeBorder}" BorderThickness="1"`

```xml
        <!-- Same triangle-exclamation glyph as ObsDisconnectedBadge above, but
             its Fill is a plain SolidColorBrush assigned and animated from
             code-behind (SetEncoderOverloaded), not a DynamicResource: it
             needs to flash between RecDark and Rec on a continuous loop while
             an overload is ongoing, which a static DynamicResource lookup
             can't do. No Fill set here on purpose; the code-behind always
             assigns one before this badge is ever made visible. -->
```

### Lines 70-73
**Context**: `<Border x:Name="VirtualCamBadge" Width="27" Height="27" Background="{DynamicResource BadgeBg}" BorderBrush="{DynamicResource BadgeBorder}" BorderThickness="1"`

```xml
        <!-- Font Awesome solid "video" (CC BY 4.0): a plain camcorder
             silhouette, the closest free-icon match to "virtual camera"
             (Font Awesome doesn't have a distinct "webcam" glyph in its free
             set). On while OBS's own Virtual Camera output is active. -->
```

### Line 86
**Context**: `<Path Fill="{DynamicResource Rec}" Stretch="Uniform" Width="12" Height="12"`

```xml
                <!-- Real mic silhouette (capsule head + stand arc + base), not a placeholder shape. -->
```


## `Overlays/StatusOverlay.xaml.cs`

*Total comments: 16*

### Line 13
**Context**: `public enum StatusIndicatorOrientation { Horizontal, Vertical }`

```csharp
/// <summary>Which axis the status badges lay out along. Settings > Status Indicators > Orientation.</summary>
```

### Line 16
**Context**: `public enum StatusIndicatorLocation { TopLeft, TopRight, BottomLeft, BottomRight }`

```csharp
/// <summary>Which screen corner the status indicator anchors to. Settings > Status Indicators > Location.</summary>
```

### Line 21
**Context**: `private const double StripLength = 7 * 27 + 6 * 5;`

```csharp
    // 7 badges * 27px + 6 * 5px gaps between them.
```

### Lines 24-25
**Context**: `private const double FadeRadius = 140;`

```csharp
    // Distance (px) at which the fade starts -- full opacity outside this
    // radius, fading down smoothly to fully transparent right at the badges.
```

### Lines 29-37
**Context**: `private readonly DispatcherTimer _repositionPollTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };`

```csharp
    // Neither a real taskbar dock/undock nor a fullscreen app starting/
    // stopping raises any event this app can subscribe to (unlike
    // MainWindow's own SystemEvents.DisplaySettingsChanged hook for actual
    // resolution changes) -- a light poll is the only way to notice either
    // one happened and re-anchor accordingly. Reposition() itself is cheap
    // (a couple of Win32 calls plus a handful of property sets that no-op
    // when unchanged) -- 300ms was picked after 1s felt noticeably laggy
    // in practice (launching a game and visibly waiting up to a full
    // second for the indicator to drop down reads as broken, not just slow).
```

### Lines 39-41
**Context**: `private bool _horizontalLayout = true;`

```csharp
    // Set by ApplyLayout, read by RefreshBadgeMargins whenever a badge's own
    // Visibility changes later (SetRecording etc.) -- avoids an AppSettings.Load()
    // disk read on every single status tick just to know which gap Thickness to use.
```

### Lines 43-46
**Context**: `private Rect _lastLoggedScreenBounds;`

```csharp
    // Edge-triggered diagnostic logging for Reposition()'s own bounds math --
    // same "log it instead of guessing again" approach FullscreenDetector's
    // own _lastLoggedCulprit uses, here for whenever the WorkArea/Bounds
    // rect actually used to position this window changes.
```

### Lines 49-59
**Context**: `public bool IsHudOpen { get; set; }`

```csharp
    /// <summary>
    /// Set by MainWindow (ToggleVisible/CloseOverlay) whenever the HUD
    /// itself opens or closes. While it's open, this alone forces the
    /// indicator to the true screen edge, taskbar or not -- Backtrack's own
    /// overlay draws on top of everything else regardless of where the
    /// taskbar is, so there's nothing for it to actually collide with, and
    /// this is a simpler, always-correct answer than trying to figure out
    /// whether something ELSE happens to still be fullscreen underneath it
    /// (which is what FullscreenDetector.IsFullscreenAppOnMonitor is for --
    /// still used below for when the HUD is closed).
    /// </summary>
```

### Line 80
**Context**: `public void Reposition()`

```csharp
    /// <summary>Re-reads the configured display AND the orientation/location/corner settings -- called again from Settings whenever any of those change mid-session, not just the display, and from _repositionPollTimer every second to catch a taskbar/fullscreen change nothing else notifies this window about.</summary>
```

### Lines 86-100
**Context**: `bool dropToEdge = IsHudOpen ||`

```csharp
        // Drop straight to the true monitor edge (BoundsDiu, taskbar
        // ignored) whenever the HUD itself is open (see IsHudOpen's own
        // comment -- unconditional, doesn't matter what's on screen behind
        // it), OR a fullscreen app is the topmost real thing on THIS
        // indicator's own monitor (independent of which monitor real OS
        // focus is actually on right now -- see IsFullscreenAppOnMonitor's
        // own comment) AND the taskbar/Start menu/Search isn't the thing
        // that ACTUALLY has real focus right now (IsShellSurfaceActive --
        // opening the taskbar over a fullscreen game moves real Windows
        // focus there, which the Z-order walk alone can't see: it
        // deliberately skips past these same processes since they can sit
        // ahead of a normal game in Z-order even during ordinary play).
        // Otherwise avoid the taskbar (WorkAreaDiu) UNLESS it's set to
        // auto-hide, which never reserves space to avoid in the first place
        // regardless of whether it's currently peeking out.
```

### Lines 119-132
**Context**: `private void ApplyLayout(StatusIndicatorOrientation orientation, StatusIndicatorLocation location)`

```csharp
    /// <summary>
    /// The strip is a fixed size (room for all badges) regardless of
    /// orientation or how many badges happen to be visible right now --
    /// SizeToContent would need Reposition() re-run on every single
    /// SetRecording/SetStreaming/SetVirtualCamActive/SetReplayOnline/
    /// SetMicStatus/SetObsDisconnected/SetEncoderOverloaded call just to
    /// stay anchored to a right/bottom corner as the content shrinks or
    /// grows, since a resize on those edges moves the window's own Left/Top.
    /// A fixed frame means only BadgesPanel's alignment (toward whichever
    /// edge(s) the corner setting points at) needs to change; the individual
    /// badges already collapse in place within it, same as the original
    /// hardcoded top-right layout always relied on (see the XAML's own
    /// comment on why a fixed frame + alignment, not resize, is deliberate).
    /// </summary>
```

### Lines 150-162
**Context**: `private void RefreshBadgeMargins()`

```csharp
    /// <summary>
    /// Gives every VISIBLE badge a leading margin except the first VISIBLE
    /// one, not just the first one BY INDEX -- badges collapse independently
    /// (SetRecording/SetStreaming/etc., not just at layout time), so with a
    /// plain "index 0 gets no margin" rule, whichever badge happens to be
    /// first in the StackPanel (Rec) getting hidden left the next visible
    /// one (Stream) still carrying its normal leading-gap margin, reading as
    /// a real dead gap between the strip's own top/left edge and its first
    /// visible badge (worse in vertical mode, where that space reads as a
    /// gap down from the corner rather than a subtle horizontal nudge).
    /// Called from ApplyLayout (orientation/corner changed) and from every
    /// Set* method below (a badge's own visibility changed).
    /// </summary>
```

### Line 177
**Context**: `private void UpdateFadeByProximity()`

```csharp
    /// <summary>Eases Opacity toward a target based on cursor distance each tick, instead of snapping Visibility on enter/leave.</summary>
```

### Lines 199-209
**Context**: `public void SetEncoderOverloaded(bool overloaded)`

```csharp
    /// <summary>
    /// Dark-red/light-red flashing warning-triangle, shown for as long as
    /// MainWindow's own edge-triggered overload check (see its
    /// EncoderOverloadDetected handler) considers a real overload ongoing.
    /// The animation itself is built here, not in XAML, because it needs to
    /// flash between two THEMED colors (RecDark/Rec) -- a plain XAML
    /// Storyboard can't read DynamicResource values, and the two colors are
    /// read fresh from Application.Current.Resources each time this turns on
    /// rather than cached, same reasoning as ToastOverlay's own
    /// dynamically-built elements (see CLAUDE.md's theming section).
    /// </summary>
```

### Lines 233-238
**Context**: `if (EncoderOverloadIconPath.Fill is SolidColorBrush activeBrush)`

```csharp
            // Stop the clock explicitly rather than just collapsing the
            // badge -- a Forever animation on a brush nobody's looking at
            // anymore is a small but pointless ongoing CPU cost. (The next
            // time this turns back on, a brand new brush is created above,
            // so this isn't needed to get a clean dark-red restart -- it's
            // purely to actually stop the old clock.)
```

### Lines 274-276
**Context**: `if (status != _lastLoggedMicStatus)`

```csharp
        // Edge-triggered diagnostic logging (called every 1s from
        // MainWindow's _micTimer regardless of change) -- Settings >
        // Experimental > Diagnostics > Open Diagnostic Log.
```


## `Overlays/StreamingStatusOverlay.xaml`

*Total comments: 1*

### Lines 13-26
**Context**: `<Border Background="{DynamicResource PanelBg}" BorderBrush="{DynamicResource Hairline}" BorderThickness="1" Padding="10,7">`

```xml
    <!--
      Same reasoning as LogoOverlay/DisclaimerOverlay: MainWindow itself is
      AllowsTransparency="False" (needed for the VLC video surface it hosts
      to render at all; layered/AllowsTransparency="True" windows and
      HWND-hosted native content like that don't reliably mix), so anything
      that needs to render as a genuinely separate floating box with real
      transparent space around it, not just another element sharing
      MainWindow's own single opaque backdrop, has to be its own window
      like this one instead. Repositioned by MainWindow (see Reposition)
      every time MainWindow's own bounds change, so it tracks directly
      underneath the main pill wherever that currently is on screen, and
      shown/hidden in lockstep with the HUD's own open/close state, not an
      always-on fixture like Status/Toast.
    -->
```


## `Overlays/StreamingStatusOverlay.xaml.cs`

*Total comments: 1*

### Lines 21-30
**Context**: `public void Reposition(Rect mainWindowBounds)`

```csharp
    /// <summary>
    /// Centered directly underneath MainWindow's own current bounds -- unlike
    /// LogoOverlay/DisclaimerOverlay, which sit at a fixed spot on the
    /// display independent of MainWindow, this one is meant to read as
    /// attached to the main pill specifically, so it has to follow MainWindow
    /// around every time its size/position changes (compact pill vs. the big
    /// Gallery/Player panel vs. Settings' WideWidth, and moving between
    /// monitors). Called by MainWindow itself right after any of that
    /// changes.
    /// </summary>
```


## `Overlays/ToastOverlay.xaml`

*Total comments: 1*

### Lines 14-18
**Context**: `<StackPanel x:Name="ToastStack" Margin="0,0,0,0"/>`

```xml
    <!--
      Always-on, left side of the screen, independent of MainWindow; same
      reasoning as StatusOverlay, notifications about what just happened
      shouldn't disappear just because the hotkey HUD is hidden.
    -->
```


## `Overlays/ToastOverlay.xaml.cs`

*Total comments: 23*

### Lines 17-24
**Context**: `private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3E, 0xCF, 0x8E));`

```csharp
    // Rec/Stream/Green/Warning/Accent are brand/status accent colors, deliberately
    // IDENTICAL in both themes (see Theme.Dark.xaml's own comment on this), so
    // caching them once as static brushes is fine. PanelBg/Hairline/Text0/Text2
    // are neutrals that DO differ by theme -- toasts are built entirely in code
    // (not XAML), so they can't use DynamicResource; instead these are looked
    // up from the CURRENT theme dictionary at the moment each toast is built
    // (see ThemeBrush below), so a runtime theme swap is picked up by the next
    // toast shown rather than needing a cached brush to somehow update itself.
```

### Lines 74-79
**Context**: `UIElement icon = started`

```csharp
        // A real shape instead of a bigger text glyph for the "started" dot --
        // matching the record tile's own Ellipse gives pixel-exact size and
        // centering, where a bigger font glyph (tried first) doesn't reliably
        // center against a TextBlock's line and inflates the row's own height
        // to fit its larger em-box. Explicit Width/Height keeps it fixed at 10px
        // regardless of the row's height, so it can't grow the toast at all.
```

### Lines 93-100
**Context**: `public void ShowRemotePcDisconnected(string ip)`

```csharp
    /// <summary>
    /// 10s, not the usual 4s -- this one means "whatever you were about to
    /// do with that PC's clips just stopped working," worth a longer look
    /// than a routine confirmation toast. Same red warning-triangle glyph
    /// as StatusOverlay's own ObsDisconnectedBadge (Google Material Icons,
    /// Apache 2.0), a real vector Path rather than GlyphIcon's Unicode
    /// character so it's a pixel-exact visual match, not just similar.
    /// </summary>
```

### Lines 103-107
**Context**: `var icon = new Path`

```csharp
        // Font Awesome solid "triangle-exclamation" (CC BY 4.0), not Google
        // Material's outline glyph originally used here -- see
        // StatusOverlay.xaml's ObsDisconnectedBadge comment for why (the
        // outline "!" is a hairline stroke, invisible at small sizes; this
        // one cuts it as solid negative space out of a filled triangle).
```

### Line 123
**Context**: `UIElement icon = started`

```csharp
        // Same real-Ellipse-for-the-started-dot reasoning as ShowRecording above.
```

### Lines 183-186
**Context**: `private readonly Dictionary<string, (Border Toast, Border Fill, DispatcherTimer Timer)> _processingToasts = new();`

```csharp
    // Keyed by row key, not label -- CompleteProcessingClip needs to find the
    // right toast again once ReplaySaved fires for that same key, and two
    // rows can share a label in principle (nothing enforces uniqueness on
    // obs-replay-slider's side).
```

### Lines 190-206
**Context**: `public void ShowProcessingClip(string key, string label)`

```csharp
    /// <summary>
    /// Fired the moment a row's Save button is clicked, before the actual work
    /// is even confirmed started -- obs-replay-slider's save_row request
    /// returns almost instantly (it just flushes the buffer), but the real
    /// trim down to the requested clip length happens afterward, on a
    /// background thread on the OBS side, with zero progress signal
    /// Backtrack can see (that background thread is C++/Qt on the OBS
    /// process, not something this app has any visibility into). So the
    /// fill bar here is a SIMULATED estimate, not real progress: eases up to
    /// 100% over ~6s (long enough for a typical short clip to genuinely
    /// finish inside that window) and then just holds there, full, for
    /// however much longer a big buffer's real trim actually needs, rather
    /// than snapping back to 0 or lying about being done. Unlike every other
    /// toast in this file, this one does NOT auto-dismiss on a timer --
    /// CompleteProcessingClip (called once the matching ReplaySaved event
    /// actually arrives) is the only thing that removes it.
    /// </summary>
```

### Lines 241-242
**Context**: `var row = new StackPanel { Orientation = Orientation.Horizontal };`

```csharp
        // Same glyph as ShowReplaySaved's own icon -- grey instead of green
        // is what marks this as "still in progress, not the success state".
```

### Lines 247-250
**Context**: `var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };`

```csharp
        // Fills UP toward 100%, opposite direction from Show()'s own
        // progressFill (which counts DOWN to 0 as a dismiss timer) -- same
        // visual language (a bar at the bottom of the card), different
        // meaning, so this can't just reuse Show()'s timer logic as-is.
```

### Lines 301-310
**Context**: `public void CompleteProcessingClip(string key, string label, string resolvedPath)`

```csharp
    /// <summary>
    /// Called once the matching ReplaySaved event actually arrives. Rather
    /// than yanking the processing toast out and dropping ShowReplaySaved in
    /// its place regardless of whatever percentage the simulated bar
    /// happened to be sitting at (jarring if it was caught mid-ramp, e.g. a
    /// short clip that finished before the ~6s ease-out settled), this
    /// quick-finishes that SAME bar to full over ~250ms first, then swaps to
    /// the completed toast once it visually reads as "done" rather than
    /// "interrupted".
    /// </summary>
```

### Lines 317-319
**Context**: `ShowReplaySaved(label, resolvedPath);`

```csharp
            // No processing toast was showing for this key (e.g. an
            // OBS-hotkey-triggered save -- see ShowProcessingClip's own
            // comment) -- nothing to finish, just show the normal toast.
```

### Line 345
**Context**: `private void RemoveProcessingToast(string key)`

```csharp
    /// <summary>Immediate removal, no finish-animation -- only for ShowProcessingClip's own re-click dedup, where there's no label/path to hand off to a completion toast anyway.</summary>
```

### Lines 364-369
**Context**: `private readonly Dictionary<string, Border> _updateInProgressToasts = new();`

```csharp
    // Keyed by component name ("Replay Slider", "Source Record", "Backtrack")
    // -- same grouping idea as _processingToasts: ShowUpdateInProgress/
    // ShowUpdateApplied for the SAME component share one toast slot instead
    // of stacking as two independent toasts, and two DIFFERENT components
    // updating around the same time (see CheckForUpdatesAsync's batch) each
    // still only ever show one toast at a time, not one per call.
```

### Line 378
**Context**: `public void ClearUpdateInProgress(string component)`

```csharp
    /// <summary>Removes component's in-progress toast without showing anything in its place -- for a failed/aborted update, so it doesn't sit there claiming to still be updating forever.</summary>
```

### Line 388
**Context**: `public void ShowOldClipsAutoDeleted(int count, int afterDays) =>`

```csharp
    /// <summary>Fired after RunAutoDeleteOldClips actually removes something -- Settings > Clips > Auto-delete old clips.</summary>
```

### Lines 402-405
**Context**: `public void ShowFirewallSetup() =>`

```csharp
    // Fired right before App.xaml.cs kicks off FirewallRules.AddRulesElevated
    // on a brand new install -- a UAC prompt appearing with zero warning,
    // moments after someone's very first launch, reads as suspicious with no
    // context for what's asking or why. This is that context, shown first.
```

### Lines 410-419
**Context**: `public void ShowUpdateInProgress(string component)`

```csharp
    /// <summary>
    /// Fired right before the download+install actually starts (which can
    /// take a while and, for a plugin, closes and relaunches OBS along the
    /// way) so it doesn't look like the app just silently glitched or hung.
    /// Persistent, no auto-dismiss timer like every OTHER toast here -- an
    /// install can genuinely take longer than the usual 4s, and this is
    /// meant to be replaced by ShowUpdateApplied (or cleared by
    /// ClearUpdateInProgress on failure) once this component's own update
    /// actually resolves, not to vanish on its own partway through.
    /// </summary>
```

### Line 475
**Context**: `public void ShowDeleteUndo(string clipName, Action onExpire, Action? onUndo = null) =>`

```csharp
    /// <summary>Shows a 5-second toast with sliding status indicator and Undo button; calls onExpire only if not undone.</summary>
```

### Lines 479-485
**Context**: `public void ShowMultiDeleteUndo(int count, Action onExpire, Action? onUndo = null) =>`

```csharp
    /// <summary>
    /// Same idea as ShowDeleteUndo, one toast for a whole batch instead of one
    /// per clip -- deleting several clips at once used to fire ShowDeleteUndo
    /// in a loop, stacking that many separate toasts (each with its own
    /// 60fps DispatcherTimer for the progress bar), which was the actual
    /// cause of the reported slowdown, not just visual clutter.
    /// </summary>
```

### Lines 506-508
**Context**: `var iconBlock = new System.Windows.Shapes.Path`

```csharp
        // Same Material "delete" icon as the Player screen's own Delete
        // button, not the old Segoe MDL2 Assets trash glyph -- that one
        // looked visually inconsistent with the rest of the icon set.
```

### Line 551
**Context**: `var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };`

```csharp
        // Sliding Status Indicator (Progress Bar at Bottom)
```

### Lines 613-621
**Context**: `private void Show(UIElement icon, Brush accentColor, string message, string? subMessage, double durationSec = 4.0, bool truncateSubMessage = false)`

```csharp
    /// <summary>
    /// truncateSubMessage: wrap is the general fix (long ordinary text stays
    /// fully readable across 2 lines instead of getting cut off), but a full
    /// file path (ShowRecording/ShowReplaySaved's "Saved at '...'") is
    /// exactly the case wrapping doesn't help -- it's long by nature, wraps
    /// mid-path with no natural break point, and just makes the toast
    /// visually messy. Those two ask for the old single-line ellipsis
    /// truncation back explicitly; everything else keeps wrapping.
    /// </summary>
```

### Line 660
**Context**: `var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };`

```csharp
        // Sliding Status Indicator (Progress Bar at Bottom)
```


## `Overlays/UpdatePromptOverlay.xaml`

*Total comments: 3*

### Lines 14-20
**Context**: `<Window.Resources>`

```xml
    <!--
      Deliberately NOT always-on like ToastOverlay/StatusOverlay: this is
      shown/hidden by MainWindow in lockstep with its own Show()/Hide() (see
      ToggleVisible/CloseOverlay), since "there's an update waiting for OBS to
      go idle" is only worth interrupting someone with while they've actually
      got the HUD open, not as a persistent always-visible nag.
    -->
```

### Lines 22-23
**Context**: `this is a separate window with its own resource scope, same reason`

```xml
        <!-- Text0/Text1/Rec used to be defined locally here; now DynamicResource
             into the shared theme, same reason as every other overlay window. -->
```

### Lines 25-27
**Context**: `<Style x:Key="FlatButton" TargetType="Button">`

```xml
        <!-- Same FlatButton style as MainWindow's, kept in sync by hand since
             this is a separate window with its own resource scope, same reason
             it's duplicated instead of shared. -->
```


## `Overlays/UpdatePromptOverlay.xaml.cs`

*Total comments: 2*

### Lines 8-16
**Context**: `public partial class UpdatePromptOverlay : Window`

```csharp
/// <summary>
/// Bottom-left, screen-anchored prompt shown when an update was found but
/// deferred because OBS is actively recording/streaming (see
/// ObsService.IsRecordingOrStreamingAsync and its callers in MainWindow). Unlike
/// ToastOverlay/StatusOverlay, this is NOT click-through and NOT always-on --
/// it needs to receive the Install button's clicks, and MainWindow shows/hides
/// it in lockstep with its own visibility (ToggleVisible/CloseOverlay) so it
/// only ever appears while the HUD is actually open, not as a persistent nag.
/// </summary>
```

### Line 35
**Context**: `public void ShowPrompt(string componentDisplayName, Action onInstall)`

```csharp
    /// <summary>Call only while MainWindow is visible -- see the class doc. onInstall runs once, from the button click, then the prompt hides itself.</summary>
```


## `Pairing/PairingService.Client.cs`

*Total comments: 4*

### Line 19
**Context**: `public async Task<RamDiskSnapshot?> GetRemoteRamDiskSettingsAsync()`

```csharp
    /// <summary>Fetches the paired transmitter PC's current RAM disk configuration. Null if not paired, unreachable, or denied.</summary>
```

### Line 44
**Context**: `public async Task<(bool Success, string? Error)> SetRemoteRamDiskSettingsAsync(bool enabled, char driveLetter, int sizeMb)`

```csharp
    /// <summary>Asks the paired transmitter PC to apply a new RAM disk configuration.</summary>
```

### Line 78
**Context**: `public async Task<string?> GetRemoteNewestClipPathAsync()`

```csharp
    /// <summary>Forward-slash relative path of the paired transmitter PC's own newest clip.</summary>
```

### Line 102
**Context**: `public async Task<RemoteGalleryListing?> ListRemoteGalleryAsync(string relativePath)`

```csharp
    /// <summary>Lists one folder of the paired transmitter PC's clips.</summary>
```


## `Pairing/PairingService.Discovery.cs`

*Total comments: 5*

### Line 13
**Context**: `public void StartDiscoveryListener()`

```csharp
    // ------------------------------------------------------------- discovery
```

### Line 15
**Context**: `public void StartDiscoveryListener()`

```csharp
    /// <summary>Always listening in the background (cheap: one idle UDP socket) so Settings has a live list ready the moment it's opened.</summary>
```

### Line 59
**Context**: `}`

```csharp
                // Not a Backtrack announcement (or a corrupt one) -- ignore.
```

### Line 64
**Context**: `public void StartAnnouncing()`

```csharp
    /// <summary>Broadcasts this machine as pairable every few seconds. Call when "Share my clips" is turned on.</summary>
```

### Line 89
**Context**: `}`

```csharp
                // A transient network hiccup -- just try again next tick.
```


## `Streaming/RemoteClipStreamServer.cs`

*Total comments: 14*

### Lines 12-30
**Context**: `public sealed class RemoteClipStreamServer`

```csharp
/// <summary>
/// A tiny loopback-only HTTP server that lets libvlc play a remote clip
/// directly over the network instead of Backtrack downloading the whole
/// file to disk first (see OpenRemoteClipStreamingAsync). libvlc's own
/// Media can be pointed at any HTTP URL and buffers/plays progressively on
/// its own -- this server's only job is translating that HTTP request
/// (including any Range header libvlc sends when seeking) into this app's
/// existing pairing-protocol get_clip request against the transmitter PC,
/// relaying bytes straight through as they arrive. Nothing ever touches
/// disk here -- a genuinely different tradeoff from RemoteCache/
/// DownloadRemoteClipAsync's own download-then-play path, deliberately: no
/// local copy survives after playback, by design (see OpenRemoteClipStreamingAsync's
/// own comment).
///
/// Loopback (127.0.0.1) only, never 0.0.0.0 -- nothing outside this PC
/// should ever be able to hit this, and Windows Firewall doesn't filter
/// loopback traffic at all, so this needs no firewall rule the way the
/// real pairing ports do.
/// </summary>
```

### Lines 37-49
**Context**: `private readonly ConcurrentDictionary<string, string> _sessions = new();`

```csharp
    // Keyed by a random per-open-clip token (not the relative path itself --
    // a path can contain characters that don't round-trip cleanly through a
    // URL segment, and a token also means a stale/old tab can't accidentally
    // keep working after a newer clip replaces it). One entry per
    // currently-open streamed clip; in practice just ever the most recent
    // one, but keyed rather than a single field in case a stale request from
    // a clip that was just switched away from is still in flight.
    //
    // Just the relative path -- no size tracked here at all anymore. Each
    // request asks the transmitter fresh (see OpenRemoteClipStreamAsync) and
    // trusts THAT answer for Content-Length, never a value cached from
    // whenever this session was first prepared -- see HandleRequestAsync's
    // own comment for the real bug that fixed.
```

### Lines 59-66
**Context**: `public void EnsureStarted()`

```csharp
    /// <summary>
    /// Starts listening if not already running. Tries a small fixed range of
    /// ports (the real pairing TCP/UDP ports plus one, see PairingService's
    /// own DefaultPairingPort/BroadcastPort) rather than just failing outright
    /// if the first choice is somehow taken -- another app (or a second
    /// Backtrack instance, however unlikely) squatting on one exact port
    /// shouldn't take this whole feature down.
    /// </summary>
```

### Line 86
**Context**: `}`

```csharp
                // Port already in use -- try the next one.
```

### Lines 93-99
**Context**: `public string PrepareStream(string relativePath)`

```csharp
    /// <summary>
    /// Registers a new streaming session for one clip and returns the local
    /// URL to hand libvlc. No size passed in at all -- every request against
    /// this token asks the transmitter for the clip's real, current size
    /// fresh (see HandleRequestAsync), so there's nothing here that can ever
    /// go stale.
    /// </summary>
```

### Lines 108-118
**Context**: `public void UpdateSessionPath(string token, string newRelativePath)`

```csharp
    /// <summary>
    /// Updates an already-open session's relative path in place -- for a
    /// remote rename applied WHILE that same clip is actively streaming
    /// (see MainWindow.PlayerRename_Click's remote branch): the clip itself
    /// on the transmitter didn't change, just the path it lives at, and any
    /// already-in-flight relay request keeps reading from wherever it
    /// already connected regardless. Only a FUTURE seek (a fresh HTTP Range
    /// request against this same token) would otherwise ask for the OLD,
    /// now-renamed path and 404 -- this is what keeps that working without
    /// needing to restart playback over a brand new URL/token.
    /// </summary>
```

### Lines 164-170
**Context**: `long offset = 0;`

```csharp
            // libvlc sends "bytes=N-" when it seeks (never a bounded "N-M"
            // range in practice for this kind of open-ended media playback,
            // but only the start offset is actually needed here regardless --
            // this server always streams from that point to the real end).
            // Clamped to a floor of 0 only -- the real ceiling isn't known
            // yet at this point (see below), unlike the old version of this
            // method which had a cached total to clamp against.
```

### Lines 181-186
**Context**: `(bool opened, string? openError, upstreamClient, System.Net.Sockets.NetworkStream? sourceStream, long remaining) =`

```csharp
            // Connects and reads the transmitter's response header (which
            // always reflects the file's real, current size -- freshly read
            // off disk on that end, see StreamFileResponseAsync) BEFORE this
            // commits to any HTTP headers of its own -- see
            // OpenRemoteClipStreamAsync's own comment for the stale-size bug
            // this replaced.
```

### Lines 203-206
**Context**: `context.Response.StatusCode = 206;`

```csharp
                // 206 Partial Content -- what tells libvlc (and any other real
                // HTTP client) this server actually honors Range requests, so
                // seeking keeps working instead of it giving up after the
                // first one and re-downloading from the start every time.
```

### Lines 215-219
**Context**: `await sourceStream.CopyToAsync(context.Response.OutputStream);`

```csharp
            // No WriteTimeout override here -- HttpListenerResponse.OutputStream
            // doesn't reliably support setting one (CanTimeout is false on its
            // real implementation), and setting it anyway throws immediately,
            // before a single byte goes out. Confirmed live as an earlier
            // cause of a total playback failure.
```

### Lines 224-227
**Context**: `Debug.WriteLine($"RemoteClipStreamServer: request ended: {ex.Message}");`

```csharp
            // The client (libvlc) disconnecting mid-stream -- e.g. it seeked
            // again before this response finished, or playback just stopped
            // -- surfaces here as a write failure on the now-closed response.
            // Entirely normal, not worth logging as an error every time.
```

### Line 233
**Context**: `}`

```csharp
            try { context.Response.Close(); } catch { /* best effort -- may already be closed/broken */ }
```

### Line 237
**Context**: `public void Stop()`

```csharp
    /// <summary>Called once, from App shutdown -- releases the port cleanly instead of leaving it bound until the process actually dies.</summary>
```

### Line 240
**Context**: `_listener = null;`

```csharp
        try { _listener?.Stop(); } catch { /* best effort */ }
```


## `Themes/Theme.Amoled.xaml`

*Total comments: 3*

### Lines 4-17
**Context**: `<sys:String x:Key="ThemeDisplayName">AMOLED</sys:String>`

```xml
    <!--
      True #000000 panel background, not just "very dark grey"; on an OLED
      panel those pixels are actually off (real power savings, deepest
      possible black), and #000000 also sidesteps the near-black color-crush
      issue #080808 ran into on some monitors, since 0,0,0 is the one value
      every display's gamma curve treats as a fixed calibration point rather
      than an interpolated one. High-contrast pure white text to match.

      Otherwise the same structure/steps as Theme.Dark.xaml, just anchored
      to 0 instead of 8; Rec/RecDark/Stream/Green stay Backtrack's own brand
      colors, same as every other theme.

      ThemeDisplayName/ThemeSortOrder/ThemeBuiltIn: see Theme.Dark.xaml's own comment.
    -->
```

### Lines 23-26
**Context**: `<SolidColorBrush x:Key="ThumbnailBg" Color="#161616"/>`

```xml
    <!-- Recessed card surface; see Theme.Dark.xaml's own comment on this key.
         Can't go darker than PanelBg's true black, so this is lighter
         instead: a dark grey a card can actually be told apart from the
         page behind it. -->
```

### Lines 38-39
**Context**: `<SolidColorBrush x:Key="NewestClip" Color="#3B82F6"/>`

```xml
    <!-- See Theme.Dark.xaml's own comment on this key: a fixed brand blue,
         same reasoning as Rec/Stream/Green, not derived from Accent. -->
```


## `Themes/Theme.Dark.xaml`

*Total comments: 6*

### Lines 4-32
**Context**: `<sys:String x:Key="ThemeDisplayName">Dark</sys:String>`

```xml
    <!--
      The app's one and only dark palette; merged into Application.Resources
      at startup (see App.xaml.cs), and swapped for Theme.Light.xaml at
      runtime when the user toggles Settings > Appearance. Every window in
      the app references these same keys via DynamicResource (not
      StaticResource, which wouldn't react to the swap) instead of each
      defining its own local copies, so a single merge/swap here updates
      every window at once.

      PanelBg was #101113 pre-theming; every other window had independently
      hardcoded its own near-identical copy of the same handful of colors
      (plus one real outlier: a distinctly blue-tinted #1E1E24 on the
      Player seek-bar tooltip, fixed to just use PanelBg like everything
      else already did).

      Theme files are discovered from disk at runtime (ThemeManager.
      DiscoverThemes), not a hardcoded list: this file's own name
      ("Theme.Dark.xaml") IS its Id. ThemeDisplayName/ThemeSortOrder/
      ThemeBuiltIn below are optional per-theme metadata a theme file can
      set to control its own label/position in Settings, and to mark
      itself as one of the themes this app actually ships; none of the
      three is a DynamicResource lookup anywhere, so there's no risk of
      colliding with a real color key. A theme that doesn't set
      ThemeDisplayName/ThemeSortOrder just gets a prettified version of its
      filename and sorts alphabetically after every theme that does;
      ThemeBuiltIn absent (the normal case for anything not shipped with
      the app) just means IsBuiltIn reads false: purely backend
      bookkeeping right now, nothing in the UI shows or reads it.
    -->
```

### Lines 38-42
**Context**: `<SolidColorBrush x:Key="ThumbnailBg" Color="#181A1E"/>`

```xml
    <!-- Recessed card surface: folder icons (Gallery) and the neutral
         placeholder shown while a clip's real thumbnail is still loading.
         Was hardcoded RGB(24,26,30) directly in code-behind (MainWindow.xaml.cs,
         BuildFolderCard/BuildClipCard); stayed this exact color regardless of
         theme. This IS that same value, just promoted to a real per-theme key. -->
```

### Lines 50-53
**Context**: `<SolidColorBrush x:Key="Rec" Color="#FF5B52"/>`

```xml
    <!-- Accent colors are deliberately IDENTICAL in both themes; these are
         brand/status colors (recording red, live purple, success green),
         not neutrals, and changing them by theme would make "is this
         recording" mean a different color depending on a setting. -->
```

### Lines 58-61
**Context**: `<SolidColorBrush x:Key="NewestClip" Color="#3B82F6"/>`

```xml
    <!-- Newest-clip/folder-trail dot on the Gallery; a genuinely fixed blue
         for the same reason as Rec/Stream/Green above, not Accent: Accent
         is a general highlight color that's near-white/near-black in
         Dark/Light/Amoled, only incidentally blue in Yami/YamiAcri. -->
```

### Lines 64-71
**Context**: `<SolidColorBrush x:Key="RowBg" Color="#0BFFFFFF"/>`

```xml
    <!-- Subtle white-tinted overlays for hover/pressed/row backgrounds and
         borders; on a dark panel, "add a bit of white" is what reads as
         "slightly lighter than the background". Light.xaml's equivalents
         use black instead, for the same reason in reverse. Consolidated
         from what used to be a dozen+ very-slightly-different inline alpha
         values scattered across every window (#0BFFFFFF, #0FFFFFFF,
         #12FFFFFF, #14FFFFFF, #17FFFFFF, #1AFFFFFF, #26FFFFFF, #2AFFFFFF...)
         down to 3 named steps. -->
```

### Lines 81-84
**Context**: `<SolidColorBrush x:Key="BadgeBg" Color="#CC0A0B0D"/>`

```xml
    <!-- Small floating badges (StatusOverlay's Rec/Stream/Replay/Mic pills)
         want their own near-opaque dark chip regardless of the main panel's
         own opacity, since they sit directly over a game/desktop, not over
         the app's own panel. -->
```


## `Themes/Theme.Light.xaml`

*Total comments: 4*

### Lines 4-12
**Context**: `<sys:String x:Key="ThemeDisplayName">Light</sys:String>`

```xml
    <!--
      Light counterpart to Theme.Dark.xaml; same keys, same structure,
      mirrored values. See that file's own comment for how/why this gets
      merged in. Clean white/light-grey base with dark text; accent colors
      (Rec/RecDark/Stream/Green) stay identical to dark mode on purpose,
      see Theme.Dark.xaml's own comment on that.

      ThemeDisplayName/ThemeSortOrder/ThemeBuiltIn: see Theme.Dark.xaml's own comment.
    -->
```

### Lines 18-21
**Context**: `<SolidColorBrush x:Key="ThumbnailBg" Color="#E4E4E6"/>`

```xml
    <!-- Recessed card surface; see Theme.Dark.xaml's own comment on this key.
         Noticeably darker than PanelBg here specifically, not just a lighter
         variant of it, since a light-on-light card would barely read as a
         card at all against this theme's near-white page background. -->
```

### Lines 33-34
**Context**: `<SolidColorBrush x:Key="NewestClip" Color="#3B82F6"/>`

```xml
    <!-- See Theme.Dark.xaml's own comment on this key: a fixed brand blue,
         same reasoning as Rec/Stream/Green, not derived from Accent. -->
```

### Lines 37-39
**Context**: `<SolidColorBrush x:Key="RowBg" Color="#0B000000"/>`

```xml
    <!-- Black-tinted instead of white-tinted; "add a bit of black" is what
         reads as "slightly darker than the background" on a light panel,
         the same relationship white-tinted overlays have on a dark one. -->
```


## `Themes/Theme.Yami.xaml`

*Total comments: 3*

### Lines 4-19
**Context**: `<sys:String x:Key="ThemeDisplayName">Yami (OBS)</sys:String>`

```xml
    <!--
      OBS Studio's own default "Yami" theme, ported from its real palette
      (obs-studio's frontend/data/themes/Yami.obt, the grey/blue scale OBS
      itself ships with) rather than invented; so this genuinely looks
      like the OBS window it sits next to, not just "another dark theme".

      Grey ramp used: grey8 #13141A (darkest) .. grey1 #5B6273 (lightest),
      window bg is grey7, panel/row surfaces step up through grey6-grey4.
      Accent blue is Yami's own "highlight_color" variable (blue1 #718CDC).

      Rec/RecDark/Stream/Green stay Backtrack's own brand colors, same as
      Dark/Light; see Theme.Dark.xaml's comment on why those don't change
      per theme, only the neutrals borrow Yami's real values.

      ThemeDisplayName/ThemeSortOrder/ThemeBuiltIn: see Theme.Dark.xaml's own comment.
    -->
```

### Lines 25-27
**Context**: `<SolidColorBrush x:Key="ThumbnailBg" Color="#252831"/>`

```xml
    <!-- Recessed card surface; see Theme.Dark.xaml's own comment on this key.
         Stays within this theme's own grey ramp (close to RowBg's step),
         not an unrelated invented tone. -->
```

### Lines 39-40
**Context**: `<SolidColorBrush x:Key="NewestClip" Color="#3B82F6"/>`

```xml
    <!-- See Theme.Dark.xaml's own comment on this key: a fixed brand blue,
         same reasoning as Rec/Stream/Green, not derived from Accent. -->
```


## `Themes/Theme.YamiAcri.xaml`

*Total comments: 4*

### Lines 4-40
**Context**: `<sys:String x:Key="ThemeDisplayName">Acri (OBS)</sys:String>`

```xml
    <!--
      OBS Studio's "Acri" variant of its own Yami theme (obs-studio's
      themes/Yami_Acri.ovt), same source-of-truth approach as Theme.Yami.xaml
      right next to this file: ported from the real file, not invented.

      First pass at this file only ported Acri's accent override and left
      every neutral copied straight from base Yami: wrong. Acri's own
      .ovt ALSO redefines its entire grey1-grey8 ramp, separately from base
      Yami's, and those values are meaningfully different (generally
      darker, closer to true black): Yami's own grey6/grey7 (#272A33/
      #1D1F26) versus Acri's (#181819/#101010) is a real, visible
      difference, not a subtle one, confirmed against the real color values
      after a report that this reused Yami's colors verbatim. Every neutral
      below is now re-derived from ACRI's own ramp, same POSITION in the
      ramp base Yami used (wherever Theme.Yami.xaml used its own grey6,
      this uses Acri's grey6, etc.), except grey4, which Acri repurposes
      as a literal navy (#162458, the button color) rather than a real
      grey, so anywhere base Yami used grey4 for an actual neutral
      (dividers, hover states) this substitutes Acri's grey3 instead:
      the closest thing Acri has to a real "medium visibility" neutral in
      that same role, not an invented value.

      Acri's ramp: grey1 #616161, grey2 #575757, grey3 #3D3D3F,
      grey4 #162458 (navy, not neutral), grey5 #28282A, grey6 #181819,
      grey7 #101010, grey8 #090909.

      Accent is Acri's primary_light (#2A3A75, rgb(42,58,117)): the
      color that actually shows up as the visible accent on hover/tab/
      scrollbar states in the real theme, not its near-black primary base;
      see the original version of this comment (still accurate) for why.

      Rec/RecDark/Stream/Green stay Backtrack's own brand colors, same as
      every other theme; see Theme.Dark.xaml's comment on why those don't
      change per theme, only the neutrals/accent borrow the real OBS values.

      ThemeDisplayName/ThemeSortOrder/ThemeBuiltIn: see Theme.Dark.xaml's own comment.
    -->
```

### Lines 46-48
**Context**: `<SolidColorBrush x:Key="ThumbnailBg" Color="#141416"/>`

```xml
    <!-- Recessed card surface; see Theme.Dark.xaml's own comment on this key.
         An in-between step toward grey6, same relationship base Yami's own
         ThumbnailBg had to ITS grey6/grey7, just using Acri's darker pair. -->
```

### Lines 50-52
**Context**: `<SolidColorBrush x:Key="Hairline" Color="#3D3D3F"/>`

```xml
    <!-- Acri's grey4 is a repurposed navy (#162458, its button color), not a
         real neutral: grey3 (#3D3D3F) is the closest thing Acri has to
         base Yami's own grey4 in this "medium visibility divider" role. -->
```

### Lines 63-66
**Context**: `<SolidColorBrush x:Key="NewestClip" Color="#3B82F6"/>`

```xml
    <!-- Newest-clip/folder-trail dot on the Gallery; a genuinely fixed blue
         for the same reason as Rec/Stream/Green above, not Accent: Accent
         is a general highlight color that's near-white/near-black in
         Dark/Light/Amoled, only incidentally blue in Yami/YamiAcri. -->
```


## `Themes/ThemeManager.cs`

*Total comments: 11*

### Lines 10-19
**Context**: `public sealed record ThemeInfo(string Id, string DisplayName, double SortOrder, bool IsBuiltIn, ResourceDictionary Dictionary);`

```csharp
/// <summary>
/// One discovered theme: its file-derived Id (used for persistence and
/// selection -- AppSettings.Theme stores this string, not an index), the
/// display name shown in Settings, where it sorts among its peers, whether
/// it self-declared as one of the themes this app actually ships (see
/// ThemeBuiltInKey's own comment -- backend bookkeeping only, nothing in
/// the UI currently reads this), and the already-loaded ResourceDictionary
/// itself so Apply doesn't need to re-parse the file a second time right
/// after DiscoverThemes just did.
/// </summary>
```

### Lines 22-33
**Context**: `public static class ThemeManager`

```csharp
/// <summary>
/// Loads/swaps the app-wide theme resource dictionary into
/// Application.Resources -- every window references the same shared keys
/// (PanelBg, Text0, Rec, ...) via DynamicResource (never StaticResource,
/// which wouldn't react to a runtime swap), so a single Apply() call here
/// updates every open window at once, no per-window plumbing needed.
///
/// Themes are DISCOVERED from Themes\Theme.*.xaml on disk, next to the
/// .exe (see Backtrack.csproj's own comment on why those files are loose,
/// not compiled Page/BAML resources), not a hardcoded list -- adding a new
/// theme, built-in or a user's own, is "drop a file there," nothing else.
/// </summary>
```

### Lines 36-42
**Context**: `private static readonly string[] RequiredKeys =`

```csharp
    // Every key this app actually looks up via DynamicResource somewhere.
    // Checked at discovery time so an incomplete theme file (a typo, or a
    // user's own in-progress theme missing a key) gets skipped with a log
    // line instead of leaving some window silently unstyled or throwing
    // the moment a DynamicResource lookup for a genuinely absent key
    // finally happens to run -- which is only ever at USE time, not load
    // time, so this is the one place that can catch it safely up front.
```

### Lines 51-54
**Context**: `private const string DisplayNameKey = "ThemeDisplayName";`

```csharp
    // Optional resource keys a theme file can set to control how it's
    // presented, read as plain values out of the same dictionary -- none of
    // these is looked up via DynamicResource anywhere, so there's no naming
    // collision risk with the real color keys above.
```

### Lines 57-68
**Context**: `private const string BuiltInKey = "ThemeBuiltIn";`

```csharp
    // Self-declared by the five themes this app actually ships (see each
    // Theme.*.xaml's own comment); anything without it is, by definition,
    // not one of those -- a theme a user dropped in themselves, or copied
    // from a built-in one without carrying this over. Purely a backend
    // distinction for now (nothing in Settings' UI shows or reads this),
    // kept around for whatever actually needs to tell the two apart later
    // (not overwriting a user's own file on an app update, for instance).
    // Self-declared rather than derived from a hardcoded Id list here on
    // purpose -- keeping the classification IN the file alongside
    // everything else about it, the same way ThemeDisplayName/
    // ThemeSortOrder already work, rather than a second place that could
    // drift out of sync with which files actually ship.
```

### Line 71
**Context**: `public static string ThemesFolder => Path.Combine(AppContext.BaseDirectory, "Themes");`

```csharp
    /// <summary>Public so Settings' "Open themes folder" button can point at the exact same path this class discovers themes from, rather than a second hardcoded copy that could drift.</summary>
```

### Lines 76-83
**Context**: `public static List<ThemeInfo> DiscoverThemes()`

```csharp
    /// <summary>
    /// Scans Themes\Theme.*.xaml and loads each as a real ResourceDictionary.
    /// Re-scans disk every call rather than caching -- cheap (a handful of
    /// small XAML files), and means a theme file edited while Backtrack is
    /// running (someone actively developing their own) shows up correctly
    /// the next time Settings rebuilds its swatches or Apply runs, without
    /// needing a dedicated "reload themes" step.
    /// </summary>
```

### Lines 123-126
**Context**: `return result.OrderBy(t => t.SortOrder).ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();`

```csharp
        // Built-in themes each set ThemeSortOrder to keep their historical
        // Dark/Light/Yami/Acri/Amoled order; anything without an opinion
        // (sortOrder left at the 1000 default above) falls in alphabetically
        // by Id after all of those, rather than interleaving unpredictably.
```

### Lines 130-133
**Context**: `private static string PrettifyId(string id) => Regex.Replace(id, "(?<!^)(?<![A-Z])([A-Z])", " $1");`

```csharp
    // "MyCoolTheme" -> "My Cool Theme" -- only used as a fallback for a
    // theme file that didn't set its own ThemeDisplayName; a user dropping
    // in a new file still gets a readable label with zero extra effort,
    // just not a hand-tuned one like the built-ins' "Yami (OBS)"/"AMOLED".
```

### Lines 136-142
**Context**: `public static void Apply(string themeId)`

```csharp
    /// <summary>
    /// Applies the theme with this Id if one was actually discovered;
    /// otherwise falls back to the first available theme (by sort order),
    /// and if literally none were found (Themes folder missing/empty --
    /// shouldn't happen in a normal install), leaves whatever's already
    /// merged alone rather than clearing it down to nothing.
    /// </summary>
```

### Lines 157-160
**Context**: `for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)`

```csharp
        // Removes any previously-merged theme dictionary by content (has a
        // "PanelBg" key -- guaranteed present in every theme dictionary and
        // nothing else this app merges in) rather than assuming a fixed
        // index, so this stays safe to call again later (Settings' toggle).
```


## `UI/Cards/MainWindow.Cards.Remote.cs`

*Total comments: 2*

### Line 25
**Context**: `var compressOverlay = new Grid`

```csharp
        // Compression progress overlay
```

### Line 244
**Context**: `var compressOverlay = new Grid`

```csharp
        // Compression progress overlay
```


## `UI/Cards/MainWindow.Cards.cs`

*Total comments: 1*

### Line 91
**Context**: `var compressOverlay = new Grid`

```csharp
        // Compression progress overlay
```


## `UI/MainWindow/MainWindow.Updates.Apply.cs`

*Total comments: 4*

### Line 62
**Context**: `if (installedFileMissing)`

```csharp
        // 1. Missing file: definitely update!
```

### Lines 66-67
**Context**: `if (versionBumped)`

```csharp
        // 2. Version bumped (candidate > installed): definitely update!
        // A previous failed update or cached digest must NEVER block updating to a newer version.
```

### Line 71
**Context**: `DateTimeOffset? lastApplied = getLastApplied();`

```csharp
        // 3. For same-version checks (candidate <= installed), only update if GitHub release was re-published in-place with a new digest/timestamp.
```

### Line 83
**Context**: `if (UpdateService.IsNewer(UpdateService.CurrentAppVersion.ToString(3), release.Version))`

```csharp
        // Never auto-downgrade if candidate version is strictly older than installed
```


## `UI/MainWindow/MainWindow.WindowChrome.cs`

*Total comments: 2*

### Line 98
**Context**: `const double ForcefieldThreshold = 45.0;`

```csharp
        // Forcefield padding around MainWindow and RecentClipsOverlay to prevent accidental exits
```

### Line 115
**Context**: `Dispatcher.BeginInvoke(() => CloseOverlay());`

```csharp
        // Passed beyond the forcefield -> exit overlay cleanly!
```


## `UI/MainWindow/MainWindow.xaml`

*Total comments: 53*

### Lines 21-27
**Context**: `<Style TargetType="ScrollViewer">`

```xml
        <!-- PanelBg/Hairline/Text0-2/Accent/Rec/RecDark/Stream/Green/RowBg/
             RowHoverBg/TileHoverBg used to be defined locally right here;
             moved to Theme.Dark.xaml/Theme.Light.xaml (merged into
             Application.Resources by ThemeManager) so Settings' light/dark
             toggle can actually swap them. Every reference to these below is
             DynamicResource now, not StaticResource; that's what makes the
             swap actually repaint this window instead of needing a restart. -->
```

### Lines 75-77
**Context**: `<Style x:Key="BackButtonHost" TargetType="Grid">`

```xml
        <!-- A small circular button, not a bare oversized glyph; a plain
             big TextBlock arrow didn't align cleanly against the title text
             next to it (different font metrics, no shared baseline). -->
```

### Line 103
**Context**: `<ControlTemplate x:Key="BufRowTemplate" TargetType="Button">`

```xml
        <!-- Base template shared by both variants; only the hover behavior differs -->
```

### Line 110
**Context**: `<Style x:Key="BufRowButton" TargetType="Button">`

```xml
        <!-- Online (green) rows: hoverable, they're actually actionable -->
```

### Line 126
**Context**: `<Style x:Key="BufRowButtonNoHover" TargetType="Button">`

```xml
        <!-- Grey/red rows: no hover feedback, since saving them isn't expected to do much -->
```

### Lines 162-165
**Context**: `<Style x:Key="ModernSlider" TargetType="Slider">`

```xml
        <!-- Flat track + filled progress + circular thumb, a real modern slider
             instead of the stock OS control. Assumes Minimum=0 (both usages hold);
             the filled portion is two Star-weighted Grid columns sized by fraction
             of Value/Maximum, since a Slider's own rendered width isn't known from a style. -->
```

### Lines 217-219
**Context**: `<Style x:Key="RowLengthSlider" TargetType="Slider" BasedOn="{StaticResource ModernSlider}">`

```xml
        <!-- Position on a 0-1000 scale, not raw seconds. A straight 15s-3600s linear
             slider squeezes every length anyone actually wants (15s-2min) into the
             first couple percent of the track. See PosToSeconds/SecondsToPos. -->
```

### Lines 227-228
**Context**: `<Style x:Key="PlayerTransportButton" TargetType="Button">`

```xml
        <!-- Bigger, circular play/pause transport button; the default TileButton
             sizing reads too small for the Player's primary control. -->
```

### Line 254
**Context**: `<Style x:Key="AppToggle" TargetType="ToggleButton">`

```xml
        <!-- Track+knob toggle switch, matching the design mockup exactly -->
```

### Lines 269-270
**Context**: `<Setter TargetName="Knob" Property="Fill" Value="{DynamicResource Text2}"/>`

```xml
                                <!-- Accent is white now too, so the knob needs to switch to grey here,
                                     otherwise a white knob on a white track has zero contrast, invisible. -->
```

### Lines 368-369
**Context**: `<Style x:Key="ModernComboBox" TargetType="ComboBox">`

```xml
        <!-- Flat, dark ComboBox for the audio track selector, since the stock one
             renders with white system chrome that clashes badly with this theme. -->
```

### Lines 384-386
**Context**: `<ToggleButton x:Name="Toggle" Background="{TemplateBinding Background}" Focusable="False"`

```xml
                            <!-- Toggle is just background+chevron; the visible label is a
                                 separate, non-hit-testable ContentPresenter layered on top,
                                 avoiding a ContentPresenter nested inside another one. -->
```

### Line 402
**Context**: `<TextBlock Grid.Column="0"`

```xml
                                    <!-- TextBlock bound to SelectionBoxItem using ComboBoxItemTextConverter -->
```

### Lines 451-454
**Context**: `<Style TargetType="ScrollBar">`

```xml
        <!-- Applies to every ScrollBar in the window (no x:Key, an implicit
             TargetType style), including the default one inside GalleryScrollHost's
             ScrollViewer, so the Gallery's scrollbar matches the theme instead of
             using the plain system one. -->
```

### Lines 534-538
**Context**: `<Border x:Name="RootBorder" Background="{DynamicResource PanelBg}" BorderBrush="{DynamicResource Hairline}" BorderThickness="1">`

```xml
    <!-- The brand logo used to live here, above RootBorder, but that meant it moved
         and resized along with MainWindow itself (which changes size a lot between
         the compact pill and the big Gallery/Player panel). It's now its own
         separate, fixed-position window (see LogoOverlay.xaml), shown/hidden in
         lockstep with the HUD by MainWindow.ToggleVisible/CloseOverlay. -->
```

### Line 541
**Context**: `<StackPanel x:Name="IdlePanel">`

```xml
            <!-- Idle screen: Record / Save Replay / Gallery -->
```

### Lines 603-606
**Context**: `<StackPanel x:Name="TopRightButtons" Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,6,6,0">`

```xml
            <!-- Settings gear, declared AFTER IdlePanel so it wins hit-testing in the
                 corner where it would otherwise sit under the Gallery tile.
                 Only ever visible while IdlePanel itself is showing. The actual close
                 button lives on the Scrim, top-left of the screen, not in here. -->
```

### Line 616
**Context**: `<DockPanel x:Name="SaveReplayPanel" Visibility="Collapsed" Margin="14,12,14,14">`

```xml
            <!-- Save Replay screen -->
```

### Line 628
**Context**: `<DockPanel x:Name="StartRecordPanel" Visibility="Collapsed" Margin="14,12,14,14">`

```xml
            <!-- Start Recording screen -->
```

### Line 640
**Context**: `<DockPanel x:Name="GalleryPanel" Visibility="Collapsed" Margin="16,14,16,16">`

```xml
            <!-- Gallery screen -->
```

### Line 656
**Context**: `<ComboBox x:Name="GallerySortComboBox" Width="140" Margin="0,0,8,0"`

```xml
                        <!-- Sort / Filter Selector -->
```

### Line 669
**Context**: `<Grid Width="170" VerticalAlignment="Center">`

```xml
                        <!-- Filters the current folder's clips by name -->
```

### Line 679
**Context**: `<StackPanel x:Name="GallerySourceTabs" DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,12" Visibility="Collapsed">`

```xml
                <!-- Only shown at all once paired with a transmitter PC -->
```

### Line 685
**Context**: `<StackPanel x:Name="GalleryPathBar" DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,12" Visibility="Collapsed">`

```xml
                <!-- Folder breadcrumb -->
```

### Line 695
**Context**: `<Grid x:Name="GallerySelectionBar" DockPanel.Dock="Top" Margin="0,0,0,12" Visibility="Collapsed">`

```xml
                <!-- Mass-selection bar -->
```

### Line 710
**Context**: `<Border DockPanel.Dock="Bottom" Margin="0,10,0,0" Padding="12,8" Background="{DynamicResource RowBg}" CornerRadius="4">`

```xml
                <!-- Gallery Footer Bar: cumulative folder size & clip count + Storage Space Bar -->
```

### Line 719
**Context**: `<TextBlock x:Name="GalleryTotalStatsText" Grid.Column="0" Text="0 clips · 0 MB" FontSize="11" Foreground="{DynamicResource Text2}" VerticalAlignment="Center"/>`

```xml
                        <!-- Left: Total clips and size in current folder -->
```

### Line 722
**Context**: `<StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">`

```xml
                        <!-- Right: Storage Progress Slider / Bar with Free Space or Limit info -->
```

### Line 724
**Context**: `<Border Width="110" Height="6" Background="{DynamicResource SeekTrackBg}" CornerRadius="3" VerticalAlignment="Center" Margin="0,0,10,0" ClipToBounds="True">`

```xml
                            <!-- Progress Bar Track -->
```

### Line 728
**Context**: `<TextBlock x:Name="GalleryStorageText" Text="" FontSize="11" FontWeight="Medium" Foreground="{DynamicResource Text1}" VerticalAlignment="Center"/>`

```xml
                            <!-- Storage Text -->
```

### Line 741
**Context**: `<Grid x:Name="PlayerPanel" Visibility="Collapsed">`

```xml
            <!-- Player screen: video + transport, with top-right overlay action menu -->
```

### Line 913
**Context**: `<Button x:Name="PlayerStarButton" Style="{StaticResource BareIconButton}" Margin="8,0,0,0" Padding="4,2"`

```xml
                                <!-- Starred Indicator Toggle in Title Pill -->
```

### Line 934
**Context**: `<Popup x:Name="PlayerMenuPopup" PlacementTarget="{Binding ElementName=PlayerMenuPill}" Placement="Bottom"`

```xml
                <!-- Floating options & metadata menu anchored to the top-right 3-dot pill -->
```

### Line 943
**Context**: `<UniformGrid Columns="2" Rows="3">`

```xml
                            <!-- 2x3 Action Containers -->
```

### Line 961
**Context**: `<Path Data="M19,9h-4V3H9v6H5l7,7 7,-7zM5,18v2h14v-2H5z"`

```xml
                                        <!-- Compress zip/down icon -->
```

### Line 969
**Context**: `<Path Data="M17,3H7c-1.1,0 -1.99,0.9 -1.99,2L5,21l7,-3 7,3V5c0,-1.1 -0.9,-2 -2,-2z"`

```xml
                                        <!-- Bookmark icon -->
```

### Line 991
**Context**: `<Border BorderBrush="{DynamicResource Hairline}" BorderThickness="0,1,0,0" Margin="2,8,2,8"/>`

```xml
                            <!-- Subtle Divider -->
```

### Line 994
**Context**: `<StackPanel Margin="6,0,6,4">`

```xml
                            <!-- Metadata Stats -->
```

### Line 1037
**Context**: `<Popup x:Name="CompressPopup" PlacementTarget="{Binding ElementName=PlayerMenuPill}" Placement="Bottom"`

```xml
                <!-- Floating Compress Dialog Popup -->
```

### Line 1055
**Context**: `<UniformGrid x:Name="CompressPresetsGrid" Columns="3" Margin="0,0,0,8">`

```xml
                            <!-- Preset Buttons Grid -->
```

### Line 1065
**Context**: `<Grid x:Name="CompressCustomRow" Visibility="Collapsed" Margin="0,0,0,8">`

```xml
                            <!-- Custom Size Input Row -->
```

### Line 1076
**Context**: `<Grid Margin="0,4,0,0">`

```xml
                            <!-- Action Buttons: Replace & Save New -->
```

### Line 1105
**Context**: `<Popup x:Name="BookmarkPopup" PlacementTarget="{Binding ElementName=PlayerMenuPill}" Placement="Bottom"`

```xml
                <!-- Floating Bookmarks Dialog Popup -->
```

### Line 1139
**Context**: `<DockPanel x:Name="SettingsPanel" Visibility="Collapsed" Margin="20,16,20,20">`

```xml
            <!-- Settings screen -->
```

### Lines 1148-1154
**Context**: `<ScrollViewer x:Name="SettingsScrollHost" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False"`

```xml
                <!-- Real middle-click autoscroll (SettingsScrollHost_PreviewMouseDown/Up and
                     the SettingsAutoscroll_Tick loop the down starts, in code-behind), the
                     conventional button for this gesture (browsers/most apps use it), so
                     it doesn't compete with a left click on any of the rows/controls
                     inside. Pressing sets a fixed reference point and starts scrolling;
                     releasing (anywhere: mouse capture, not hover) stops it: see the
                     code-behind's own comment for the full behavior. -->
```

### Line 1159
**Context**: `<TextBlock Text="GENERAL" Style="{StaticResource SettingsSectionHeader}" Margin="0,4,0,10"/>`

```xml
                        <!-- 1. GENERAL -->
```

### Line 1320
**Context**: `<TextBlock Text="CLIPS" Style="{StaticResource SettingsSectionHeader}" Margin="0,20,0,10"/>`

```xml
                        <!-- 2. CLIPS -->
```

### Line 1423
**Context**: `<TextBlock Text="OVERLAY &amp; HOTKEYS" Style="{StaticResource SettingsSectionHeader}" Margin="0,20,0,10"/>`

```xml
                        <!-- 3. OVERLAY & HOTKEYS -->
```

### Line 1555
**Context**: `<TextBlock Text="OBS" Style="{StaticResource SettingsSectionHeader}" Margin="0,20,0,10"/>`

```xml
                        <!-- 4. OBS -->
```

### Line 1644
**Context**: `<TextBlock Text="SHARING" Style="{StaticResource SettingsSectionHeader}" Margin="0,20,0,10"/>`

```xml
                        <!-- 5. SHARING -->
```

### Line 1704
**Context**: `<Border BorderBrush="{DynamicResource Hairline}" BorderThickness="0,1,0,0" Margin="0,28,0,4"/>`

```xml
                        <!-- Visual Divider before Experimental and Maintenance -->
```

### Line 1707
**Context**: `<Border x:Name="ExperimentalHeader" Margin="0,16,0,0"`

```xml
                        <!-- 6. EXPERIMENTAL (Collapsible in Red) -->
```

### Line 1875
**Context**: `<Border x:Name="DestructiveHeader" Margin="0,20,0,0"`

```xml
                        <!-- 7. MAINTENANCE (Collapsible in Red) -->
```


## `UI/Player/MainWindow.Compress.cs`

*Total comments: 3*

### Line 193
**Context**: `StopPlayerPlayback();`

```csharp
        // Return immediately to gallery or recent clips HUD
```

### Line 265
**Context**: `string thumbCache = GetThumbnailCachePath(new FileInfo(sourcePath));`

```csharp
                    // Bust thumbnail and duration cache so fresh info loads
```

### Line 368
**Context**: `(string enc, string argsTemplate)[] candidateEncoders = new[]`

```csharp
        // Try GPU hardware encoders in order of performance
```


## `UI/Player/MainWindow.Player.AudioVolume.cs`

*Total comments: 4*

### Line 96
**Context**: `AudioTrackCombo.Visibility = Visibility.Visible;`

```csharp
            // Always display the Audio Track box whenever audio tracks exist
```

### Line 100
**Context**: `int currentVlcTrackId = _vlcPlayer.AudioTrack;`

```csharp
            // Match currently playing VLC track, or preferred track from settings
```

### Line 111
**Context**: `if (selectedIdx > 0 && selectedIdx < options.Count && _vlcPlayer.AudioTrack != options[selectedIdx].Id)`

```csharp
            // If a non-default audio track is preferred and differs from active track, switch to it
```

### Line 129
**Context**: `if (_vlcPlayer.AudioTrack == opt.Id)`

```csharp
        // If VLC is already playing this track, don't restart the audio output decoder
```


## `UI/Player/MainWindow.Player.Bookmarks.cs`

*Total comments: 2*

### Line 126
**Context**: `var jumpPanel = new StackPanel`

```csharp
            // Left side: Clickable bookmark item to seek
```

### Line 175
**Context**: `var trashBtn = new Button`

```csharp
            // Right side: Trash / Delete button
```


## `UI/Player/MainWindow.Player.Fullscreen.cs`

*Total comments: 1*

### Line 40
**Context**: `PlayerVideoColumnDock.Children.Remove(PlayerTransportBar);`

```csharp
        // Reparent transport bar into popup before resizing the video host
```


## `UI/Player/MainWindow.Player.cs`

*Total comments: 1*

### Line 32
**Context**: `PlayerVideoHost.Height = contentHeight;`

```csharp
        // PlayerVideoHost must stay exactly 16:9 (contentHeight) so video fits edge-to-edge with no black bars
```


## `UI/RecentClips/MainWindow.RecentClipsOverlay.Tiles.cs`

*Total comments: 1*

### Line 32
**Context**: `var compressOverlay = new Grid`

```csharp
        // Compression progress overlay
```


## `UI/RecentClips/MainWindow.RecentClipsOverlay.cs`

*Total comments: 1*

### Line 157
**Context**: `Left = -32000;`

```csharp
            // Start off-screen so the initial layout + DirectX buffer swap occurs off-screen
```


## `UI/ReplayRecord/MainWindow.Obs.cs`

*Total comments: 1*

### Line 419
**Context**: `_audioCuePreviewDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };`

```csharp
        // Debounce audio feedback preview
```


## `UI/ReplayRecord/MainWindow.SaveReplay.Status.cs`

*Total comments: 1*

### Line 432
**Context**: `var orphanCancelledKeys = _cancelledRecordRows.Where(k => !activeKeys.Contains(k)).ToList();`

```csharp
                // Also ensure any explicitly cancelled row that is no longer active displays the cancellation toast
```


## `Updates/UpdateService.cs`

*Total comments: 33*

### Lines 16-27
**Context**: `public sealed record ReleaseInfo(string Version, string? DownloadUrl, DateTimeOffset? PublishedAt, string? Digest);`

```csharp
/// <summary>
/// Digest is the matched asset's own "sha256:..." content hash (GitHub computes
/// and returns this for uploaded release assets) -- the authoritative signal
/// for "is this literally the same file", immune to clock skew or metadata-only
/// touches. PublishedAt is that same asset's "updated_at" (not the release's
/// created_at), kept as a fallback for the rare asset that has no digest.
/// Either one exists specifically because re-uploading a replacement file to an
/// existing release changes neither the release's tag nor its created_at --
/// this repo's own release workflow (see obs-replay-slider / obs-source-record)
/// sometimes reuses the same version tag for a small fix, so version-number
/// comparison alone misses that case entirely.
/// </summary>
```

### Lines 30-36
**Context**: `public sealed class UpdateService`

```csharp
/// <summary>
/// Checks GitHub's "latest release" endpoint (never drafts/prereleases -- that
/// API only ever returns the most recent published one, which matters here
/// since e.g. obs-replay-slider has newer draft releases sitting ahead of its
/// actual published latest) for the app itself and for the two OBS plugins,
/// and can silently apply whichever updates it finds.
/// </summary>
```

### Lines 39-48
**Context**: `public static bool DeveloperModeEnabled { get; set; }`

```csharp
    /// <summary>
    /// Set from AppSettings.DeveloperModeEnabled (Settings > Experimental >
    /// Diagnostics) -- the actual, sole authority for IsDevBuild below now,
    /// not an override on top of a path guess. MainWindow.LoadSettingsUi
    /// pre-sets it to true, once, the first time IsRunningFromDevLocation
    /// suggests it (see that property's own comment) -- after that one-time
    /// nudge it's fully user-controlled either direction, including turning
    /// it back off while running somewhere IsRunningFromDevLocation would
    /// still flag, or on while running from a genuinely installed copy.
    /// </summary>
```

### Lines 51-67
**Context**: `public static bool IsDevBuild => DeveloperModeEnabled;`

```csharp
    /// <summary>
    /// Auto-update is deliberately never allowed to run here: a locally
    /// compiled binary's digest will essentially never match the official
    /// release's (builds aren't byte-reproducible across machines/compile
    /// runs even from identical source), so a dev build would ALWAYS look
    /// "out of date" by the digest check regardless of its version string --
    /// and worse, letting the startup auto-apply run would silently overwrite
    /// whatever's actively being tested with the real published release.
    ///
    /// Used to be a path comparison against a single hardcoded install
    /// location, which broke the moment the installer could put Backtrack
    /// anywhere else (see the installer's own new folder-picker) -- ANY
    /// custom-but-legitimate install location would have permanently and
    /// silently misidentified itself as a dev build forever, no way to
    /// self-correct. DeveloperModeEnabled is the real signal now;
    /// IsRunningFromDevLocation only ever feeds it a one-time initial guess.
    /// </summary>
```

### Lines 70-78
**Context**: `public static bool IsRunningFromDevLocation`

```csharp
    /// <summary>
    /// True unless running from wherever the installer itself last recorded
    /// as the real install location (its own uninstall registry key's
    /// InstallLocation value -- see installer/Program.cs), or that key
    /// doesn't exist at all (never installed through it). Purely a one-time
    /// suggestion signal for MainWindow.LoadSettingsUi to pre-set
    /// DeveloperModeEnabled with -- see that property's own comment on why
    /// this isn't IsDevBuild's actual authority anymore.
    /// </summary>
```

### Lines 94-98
**Context**: `private static string? _cachedObsInstallDir;`

```csharp
    // Not hardcoded -- see ResolveObsInstallDir. Cached once resolved with real
    // confidence (registry or a currently-running OBS), but NOT cached when it
    // falls all the way through to the bare default guess, so a later call
    // (e.g. once OBS actually starts) still gets a chance to find the real path
    // instead of being stuck on a wrong guess for the rest of the session.
```

### Lines 104-114
**Context**: `public bool IsObsInstalled => File.Exists(Obs64Path);`

```csharp
    /// <summary>
    /// True only when obs64.exe actually exists at the resolved install dir --
    /// real proof OBS is installed on THIS machine, regardless of which of
    /// ResolveObsInstallDir's three sources found it (registry, a running
    /// process, or the bare hardcoded-default guess). A receiver-only PC
    /// (paired to a transmitter's OBS over the network, see PairingService)
    /// legitimately has no local OBS install at all -- callers use this to
    /// skip the plugin update check/install entirely there instead of
    /// silently downloading and running an installer that has nothing to
    /// install into, which just surfaced as update errors.
    /// </summary>
```

### Lines 117-127
**Context**: `private static string ResolveObsInstallDir()`

```csharp
    /// <summary>
    /// OBS's own (Inno Setup) installer writes its install directory to
    /// HKLM\SOFTWARE\OBS Studio's default value -- confirmed directly against a
    /// real install -- so reading that instead of assuming the common default
    /// path is what actually survives someone installing to a different drive.
    /// Portable installs never touch the registry at all, so those fall back to
    /// deriving the path from obs64.exe's own location if it happens to be
    /// running right now (".../bin/64bit/obs64.exe" -> three levels up). If
    /// neither source has an answer yet, falls back to the old hardcoded
    /// default so callers always get *something* rather than null.
    /// </summary>
```

### Line 141
**Context**: `}`

```csharp
            // Key missing, access denied, etc. -- fall through to the next source.
```

### Lines 156-157
**Context**: `}`

```csharp
            // Process/module inspection can throw (e.g. access denied on a
            // 32-bit/64-bit module mismatch) -- fall through either way.
```

### Lines 163-164
**Context**: `private const string InnoSetupSilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";`

```csharp
    // Inno Setup's standard unattended flags: no UI, no "reboot now?" prompt, and
    // /SP- skips the "This will install... Do you wish to continue?" prompt too.
```

### Line 172
**Context**: `client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Backtrack", "1.0"));`

```csharp
        // GitHub's API rejects requests with no User-Agent.
```

### Lines 175-178
**Context**: `client.Timeout = TimeSpan.FromMinutes(10);`

```csharp
        // This same client is also used to download release assets (the self-update
        // zip is 200MB+), not just lightweight API calls -- a 15s timeout meant for
        // the latter silently killed every download attempt well before it could
        // finish, which looked like the update check just doing nothing.
```

### Lines 209-211
**Context**: `digest = asset.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;`

```csharp
                        // Not present on every asset ever uploaded (GitHub only
                        // started computing this at some point) -- null here just
                        // means callers fall back to the PublishedAt comparison.
```

### Lines 222-223
**Context**: `return null;`

```csharp
            // No network, GitHub unreachable, repo has zero releases yet, etc. -- just
            // means "nothing to update", not worth surfacing as an error.
```

### Line 228
**Context**: `public static bool IsNewer(string candidateVersion, Version installed)`

```csharp
    /// <summary>Compares only Major.Minor.Build -- release tags are plain "0.2.8", not 4-part assembly versions.</summary>
```

### Line 255
**Context**: `public static readonly Version MissingPluginVersion = new(0, 0, 0);`

```csharp
    // ------------------------------------------------------------- plugins
```

### Line 257
**Context**: `public static readonly Version MissingPluginVersion = new(0, 0, 0);`

```csharp
    /// <summary>The exact sentinel GetInstalledPluginVersion returns when the DLL genuinely isn't there -- a shared named constant so callers checking for "actually missing" (not just "an old version") compare against the one real source of truth instead of a second `new Version(0, 0, 0)` literal that could drift from it.</summary>
```

### Lines 269-285
**Context**: `public async Task<(bool WasObsRunning, bool Success)> InstallPluginUpdateAsync(string downloadUrl, string? expectedDigest = null, bool reopenAfterInstall = true)`

```csharp
    /// <summary>
    /// Downloads and silently installs a plugin's Windows installer, closing OBS
    /// first if it's running (installing over a loaded plugin DLL fails while
    /// OBS holds it open).
    ///
    /// reopenAfterInstall=false (used when updating more than one plugin in the
    /// same batch -- see CheckForUpdatesAsync) skips relaunching here; the
    /// caller is responsible for doing that itself, once, after every plugin in
    /// the batch has been installed. Reopening after EACH individual plugin
    /// used to mean: close OBS, install plugin 1, relaunch OBS, then almost
    /// immediately close it again for plugin 2 -- OBS still mid-startup (main
    /// window not up yet, websocket server not listening yet) got killed out
    /// from under itself, which is exactly the kind of race that would explain
    /// the second plugin's update looking like it failed right after the first
    /// one succeeded. Returns whether OBS was actually running (and so got
    /// closed) either way, so the caller knows whether it owes a reopen.
    /// </summary>
```

### Line 304
**Context**: `if (reopenAfterInstall && wasObsRunning)`

```csharp
        try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
```

### Line 312
**Context**: `public void RelaunchObsIfInstalled()`

```csharp
    /// <summary>Extracted so a caller managing OBS lifecycle across several plugin installs (see reopenAfterInstall above) can call this itself, once, after the whole batch.</summary>
```

### Lines 319-322
**Context**: `private const string SourceRecordAppId = "E0B6FC31-8FD5-4921-95DA-066EBE79A2AE";`

```csharp
    // AppId GUIDs baked into each plugin's own Inno Setup installer.iss -- Inno
    // registers its uninstall entry under "{AppId}_is1", not by display name,
    // so this GUID is the only lookup key stable across every version either
    // plugin has ever shipped.
```

### Lines 329-335
**Context**: `private async Task<(bool Success, string? Error)> UninstallInnoPluginAsync(string appId, string displayName)`

```csharp
    /// <summary>
    /// Runs an Inno-Setup-installed plugin's own bundled uninstaller silently
    /// (the same unins000.exe Windows' own "Apps &amp; features" would run),
    /// closing OBS first since the uninstaller can't delete a plugin DLL OBS
    /// still has loaded -- same reasoning as InstallPluginUpdateAsync above,
    /// just never reopening OBS afterward (there's nothing to reopen it for).
    /// </summary>
```

### Lines 360-367
**Context**: `private static string? FindInnoUninstallString(string appId)`

```csharp
    /// <summary>
    /// Inno Setup writes its uninstall registry key to whichever hive/view
    /// matches how the installer itself was built (LocalMachine for a normal
    /// system-wide install, the 32-bit view on a 32-bit build even on 64-bit
    /// Windows) -- checking all three here instead of guessing one avoids a
    /// false "not installed" for a real install that just landed somewhere
    /// other than the one view checked.
    /// </summary>
```

### Line 403
**Context**: `}`

```csharp
                // Ignore -- fall through to the wait/kill below regardless.
```

### Line 413
**Context**: `}`

```csharp
            try { proc.Kill(); } catch { /* already gone */ }
```

### Line 419
**Context**: `public static Version CurrentAppVersion`

```csharp
    // --------------------------------------------------------------- self
```

### Lines 431-443
**Context**: `public async Task ApplySelfUpdateAsync(string downloadUrl, string version, string? expectedDigest = null)`

```csharp
    /// <summary>
    /// Downloads a "backtrack-{version}-windows-x64.zip" release asset, extracts
    /// it, and writes a small batch script that waits for this process to exit,
    /// mirrors the new files over the current install directory, and relaunches --
    /// a running .exe can't overwrite itself directly, so a detached helper script
    /// does the actual file swap after Backtrack has exited.
    ///
    /// Passes the new version as a --updated= argument to the relaunched process,
    /// since this process is about to exit (Application.Current.Shutdown() runs
    /// right after this returns) -- a toast shown here would close with the window
    /// before anyone could see it. The freshly-launched process reads that arg on
    /// startup and shows the toast itself instead (see App.xaml.cs).
    /// </summary>
```

### Lines 452-458
**Context**: `await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));`

```csharp
        // ZipFile.ExtractToDirectory is fully synchronous -- since none of the
        // awaits above use ConfigureAwait(false), the continuation after them
        // resumes on the UI thread by default, so calling it directly here froze
        // the whole app (including the Check Now button) for as long as
        // extracting a 200MB+ self-contained build took. Task.Run moves the
        // actual CPU/IO-bound work off the UI thread; awaiting it still keeps
        // this method's own async flow correct.
```

### Lines 505-531
**Context**: `private const int DownloadRetryAttempts = 3;`

```csharp
    /// <summary>
    /// expectedDigest is GitHub's own "digest" field for this release asset
    /// (ReleaseInfo.Digest, "sha256:&lt;hex&gt;") -- GetLatestReleaseAsync was
    /// already fetching this for every asset, but only ever used it to detect
    /// "did the asset change" (ShouldApplyUpdate), never to confirm the bytes
    /// that actually landed on disk are the bytes GitHub said they'd be. HTTPS
    /// (already in use here) protects the transfer itself; this catches
    /// anything else that could put different bytes at that URL by the time
    /// they're downloaded -- a compromised/rotated release asset, a corrupted
    /// download, a proxy/cache doing something it shouldn't. Doesn't (can't)
    /// protect against a compromised GitHub account publishing a legitimately
    /// matching malicious build in the first place; still real value for the
    /// same reason checking a downloaded installer's own published checksum
    /// always is elsewhere. Null (an old asset with no digest published, or
    /// a caller that doesn't have one) just skips the check entirely rather
    /// than blocking on something that was never verifiable to begin with.
    /// </summary>
    // A self-update download is a real ~140MB+ transfer with zero retry
    // logic before this -- one dropped connection partway through (or even
    // right at the start, confirmed live: failed in well under a second,
    // nowhere near long enough to have actually transferred that much
    // regardless of connection speed -- a genuine network hiccup, not a
    // corrupted release) killed the whole update with a bare red status and
    // no automatic recovery. Same bounded-retry reasoning as the apply
    // script's own robocopy step (see ApplySelfUpdateAsync) -- a few quick
    // attempts covers a transient blip without hanging forever on something
    // genuinely broken.
```

### Lines 545-547
**Context**: `try { File.Delete(destPath); } catch { /* best effort */ }`

```csharp
                // Best-effort cleanup of whatever partial/mismatched bytes this
                // attempt left behind, so the next attempt starts clean rather
                // than potentially appending to or half-overwriting a stale file.
```

### Line 548
**Context**: `await Task.Delay(TimeSpan.FromSeconds(2));`

```csharp
                try { File.Delete(destPath); } catch { /* best effort */ }
```

### Line 579
**Context**: `throw new InvalidOperationException(`

```csharp
            try { File.Delete(destPath); } catch { /* best effort -- don't leave a mismatched file lying around either way */ }
```


## `installer/BacktrackSetup.csproj`

*Total comments: 2*

### Lines 9-15
**Context**: `<AssemblyName>Backtrack-Setup-dev</AssemblyName>`

```xml
    <!-- Overridden per-release by Build-ReleaseInstaller.ps1 (-p:AssemblyName=
         Backtrack-Setup-v$Version) so this can't drift out of sync with the
         actual version being built again; it previously sat hardcoded here
         at an old version number for multiple releases, silently producing
         a wrongly-named .exe (or none at all, once the release script's own
         filename check stopped matching) every time. This default only
         matters for an ad-hoc build outside that script. -->
```

### Lines 18-26
**Context**: `<SelfContained>true</SelfContained>`

```xml
    <!-- A framework-dependent apphost .exe is just a tiny native stub that
         loads its real code from a sibling .dll sitting next to it: the
         release script only ever uploaded the .exe, never that .dll, so the
         published installer silently failed to even start (no managed
         assembly to load = no MessageBox, nothing) for anyone who downloaded
         just the one file, which is the whole point of a "Setup.exe". Publishing
         self-contained + single-file means everything (including the payload.zip
         resource) is embedded directly in the one .exe, so it has no external
         dependency to go missing. -->
```


## `installer/Program.cs`

*Total comments: 6*

### Lines 26-32
**Context**: `using var folderDialog = new FolderBrowserDialog`

```csharp
            // FolderBrowserDialog's own dialog doesn't let you type/create a
            // brand new leaf folder by name the way a real "choose install
            // location" flow needs (only navigate to EXISTING ones) --
            // SelectedPath pre-seeded at the default install dir at least
            // means picking a parent and accepting it back gives a sane
            // "<chosen>\Backtrack"-shaped result instead of installing
            // straight into whatever folder they happened to land on.
```

### Line 50
**Context**: `foreach (var proc in Process.GetProcessesByName("Backtrack"))`

```csharp
            // Kill running Backtrack process if open
```

### Line 58
**Context**: `Assembly asm = Assembly.GetExecutingAssembly();`

```csharp
            // Extract embedded payload.zip
```

### Line 80
**Context**: `string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");`

```csharp
            // Create Start Menu Shortcut
```

### Line 87
**Context**: `string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Backtrack";`

```csharp
            // Add Control Panel Uninstall Registry Key
```

### Lines 92-95
**Context**: `key.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "0.0.0");`

```csharp
                // Read off the just-extracted Backtrack.exe's own version instead of a
                // hardcoded string here -- that string was already stuck on "0.2.0"
                // (a whole release behind) before anyone noticed, since nothing forced
                // it to be touched on a version bump. This way it can't drift again.
```


---

## Newly Archived Comments (v0.3.14 Refactor)

### `App.xaml`

- **Line 8**: `//application:,,,/Backtrack;component/UI/Styles/Converters.xaml"/>`
- **Line 9**: `//application:,,,/Backtrack;component/UI/Styles/ButtonStyles.xaml"/>`
- **Line 10**: `//application:,,,/Backtrack;component/UI/Styles/ControlStyles.xaml"/>`
- **Line 11**: `//application:,,,/Backtrack;component/UI/Styles/PlayerStyles.xaml"/>`
- **Line 12**: `//application:,,,/Backtrack;component/UI/Styles/MenuStyles.xaml"/>`

### `Core/AppLog.cs`

- **Line 55**: `/* best effort */`

### `Core/AppSettings.cs`

- **Line 199**: `// If bookmarks.json does not exist yet, check if settings.json contains legacy ClipMarkers to migrate`
- **Line 257**: `/* best effort -- e.g. file briefly locked; caller still restarts either way */`

### `Interop/FirewallRules.cs`

- **Line 54**: `/* best effort -- still report the exit code below either way */`
- **Line 76**: `/* best effort cleanup */`
- **Line 77**: `/* best effort cleanup */`

### `Interop/RamDisk.cs`

- **Line 55**: `/* best effort -- still report the exit code below either way */`
- **Line 75**: `/* best effort cleanup */`
- **Line 76**: `/* best effort cleanup */`
- **Line 142**: `/* best effort */`

### `Interop/ShellDragHelper.cs`

- **Line 422**: `// Exact SVG: M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z`

### `StreamDeck/StreamDeckIpcServer.cs`

- **Line 19**: `/// <summary>`
- **Line 20**: `/// Dedicated, modular Localhost IPC & WebSocket Server for Elgato Stream Deck integration.`
- **Line 21**: `/// Handles two-way WebSocket communication on 127.0.0.1:44558 with zero external dependencies.`
- **Line 22**: `/// </summary>`

### `StreamDeck/com.ilyambr.backtrack.sdPlugin/app.js`

- **Line 1**: `// Backtrack Stream Deck Dynamic Canvas & Animation Engine (Massive 23px Header Typography)`
- **Line 150**: `// Reusable static canvas to prevent DOM garbage collection overhead`
- **Line 170**: `// 60 FPS real-time desktop UI rate`
- **Line 230**: `// INACTIVE - unable to clip, do nothing`
- **Line 238**: `// INACTIVE - unable to record, do nothing`
- **Line 264**: `// If main or unset, return the main OBS replay buffer`
- **Line 271**: `// Exact or partial match`
- **Line 298**: `// 1. Exact or partial match for specific named source`
- **Line 566**: `// State deduplication key (Include animation step index for smooth pulse/scale)`

### `StreamDeck/com.ilyambr.backtrack.sdPlugin/js/backtrack-client.js`

- **Line 1**: `// Backtrack Localhost WebSocket IPC Client & State Sync`

### `StreamDeck/com.ilyambr.backtrack.sdPlugin/js/keycap-canvas.js`

- **Line 1**: `// Backtrack HTML5 Keycap Canvas Rendering Engine`
- **Line 25**: `// 1. Background`
- **Line 29**: `// 2. Keycap Border`
- **Line 34**: `// 3. Header Title`
- **Line 56**: `// Inset Top Right Dot`
- **Line 66**: `// 4. Dynamic Icon Size & Vertical Position (58px base)`
- **Line 105**: `// 5. Inset Bottom Footer`

### `Streaming/RemoteClipStreamServer.cs`

- **Line 148**: `/* best effort -- may already be closed/broken */`
- **Line 154**: `/* best effort */`

### `UI/Gallery/MainWindow.Gallery.Metadata.cs`

- **Line 57**: `// Resolve full file path and embed chapters into video container for DaVinci Resolve, Premiere Pro, VLC`
- **Line 93**: `// Determine video duration in seconds`
- **Line 106**: `// Build FFMETADATA`

### `UI/ReplayRecord/MainWindow.SaveReplay.Rows.cs`

- **Line 281**: `// Explicitly changed clip length: ignore previous back-to-back timestamp to save full updated duration!`

### `Updates/UpdateService.cs`

- **Line 186**: `/* best-effort cleanup */`
- **Line 274**: `/* already gone */`
- **Line 357**: `/* best effort */`
- **Line 388**: `/* best effort -- don't leave a mismatched file lying around either way */`



---

## Session Archive (2026-08-30 - Deduplication & Merge Engine)

### `Core/DeduplicationService.cs`

- **Line 15**: `// Standalone modular deduplication service with origin clip lineage tracking and independent JSON persistence`
- **Line 58**: `// Prepare save: check if save is back-to-back without duration change and calculate elapsed seconds`
- **Line 105**: `// Restore preferred duration after OBS writes clip`
- **Line 130**: `// Register saved clip and record origin link`

### `UI/Gallery/MainWindow.Merge.cs`

- **Line 35**: `// Concatenate deduplicated slice onto origin clip via FFmpeg`
- **Line 65**: `// Atomic file swap & marker offset calculation`

