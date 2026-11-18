using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Leve.Core;

namespace Leve.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

public static class Labels
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    public static string Area(Area area) => area switch
    {
        Core.Area.Privacy => "privacidade",
        Core.Area.Background => "fundo",
        Core.Area.Services => "serviços",
        Core.Area.Gaming => "gaming",
        Core.Area.Visual => "visual",
        Core.Area.Apps => "apps",
        Core.Area.Startup => "inicialização",
        Core.Area.Power => "energia",
        Core.Area.Security => "segurança",
        _ => area.ToString().ToLowerInvariant(),
    };

    public static string Impact(Impact impact, int strength)
    {
        var arrows = new string('▲', Math.Clamp(strength, 1, 3));
        if (impact.HasFlag(Core.Impact.Fps)) return "FPS " + arrows;
        if (impact.HasFlag(Core.Impact.Lows)) return "1%low " + arrows;
        if (impact.HasFlag(Core.Impact.Latency)) return "latência " + arrows;
        if (impact.HasFlag(Core.Impact.Loading)) return "carga " + arrows;
        if (impact.HasFlag(Core.Impact.Network)) return "ping " + arrows;
        if (impact.HasFlag(Core.Impact.Ram)) return "RAM";
        if (impact.HasFlag(Core.Impact.Privacy)) return "privac.";
        if (impact.HasFlag(Core.Impact.Comfort)) return "uso";
        return "—";
    }

    public static string Risk(Risk risk) => risk switch
    {
        Core.Risk.Low => "baixo",
        Core.Risk.Medium => "médio",
        _ => "alto",
    };

    public static string Restart(Restart restart) => restart switch
    {
        Core.Restart.AppRestart => "reabrir o app/jogo",
        Core.Restart.Logoff => "sair e entrar de novo",
        Core.Restart.Reboot => "reiniciar o PC",
        _ => "imediato",
    };
}
