using System;
using System.Windows;

namespace Backtrack;

// Appended in order added (never inserted before an existing member) --
// AppSettings persists this as JsonSerializer's default numeric enum value,
// so an existing settings file's stored 0/1/2 must keep meaning Dark/Light/Yami.
public enum AppTheme { Dark, Light, Yami, Amoled }

/// <summary>
/// Loads/swaps the app-wide theme resource dictionary (Theme.Dark.xaml,
/// Theme.Light.xaml, Theme.Yami.xaml, or Theme.Amoled.xaml) into
/// Application.Resources -- every window references the same shared keys
/// (PanelBg, Text0, Rec, ...) via DynamicResource (never StaticResource,
/// which wouldn't react to a runtime swap), so a single Apply() call here
/// updates every open window at once, no per-window plumbing needed.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        // Root-relative ("/Themes/...", not "Theme.Dark.xaml") -- a bare
        // relative Uri resolves against the CALLING code's own location,
        // which happens to still work here since ThemeManager.cs lives in
        // the same Themes/ folder as the XAML files it's loading, but a
        // leading slash is the actually-correct, location-independent form
        // for a pack URI and doesn't silently break if either one ever
        // moves again on its own.
        string file = theme switch
        {
            AppTheme.Light => "/Themes/Theme.Light.xaml",
            AppTheme.Yami => "/Themes/Theme.Yami.xaml",
            AppTheme.Amoled => "/Themes/Theme.Amoled.xaml",
            _ => "/Themes/Theme.Dark.xaml",
        };
        var dict = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };

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
        app.Resources.MergedDictionaries.Add(dict);
    }
}
