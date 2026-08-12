# Backtrack — notes for future Claude sessions

Backtrack is a C# WPF companion overlay app for OBS (hotkey-summoned HUD:
record/save-replay/gallery/settings, all built around obs-websocket). It's
part of a 3-repo family maintained by the same owner (`ilyambr`):

- **Backtrack** (this repo) — the overlay app.
- **obs-replay-slider** — OBS plugin (C++/Qt), a `ControlPanelDock` for
  per-source replay buffers, exposes a `replay-buffer-slider` websocket
  vendor.
- **obs-source-record** — OBS plugin (C), records/replays individual
  sources via a filter, exposes a `source-record` websocket vendor.

This file is for *durable, cross-cutting* lessons — things that generalize
beyond one line of code. Line-specific reasoning ("why this exact call is
ordered this way") lives in comments at that call site, not here; keep it
there; it survives refactors better than a file:line pointer would, and
it's where it's actually useful — right as someone edits that line.

## Build / verify / push workflow

```
taskkill //IM Backtrack.exe //F
dotnet build -c Release        # NOT a bare `dotnet build` -- that's Debug config, writes to a different output folder the dev shortcut doesn't use
cmd //c start "" "bin\Release\net8.0-windows\Backtrack.exe"
```

There's no way to screenshot/visually verify a live WPF desktop app from
this environment — compile success plus the user's own eyes is the loop.
Don't claim something "looks right" without them confirming.

Recurring build error, happens constantly, always the same fix: XAML's
comment syntax (`<!-- -->`) rejects `--` *anywhere* inside the comment body,
not just at the end (MSBuild error MC3000). Any comment prose containing
"--" as an em-dash substitute, or literally describing a `--` token, needs
rewording (semicolon, or restructure the sentence). Check new comments for
this before building, it will keep happening otherwise.

Git: nothing in this repo auto-commits. A long session can accumulate many
uncommitted experimental changes; `git status`/`git diff --stat` before
assuming "what I last wrote" is still what's on disk — the user edits
files directly sometimes and says "revert to when you did X," which means
diffing against your own last known-good edit, not a git commit.

## WPF/native-content gotchas (the recurring bug family)

**The resize/visibility race ("WPF's FOUC"):** a Window's Win32 resize
(`Width`/`Left`/`Top` → `SetWindowPos`) and a Visibility/panel swap aren't
atomic; a repaint triggered by one can catch the other's stale state for a
frame. `MainWindow.ShowScreen` fixed this with a strict order: hide
everything switch-relevant first (including sibling elements, not just the
6 screen panels) → resize while nothing's visible → show the target panel
already at final bounds. Don't reorder this without re-deriving why each
step is where it is (see the big comment block right there).

**Popups don't reposition on content change.** A `Popup` with
`Placement="Relative"` only reliably recomputes position on a genuine
`IsOpen` false→true transition, not because the content underneath moved
while it stayed open. If a popup's position can go stale (e.g. tracking a
resizing video), force a close+reopen, and do it exactly once per
transition — closing AND reopening from two different places produces a
double-toggle (extra visible flash), not a no-op.

**Native child HWNDs ("airspace") don't play by WPF's rules.** VLC's
`VideoView` hosts a real Win32 window inside the WPF tree. Two consequences,
both bit us more than once:
- It keeps rendering regardless of a WPF ancestor's `Visibility=Collapsed`
  until actually detached (`VideoView.MediaPlayer = null`) — detach FIRST,
  before the slow blocking `Stop()`/`Dispose()` calls, not last.
- `CacheMode = new BitmapCache()` on an ancestor can't capture native HWND
  pixels at all — applying it to a panel that hosts VLC produces a
  stale/partial/blank frame around the video. Any per-panel animation
  optimization must exclude Player specifically.

**`AllowsTransparency="True"` (layered window) tradeoffs**, learned the
hard way this session:
- Enables genuine `Window.Opacity` animation (a non-layered window silently
  ignores it). This is the *only* reliable way to fade a WPF window; the
  native `AnimateWindow(AW_BLEND)` trick was tried first and reverted —
  it doesn't reliably blend on every setup.
- But EVERY frame of ANY animation (window-level opacity, or a panel's own
  fade/scale) forces a full-window software recomposite (GDI
  `UpdateLayeredWindow`), not the cheap hardware-composited incremental
  repaint a normal window gets. Under that cost, WPF drops frames —
  animations read as "snappy"/"bouncy" (`BackEase` overshoot in particular
  degrades badly) unless mitigated with `BitmapCache` on the animating
  element (rasterize once, animate the cached bitmap) and non-overshoot
  easing.
