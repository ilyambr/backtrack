using System;
using System.Windows;

namespace Backtrack;

public enum AppTheme { Dark, Light }

/// <summary>
/// Loads/swaps the app-wide theme resource dictionary (Theme.Dark.xaml or
/// Theme.Light.xaml) into Application.Resources -- every window references
/// the same shared keys (PanelBg, Text0, Rec, ...) via DynamicResource
/// (never StaticResource, which wouldn't react to a runtime swap), so a
/// single Apply() call here updates every open window at once, no per-window
/// plumbing needed.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var dict = new ResourceDictionary
        {
            Source = new Uri(theme == AppTheme.Dark ? "Theme.Dark.xaml" : "Theme.Light.xaml", UriKind.Relative)
        };

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
