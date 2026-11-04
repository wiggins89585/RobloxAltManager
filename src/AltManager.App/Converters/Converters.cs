using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AltManager.Core.Models;

namespace AltManager.App.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Valid = Frozen("#23A55A");
    private static readonly Brush Expired = Frozen("#F23F43");
    private static readonly Brush Error = Frozen("#F0B232");
    private static readonly Brush Banned = Frozen("#B4181B");
    private static readonly Brush Unknown = Frozen("#80848E");

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
