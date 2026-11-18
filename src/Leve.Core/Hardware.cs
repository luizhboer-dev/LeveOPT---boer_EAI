using static Leve.Core.Hive;

namespace Leve.Core;

/// <summary>Uma placa de rede física e as palavras-chave de economia de energia que o driver dela realmente expõe.</summary>
public sealed record NetworkAdapterInfo(string ClassSubkey, string Description, IReadOnlyDictionary<string, string> PowerKeywords);

/// <summary>Ajustes da v1.1 montados a partir do hardware e dos jogos encontrados NESTE PC.</summary>
public static class HardwareCatalog
{
    public const string DirectXPreferences = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string NetworkClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Palavras-chave de economia de energia de drivers Intel/Realtek/Killer. Só as que JÁ existem no driver são
    /// alteradas (para "0" = desligado); nada é criado às cegas.
    /// </summary>
    public static readonly string[] PowerSavingKeywords =
    [
        "*EEE", "EEE", "EEELinkAdvertisement", "AdvancedEEE", "EnableGreenEthernet", "GreenEthernet",
        "PowerSavingMode", "ULPMode", "*SelectiveSuspend", "EnablePowerManagement", "GigaLite",
    ];

    /// <summary>Notebook com duas GPUs: grava "alto desempenho" (GpuPreference=2) para cada executável de jogo.</summary>
    public static Tweak? GpuPerGame(IEnumerable<string> gameExecutables)
    {
        var exes = gameExecutables
            .Where(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !e.Contains(';') && !e.Contains('='))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exes.Count == 0) return null;

        return new Tweak
        {
            Id = "gaming.gpu-per-game",
            Title = $"GPU dedicada para {exes.Count} jogo(s)",
            Area = Area.Gaming,
            Description = "Em notebooks com GPU integrada + dedicada, alguns jogos abrem na integrada e perdem metade do FPS. "
                          + "Grava \"alto desempenho\" para cada jogo encontrado, o mesmo que Configurações > Sistema > Tela > Gráficos.",
            Profiles = Profiles.All,
            Restart = Restart.AppRestart,
            AppliesTo = f => f.HasHybridGpu,
            Impact = Impact.Fps,
            Strength = 3,
            Gain = "0% ou 2×–5×: enorme se o jogo estava rodando na GPU integrada",
            Actions = exes.Select(e => (TweakAction)new SetStringFlags(Target.Reg(HKCU, DirectXPreferences, e), [new("GpuPreference", "2")])).ToList(),
        };
    }

    /// <summary>Um ajuste por placa: desliga Energy Efficient Ethernet / Green Ethernet e o "permitir desligar este dispositivo".</summary>
    public static IReadOnlyList<Tweak> NetworkPowerSaving(IEnumerable<NetworkAdapterInfo> adapters)
    {
        var tweaks = new List<Tweak>();
        foreach (var adapter in adapters)
        {
            if (!IsSafeSubkey(adapter.ClassSubkey)) continue;
            var path = $@"{NetworkClass}\{adapter.ClassSubkey}";

            var actions = adapter.PowerKeywords.Keys
                .Where(k => PowerSavingKeywords.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => (TweakAction)new SetValue(Target.Reg(HKLM, path, k), StateValue.Text("0")))
                .ToList();
            // 0x18 = "não permitir que o Windows desligue este dispositivo para economizar energia".
            actions.Add(new SetValue(Target.Reg(HKLM, path, "PnPCapabilities"), StateValue.DWord(0x18)));

            tweaks.Add(new Tweak
            {
                Id = $"network.power.{adapter.ClassSubkey}",
                Title = $"Rede sem economia: {adapter.Description}",
                Area = Area.Power,
                Description = "Desliga a economia de energia da placa de rede (EEE/Green Ethernet e desligamento pelo Windows). "
                              + "Algumas placas Realtek/Intel geram picos de ping com isso ligado.",
                Risk = Risk.Medium,
                Profiles = Profiles.Ultra,
                Restart = Restart.Reboot,
                Impact = Impact.Network,
                Gain = "menos picos de ping em algumas placas Realtek/Intel; não muda FPS",
                Note = "a placa reconecta ao reiniciar; em notebook na bateria a rede gasta um pouco mais",
                Actions = actions,
            });
        }
        return tweaks;
    }

    private static bool IsSafeSubkey(string subkey) => subkey.Length == 4 && subkey.All(char.IsAsciiDigit);
}
