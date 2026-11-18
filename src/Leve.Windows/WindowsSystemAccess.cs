using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

/// <summary>
/// Leitura e escrita reais. Tudo por API nativa: Registro (visão 64 bits), SCM para serviços
/// (inclusive "Automático atrasado") e Task Scheduler via COM. Leituras nunca abrem processo filho.
/// Serviços: muda o tipo de início e, em seguida, para (ou inicia) o serviço na hora, em modo best-effort.
/// Apps e energia: ver WindowsSystemAccess.Packages.cs.
/// </summary>
public sealed partial class WindowsSystemAccess : ISystemAccess
{
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";

    public StateValue Read(Target target)
    {
        try
        {
            return target.Type switch
            {
                TargetType.Registry => ReadRegistry(target),
                TargetType.Service => ReadService(target.Name),
                TargetType.ScheduledTask => ReadTask(target.Path),
                TargetType.Package => ReadPackage(target.Name),
                TargetType.Power => ReadPower(target.Name),
                TargetType.DefenderExclusion => ReadExclusion(target.Path),
                TargetType.Display => ReadDisplay(target.Name),
                _ => throw new NotSupportedException(target.Type.ToString()),
            };
        }
        catch (Exception ex) when (ex is not SystemAccessException)
        {
            throw new SystemAccessException($"não foi possível ler: {Describe(ex)}", ex);
        }
    }

    public void Write(Target target, StateValue value)
    {
        try
        {
            switch (target.Type)
            {
                case TargetType.Registry: WriteRegistry(target, value); break;
                case TargetType.Service: WriteService(target.Name, value); break;
                case TargetType.ScheduledTask: WriteTask(target.Path, value); break;
                case TargetType.Package: WritePackage(target.Name, value); break;
                case TargetType.Power: WritePower(target.Name, value); break;
                case TargetType.DefenderExclusion: WriteExclusion(target.Path, value); break;
                case TargetType.Display: WriteDisplay(target.Name, value); break;
                default: throw new NotSupportedException(target.Type.ToString());
            }
        }
        catch (Exception ex) when (ex is not SystemAccessException)
        {
            throw new SystemAccessException($"não foi possível gravar: {Describe(ex)}", ex);
        }
    }

