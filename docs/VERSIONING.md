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
