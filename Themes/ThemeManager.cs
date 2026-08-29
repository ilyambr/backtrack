using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace Backtrack;

public sealed record ThemeInfo(string Id, string DisplayName, double SortOrder, bool IsBuiltIn, ResourceDictionary Dictionary);

public static class ThemeManager
{
    private static readonly string[] RequiredKeys =
    {
        "PanelBg", "PanelBgOpaque", "ThumbnailBg", "Hairline", "Text0", "Text1", "Text2", "Accent",
        "Rec", "RecDark", "Stream", "Green", "NewestClip",
        "RowBg", "RowHoverBg", "TileHoverBg", "BorderSubtle", "BorderMedium", "BorderStrong",
        "SeekTrackBg", "SeekTrackBuffer", "BadgeBg", "BadgeBorder",
    };

    private const string DisplayNameKey = "ThemeDisplayName";
    private const string SortOrderKey = "ThemeSortOrder";
    private const string BuiltInKey = "ThemeBuiltIn";

    public static string ThemesFolder => Path.Combine(AppContext.BaseDirectory, "Themes");

    public static string Current { get; private set; } = "Dark";

    public static List<ThemeInfo> DiscoverThemes()
    {
        var result = new List<ThemeInfo>();
        if (!Directory.Exists(ThemesFolder))
            return result;

        foreach (string path in Directory.EnumerateFiles(ThemesFolder, "Theme.*.xaml"))
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
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

        return result.OrderBy(t => t.SortOrder).ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string PrettifyId(string id) => Regex.Replace(id, "(?<!^)(?<![A-Z])([A-Z])", " $1");

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
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (app.Resources.MergedDictionaries[i].Contains("PanelBg"))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }
        app.Resources.MergedDictionaries.Add(theme.Dictionary);
    }
}
