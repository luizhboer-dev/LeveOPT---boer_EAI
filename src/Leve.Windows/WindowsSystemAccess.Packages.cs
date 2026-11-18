using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

public sealed partial class WindowsSystemAccess
{
    // Registro por usuário dos pacotes AppX: leitura instantânea, sem PowerShell.
    private const string PackageRepository =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly Regex SafePackageName = new(@"^[A-Za-z0-9._\-]+$", RegexOptions.CultureInvariant);

    private static void ValidatePackageName(string name)
    {
        if (!SafePackageName.IsMatch(name)) throw new ArgumentException($"nome de pacote inválido: {name}");
    }

    // ── Apps (AppX) ──────────────────────────────────────────────────────────

    private static StateValue ReadPackage(string name)
    {
        ValidatePackageName(name);
        using var root = OpenBase(Hive.HKCU);
        using var repository = root.OpenSubKey(PackageRepository, writable: false)
            ?? throw new InvalidOperationException("repositório de apps do usuário não encontrado");

        foreach (var fullName in repository.GetSubKeyNames())
        {
            if (!fullName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) continue;

            // Chaves órfãs existem: só conta como instalado se a pasta do pacote ainda existir.
            using (var package = repository.OpenSubKey(fullName, writable: false))
            {
                if (package?.GetValue("PackageRootFolder") is string folder && !Directory.Exists(folder)) continue;
            }

            // Nome_Versão_Arquitetura_Recurso_Publicador → família = Nome_Publicador
            var publisherId = fullName[(fullName.LastIndexOf('_') + 1)..];
            return StateValue.PackageInstalled($"{name}_{publisherId}");
        }
        return StateValue.PackageRemoved;
    }