- Silently defeats DWM's real Acrylic blur-behind (`Acrylic.cs`'s
  `SetWindowCompositionAttribute` call) — GDI layered windows and DWM
  composition are mutually exclusive. Currently moot in THIS app because
  `RootBorder`'s background is fully opaque in every theme (no gap for the
  blur to show through either way) — but if `PanelBg` ever becomes
  semi-transparent, this conflict becomes real and someone has to choose.
- A forced synchronous `UpdateLayout()` right after a Visibility swap fixes
  a real bug (content rendering at the wrong size for a frame or two on a
  layered window) but bakes in whatever state the element is in AT THAT
  MOMENT as a real rendered frame. Order matters a lot: set the animation's
  START state (Opacity=0 etc.) *before* the swap+UpdateLayout, not after —
  otherwise you get a flash of the END state followed by a jarring snap
  back to START when the animation actually begins.
- `EnableAnimations` (`AppSettings`, default `false`) is the escape hatch —
  gates both the per-screen entrance animation (`ShowScreen`'s
  `animateEntrance`) and the whole-window fade (`ToggleVisible`/
  `CloseOverlay`'s `FadeWindowIn`/`FadeWindowOut`). The `UpdateLayout()`
  race-condition fix stays active regardless of this setting, since
  `AllowsTransparency` itself can't be toggled at runtime (fixed at window
  creation) — the resize race exists independent of whether animations are
  on.

## Theming

