# [Free] Backtrack - a hotkey-summoned overlay HUD for recording, replay buffer, and clip browsing

**Download:** https://github.com/ilyambr/backtrack/releases
**Source:** https://github.com/ilyambr/backtrack (MIT - see repo)
**Platform:** Windows 10/11
**Requires:** OBS Studio with obs-websocket enabled (built in since OBS 28)

---

## What it is

Backtrack is a small companion app that sits over whatever you're doing - game, browser, doesn't matter - and pops up on a hotkey (default `Ctrl+Alt+G`) so you can record, save your replay buffer, and browse your clips without ever alt-tabbing into OBS itself.

It talks to OBS entirely over **obs-websocket**. It never touches your scene collection, profiles, or settings directly - it's a companion, not a plugin, and it works fine even if you never install anything else alongside it.

## Features

- **Fullscreen clip player** - VLC-backed playback with trim, rename, and a true edge-to-edge fullscreen mode.
- **Gallery** - browse saved clips by folder, with a small "newest" indicator so you can find your last clip at a glance.
- **Record / Save Replay from the HUD** - start/stop recording, or flush a running replay buffer to disk, without leaving your game.
- **Themes** - Dark, Light, AMOLED, and a Yami/Acri theme that matches OBS's own built-in themes, auto-detected from disk (drop in your own theme file and it shows up - no rebuild needed).
- **Optional RAM disk support** - mount a RAM-backed virtual drive for OBS to write its replay buffer to, for lower-latency saves on big buffers. Fully optional, see disclosure below.
- **Remote OBS** - point Backtrack at an OBS instance running on a *different* PC (e.g. a separate capture/streaming box) instead of the one it's running on.
- **Clip sharing** - pull clips from another PC's Backtrack instance on the same network (or over Tailscale) into your own Gallery.
- **Per-source buffers** - pairs with two plugins I also maintain, [obs-source-record](https://github.com/ilyambr/obs-source-record) (fork) and [obs-replay-slider](https://github.com/ilyambr/obs-replay-slider), to record/replay individual sources instead of just the whole scene. Neither is required - Backtrack works with just OBS's own global replay buffer too.
- **Status indicators** - floating badges for Recording, Streaming, Replay Buffer, Virtual Camera, and Mic, plus an encoder-overload warning, so you can see what's active without tabbing into OBS.

## Screenshots

| Idle | Gallery | Player | Settings |
|---|---|---|---|
| ![idle](https://raw.githubusercontent.com/ilyambr/backtrack/main/docs/idle.png) | ![gallery](https://raw.githubusercontent.com/ilyambr/backtrack/main/docs/gallery.png) | ![player](https://raw.githubusercontent.com/ilyambr/backtrack/main/docs/player.png) | ![settings](https://raw.githubusercontent.com/ilyambr/backtrack/main/docs/settings.png) |

## Full disclosure - please read before installing

I'd rather you know this going in than find it out later:

- **If you have obs-source-record and/or obs-replay-slider installed, Backtrack can auto-update them.** Applying an update closes OBS (if it's running), silently runs the plugin's own installer, then reopens OBS. This is on by default - it's off automatically while you're live, and you can disable it entirely in `Settings > Disable OBS plugin auto-updates`. Both plugins are mine, built and released from this same account, but "an app closes OBS and installs things without asking each time" is exactly the kind of behavior you should know about up front, not discover later.
- **The optional RAM disk feature installs a driver.** It's backed by [ImDisk](https://ltr-data.se/opencode.html#ImDisk), a well-known open-source virtual disk driver - not anything custom. It's bundled unmodified, signed by a certificate Windows already trusts (works fine under Secure Boot), and every install goes through a normal Windows UAC prompt - never silent. It's entirely optional and removable like any other driver.
- **It adds a Windows Firewall exemption for itself, once, on first launch.** A real UAC prompt (not silent) to add four rules scoped specifically to `Backtrack.exe` - not a blanket port opening. These are for the peer-to-peer clip-sharing feature; happens once ever, whether or not you ever turn that on.

None of these touch your scene collection, OBS settings, or profiles. Full detail is in the [README](https://github.com/ilyambr/backtrack#3-what-backtrack-installs-on-your-system).

## Known limitations

- obs-source-record has no way to report its own active/recording status - Backtrack only shows per-source status live in the HUD if obs-replay-slider is also installed.
- Clip sharing between two PCs times out after 15s if the paired PC is asleep or unreachable.
- This is alpha software - expect rough edges, and please file issues rather than suffering in silence.

## AI disclosure

Most of Backtrack's code was written with AI assistance (Claude) - same for the two plugins linked above: Every feature was specified, iterated on, tested, and verified against a running build by hand, including catching and fixing AI-introduced bugs along the way. But the code itself is substantially AI-written.

## Feedback / bugs

[github.com/ilyambr/backtrack/issues](https://github.com/ilyambr/backtrack/issues) - genuinely want to hear about anything broken, especially compatibility with less common OBS/GPU/driver setups I don't have here to test against.
