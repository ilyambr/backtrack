using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace Backtrack;

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
public sealed record ThemeInfo(string Id, string DisplayName, double SortOrder, bool IsBuiltIn, ResourceDictionary Dictionary);

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
public static class ThemeManager
{
    // Every key this app actually looks up via DynamicResource somewhere.
    // Checked at discovery time so an incomplete theme file (a typo, or a
    // user's own in-progress theme missing a key) gets skipped with a log
    // line instead of leaving some window silently unstyled or throwing
    // the moment a DynamicResource lookup for a genuinely absent key
    // finally happens to run -- which is only ever at USE time, not load
    // time, so this is the one place that can catch it safely up front.
    private static readonly string[] RequiredKeys =
    {
        "PanelBg", "PanelBgOpaque", "ThumbnailBg", "Hairline", "Text0", "Text1", "Text2", "Accent",
        "Rec", "RecDark", "Stream", "Green", "NewestClip",
        "RowBg", "RowHoverBg", "TileHoverBg", "BorderSubtle", "BorderMedium", "BorderStrong",
        "SeekTrackBg", "SeekTrackBuffer", "BadgeBg", "BadgeBorder",
    };

    // Optional resource keys a theme file can set to control how it's
    // presented, read as plain values out of the same dictionary -- none of
    // these is looked up via DynamicResource anywhere, so there's no naming
    // collision risk with the real color keys above.
    private const string DisplayNameKey = "ThemeDisplayName";
    private const string SortOrderKey = "ThemeSortOrder";
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
    private const string BuiltInKey = "ThemeBuiltIn";

    private static string ThemesFolder => Path.Combine(AppContext.BaseDirectory, "Themes");

    public static string Current { get; private set; } = "Dark";

    /// <summary>
    /// Scans Themes\Theme.*.xaml and loads each as a real ResourceDictionary.
    /// Re-scans disk every call rather than caching -- cheap (a handful of
    /// small XAML files), and means a theme file edited while Backtrack is
    /// running (someone actively developing their own) shows up correctly
    /// the next time Settings rebuilds its swatches or Apply runs, without
    /// needing a dedicated "reload themes" step.
    /// </summary>
    public static List<ThemeInfo> DiscoverThemes()
    {
        var result = new List<ThemeInfo>();
        if (!Directory.Exists(ThemesFolder))
            return result;

        foreach (string path in Directory.EnumerateFiles(ThemesFolder, "Theme.*.xaml"))
        {
            string fileName = Path.GetFileNameWithoutExtension(path); // "Theme.YamiAcri"
            if (!fileName.StartsWith("Theme.", StringComparison.OrdinalIgnoreCase))
                continue;
            string id = fileName["Theme.".Length..];
            if (id.Length == 0)
                continue;

            try
            {
                var dict = new ResourceDictionary { Source = new Uri(path, UriKind.Absolute) };
                string? missingKey = RequiredKeys.FirstOrDefault(k => !dict.Contains(k));
                if (missingKey is not null)
                {
                    AppLog.Write($"ThemeManager: skipping '{id}' ({Path.GetFileName(path)}) -- missing required key '{missingKey}'.");
                    continue;
                }

                string displayName = dict.Contains(DisplayNameKey) && dict[DisplayNameKey] is string s && s.Length > 0
                    ? s
                    : PrettifyId(id);
                double sortOrder = dict.Contains(SortOrderKey) && dict[SortOrderKey] is double d ? d : 1000;
                bool isBuiltIn = dict.Contains(BuiltInKey) && dict[BuiltInKey] is true;

                result.Add(new ThemeInfo(id, displayName, sortOrder, isBuiltIn, dict));
            }
            catch (Exception ex)
            {
                AppLog.Write($"ThemeManager: failed to load '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        // Built-in themes each set ThemeSortOrder to keep their historical
        // Dark/Light/Yami/Acri/Amoled order; anything without an opinion
        // (sortOrder left at the 1000 default above) falls in alphabetically
        // by Id after all of those, rather than interleaving unpredictably.
        return result.OrderBy(t => t.SortOrder).ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // "MyCoolTheme" -> "My Cool Theme" -- only used as a fallback for a
    // theme file that didn't set its own ThemeDisplayName; a user dropping
    // in a new file still gets a readable label with zero extra effort,
    // just not a hand-tuned one like the built-ins' "Yami (OBS)"/"AMOLED".
    private static string PrettifyId(string id) => Regex.Replace(id, "(?<!^)(?<![A-Z])([A-Z])", " $1");

    /// <summary>
    /// Applies the theme with this Id if one was actually discovered;
    /// otherwise falls back to the first available theme (by sort order),
    /// and if literally none were found (Themes folder missing/empty --
    /// shouldn't happen in a normal install), leaves whatever's already
    /// merged alone rather than clearing it down to nothing.
    /// </summary>
    public static void Apply(string themeId)
    {
        List<ThemeInfo> themes = DiscoverThemes();
        ThemeInfo? theme = themes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase))
            ?? themes.FirstOrDefault();
        if (theme is null)
        {
            AppLog.Write($"ThemeManager.Apply: no theme files found in '{ThemesFolder}' (wanted '{themeId}').");
            return;
        }

        Current = theme.Id;

        Application app = Application.Current;
        // Removes any previously-merged theme dictionary by content (has a
        // "PanelBg" key -- guaranteed present in every theme dictionary and
        // nothing else this app merges in) rather than assuming a fixed
        // index, so this stays safe to call again later (Settings' toggle).
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (app.Resources.MergedDictionaries[i].Contains("PanelBg"))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }
        app.Resources.MergedDictionaries.Add(theme.Dictionary);
    }
}
