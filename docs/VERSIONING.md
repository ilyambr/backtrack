# Versioning

Backtrack's Windows build (`Backtrack.csproj`, WPF, `net8.0-windows`) and any
future Linux build version **independently** -- they're not the same release
train, and there's no expectation they'll ever share a version number.

## Windows

- Source of truth: `<Version>` in `Backtrack.csproj`.
- Git tags: `vX.Y.Z` (e.g. `v0.2.4`).
- GitHub releases: same tag, built via `Build-ReleaseInstaller.ps1`.
- Current: see `Backtrack.csproj`.

## Linux

Doesn't exist yet. There is currently zero Linux-specific code in this repo
-- WPF is Windows-only, and the app leans on several Win32-specific APIs
throughout (global hotkey registration, EDID-based display detection, DWM
acrylic blur, the ImDisk RAM disk driver). A real Linux build means a
separate UI project (most likely Avalonia, the closest thing to WPF that
runs on Linux) sharing whatever non-UI logic reasonably can be shared
(obs-websocket client, pairing protocol, update checking), plus either a
Linux-native replacement or an intentional no-op for each Windows-only
feature.

When that work starts:

- It gets its own project (e.g. `Backtrack.Linux.csproj` or an Avalonia
  head), not a `net8.0-windows` retarget of the existing one.
- Version starts fresh at `0.1.0` -- it is not tied to whatever the Windows
  build's number happens to be at the time.
- Git tags: `linux-vX.Y.Z`, kept distinct from Windows' `vX.Y.Z` tags so the
  two release trains never collide or get confused with each other.
- GitHub releases: same `linux-vX.Y.Z` tag.

---

## Architecture & Tech Stack Evaluation

### Current Windows Architecture: C# (.NET 8) + WPF + OBS-WebSocket + LibVLC

| Component | Technology | Evaluation & Fit |
| :--- | :--- | :--- |
| **Language** | C# / .NET 8 | **9.5 / 10** - Excellent balance. Direct Win32 P/Invoke, low-latency async IPC, high JIT throughput, memory safety, and rapid iteration speed. Avoids GC bloat while keeping code vastly simpler than C++/Rust. |
| **UI Framework** | WPF | **8.5 / 10** - Battle-tested support for borderless transparent overlays (`WS_EX_TRANSPARENT`, `WS_EX_LAYERED`), DWM blur/composition, and embedded HWND hosting for LibVLC. Outperforms WinUI 3 in stability and beats Electron in memory footprint. |
| **Capture Engine** | OBS-WebSocket 5.x | **10 / 10** - Offloads capture hooks, replay buffer management, HDR, anti-cheat bypass, and NVENC/AMF encoding directly to OBS Studio instead of rebuilding a brittle custom capture driver. |
| **Playback & Trim Engine** | LibVLCSharp (LibVLC) | **9.0 / 10** - Cross-codec hardware-accelerated playback with zero seek latency across variable framerate HEVC/H.264 files where Windows Media Foundation chokes. |
| **IPC** | Windows Named Pipes | **10 / 10** - Microsecond latency, no TCP port collisions or firewall alerts, and seamless integration with Stream Deck companion plugins. |

### Architectural Roadmap for Linux

Rather than rewriting the codebase in C++ or Rust, the target strategy is a **C# (.NET 8/9) multi-head architecture** utilizing **Avalonia UI** for the presentation layer.

#### 1. Target Tech Stack

- **Runtime**: .NET 8 or 9 (Native Linux x64 and ARM64 / Steam Deck).
- **UI Framework**: **Avalonia UI** with `LibVLCSharp.Avalonia`. Avalonia provides Skia/Vulkan rendering, X11/Wayland compatibility, and XAML paradigms matching WPF.
- **Video Subsystem**: LibVLC on Linux (`libvlc-dev`) with VA-API / VDPAU hardware decode acceleration.
- **IPC Protocol**: **Unix Domain Sockets** (`$XDG_RUNTIME_DIR/backtrack/streamdeck.sock`) replacing Windows Named Pipes (`\\.\pipe\...`).
- **Global Hotkeys**: Freedesktop XDG Desktop Portal (`org.freedesktop.portal.GlobalShortcuts`) for Wayland compatibility, with X11 `XGrabKey` fallback.
- **Desktop Overlays & Notifications**:
  - Wayland: `wlr-layer-shell` protocol for supported compositors (KDE, Hyprland, Sway), and standard D-Bus desktop notifications (`org.freedesktop.Notifications`) fallback on GNOME.
  - X11: Shaped transparent top-level windows (`XShapeCombineRectangles`).
- **Audio Feedback**: `pw-play` (PipeWire) / `libcanberra` replacing `System.Media.SoundPlayer`.

#### 2. Code Reuse Analysis (~70% Shared Logic)

```text
Backtrack/
├── Backtrack.Core/                     [Shared Platform-Agnostic Core]
│   ├── OBS/                            - 100% reusable (obs-websocket-dotnet protocol)
│   ├── Video/                          - 100% reusable (trim calculations, transcode logic)
│   ├── Pairing/                        - 100% reusable (pairing state machine & JSON RPCs)
│   └── Settings/                       - 100% reusable (JSON models & config serialization)
│
├── Backtrack.Windows/ (WPF)            [Windows Head - vX.Y.Z]
│   ├── UI/                             - WPF XAML, Windows Acrylic / DWM composition
│   └── Platform/                       - Win32 hooks, Named Pipes, RAM disk (ImDisk)
│
└── Backtrack.Linux/ (Avalonia)         [Linux Head - linux-vX.Y.Z]
    ├── UI/                             - Avalonia XAML, LibVLCSharp.Avalonia
    └── Platform/                       - Unix sockets, XDG portals, Wayland layer-shell
```

