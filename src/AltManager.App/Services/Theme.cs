using System.Windows;
using System.Windows.Media;

namespace AltManager.App.Services;

/// <summary>
/// Runtime theming. The palette lives as named <see cref="SolidColorBrush"/> resources in App.xaml;
/// because every control references the same brush instance via StaticResource, mutating that
/// brush's Color updates the whole UI live — no restart needed.
/// </summary>
public static class Theme
{
    public const string DefaultAccent = "#5865F2";

    /// <summary>A few tasteful accent presets for the settings picker.</summary>
    public static readonly (string Name, string Hex)[] Presets =
    {
        ("Blurple", "#5865F2"),
        ("Blue",    "#3B82F6"),
        ("Green",   "#23A55A"),
        ("Teal",    "#14B8A6"),
        ("Purple",  "#9B59F6"),
        ("Pink",    "#EB459E"),
        ("Red",     "#F23F43"),
        ("Orange",  "#F0883E"),
        ("Gold",    "#F0B232"),
    };

    /// <summary>Applies the accent colour across the app. Falls back to the default for a bad value.</summary>
    public static void ApplyAccent(string? hex)
    {
        if (!TryParseColor(hex, out var c)) TryParseColor(DefaultAccent, out c);
        SetBrush("Accent", c);
        SetBrush("AccentHover", Scale(c, 0.82)); // slightly darker, for pressed/hover states
    }

    private static void SetBrush(string key, Color c)
    {
        if (Application.Current is not { } app) return;
        if (app.Resources[key] is SolidColorBrush { IsFrozen: false } b) b.Color = c;
        else app.Resources[key] = new SolidColorBrush(c);
    }

    private static Color Scale(Color c, double f) =>
        Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color c) { color = c; return true; }
        }
        catch { /* not a colour */ }
        return false;
    }
}
