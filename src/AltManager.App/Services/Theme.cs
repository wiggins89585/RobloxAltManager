using System.Windows;
using System.Windows.Media;

namespace AltManager.App.Services;

/// <summary>A full colour palette for one theme. Hex strings keep the presets readable.</summary>
public sealed record ThemePreset(
    string Name, string Bg, string Panel, string Raised, string Hover,
    string Line, string Fg, string Muted, string Accent, string Selection);

/// <summary>
/// Runtime theming. The palette lives as named <see cref="SolidColorBrush"/> resources in App.xaml
/// and every control references them via DynamicResource, so swapping the dictionary entries
/// repaints the whole UI live — no restart. (Sealed styles freeze the brush instances they capture,
/// which is why we <b>replace</b> the resources rather than mutate them.)
/// </summary>
public static class Theme
{
    public const string DefaultAccent = "#5865F2";
    public const string DefaultTheme = "Dark";

    /// <summary>Built-in themes. Each is a complete palette; the accent can be overridden per user.</summary>
    public static readonly IReadOnlyList<ThemePreset> Themes = new[]
    {
        //                 Name              Bg         Panel      Raised     Hover      Line       Fg         Muted      Accent     Selection
        new ThemePreset("Dark",          "#1E1F22", "#2B2D31", "#383A40", "#404249", "#3F4147", "#DBDEE1", "#949BA4", "#5865F2", "#33386A"),
        new ThemePreset("Light",         "#F2F3F5", "#FFFFFF", "#E3E5E8", "#D7D9DD", "#D1D3D7", "#1E1F22", "#5C5E66", "#5865F2", "#D7DBF7"),
        new ThemePreset("Midnight",      "#0B0D10", "#15181D", "#1E232B", "#272D37", "#262B33", "#E6EAF0", "#8A93A3", "#3B82F6", "#16305E"),
        new ThemePreset("Windows XP",    "#ECE9D8", "#FFFFFF", "#D4D0C8", "#C8E0F4", "#919B9C", "#101828", "#3A4A5A", "#2F6FD8", "#CBD9F2"),
        new ThemePreset("Frutiger Aero", "#DCF2FF", "#FFFFFF", "#C7E9F7", "#B2E2F2", "#8FCBE2", "#0B2A3A", "#3E6A80", "#17B6C9", "#BDEAF4"),
        // Liquid Glass / macOS use translucent panels so the Acrylic/Mica backdrop reads as frosted glass.
        new ThemePreset("Liquid Glass",  "#EAF0F8", "#B8FFFFFF", "#8CFFFFFF", "#A6DCE8F8", "#66AEC2DA", "#10203A", "#4A5A74", "#2F7BF6", "#99B9D6FA"),
        new ThemePreset("macOS",         "#ECECEC", "#EAFFFFFF", "#D9E4E4E6", "#C8D8D8DB", "#D0D0D3", "#1D1D1F", "#6E6E73", "#0A84FF", "#D4E7FE"),
        new ThemePreset("Dracula",       "#282A36", "#343746", "#44475A", "#4F5267", "#44475A", "#F8F8F2", "#9FA3B3", "#BD93F9", "#3B3A5A"),
        new ThemePreset("Nord",          "#2E3440", "#3B4252", "#434C5E", "#4C566A", "#434C5E", "#ECEFF4", "#A9B2C3", "#88C0D0", "#3A4961"),
    };

