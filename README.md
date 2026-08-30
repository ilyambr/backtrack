# Backtrack

A hotkey-summoned overlay HUD for OBS to record, save your replay buffer, and
browse your clips without alt-tabbing out of your game.

Backtrack talks to OBS entirely over **obs-websocket**, so it never touches
your scene collection or settings directly. It is a companion, not a plugin:
it sits on top of whatever OBS setup you already have (including a global
replay buffer, or per-source buffers via [obs-source-record](https://github.com/ilyambr/obs-source-record)
and [obs-replay-slider](https://github.com/ilyambr/obs-replay-slider), if
you're running those too).

---

## Screenshots

| Idle | Gallery | Player | Settings |
|---|---|---|---|
| <img src="docs/idle.png" width="220" alt="Idle screen"> | <img src="docs/gallery.png" width="220" alt="Gallery screen"> | <img src="docs/player.png" width="220" alt="Player screen"> | <img src="docs/settings.png" width="220" alt="Settings screen"> |

---

## 1. Prerequisites

- **Windows 10 / 11** (64-bit).
- **Zero external runtimes required**: releases are fully self-contained and embed the .NET runtime, LibVLC decoders, and FFmpeg tools.
- **OBS Studio** with its built-in **obs-websocket** server enabled (`Tools > WebSocket Server Settings`).

---

## 2. Installation

Download the latest installer or portable ZIP from [Releases](https://github.com/ilyambr/backtrack/releases)
and run it:
- **`Backtrack-Setup-vX.X.X.exe`**: Standard one-click installer.
- **`Backtrack-vX.X.X-win-x64.zip`**: Portable extract-and-run archive.
- **`Backtrack.streamDeckPlugin`**: Standalone double-clickable Elgato Stream Deck plugin.

Backtrack checks for updates once at startup and can install one right then if it finds one. That is the only unattended check/install;
every other update check (the Settings "Check now" button, or the deferred install prompt) is a manual click.

To build from source:

```bash
dotnet build -c Release
```

---

## 3. Stream Deck Integration

Backtrack includes a native two-way WebSocket IPC server (`127.0.0.1:44558`) and an official Elgato Stream Deck plugin (`Backtrack.streamDeckPlugin`):

- **🟩 Clip Replay Buffer**: Flushes and saves instant replay buffer for any selected source with buffer duration indicator.
- **🔴 Record Source**: Starts/stops recording for any source with live stopwatch timer on the keycap.
- **⏹️ Cancel Recording**: Instantly purges and discards the active recording.
- **⭐ Bookmark Segment**: Drops a chapter star bookmark into the current clip metadata.
- **🖥️ Toggle Backtrack HUD**: Toggles the overlay window on screen with live OBS connection status.
- **Dynamic Keycap Renderer**: Pixel-perfect HTML5 Canvas engine with automatic corner-radius safe insets and disconnected/offline desaturation.

---

## 4. Architecture & Modular Views

Backtrack is structured into modular views and focused partial controllers:
- **`UI/ReplayRecord/`**: Replay buffer management, source recording tiles, clip duration slider memory (`SaveReplayView.xaml`).
- **`UI/Gallery/`**: Media grid, sorting, search filter, starred tags, and path navigation (`GalleryView.xaml`).
- **`UI/Player/`**: Fullscreen LibVLC playback surface, interactive timeline trimming, audio track selection, and compression (`PlayerView.xaml`).
- **`UI/Settings/`**: Comprehensive grouped settings with categorized sub-panels (`SettingsView.xaml`).
- **`UI/Styles/`**: Standalone style dictionaries for controls, buttons, player controls, and menus merged at application level.
- **`StreamDeck/`**: Modular localhost WebSocket IPC server and Elgato Stream Deck plugin distribution package (`StreamDeckIpcServer.cs`).

---

## 5. What Backtrack installs on your system

Backtrack talks to OBS, closes it, and can install other software. That is
worth stating plainly instead of leaving it for you to find out:

- **It can auto-update the OBS plugins listed below.** If you have
  obs-source-record and/or obs-replay-slider installed, Backtrack checks for
  new versions of them alongside itself. Applying an update closes OBS (if
  it's running), silently runs the plugin's own Windows installer, then
  reopens OBS. This is **on by default** and only skipped if you're currently
  live (Backtrack refuses to touch anything mid-stream) or have turned it off
  in `Settings > Disable OBS plugin auto-updates`. Both plugins are built and
  released from this same GitHub account, but "an app closes OBS and runs an
  installer without asking each time" is worth knowing before you install it,
  not after.
- **It can install a RAM disk driver.** The optional RAM disk feature (faster
  replay-buffer writes) is backed by [ImDisk](https://ltr-data.se/opencode.html#ImDisk),
  a real open-source virtual disk driver, bundled unmodified under
  `ThirdParty/ImDisk` per its own license. It's signed by a certificate
  Windows already trusts (works fine under Secure Boot), and installing it
  triggers a normal Windows UAC prompt every time; it never installs
  silently. It's entirely optional; Backtrack works fine without ever
  touching it, and the driver can be removed like any other Windows driver if
  you decide against it.
- **It adds a Windows Firewall exemption for itself.** The first time you
  ever launch Backtrack, it asks for admin permission once (a real UAC
  prompt, not silent) to add four rules scoped specifically to
  `Backtrack.exe`'s own path, not a blanket port opening for anything else
  on your PC. These are for the peer-to-peer clip-sharing feature
  (discovering and pairing with another PC's Backtrack instance on your
  network). This happens once ever, whether or not you ever turn clip
  sharing on.

None of these touch your OBS scene collection, settings, or profiles. See
the note above about obs-websocket being the only thing Backtrack actually
talks to OBS through.

---

## 6. Usage

Press **Ctrl+Alt+G** (configurable in Settings) to summon the HUD over
whatever you're doing (game, browser, it floats on top).
From there:

- **Record**: start/stop recording a source, or the whole scene.
- **Save Replay**: flush a running replay buffer straight to disk with automatic clip duration memory.
- **Gallery**: browse saved clips by folder, star favorites, rename/trim/compress/delete them, or open one in the built-in player.
- **Settings**: gear icon, top-right of the HUD.

Press **Escape** to back out of whatever you're doing, or to close the HUD entirely.

---

## 7. Features

- **Stream Deck Native Plugin**: Official two-way Elgato Stream Deck plugin with dynamic live timer canvas rendering and source control.
- **Fullscreen clip player**: VLC-backed playback with interactive timeline trimming, audio track selection, and true edge-to-edge fullscreen mode with floating transport controls.
- **Themes**: Dark, Light, Yami (matches OBS's own default theme), AMOLED, and Yami Acri.
- **Clip length slider memory**: Remembers whatever clip duration you set on the slider automatically across sessions.
- **Starred clips & filtering**: Star favorite clips, filter by starred status, search by filename, and sort by date, size, or name.
- **Video compression**: Built-in background MP4 compression presets powered by bundled FFmpeg.
- **Native Drag & Drop**: Drag clips directly into Discord, Premiere, or folder windows with native Windows Shell drag images.
- **RAM disk support**: Mount an ImDisk virtual drive for OBS to write its replay buffer to, for lower-latency saves.
- **Remote OBS**: Point Backtrack at OBS running on a *different* PC (e.g. a separate streaming/capture box) instead of the local one.
- **Clip sharing**: Pull clips from another PC's Backtrack instance on the same network (or over Tailscale) into your own Gallery.
- **Per-source buffers**: If you're running obs-source-record and/or obs-replay-slider, Backtrack lists and saves each individually.
- **Status indicators**: Floating badges for Recording, Streaming, Replay Buffer, Virtual Camera, and Mic, plus an encoder-overload warning.

---

## 8. Known limitations

- **Per-source status needs obs-replay-slider installed too.** obs-source-record
  has no way to report "am I currently recording/buffering" on its own; Backtrack
  gets that status exclusively from obs-replay-slider's dock. If you're running
  obs-source-record by itself, its filters work fine inside OBS, but Backtrack
  won't show them as active in the HUD.
- **Clip sharing needs both PCs reachable and awake.** A remote Gallery action
  (delete, rename, download) times out after 15 seconds if the paired PC doesn't
  respond. Expect that error if it's asleep, offline, or unreachable over
  whatever network path you paired it on.

---

## 9. Related projects

- [obs-source-record](https://github.com/ilyambr/obs-source-record): OBS
  plugin, records/replays individual sources via a filter.
- [obs-replay-slider](https://github.com/ilyambr/obs-replay-slider): OBS
  plugin, a dock for per-source replay buffers.

---

## 10. AI disclosure

Most of Backtrack's code was written with AI assistance (Claude). Every feature was
specified, iterated on, tested, and verified by hand against a running build with alpha testers before being considered done, including catching and fixing AI-introduced bugs
along the way. But the code itself is substantially AI-written; obs-source-record
and obs-replay-slider were developed the same way.

---

## Issues

Backtrack is alpha software, so expect rough edges. Report bugs at
[github.com/ilyambr/backtrack/issues](https://github.com/ilyambr/backtrack/issues).
