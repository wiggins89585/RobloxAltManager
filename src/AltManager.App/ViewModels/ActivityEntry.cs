namespace AltManager.App.ViewModels;

public enum ActivityLevel { Info, Warn, Alert }

/// <summary>One line in the activity feed / audit log.</summary>
public sealed class ActivityEntry
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public ActivityLevel Level { get; init; }
    public string Text { get; init; } = "";

    public string TimeText => Time.LocalDateTime.ToString("HH:mm:ss");
}
