using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AltManager.Core.Models;

namespace AltManager.App.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    // Valid green, Expired red, Banned orange, Error amber, Unknown gray.
    internal static readonly Brush Valid = Frozen("#35B779");
    internal static readonly Brush Expired = Frozen("#E5534B");
    internal static readonly Brush Error = Frozen("#D9B44A");
    internal static readonly Brush Banned = Frozen("#E8853D");
    internal static readonly Brush Unknown = Frozen("#8B948E");

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        AccountStatus.Valid => Valid,
        AccountStatus.Expired => Expired,
        AccountStatus.Error => Error,
        AccountStatus.Banned => Banned,
        _ => Unknown
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

/// <summary>The status colour at low opacity, for the background of a status badge.</summary>
public sealed class StatusToTintConverter : IValueConverter
{
    private static readonly Dictionary<AccountStatus, Brush> Cache = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var status = value is AccountStatus s ? s : AccountStatus.Unknown;
        lock (Cache)
        {
            if (Cache.TryGetValue(status, out var b)) return b;
            var full = (SolidColorBrush)new StatusToBrushConverter().Convert(status, t, p, c);
            var tint = new SolidColorBrush(Color.FromArgb(0x2E, full.Color.R, full.Color.G, full.Color.B));
            tint.Freeze();
            return Cache[status] = tint;
        }
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ActivityLevelToBrushConverter : IValueConverter
{
    private static readonly Brush Info = Frozen("#949BA4");
    private static readonly Brush Warn = Frozen("#F0B232");
    private static readonly Brush Alert = Frozen("#F23F43");

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        AltManager.App.ViewModels.ActivityLevel.Warn => Warn,
        AltManager.App.ViewModels.ActivityLevel.Alert => Alert,
        _ => Info
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

public sealed class PresenceToBrushConverter : IValueConverter
{
    private static readonly Brush Offline = Frozen("#80848E");
    private static readonly Brush Online = Frozen("#5B9BD5");
    private static readonly Brush InGame = Frozen("#23A55A");
    private static readonly Brush InStudio = Frozen("#F0B232");

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        PresenceType.Online => Online,
        PresenceType.InGame => InGame,
        PresenceType.InStudio => InStudio,
        _ => Offline
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        AccountStatus.Valid => "Valid",
        AccountStatus.Expired => "Expired Cookie",
        AccountStatus.Error => "Check failed",
        AccountStatus.Banned => "Banned",
        _ => "Not checked"
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is not true;
}

/// <summary>true → GridLength(parameter px), false → GridLength(0). Used to collapse side columns.</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not true) return new GridLength(0);
        return p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var w)
            ? new GridLength(w) : new GridLength(1, GridUnitType.Star);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