    public static ThemePreset Find(string? name) =>
        Themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    /// <summary>A few accent presets for the custom-accent picker.</summary>
    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Blurple", "#5865F2"), ("Blue", "#3B82F6"), ("Green", "#23A55A"), ("Teal", "#14B8A6"),
        ("Purple", "#9B59F6"), ("Pink", "#EB459E"), ("Red", "#F23F43"), ("Orange", "#F0883E"), ("Gold", "#F0B232"),
    };

    /// <summary>Applies a theme by name. A non-blank, valid <paramref name="accentOverride"/> replaces the theme's own accent.</summary>
    public static void ApplyTheme(string? themeName, string? accentOverride = null)
    {
        if (Application.Current is not { } app) return;
        var t = Find(themeName);

        Set(app, "Bg", t.Bg);
        Set(app, "Panel", t.Panel);
        Set(app, "Raised", t.Raised);
        Set(app, "Hover", t.Hover);
        Set(app, "Line", t.Line);
        Set(app, "Fg", t.Fg);
        Set(app, "Muted", t.Muted);
        Set(app, "Selection", t.Selection);

        // Liquid Glass: make EVERY surface glassy — gradient frosted panels, glossy translucent
        // buttons and bright glass edges — so the acrylic blur shows through the whole UI.
        if (t.Name == "Liquid Glass")
        {
            app.Resources["Bg"]     = Solid("#38EDF4FD");               // very translucent base (toolbars, inputs)
            app.Resources["Panel"]  = VGrad("#4FFFFFFF", "#26FFFFFF");   // see-through frosted surface
            app.Resources["Raised"] = VGrad("#8CFFFFFF", "#47FFFFFF");   // glossy glass button (kept readable)
            app.Resources["Hover"]  = Solid("#B3FFFFFF");
            app.Resources["Line"]   = Solid("#8CFFFFFF");               // soft glass edge
            app.Resources["Selection"] = Solid("#80B6D2FB");
        }

        // Title bar gets a theme-specific treatment (gradients for XP / Aero / Glass) so the top of
        // the app reads as the theme it's named after.
        app.Resources["TitleBar"] = BuildTitleBar(t);
        app.Resources["TitleBarText"] = new SolidColorBrush(TitleBarText(t));

        var accent = TryParseColor(accentOverride, out _) && !string.IsNullOrWhiteSpace(accentOverride)
            ? accentOverride! : t.Accent;
        ApplyAccent(accent);

        _currentTheme = t.Name;
        ApplyBackdrop();
        ThemeChanged?.Invoke(t.Name);
    }

    /// <summary>Raised after a theme is applied (name of the theme). Lets the window swap chrome, e.g. macOS traffic lights.</summary>
    public static event Action<string>? ThemeChanged;

    public static string CurrentTheme => _currentTheme;
    private static string _currentTheme = DefaultTheme;

    /// <summary>Which Win11 backdrop a theme wants behind its (translucent) surfaces.</summary>
    public static WindowBackdrop.Kind BackdropFor(string? name) => Find(name).Name switch
    {
        "Liquid Glass"  => WindowBackdrop.Kind.Acrylic, // stronger blur = liquid glass
        "macOS"         => WindowBackdrop.Kind.Mica,
        "Frutiger Aero" => WindowBackdrop.Kind.Mica,
        _               => WindowBackdrop.Kind.None,
    };

    /// <summary>Re-applies the current theme's backdrop to the main window (call once the handle exists).</summary>
    public static void ReapplyBackdrop() => ApplyBackdrop();

    private static void ApplyBackdrop()
    {
        if (Application.Current?.MainWindow is { } w)
            WindowBackdrop.Apply(w, BackdropFor(_currentTheme));
    }

    private static Brush BuildTitleBar(ThemePreset t) => t.Name switch
    {
        "Windows XP"    => VGrad("#3F8CEA", "#245EDC", "#1941A5"),   // Luna blue
        "Frutiger Aero" => VGrad("#86E3F7", "#28A9DD", "#1687C4"),   // glossy sky
        "Liquid Glass"  => VGrad("#CCFFFFFF", "#A6EAF2FC", "#8CD6E6F6"), // translucent frost (acrylic shows through)
        "macOS"         => VGrad("#F6F6F8", "#E4E4E7", "#D6D6DA"),       // light brushed-metal bar
        "Light"         => Solid("#E7E9EC"),
        _               => Solid(t.Bg),                             // dark themes: flat
    };

    private static Color TitleBarText(ThemePreset t)
    {
        // White on the saturated bars, otherwise the theme's own foreground.
        if (t.Name is "Windows XP" or "Frutiger Aero") return Colors.White;
        TryParseColor(t.Fg, out var c);
        return c;
    }

    private static LinearGradientBrush VGrad(params string[] hex)
    {
        var b = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
        for (var i = 0; i < hex.Length; i++)
            if (TryParseColor(hex[i], out var c))
                b.GradientStops.Add(new GradientStop(c, hex.Length == 1 ? 0 : (double)i / (hex.Length - 1)));
        return b;
    }

    private static SolidColorBrush Solid(string hex)
    {
        TryParseColor(hex, out var c);
        return new SolidColorBrush(c);
    }

    /// <summary>Replaces only the accent brushes (used for the live accent preview).</summary>
    public static void ApplyAccent(string? hex)
    {
        if (Application.Current is not { } app) return;
        if (!TryParseColor(hex, out var c)) TryParseColor(DefaultAccent, out c);
        app.Resources["Accent"] = new SolidColorBrush(c);
        app.Resources["AccentHover"] = new SolidColorBrush(Scale(c, 0.82));
    }

    private static void Set(Application app, string key, string hex)
    {
        if (TryParseColor(hex, out var c)) app.Resources[key] = new SolidColorBrush(c);
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