`Theme.Dark.xaml` / `Theme.Light.xaml` / `Theme.Yami.xaml` /
`Theme.Amoled.xaml` merge into `Application.Resources` via `ThemeManager`;
every window must use `DynamicResource` for themed colors, never
`StaticResource` (won't react to a runtime swap), and must NOT define its
own local copy of a themed key (shadows the app-level lookup, silently
breaks theme switching for that one window).

**Every new element with a color needs a `DynamicResource`, checked at the
moment it's added, not after.** Shipped two new floating pills (Player
fullscreen's title chip and transport bar) with a literal hardcoded hex
background — worked fine to look at in whatever theme was active during
that session, but stayed that same color regardless of which theme the
user actually had set, exactly the bug `DynamicResource` throughout this
file exists to prevent. Before hardcoding ANY color on a new element, check
`Theme.Dark.xaml` (or the other three) for an existing key that already
fits (`PanelBgOpaque` — a per-theme translucent surface color — was
already sitting right there and unused for this). Only add a new key
across all four theme files if genuinely nothing existing fits.

Code-behind that builds UI dynamically (not XAML) — `ToastOverlay.xaml.cs`
is the example — can't use `DynamicResource` at all. Look up
`Application.Current.Resources[key]` at the moment each element is built,
don't cache the brush in a `static readonly` field (that's what a `Theme`
key means: it changes at runtime, a cached value doesn't).

A "neutral grey" isn't neutral unless R=G=B exactly. `#101113` LOOKS
plausible but is R16 G17 B19 — B leads R by 3, a real (if small) blue
skew, confirmed with a literal color picker. Check hex values as actual
numbers, not by eye, when a "make this grey not blue/warm/etc." request
comes in — the eye is bad at catching a few-unit channel skew, especially
near black (near-black values are ALSO prone to genuine display-side
color-cast artifacts unrelated to the hex value — a monitor's gamma curve
can make a truly neutral `#080808` look brown/green on some panels; that's
not fixable in code).

`AppSettings` persists enums as `JsonSerializer`'s default numeric value.
New enum members (`AppTheme`, etc.) must be APPENDED, never inserted
before an existing member — an existing settings file's stored `0`/`1`/`2`
would silently start meaning something else.

**No rounded corners as the default** — square edges throughout, no
`CornerRadius` on new elements unless explicitly asked for. Learned via a
real round-trip: added a rounded pill for Player's fullscreen title/back-
button chip, got told "no curves, we don't do rounded corners" and squared
it off, then got told the ROUNDED version was actually what was wanted for
that one specifically ("that looked nice") -- so the rule is "square unless
asked," not "always square." `PlayerTitlePill` (MainWindow.xaml) carries an
explicit rounded exception for exactly this reason;
`PlayerFullscreenTransportBorder` right next to it stayed square, since
that one was never asked to change. A few other pre-existing small ones
also exist (the seek track's own pill/thumb, Settings' theme swatches) --
grandfathered in, not evidence the rule doesn't apply. Default to
`CornerRadius="0"` or just omit it for anything new; ask (or match
`PlayerTitlePill`'s own precedent) before rounding something new rather
than guessing either way.

## OBS/NVENC facts (from live troubleshooting, not guesses)

One physical NVENC chip per GPU generation on most consumer cards (RTX
3080-class); higher-end 40-series parts have two, load-balanced by the
driver. Sharing one encoder between two OBS outputs requires "100%
identical" encoded content — only true for main stream / main recording /
main (global) replay buffer, since those all encode the same composited
scene. A Source Record filter (or obs-replay-slider's per-source rows) can
NEVER share; it encodes an isolated single source. OBS's real
already-existing mitigation is Simple mode's "Use stream encoder for
recording" or Advanced mode's "(Use stream encoder)" — obs-source-record
can't do anything about *that* sharing, only about (a) not defaulting new
filters to NVENC when the main output already claimed it, and (b)
detecting/reporting overload with attribution (which output/filter).

`obs-frontend-api.h` exposes `obs_frontend_get_recording_output()` /
`_streaming_output()` / `_replay_buffer_output()`; `obs.h` exposes
`obs_output_get_total_frames`/`_frames_dropped`/`obs_output_active` on any
`obs_output_t*`. Use delta-since-last-check dropped-frame rate, not
lifetime-cumulative, or a brief startup hiccup pollutes the number forever.

**obs-source-record has no independent way to report "am I active" as a
list/status query** — it registers per-filter action requests
(`record_start`, `replay_buffer_start`, etc.) on its own `source-record`
vendor, but `ListRecordRowsAsync`/`ListReplayRowsAsync` in
`ObsService.cs` both query `replay-buffer-slider` (obs-replay-slider's
`ControlPanelDock`) exclusively — that's a SEPARATE plugin acting as the
only aggregator/discovery layer. If obs-replay-slider isn't installed, or
its dock isn't loaded/tracking a given filter, Backtrack has zero visibility
into that filter's real status even though obs-source-record itself is
running fine and the buffer is visibly active inside OBS. This is a real
gap, not just a race condition — a genuine fix means adding a `list_rows`-
style request directly to obs-source-record's own vendor.

## Release workflow

obs-replay-slider: CI auto-creates a release only on a tag push, and it has
repeatedly landed as a broken draft under a mismatched "untagged-XXXX" URL
— `gh release delete` + `gh release create` with downloaded CI artifacts is
the reliable path. obs-source-record: no release automation at all, always
`gh release create` manually after a plain push + CI build. Both repos'
CI has a recurring harmless infra flake (clang-format's Homebrew install /
"Remove temp artifacts") that fails even when every real build job
succeeded — check per-job status (`gh api .../jobs`), don't trust the
overall run `conclusion` field alone.

**Standing instruction: never touch the user's local OBS install** for a
plugin update (no taskkill/reinstall/relaunch) — publish the GitHub release
and let Backtrack's own auto-updater install it.

## Design tips for myself

- **Verify assumptions about hex/RGB/pixel values with actual numbers**,
  not by eye from a screenshot — "looks blue-ish" turned into a real,
  provable 3-unit channel skew once actually computed; guessing a fix
  before confirming the number wastes a whole round-trip.
- **When a UI positioning complaint has already flip-flopped once**
  ("too far" → fixed → "too close"), ask for a screenshot before guessing
  a new number in either direction — this exact back-and-forth happened
  with the Player overlay's back button.
- **A synchronous `UpdateLayout()`/forced-paint fix is a scalpel, not a
  hammer.** Each time one got added to fix a real rendering bug, it also
  baked in the CURRENT state of whatever it touched as a real visible
  frame — introduced 3 different follow-on bugs across this session
  (blank box, rubberband, "idle panel stays") purely from WHERE in the
  sequence it ran relative to other state changes. Trace the full
  before/after property state at the exact moment it'll fire, on paper,
  before adding another one.
- **When the user says "I made some changes, revert to when you did X,"**
  diff against your own last edit (you know exactly what you wrote), don't
  assume a clean git commit exists to revert to — this session's
  AllowsTransparency work went through ~15 rounds of edits before any of
  it was committed.
- **A big irreversible-feeling mechanical request** (rewrite every file,
  strip every comment, etc.) is worth a moment of pushback with concrete
  reasoning before executing, especially when it trades a currently-working
  property (comments staying accurate because they live with the code) for
  a superficially-tidier one that will silently rot (external line
  references). State the tradeoff, offer the alternative that gets at the
  likely underlying goal, let the user decide — don't just comply or just
  refuse.
- **This app's whole animation system exists in tension with itself**: the
  fixes that make one screen's transition correct (force a sync layout,
  cache a bitmap, skip a cache) are per-screen exceptions, not a universal
  rule, because the underlying cause (layered window + native HWND +
  dynamic content) differs per screen. Don't generalize a fix from one
  screen to all screens without checking which of those three factors
  actually applies there too.
