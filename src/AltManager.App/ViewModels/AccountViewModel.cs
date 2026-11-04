using AltManager.Core.Models;
using AltManager.Core.Launch;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltManager.App.ViewModels;

/// <summary>Bindable wrapper. Edits to Alias/Group write through to the model; the shell VM persists.</summary>
public partial class AccountViewModel : ObservableObject
{
    public Account Model { get; }

    public AccountViewModel(Account model) => Model = model;

    /// <summary>When on, usernames and IDs are masked in the UI (for streaming/screen-sharing).</summary>
    public static bool Masked { get; set; }

    public long UserId => Model.UserId;
    public string Username => Mask(Model.Username);
    public string DisplayName => Model.DisplayName;
    public string Label => Mask(Model.Label);
    public string UserIdText => Masked ? "•••••••" : Model.UserId.ToString();

    private static string Mask(string s) =>
        !Masked || string.IsNullOrEmpty(s) ? s : s[0] + new string('•', Math.Max(3, s.Length - 1));

    public string Notes
    {
        get => Model.Notes;
        set { if (Model.Notes != value) { Model.Notes = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasNotes)); Changed?.Invoke(this); } }
    }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Model.Notes);

    public string TagsText => string.Join(", ", Model.Tags);
    public bool HasTags => Model.Tags.Count > 0;

    public void SetTags(IEnumerable<string> tags)
    {
        Model.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(HasTags));
        Changed?.Invoke(this);
    }

    public bool Favorite
    {
        get => Model.Favorite;
        set { if (Model.Favorite != value) { Model.Favorite = value; OnPropertyChanged(); Changed?.Invoke(this); } }
    }

    /// <summary>"3m ago" / "never" — how long since the cookie was last confirmed.</summary>
    public string LastCheckedText
    {
        get
        {
            if (Model.LastValidated is not { } t) return "never checked";
            var d = DateTimeOffset.UtcNow - t;
            return d < TimeSpan.FromMinutes(1) ? "just now"
                : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m ago"
                : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours}h ago"
                : $"{(int)d.TotalDays}d ago";
        }
    }

    /// <summary>Re-reads display fields after a masking or validation change.</summary>
    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(UserIdText));
        OnPropertyChanged(nameof(LastCheckedText));
    }

    public string Alias
    {
        get => Model.Alias;
        set { if (Model.Alias != value) { Model.Alias = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); Changed?.Invoke(this); } }
    }

    public string Group
    {
        get => Model.Group;
        set { if (Model.Group != value) { Model.Group = string.IsNullOrWhiteSpace(value) ? "Ungrouped" : value.Trim(); OnPropertyChanged(); Changed?.Invoke(this); } }
    }

    public string GameOverride
    {
        get => LaunchTargetParser.Parse(Model.GameOverride).PlaceId?.ToString() ?? Model.GameOverride;
        set
        {
            var v = value?.Trim() ?? "";
            if (v.Length > 0 && (!long.TryParse(v, out var gameId) || gameId <= 0))
                throw new ArgumentException("Enter a positive numeric Roblox game ID.");
            if (Model.GameOverride != v) { Model.GameOverride = v; OnPropertyChanged(); OnPropertyChanged(nameof(GameLabel)); Changed?.Invoke(this); }
        }
    }

    /// <summary>Short text for the card view: the account's own game, or a hint that it uses the default.</summary>
    public string GameLabel => string.IsNullOrWhiteSpace(Model.GameOverride) ? "Default game" : Model.GameOverride;

    public bool HasPassword => !string.IsNullOrEmpty(Model.EncryptedPassword);
    public bool HasTotp => !string.IsNullOrEmpty(Model.EncryptedTotpSecret);
    public bool HasProxy => !string.IsNullOrWhiteSpace(Model.Proxy);
    public void RefreshProxy() => OnPropertyChanged(nameof(HasProxy));

    // ---- Cached account info (Refresh info) ----
    public string RobuxText => Model.Robux is { } r ? r.ToString("N0") : "—";
    public string PremiumText => Model.Premium is true ? "Premium" : Model.Premium is false ? "—" : "?";
    public bool IsPremium => Model.Premium is true;
    public string AgeText
    {
        get
        {
            if (Model.Created is not { } c) return "—";
            var days = (DateTimeOffset.UtcNow - c).TotalDays;
            return days >= 365 ? $"{days / 365:0.0}y" : $"{(int)days}d";
        }
    }

    public void RefreshInfo()
    {
        OnPropertyChanged(nameof(RobuxText));
        OnPropertyChanged(nameof(PremiumText));
        OnPropertyChanged(nameof(IsPremium));
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(HasTotp));
    }

    public AccountStatus Status
    {
        get => Model.Status;
        set { if (Model.Status != value) { Model.Status = value; OnPropertyChanged(); } }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    private PresenceType _presence;
    /// <summary>Where the account is, for the presence tooltip (game name when in-game).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    private string? _lastLocation;

    public string PresenceText => Presence switch
    {
        PresenceType.InGame => LastLocation is { Length: > 0 } g ? $"In game: {g}" : "In game",
        PresenceType.Online => "Online",
        PresenceType.InStudio => "In Studio",
        _ => "Offline"
    };

    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _launchState = "";
    [ObservableProperty] private int? _pid;

    /// <summary>Raised when a persisted field changes, so the vault can be saved.</summary>
    public event Action<AccountViewModel>? Changed;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(UserIdText));
        OnPropertyChanged(nameof(LastCheckedText));
    }
}
