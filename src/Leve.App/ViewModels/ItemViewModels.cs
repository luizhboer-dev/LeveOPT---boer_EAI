using Leve.Core;

namespace Leve.App.ViewModels;

public sealed class TweakItemVm : ObservableObject
{
    private bool _isChecked;
    private TweakStatus _status = TweakStatus.Unknown;

    public TweakItemVm(Tweak tweak, bool blocked)
    {
        Tweak = tweak;
        IsBlocked = blocked;
    }

    public Tweak Tweak { get; }

    /// <summary>Bloqueado quando o Leve foi elevado com outra conta e o ajuste mexe em HKCU.</summary>
    public bool IsBlocked { get; }

    public bool IsEnabled => !IsBlocked;

    public Action? CheckedChanged { get; set; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (IsBlocked) value = false;
            if (Set(ref _isChecked, value)) CheckedChanged?.Invoke();
        }
    }

    public TweakStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusLabel));
        }
    }

    public string StatusLabel => Status switch
    {
        TweakStatus.On => "ON",
        TweakStatus.Off => "off",
        TweakStatus.Partial => "parcial",
        TweakStatus.NotPresent => "n/a",
        _ => "?",
    };

    public string Title => Tweak.Title;
    public Risk Risk => Tweak.Risk;
    public string RiskLabel => Labels.Risk(Tweak.Risk);
    public string AreaLabel => Labels.Area(Tweak.Area);

    public string ProfilesLabel => Tweak.Profiles switch
    {
        Profiles.All => "Ultra + Dev",
        Profiles.Ultra => "só Ultra",
        Profiles.Dev => "só Dev",
        _ => "manual",
    };

    public string MetaLine =>
        $"{AreaLabel} · risco {RiskLabel} · {ProfilesLabel} · efeito: {Labels.Restart(Tweak.Restart)}";

    public string Details => string.Join(Environment.NewLine, Tweak.Actions.Select(a => "  " + a.Describe()));

    public string? Note => Tweak.Note is null ? null : "! " + Tweak.Note;

    public bool HasNote => Tweak.Note is not null;
}

public enum NavKind { Tweaks, Snapshots, Cleanup, Report, Fps }

public sealed class NavItemVm(MainViewModel owner, string label, Area? area, NavKind kind, int count) : ObservableObject
{
    private bool _isSelected;

    public string Label { get; } = label;
    public Area? Area { get; } = area;
    public NavKind Kind { get; } = kind;
    public string Display => Kind == NavKind.Tweaks ? $"> {Label,-14}{count,3}" : $"> {Label}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) owner.SelectNav(this);
        }
    }
}

public sealed class CleanupItemVm(Leve.Windows.CleanupCategory category) : ObservableObject
{
    private bool _isChecked = true;
    private long? _size;

    public Leve.Windows.CleanupCategory Category { get; } = category;
    public string Title => Category.Title;
    public string Description => Category.Description;

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public long? Size
    {
        get => _size;
        set
        {
            if (Set(ref _size, value)) OnPropertyChanged(nameof(SizeText));
        }
    }

    public string SizeText => Size is { } bytes ? Labels.Size(bytes) : "medindo…";
}

public sealed class SnapshotVm(Snapshot snapshot)
{
    public Snapshot Snapshot { get; } = snapshot;
    public string IdText => $"#{Snapshot.Id:D4}";
    public string When => Snapshot.CreatedAt.LocalDateTime.ToString("dd/MM HH:mm");
    public string Label => Snapshot.Label;
    public string CountText => $"{Snapshot.Entries.Count} alterações";
    public bool CanRevert => Snapshot.CanRevert;

    public string StatusText => Snapshot.Status switch
    {
        SnapshotStatus.Pending => "interrompido",
        SnapshotStatus.Committed => "aplicado",
        SnapshotStatus.Partial => "aplicado c/ falhas",
        SnapshotStatus.Reverted => "desfeito",
        _ => "desfeito em parte",
    };
}
