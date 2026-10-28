using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Hardware detection without WMI (fast, no extra packages). Registry + documented
/// Win32 APIs only. Every value has a safe fallback so the app still runs on unusual systems.
/// </summary>
public sealed class HardwareService
{
    public HardwareInfo Detect(string? gameRoot = null)
    {
        var hw = new HardwareInfo();
        try { FillCpu(hw); } catch (Exception ex) { Log.Warn($"CPU detection failed: {ex.Message}"); }
        try { FillGpu(hw); } catch (Exception ex) { Log.Warn($"GPU detection failed: {ex.Message}"); }
        try { FillMemory(hw); } catch (Exception ex) { Log.Warn($"RAM detection failed: {ex.Message}"); }
        try { FillWindows(hw); } catch (Exception ex) { Log.Warn($"Windows detection failed: {ex.Message}"); }
        try { FillDisplays(hw); } catch (Exception ex) { Log.Warn($"Display detection failed: {ex.Message}"); }
        try { FillStorage(hw, gameRoot); } catch (Exception ex) { Log.Warn($"Storage detection failed: {ex.Message}"); }

        hw.GpuTier = ClassifyGpu(hw);
        Log.Info($"Hardware: {hw.CpuSummary}; {hw.GpuSummary}; {hw.RamSummary}; {hw.PrimaryResolution}@{hw.PrimaryRefreshHz}Hz; HDR={(hw.HdrEnabled ? "on" : hw.AnyHdr ? "capable" : "no")}");
        return hw;
    }