    private static string Describe(Exception ex) =>
        (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message;

    // ── Registro ─────────────────────────────────────────────────────────────

    private static RegistryKey OpenBase(Hive hive) => RegistryKey.OpenBaseKey(
        hive == Hive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    private static StateValue ReadRegistry(Target t)
    {
        using var root = OpenBase(t.Hive);
        using var key = root.OpenSubKey(t.Path, writable: false);
        if (key is null) return StateValue.Absent;

        var raw = key.GetValue(t.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return StateValue.Absent;

        return key.GetValueKind(t.Name) switch
        {
            RegistryValueKind.DWord => StateValue.DWord(unchecked((uint)Convert.ToInt32(raw, CultureInfo.InvariantCulture))),
            RegistryValueKind.QWord => StateValue.QWord(unchecked((ulong)Convert.ToInt64(raw, CultureInfo.InvariantCulture))),
            RegistryValueKind.String => StateValue.Text((string)raw),
            RegistryValueKind.ExpandString => new StateValue(ValueKind.ExpandString, (string)raw),
            RegistryValueKind.MultiString => new StateValue(ValueKind.MultiString, string.Join('\0', (string[])raw)),
            RegistryValueKind.Binary => new StateValue(ValueKind.Binary, Convert.ToBase64String((byte[])raw)),
            var other => throw new NotSupportedException($"tipo de Registro {other} não suportado"),
        };
    }

    private static void WriteRegistry(Target t, StateValue v)
    {
        using var root = OpenBase(t.Hive);

        if (v.Kind == ValueKind.Absent)
        {
            using (var existing = root.OpenSubKey(t.Path, writable: true))
            {
                existing?.DeleteValue(t.Name, throwOnMissingValue: false);
            }
            RemoveEmptyPolicyKeys(root, t.Path);
            return;
        }

        using var key = root.CreateSubKey(t.Path, writable: true);
        var data = v.Data ?? string.Empty;
        switch (v.Kind)
        {
            case ValueKind.DWord:
                key.SetValue(t.Name, unchecked((int)uint.Parse(data, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                break;
            case ValueKind.QWord:
                key.SetValue(t.Name, unchecked((long)ulong.Parse(data, CultureInfo.InvariantCulture)), RegistryValueKind.QWord);
                break;
            case ValueKind.String:
                key.SetValue(t.Name, data, RegistryValueKind.String);
                break;
            case ValueKind.ExpandString:
                key.SetValue(t.Name, data, RegistryValueKind.ExpandString);
                break;
            case ValueKind.MultiString:
                key.SetValue(t.Name, data.Length == 0 ? Array.Empty<string>() : data.Split('\0'), RegistryValueKind.MultiString);
                break;
            case ValueKind.Binary:
                key.SetValue(t.Name, Convert.FromBase64String(data), RegistryValueKind.Binary);
                break;
            default:
                throw new NotSupportedException($"{v.Kind} não pode ser gravado no Registro");
        }
    }

    /// <summary>
    /// Ao reverter uma política, apaga chaves de política que ficaram vazias (ex.: Policies\Microsoft\Edge),
    /// senão o navegador continua dizendo "gerenciado pela organização". Só atua abaixo de "\Policies\".
    /// </summary>
    private static void RemoveEmptyPolicyKeys(RegistryKey root, string path)
    {
        var current = path;
        while (true)
        {
            var policiesIndex = current.IndexOf(@"\Policies\", StringComparison.OrdinalIgnoreCase);
            if (policiesIndex < 0) return;

            using (var key = root.OpenSubKey(current, writable: false))
            {
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            }
            root.DeleteSubKey(current, throwOnMissingSubKey: false);

            var slash = current.LastIndexOf('\\');
            if (slash <= policiesIndex + @"\Policies".Length) return; // nunca apaga a própria "Policies"
            current = current[..slash];
        }
    }

    // ── Serviços ─────────────────────────────────────────────────────────────

    private static StateValue ReadService(string name)
    {
        using var root = OpenBase(Hive.HKLM);
        using var key = root.OpenSubKey($@"{ServicesRoot}\{name}", writable: false);
        if (key?.GetValue("Start") is not int start) return StateValue.Missing;

        var delayed = key.GetValue("DelayedAutostart") is int d && d == 1;
        return StateValue.ServiceState(start switch
        {
            0 => ServiceStart.Boot,
            1 => ServiceStart.System,
            2 => delayed ? ServiceStart.AutomaticDelayed : ServiceStart.Automatic,
            3 => ServiceStart.Manual,
            4 => ServiceStart.Disabled,
            _ => throw new InvalidDataException($"Start={start} desconhecido"),
        });
    }

    private static void WriteService(string name, StateValue v)
    {
        if (v.Kind != ValueKind.Service) throw new NotSupportedException($"{v.Kind} não é tipo de início de serviço");
        var mode = v.AsService();
        uint startType = mode switch
        {
            ServiceStart.Boot => 0u,
            ServiceStart.System => 1u,
            ServiceStart.Automatic or ServiceStart.AutomaticDelayed => 2u,
            ServiceStart.Manual => 3u,
            ServiceStart.Disabled => 4u,
            _ => throw new NotSupportedException(mode.ToString()),
        };

        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = Native.OpenServiceW(scm, name, Native.SERVICE_QUERY_CONFIG | Native.SERVICE_CHANGE_CONFIG);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!Native.ChangeServiceConfigW(service, Native.SERVICE_NO_CHANGE, startType, Native.SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (startType == 2u)
                {
                    var info = new Native.SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = mode == ServiceStart.AutomaticDelayed };
                    if (!Native.ChangeServiceConfig2W(service, Native.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }

            // Efeito imediato: desligado/manual para agora; automático volta a rodar (ao desfazer).
            ApplyRuntimeState(scm, name, mode);
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }

    // ── Tarefas agendadas (COM Schedule.Service) ─────────────────────────────

    private static dynamic Scheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static (string Folder, string Name) SplitTaskPath(string path)
    {
        var i = path.LastIndexOf('\\');
        return i <= 0 ? (@"\", path.TrimStart('\\')) : (path[..i], path[(i + 1)..]);
    }

    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);

    // O binder dinâmico converte o HRESULT em FileNotFoundException/DirectoryNotFoundException,
    // não em COMException: por isso a checagem é pelo HResult de qualquer exceção.
    private static bool IsNotFound(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
        return inner.HResult is ErrorFileNotFound or ErrorPathNotFound;
    }

    // Uma conexão com o Task Scheduler reaproveitada (o objeto COM é free-threaded).
    private readonly Lock _schedulerGate = new();
    private object? _scheduler;

    private dynamic GetScheduler()
    {
        lock (_schedulerGate)
        {
            return _scheduler ??= (object)Scheduler();
        }
    }

    private void ResetScheduler()
    {
        lock (_schedulerGate)
        {
            if (_scheduler is not null && Marshal.IsComObject(_scheduler)) Marshal.FinalReleaseComObject(_scheduler);
            _scheduler = null;
        }
    }

    private dynamic GetTaskObject(string path)
    {
        var (folder, name) = SplitTaskPath(path);
        try
        {
            return GetScheduler().GetFolder(folder).GetTask(name);
        }
        catch (Exception ex) when (!IsNotFound(ex))
        {
            // Conexão pode ter caído (serviço reiniciado): reconecta uma vez.
            ResetScheduler();
            return GetScheduler().GetFolder(folder).GetTask(name);
        }
    }

    private StateValue ReadTask(string path)
    {
        try
        {
            var task = GetTaskObject(path);
            bool enabled = task.Enabled;
            return StateValue.TaskState(enabled);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return StateValue.Missing;
        }
    }

    private void WriteTask(string path, StateValue v)
    {
        if (v.Kind != ValueKind.TaskState) throw new NotSupportedException($"{v.Kind} não é estado de tarefa");
        var task = GetTaskObject(path);
        task.Enabled = v.AsTaskEnabled();
    }
}