    private static void WritePackage(string name, StateValue value)
    {
        ValidatePackageName(name);
        if (value.Kind != ValueKind.Package) throw new NotSupportedException($"{value.Kind} não é estado de app");

        if (value.PackageFamily is { } family)
        {
            // Desfazer: re-registra o pacote que continua no PC (provisionado/em WindowsApps). Sem internet.
            ValidatePackageName(family);
            try
            {
                ProcessRunner.PowerShell($"Add-AppxPackage -RegisterByFamilyName -MainPackage '{family}' -ErrorAction Stop");
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"o pacote não está mais no PC; reinstale pela Microsoft Store ({ex.Message})", ex);
            }
        }
        else
        {
            // Só a conta atual: o pacote provisionado fica, o que permite desfazer.
            ProcessRunner.PowerShell($"$p = Get-AppxPackage -Name '{name}'; if ($p) {{ $p | Remove-AppxPackage -ErrorAction Stop }}");
        }
    }

    // ── Energia ──────────────────────────────────────────────────────────────

    // (subgrupo, configuração, valor na tomada, valor na bateria). Na bateria: economia normal do Windows.
    private static readonly (string Subgroup, string Setting, int Ac, int Dc)[] LevePlanSettings =
    [
        ("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, 1),     // USB selective suspend
        ("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", 0, 2),     // PCIe ASPM (2 = economia máxima)
        ("54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", 100, 5),   // CPU mínima
        ("54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 100, 10),  // núcleos ativos mínimos (core parking)
        ("0012ee47-9041-4b5d-9b77-535fba8b1442", "6738e2c4-e8a5-4a42-b16a-e040e769756e", 0, 600),   // desligar disco (s)
    ];

    private static StateValue ReadPower(string setting)
    {
        switch (setting)
        {
            case LevePlan.ActiveSchemeSetting:
            {
                var result = Native.PowerGetActiveScheme(IntPtr.Zero, out var pointer);
                if (result != 0) throw new InvalidOperationException($"PowerGetActiveScheme falhou ({result})");
                try
                {
                    return StateValue.Text(Marshal.PtrToStructure<Guid>(pointer).ToString("D"));
                }
                finally
                {
                    Native.LocalFree(pointer);
                }
            }
            case LevePlan.HibernateSetting:
            {
                using var root = OpenBase(Hive.HKLM);
                using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power", writable: false);
                var raw = key?.GetValue("HibernateEnabled") ?? key?.GetValue("HibernateEnabledDefault");
                return StateValue.Flag(raw is not int value || value != 0);
            }
            default:
                throw new NotSupportedException($"configuração de energia desconhecida: {setting}");
        }
    }

    private static void WritePower(string setting, StateValue value)
    {
        switch (setting)
        {
            case LevePlan.ActiveSchemeSetting:
                if (value.Kind != ValueKind.String || !Guid.TryParse(value.Data, out var scheme))
                    throw new ArgumentException($"plano de energia inválido: {value}");
                if (scheme.ToString("D") == LevePlan.Id) EnsureLevePlan();
                ProcessRunner.Run(ProcessRunner.Powercfg, $"/setactive {scheme:D}");
                break;

            case LevePlan.HibernateSetting:
                if (value.Kind != ValueKind.Flag) throw new ArgumentException($"valor inválido para hibernação: {value}");
                ProcessRunner.Run(ProcessRunner.Powercfg, value.AsFlag() ? "/hibernate on" : "/hibernate off");
                break;

            default:
                throw new NotSupportedException($"configuração de energia desconhecida: {setting}");
        }
    }

    /// <summary>Cria (uma vez) o plano Leve Ultimate com GUID fixo e reaplica os valores de tomada.</summary>
    private static void EnsureLevePlan()
    {
        if (!SchemeExists(LevePlan.Id))
        {
            try
            {
                ProcessRunner.Run(ProcessRunner.Powercfg, $"/duplicatescheme {LevePlan.UltimatePerformance} {LevePlan.Id}");
            }
            catch (InvalidOperationException)
            {
                try
                {
                    ProcessRunner.Run(ProcessRunner.Powercfg, $"/duplicatescheme {LevePlan.HighPerformance} {LevePlan.Id}");
                }
                catch (InvalidOperationException)
                {
                    // PCs com Modern Standby (S0) só têm o Equilibrado.
                    ProcessRunner.Run(ProcessRunner.Powercfg, $"/duplicatescheme {LevePlan.Balanced} {LevePlan.Id}");
                }
            }
            ProcessRunner.Run(ProcessRunner.Powercfg,
                $"/changename {LevePlan.Id} \"{LevePlan.Name}\" \"Plano do Leve: desempenho máximo na tomada\"");
        }

        foreach (var (subgroup, settingId, acValue, dcValue) in LevePlanSettings)
        {
            // Configuração inexistente neste hardware (ex.: sem PCIe ASPM) só é pulada.
            TryPowercfg($"/setacvalueindex {LevePlan.Id} {subgroup} {settingId} {acValue}");
            TryPowercfg($"/setdcvalueindex {LevePlan.Id} {subgroup} {settingId} {dcValue}");
        }
    }

    private static void TryPowercfg(string arguments)
    {
        try
        {
            ProcessRunner.Run(ProcessRunner.Powercfg, arguments);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool SchemeExists(string id)
    {
        using var root = OpenBase(Hive.HKLM);
        using var key = root.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{id}", writable: false);
        return key is not null;
    }

    // ── Serviços: efeito imediato ────────────────────────────────────────────

    private static void ApplyRuntimeState(IntPtr scm, string name, ServiceStart mode)
    {
        // Best-effort: falhar aqui (serviço protegido, dependentes rodando) não invalida a mudança do tipo de início.
        var handle = Native.OpenServiceW(scm, name, Native.SERVICE_QUERY_STATUS | Native.SERVICE_STOP | Native.SERVICE_START);
        if (handle == IntPtr.Zero) return;
        try
        {
            if (mode is ServiceStart.Disabled or ServiceStart.Manual)
                Native.ControlService(handle, Native.SERVICE_CONTROL_STOP, out _);
            else if (mode is ServiceStart.Automatic or ServiceStart.AutomaticDelayed)
                Native.StartServiceW(handle, 0, IntPtr.Zero);
        }
        finally
        {
            Native.CloseServiceHandle(handle);
        }
    }
}