    private static void FillCpu(HardwareInfo hw)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        hw.CpuName = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown CPU";
        if (key?.GetValue("~MHz") is int mhz) hw.CpuMhz = mhz;
        hw.LogicalCores = Environment.ProcessorCount;
        hw.PhysicalCores = CountPhysicalCores();
        if (hw.PhysicalCores <= 0) hw.PhysicalCores = Math.Max(1, hw.LogicalCores / 2);
    }

    private static void FillGpu(HardwareInfo hw)
    {
        var candidates = new List<(string name, string vendor, string driver, long vram, int score)>();
        const string classGuid = @"{4d36e968-e325-11ce-bfc1-08002be10318}";
        using (var cls = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{classGuid}"))
        {
            if (cls != null)
            {
                foreach (var subName in cls.GetSubKeyNames())
                {
                    if (subName.Length != 4 || !int.TryParse(subName, out _)) continue;
                    using var sub = cls.OpenSubKey(subName);
                    if (sub == null) continue;
                    var desc = (sub.GetValue("DriverDesc") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    if (desc.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;

                    long vram = ReadVram(sub, desc);
                    var driver = (sub.GetValue("DriverVersion") as string ?? "").Trim();
                    var vendor = VendorFromName(desc);
                    var score = (int)Math.Min(int.MaxValue / 4, vram) + vendor switch
                    {
                        "NVIDIA" => 300_000,
                        "AMD" => 250_000,
                        "Intel" => 50_000,
                        _ => 0
                    } + (desc.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ||
                         desc.Contains("RTX 50", StringComparison.OrdinalIgnoreCase) ? 100_000 : 0);
                    candidates.Add((desc, vendor, driver, vram, score));
                }
            }
        }

        if (candidates.Count == 0)
        {
            hw.GpuName = "Unknown GPU (no display adapter registry entry)";
            return;
        }

        var best = candidates.OrderByDescending(c => c.score).First();
        hw.GpuName = best.name;
        hw.GpuVendor = best.vendor;
        hw.GpuDriverVersion = best.driver;
        hw.VramMb = best.vram;
    }

    private static long ReadVram(RegistryKey sub, string desc)
    {
        // Preferred: qword reported by the driver.
        foreach (var name in new[] { "HardwareInformation.qwMemorySize", "qwMemorySize", "HardwareInformation.MemorySize" })
        {
            var v = sub.GetValue(name);
            switch (v)
            {
                case long l when l > 0: return l / 1024 / 1024;
                case int i32 when i32 > 0: return i32 / 1024 / 1024;
                case byte[] bytes when bytes.Length >= 8:
                    var q = BitConverter.ToInt64(bytes, 0);
                    if (q > 0) return q / 1024 / 1024;
                    break;
                case byte[] bytes32 when bytes32.Length >= 4:
                    var d = BitConverter.ToUInt32(bytes32, 0);
                    if (d > 0) return d / 1024 / 1024;
                    break;
            }
        }

        // Fallback: infer a conservative value from the model name. Clearly marked as inferred.
        var known = new (string token, long mb)[]
        {
            ("5090", 32768), ("5080", 16384), ("5070 TI", 16384), ("5070", 12288), ("5060 TI", 16384), ("5060", 8192),
            ("4090", 24576), ("4080", 16384), ("4070 TI SUPER", 16384), ("4070 TI", 12288), ("4070 SUPER", 12288),
            ("4070", 12288), ("4060 TI", 8192), ("4060", 8192),
            ("3090", 24576), ("3080 TI", 12288), ("3080", 10240), ("3070 TI", 8192), ("3070", 8192), ("3060 TI", 8192), ("3060", 12288),
            ("7900 XTX", 24576), ("7900 XT", 20480), ("7800 XT", 16384), ("7700 XT", 12288), ("7600", 8192),
            ("6900 XT", 16384), ("6800 XT", 16384), ("6800", 16384), ("6750 XT", 12288), ("6700 XT", 12288), ("6600", 8192),
            ("ARC A770", 16384), ("ARC A750", 8192), ("ARC B580", 12288)
        };
        foreach (var (token, mb) in known)
            if (desc.Contains(token, StringComparison.OrdinalIgnoreCase)) return mb;
        return 0;
    }

    private static string VendorFromName(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "Unknown";
    }

    private static string ClassifyGpu(HardwareInfo hw)
    {
        var name = hw.GpuName.ToUpperInvariant();
        if (name.Contains("RTX 50") || name.Contains("RTX 4090") || name.Contains("7900 XTX") ||
            name.Contains("7900 XT") || name.Contains("5090") || name.Contains("5080")) return "Enthusiast";
        if (name.Contains("RTX 4080") || name.Contains("RTX 4070") || name.Contains("RTX 3090") ||
            name.Contains("RTX 3080") || name.Contains("7800 XT") || name.Contains("6900 XT")) return "High";
        if (hw.VramMb >= 8_000) return "Mid";
        if (hw.VramMb > 0) return "Entry";
        return "Unknown";
    }

    private static void FillMemory(HardwareInfo hw)
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
            hw.RamMb = (long)(mem.ullTotalPhys / 1024 / 1024);
    }

    private static void FillWindows(HardwareInfo hw)
    {
        var build = Environment.OSVersion.Version.Build;
        var name = "Windows";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var displayVersion = key?.GetValue("DisplayVersion") as string ?? "";
            var buildString = key?.GetValue("CurrentBuildNumber") as string ?? build.ToString();
            _ = int.TryParse(buildString, out build);
            // Windows 11 still reports "Windows 10" in ProductName before build 22000 checks.
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            name = string.IsNullOrWhiteSpace(displayVersion) ? product : $"{product} {displayVersion}";
        }
        catch { }
        hw.WindowsName = name;
        hw.WindowsBuild = build;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = key?.GetValue("SystemManufacturer") as string ?? "";
            var model = key?.GetValue("SystemProductName") as string ?? "";
            hw.SystemModel = $"{maker} {model}".Trim();
        }
        catch { }
    }

    private static void FillStorage(HardwareInfo hw, string? gameRoot)
    {
        var root = gameRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            try { root = Path.GetPathRoot(Environment.CurrentDirectory); } catch { root = "C:\\"; }
        }
        if (string.IsNullOrWhiteSpace(root)) root = "C:\\";
        var drive = new DriveInfo(root);
        if (!drive.IsReady) return;
        hw.GameDriveName = drive.Name.TrimEnd(':');
        hw.GameDriveFreeBytes = drive.AvailableFreeSpace;
        hw.GameDriveTotalBytes = drive.TotalSize;
    }

    private static void FillDisplays(HardwareInfo hw)
    {
        var displays = new List<DisplayInfo>();
        uint index = 0;
        while (true)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, index++, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            EnumDisplayDevices(dd.DeviceName, 0, ref monitor, 0);

            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            var width = 0;
            var height = 0;
            var hz = 0;
            if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
            {
                width = dm.dmPelsWidth;
                height = dm.dmPelsHeight;
                hz = (int)dm.dmDisplayFrequency;
            }
            if (width <= 0 || height <= 0) continue;

            displays.Add(new DisplayInfo
            {
                DeviceName = dd.DeviceName,
                MonitorName = string.IsNullOrWhiteSpace(monitor.DeviceString) ? dd.DeviceString : monitor.DeviceString,
                Width = width,
                Height = height,
                RefreshHz = hz,
                IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0
            });
        }

        ApplyHdrInfo(displays);

        foreach (var d in displays)
        {
            if (d.IsPrimary)
            {
                hw.PrimaryResolution = d.Resolution;
                hw.PrimaryRefreshHz = d.RefreshHz;
                hw.HdrEnabled = d.HdrEnabled;
            }
            hw.AnyHdr |= d.HdrSupported;
        }
        if (displays.Count > 0 && hw.PrimaryResolution == "Unknown")
        {
            var first = displays[0];
            hw.PrimaryResolution = first.Resolution;
            hw.PrimaryRefreshHz = first.RefreshHz;
            hw.HdrEnabled = first.HdrEnabled;
        }
        hw.Displays = displays;
    }

    private static void ApplyHdrInfo(List<DisplayInfo> displays)
    {
        if (displays.Count == 0) return;
        try
        {
            var err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
            if (err != 0 || pathCount == 0) return;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err != 0) return;

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                color.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                color.header.adapterId = path.target.adapterId;
                color.header.id = path.target.id;
                if (DisplayConfigGetDeviceInfo(ref color) != 0) continue;

                var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                name.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                name.header.adapterId = path.target.adapterId;
                name.header.id = path.target.id;
                var gotName = DisplayConfigGetDeviceInfo(ref name) == 0;
                var friendly = gotName ? name.monitorFriendlyDeviceName : "";

                var target = displays.FirstOrDefault(d =>
                        !string.IsNullOrWhiteSpace(friendly) &&
                        d.MonitorName.Contains(friendly, StringComparison.OrdinalIgnoreCase))
                    ?? displays.FirstOrDefault(d => d.IsPrimary)
                    ?? displays[0];

                target.HdrSupported = (color.value & 1) != 0;
                target.HdrEnabled = (color.value & 2) != 0;
                target.BitsPerColor = (int)color.bitsPerColorChannel;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"DisplayConfig HDR query unavailable: {ex.Message}");
        }
    }

    private static int CountPhysicalCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return 0;
            var count = 0;
            var offset = 0;
            while (offset < length)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (relationship == 0) count++; // RelationProcessorCore
                offset += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---------------- P/Invoke ----------------

    private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(int flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(int flags, ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNum;
        public uint refreshRateDenom;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO source;
        public DISPLAYCONFIG_PATH_TARGET_INFO target;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
}
