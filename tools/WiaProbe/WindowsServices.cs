// ============================================================================
// WindowsServices.cs: состояние служб Windows без sc.exe и PowerShell
//
// Зачем свои P/Invoke: вывод `sc.exe` локализован и зависит от кодовой
// страницы консоли, а для отчёта нужны одинаковые английские состояния
// (RUNNING/STOPPED) и учётная запись службы — по ней видно, работает ли
// NAPS2.WebScan под LocalSystem (это одна из проверяемых версий отказа).
// ============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WiaProbe;

/// <summary>Сведения о службе Windows</summary>
internal sealed record WindowsServiceInfo(
    string Name,
    bool Installed,
    string State,
    string StartType,
    string? Account,
    string? BinaryPath,
    uint? ProcessId,
    string? Error)
{
    public bool Running => Installed && State == "RUNNING";
}

internal static class WindowsServices
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ErrorServiceDoesNotExist = 1060;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfig(
        IntPtr service, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfig2(
        IntPtr service, uint infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        public int DelayedAutostart;
    }

    public static WindowsServiceInfo Query(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return new WindowsServiceInfo(name, false, "?", "-", null, null, null,
                $"OpenSCManager: {LastError()}");
        }

        try
        {
            var service = OpenService(manager, name, ServiceQueryStatus | ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                if (code == ErrorServiceDoesNotExist)
                {
                    return new WindowsServiceInfo(name, false, "-", "-", null, null, null, null);
                }

                return new WindowsServiceInfo(name, false, "?", "-", null, null, null,
                    $"OpenService: {new Win32Exception(code).Message} ({code})");
            }

            try
            {
                var (state, processId, stateError) = ReadState(service);
                var (startType, account, binaryPath, configError) = ReadConfig(service);
                return new WindowsServiceInfo(name, true, state, startType, account, binaryPath,
                    processId, stateError ?? configError);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static (string State, uint? ProcessId, string? Error) ReadState(IntPtr service)
    {
        const int size = 4096;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, size, out _))
            {
                return ("?", null, $"QueryServiceStatusEx: {LastError()}");
            }

            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            return (StateName(status.CurrentState), status.ProcessId == 0 ? null : status.ProcessId, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (string StartType, string? Account, string? BinaryPath, string? Error) ReadConfig(IntPtr service)
    {
        const int size = 8192;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceConfig(service, buffer, size, out _))
            {
                return ("?", null, null, $"QueryServiceConfig: {LastError()}");
            }

            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            var startType = StartTypeName(config.StartType) + (IsDelayed(service) ? " (отложенный)" : "");
            return (startType, Ptr(config.ServiceStartName), Ptr(config.BinaryPathName), null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Признак отложенного автозапуска: installer.iss создаёт службу с start= delayed-auto</summary>
    private static bool IsDelayed(IntPtr service)
    {
        const int size = 16;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, buffer, size, out _))
            {
                return false;
            }

            return Marshal.PtrToStructure<ServiceDelayedAutoStartInfo>(buffer).DelayedAutostart != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string StateName(uint state) => state switch
    {
        1 => "STOPPED",
        2 => "START_PENDING",
        3 => "STOP_PENDING",
        4 => "RUNNING",
        5 => "CONTINUE_PENDING",
        6 => "PAUSE_PENDING",
        7 => "PAUSED",
        _ => $"UNKNOWN({state})"
    };

    private static string StartTypeName(uint startType) => startType switch
    {
        0 => "загрузка",
        1 => "система",
        2 => "авто",
        3 => "вручную",
        4 => "отключена",
        _ => "?"
    };

    private static string? Ptr(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        var text = Marshal.PtrToStringUni(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
