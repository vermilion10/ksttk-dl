using System.Windows;
using KsttkDl.App.Infrastructure;
using Microsoft.Win32;

namespace KsttkDl.App.Theme;

/// <summary>swaps the light and dark token dictionaries; "system" follows the Windows app theme and keeps following it.</summary>
public static class ThemeManager
{
    private static ThemeChoice _choice = ThemeChoice.System;

    public static bool IsDark { get; private set; }

    public static void Initialize(ThemeChoice choice)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
            {
                Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            }
        };

        Apply(choice);
    }

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        IsDark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemPrefersDark(),
        };

        var tokens = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative),
        };

        // the token dictionary is always the first merged dictionary; the styles after it only refer to tokens dynamically
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[0] = tokens;
    }

    private static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
