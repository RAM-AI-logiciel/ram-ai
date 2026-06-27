using System.Runtime.InteropServices;

namespace RamAI.Phase3.Memory;

// ─────────────────────────────────────────────────────────────────────────────
//  P/Invoke surface
//
//  What user-space can legitimately do for memory management:
//
//  EmptyWorkingSet       — asks the OS to trim a process's working set
//                          (soft eviction: pages go to standby list)
//
//  PrefetchVirtualMemory — asks the OS to preload pages of another process
//                          into the standby list (Windows 8 / 2012+)
//
//  CreateFileMapping /
//  MapViewOfFile         — file-backed mapping used as our NVMe cache
//
//  VirtualAlloc/Free     — manage our own scratch buffer
//
//  SetProcessWorkingSetSizeEx — set soft/hard working-set limits
//
//  NOTE: intercepting OTHER processes' page faults requires a kernel-mode
//  driver.  This service operates entirely from user space.
// ─────────────────────────────────────────────────────────────────────────────

internal static class NativeMemory
{
    // ── kernel32 ─────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Trims the working set of a process: moves resident pages to the
    /// standby list so RAM can be reclaimed for other uses.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// Prefetches virtual-memory ranges of a process into the standby list.
    /// Available on Windows 8 / Server 2012 and later.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrefetchVirtualMemory(
        IntPtr hProcess,
        UIntPtr NumberOfEntries,
        [In] WIN32_MEMORY_RANGE_ENTRY[] VirtualAddresses,
        uint Flags);

    /// <summary>Sets the minimum and maximum working-set sizes for a process.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize,
        uint   Flags);

    // ── File mapping (NVMe cache) ─────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFileMapping(
        IntPtr  hFile,
        IntPtr  lpFileMappingAttributes,
        uint    flProtect,
        uint    dwMaximumSizeHigh,
        uint    dwMaximumSizeLow,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr MapViewOfFile(
        IntPtr  hFileMappingObject,
        uint    dwDesiredAccess,
        uint    dwFileOffsetHigh,
        uint    dwFileOffsetLow,
        UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFile(
        string lpFileName,
        uint   dwDesiredAccess,
        uint   dwShareMode,
        IntPtr lpSecurityAttributes,
        uint   dwCreationDisposition,
        uint   dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    // ── VirtualAlloc / Free (scratch buffer) ─────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAlloc(
        IntPtr  lpAddress,
        UIntPtr dwSize,
        uint    flAllocationType,
        uint    flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFree(
        IntPtr  lpAddress,
        UIntPtr dwSize,
        uint    dwFreeType);

    // ── psapi (forwarded to kernel32 on Win 7+) ───────────────────────────────

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    internal static extern bool GetProcessMemoryInfo(
        IntPtr hProcess,
        out PROCESS_MEMORY_COUNTERS_EX counters,
        uint cb);

    // ── GetSystemPowerStatus ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 = batterie, 1 = secteur, 255 = inconnu
        public byte BatteryFlag;
        public byte BatteryLifePercent; // 0-100 ou 255 si inconnu
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>
    /// Retourne true si la machine tourne sur batterie (ACLineStatus == 0).
    /// Retourne false si branchée sur secteur ou si le statut est inconnu.
    /// </summary>
    internal static bool IsOnBattery()
    {
        return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
    }

    // ── VRAM via registre pilote WDDM (64 bits) ──────────────────────────────
    // Win32_VideoController.AdapterRAM est un uint32 dans le schéma WMI — il sature
    // à 4 294 967 295 octets (= 4095 Mo) pour tout GPU > 4 Go (bug WMI non corrigé).
    // Le pilote WDDM écrit HardwareInformation.qwMemorySize en REG_QWORD (64 bits)
    // sous la clé de la classe Display Adapters → valeur exacte sans troncature.
    // Fallback WMI conservé pour les environnements sans pilote WDDM standard (VM, etc.).
    internal static long GetTotalVramMb()
    {
        const string DisplayClass =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (baseKey is not null)
            {
                long maxBytes = 0L;
                foreach (var name in baseKey.GetSubKeyNames())
                {
                    // Sous-clés numériques "0000", "0001", … = adaptateurs ; "Properties" etc. ignorées
                    if (!int.TryParse(name, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out _)) continue;

                    using var sub = baseKey.OpenSubKey(name);
                    if (sub is null) continue;

                    // REG_QWORD → long (64 bits) ; REG_DWORD → int (cast en uint pour non-signé)
                    long bytes = sub.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        int  i => (long)(uint)i,
                        _      => 0L,
                    };

                    // Fallback pilote ancien : MemorySize REG_DWORD (limité à 4 Go)
                    if (bytes <= 0)
                        bytes = sub.GetValue("HardwareInformation.MemorySize") is int i2
                            ? (long)(uint)i2 : 0L;

                    if (bytes > maxBytes) maxBytes = bytes;
                }
                if (maxBytes > 0) return maxBytes / (1024L * 1024L);
            }
        }
        catch { /* accès registre refusé ou clé absente */ }

        // Fallback WMI — peut afficher 4095 Mo pour GPU > 4 Go (limitation uint32 WMI)
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT AdapterRAM FROM Win32_VideoController");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                var raw = obj["AdapterRAM"];
                if (raw is not null && ulong.TryParse(raw.ToString(), out ulong bytes) && bytes > 0)
                    return (long)(bytes / (1024UL * 1024UL));
            }
        }
        catch { /* WMI non disponible */ }
        return 0L;
    }

    // ── GlobalMemoryStatusEx ─────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>Retourne la RAM physique disponible en Mo. 0 en cas d'erreur.</summary>
    internal static long GetAvailablePhysicalMb()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref ms)
            ? (long)(ms.ullAvailPhys / (1024UL * 1024UL))
            : 0L;
    }

    /// <summary>Retourne la RAM physique totale en Mo. 0 en cas d'erreur.</summary>
    internal static long GetTotalPhysicalMb()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref ms)
            ? (long)(ms.ullTotalPhys / (1024UL * 1024UL))
            : 0L;
    }

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WIN32_MEMORY_RANGE_ENTRY
    {
        public IntPtr  VirtualAddress;
        public UIntPtr NumberOfBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint    cb;
        public uint    PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_VM_READ           = 0x0010;
    internal const uint PROCESS_SET_QUOTA         = 0x0100;
    internal const uint PROCESS_ALL_ACCESS        = 0x1F0FFF;

    internal const uint PAGE_READWRITE      = 0x04;
    internal const uint SEC_COMMIT          = 0x08000000;
    internal const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    internal const uint FILE_MAP_WRITE      = 0x0002;
    internal const uint FILE_MAP_READ       = 0x0004;

    internal const uint MEM_COMMIT   = 0x00001000;
    internal const uint MEM_RESERVE  = 0x00002000;
    internal const uint MEM_RELEASE  = 0x00008000;

    internal const uint GENERIC_READ    = 0x80000000;
    internal const uint GENERIC_WRITE   = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint OPEN_ALWAYS     = 4;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    internal const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002;
    internal const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;
}
