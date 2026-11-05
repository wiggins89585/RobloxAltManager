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
        new ThemePreset("Dark Glass",    "#101319", "#CC20242C", "#A62A2F39", "#8C343A46", "#40FFFFFF", "#F1F4F9", "#AEB7C6", "#6EA8FE", "#663B5C94"),
        new ThemePreset("Roblox Glass",  "#111213", "#CC232527", "#A62E3033", "#8C393B3D", "#40FFFFFF", "#FFFFFF", "#BDBEBE", "#00A2FF", "#66005A8C"),
        new ThemePreset("Catppuccin",    "#1E1E2E", "#2A2A3C", "#313244", "#45475A", "#45475A", "#CDD6F4", "#A6ADC8", "#CBA6F7", "#433E5E"),
        new ThemePreset("Tokyo Night",   "#16161E", "#1F2335", "#292E42", "#343A55", "#2F344D", "#C0CAF5", "#8990B3", "#7AA2F7", "#2B3A63"),
        new ThemePreset("One Dark",      "#21252B", "#282C34", "#323842", "#3E4451", "#3E4451", "#DCDFE4", "#9DA5B4", "#61AFEF", "#2F4560"),
        new ThemePreset("Gruvbox",       "#1D2021", "#282828", "#3C3836", "#504945", "#504945", "#EBDBB2", "#BDAE93", "#FE8019", "#5A4228"),
        new ThemePreset("Solarized",     "#002B36", "#073642", "#0E4552", "#175563", "#175563", "#EEE8D5", "#93A1A1", "#2AA198", "#0F5560"),
        new ThemePreset("Forest",        "#111A15", "#1A261F", "#24332A", "#2F4236", "#2F4236", "#E2EDE5", "#9DB3A4", "#4CC38A", "#21483A"),
        new ThemePreset("Sunset",        "#1F1420", "#2C1C2C", "#3A2538", "#4A2F46", "#4A2F46", "#F6E6E4", "#C2A6AE", "#FF7A59", "#5C3040"),
        new ThemePreset("Rose",          "#FFF4F6", "#FFFFFF", "#FBE3E9", "#F7D3DC", "#EFC4CF", "#3A1B26", "#7D5563", "#E0457B", "#FAD4E0"),
        new ThemePreset("Sand",          "#F5EFE3", "#FFFBF2", "#EBE2D0", "#E0D5BF", "#D6C9B0", "#2F281C", "#6F6450", "#B7791F", "#EEDFBF"),
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

        // Popups and windows without glass sit on nothing, so they need colours with no transparency.
        TryParseColor(t.Panel, out var popup);
        TryParseColor(t.Bg, out var solid);
        app.Resources["PopupBg"] = new SolidColorBrush(Color.FromRgb(popup.R, popup.G, popup.B));
        app.Resources["BgSolid"] = new SolidColorBrush(Color.FromRgb(solid.R, solid.G, solid.B));

        // Glass themes: every surface is translucent so the acrylic blur shows through the whole UI.
        if (IsGlass(t.Name))
        {
            // Frost strength scales every surface's alpha. The floor keeps text readable over
            // anything behind the window, even with the slider at clear.
            var f = Math.Clamp(GlassFrost, 0, 100) / 100.0;
            byte top = (byte)(0x70 + f * (0xE6 - 0x70));            // panel top alpha: ~44% … ~90%
            var dark = IsDarkGlass(t.Name);
            var roblox = t.Name == "Roblox Glass";
            Color Tint(byte a) => !dark ? White(a) : roblox ? Color.FromArgb(a, 0x23, 0x25, 0x27) : Color.FromArgb(a, 0x20, 0x24, 0x2C);
            byte Up(int by) => (byte)Math.Min(255, top + by);
            app.Resources["Bg"]     = new SolidColorBrush(dark
                ? Color.FromArgb(top, 0x10, 0x13, 0x19)
                : Color.FromArgb((byte)(top * 0.8), 0xED, 0xF4, 0xFD));
            app.Resources["Panel"]  = VGradC(Tint(top), Tint((byte)(top * 0.7)));
            app.Resources["Raised"] = VGradC(Tint(Up(0x2E)), Tint((byte)(top * 0.8)));
            app.Resources["Hover"]  = new SolidColorBrush(dark ? Color.FromArgb(Up(0x18), 0x3A, 0x41, 0x4E) : White(Up(0x40)));
            app.Resources["Line"]   = new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : White(Up(0x1E)));
            app.Resources["Selection"] = new SolidColorBrush(dark
                ? (roblox ? Color.FromArgb(Up(0x10), 0x00, 0x5A, 0x8C) : Color.FromArgb(Up(0x10), 0x3B, 0x5C, 0x94))
                : Color.FromArgb(Up(0x20), 0xB6, 0xD2, 0xFB));
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
        "Liquid Glass" or "Dark Glass" or "Roblox Glass" => WindowBackdrop.Kind.Acrylic, // stronger blur = liquid glass
        "macOS"         => WindowBackdrop.Kind.Mica,
        "Frutiger Aero" => WindowBackdrop.Kind.Mica,
        _               => WindowBackdrop.Kind.None,
    };

    private static bool IsDarkGlass(string name) => name is "Dark Glass" or "Roblox Glass";

    private static bool IsGlass(string name) => name is "Liquid Glass" or "Dark Glass" or "Roblox Glass";

    /// <summary>Re-applies the current theme's backdrop to every open window.</summary>
    public static void ReapplyBackdrop() => ApplyBackdrop();

    /// <summary>Gives every window the backdrop as it opens: glass surfaces are unreadable without one.</summary>
    public static void WatchWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w) WindowBackdrop.Apply(w, BackdropFor(_currentTheme), IsDarkGlass(_currentTheme));
        }));

    private static void ApplyBackdrop()
    {
        if (Application.Current is not { } app) return;
        foreach (Window w in app.Windows)
            WindowBackdrop.Apply(w, BackdropFor(_currentTheme), IsDarkGlass(_currentTheme));
    }

    private static Brush BuildTitleBar(ThemePreset t) => t.Name switch
    {
        "Windows XP"    => VGrad("#3F8CEA", "#245EDC", "#1941A5"),   // Luna blue
        "Frutiger Aero" => VGrad("#86E3F7", "#28A9DD", "#1687C4"),   // glossy sky
        "Liquid Glass"  => VGrad("#CCFFFFFF", "#A6EAF2FC", "#8CD6E6F6"), // translucent frost (acrylic shows through)
        "Dark Glass"    => VGrad("#CC20242C", "#A61A1E26"),
        "Roblox Glass"  => VGrad("#CC232527", "#A6191B1D"),
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

    /// <summary>Liquid Glass frost strength, 0 (clear) – 100 (frosted). Set before applying the theme.</summary>
    public static int GlassFrost { get; set; } = 45;

    private static Color White(byte alpha) => Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

    private static LinearGradientBrush VGradC(params Color[] colors)
    {
        var b = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
        for (var i = 0; i < colors.Length; i++)
            b.GradientStops.Add(new GradientStop(colors[i], colors.Length == 1 ? 0 : (double)i / (colors.Length - 1)));
        return b;
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
