// Native.cs —— P/Invoke 与 PDH 封装。
//
// 设计约束（整个工程都遵守）：
//   1. 零第三方依赖：只用 .NET Framework 自带 + 系统 DLL。
//   2. 跨 Intel / AMD / NVIDIA 通用：一律走 Windows 自带的通用接口
//      （PDH 计数器、GetSystemTimes、GlobalMemoryStatusEx、DXGI/注册表），
//      不碰 NVML / ADL / MSR 驱动。
//   3. 读不到就是读不到：所有采集函数要么返回有效值，要么返回 false / NaN，
//      绝不抛异常打断整轮采集。上层据此决定整张卡片是否显示。
//
// 语言级别：csc.exe (v4.0.30319) 只支持到 C# 5。**不要用**字符串插值、
// `?.`、表达式体成员、nameof、自动属性初始化器 —— 都会编译失败。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SysPanel
{
    // ============================================================ 基础 P/Invoke

    internal static class Native
    {
        // ---------------- 内存 ----------------

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
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
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>物理内存总量 / 可用量（字节）与占用率。失败返回 false。</summary>
        public static bool TryGetMemory(out ulong total, out ulong avail, out double loadPercent)
        {
            total = 0; avail = 0; loadPercent = double.NaN;
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) return false;
            total = m.ullTotalPhys;
            avail = m.ullAvailPhys;
            loadPercent = total > 0 ? (100.0 * (total - avail) / total) : double.NaN;
            return true;
        }

        // ---------------- CPU 时间 ----------------

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
            public ulong ToUInt64() { return ((ulong)dwHighDateTime << 32) | dwLowDateTime; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        /// <summary>
        /// 真实 CPU 占用率的原始数据：忙时间与总时间（100ns 单位）。
        /// 说明：kernel 时间**包含** idle，所以 total = kernel + user，busy = total - idle。
        /// 这是与内核 GetSystemTimes 一致的口径；任务管理器那套 % Processor Utility
        /// 在部分机器上会虚高（本机实测约 12 倍），所以默认不用它。
        /// </summary>
        public static bool TryGetCpuTimes(out ulong idle, out ulong total)
        {
            idle = 0; total = 0;
            FILETIME i, k, u;
            if (!GetSystemTimes(out i, out k, out u)) return false;
            ulong idleT = i.ToUInt64();
            ulong kernelT = k.ToUInt64();
            ulong userT = u.ToUInt64();
            idle = idleT;
            total = kernelT + userT;
            return true;
        }

        // ---------------- 开机时长 ----------------

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static double GetUptimeSeconds()
        {
            return GetTickCount64() / 1000.0;
        }

        // ---------------- 磁盘剩余空间 ----------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
            out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

        public static bool TryGetDiskSpace(string driveRoot, out ulong total, out ulong free)
        {
            total = 0; free = 0;
            ulong avail, t, f;
            if (!GetDiskFreeSpaceEx(driveRoot, out avail, out t, out f)) return false;
            total = t; free = f;
            return true;
        }

        // ---------------- 注册表（CPU 型号 / 显卡显存） ----------------

        /// <summary>
        /// 读注册表字符串。读不到返回 null（不抛异常）。
        /// 用注册表而不是 WMI：WMI 首次查询要 1-3 秒、还要占一个 COM 线程，
        /// 而这两处信息（CPU 型号、显卡显存）注册表里是现成的、且是 64 位准确值
        /// —— WMI 的 Win32_VideoController.AdapterRAM 是 uint32，超过 4 GB 会溢出。
        /// </summary>
        public static string ReadRegistryString(Microsoft.Win32.RegistryKey root, string subKey, string name)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = root.OpenSubKey(subKey))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    return v == null ? null : Convert.ToString(v);
                }
            }
            catch { return null; }
        }

        public static object ReadRegistryValue(Microsoft.Win32.RegistryKey root, string subKey, string name)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = root.OpenSubKey(subKey))
                {
                    if (k == null) return null;
                    return k.GetValue(name);
                }
            }
            catch { return null; }
        }

        /// <summary>64 位标称主频（MHz）。来自 HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz。</summary>
        public static double GetNominalCpuMhz()
        {
            object v = ReadRegistryValue(Microsoft.Win32.Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz");
            if (v == null) return double.NaN;
            try { return Convert.ToDouble(v); } catch { return double.NaN; }
        }

        // ---------------- 进程信息 ----------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// 取进程的可执行文件全路径。跨位数（32/64）可用，且不需要 PROCESS_VM_READ。
        /// 取不到返回 null —— 系统进程、受保护进程、已退出的进程都会走到这条路。
        /// </summary>
        public static string TryGetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int cap = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref cap)) return sb.ToString();
                return null;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        /// <summary>显卡适配器信息。走注册表 —— 跨 NVIDIA / AMD / Intel 都通用，且显存是 64 位。</summary>
        public sealed class GpuAdapter
        {
            public string Name = "";
            public ulong MemoryBytes = 0;
        }

        /// <summary>
        /// 枚举显示适配器（名称 + 专用显存）。来自
        /// HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000..000N
        /// 的 DriverDesc 与 HardwareInformation.qwMemorySize。
        /// </summary>
        public static List<GpuAdapter> GetGpuAdapters()
        {
            List<GpuAdapter> list = new List<GpuAdapter>();
            List<string> seen = new List<string>();
            string basePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(basePath))
                {
                    if (k == null) return list;
                    string[] subs = k.GetSubKeyNames();
                    for (int i = 0; i < subs.Length; i++)
                    {
                        int n;
                        if (!int.TryParse(subs[i], out n)) continue;   // 只认 0000/0001…，跳过 Properties 等
                        using (Microsoft.Win32.RegistryKey sk = k.OpenSubKey(subs[i]))
                        {
                            if (sk == null) continue;
                            object desc = sk.GetValue("DriverDesc");
                            if (desc == null) continue;
                            string dn = Convert.ToString(desc);
                            // 虚拟/间接显示适配器必须排掉：它们不是真显卡，
                            // 而且会把显存总量抬成好几倍（本机实测：一张 12 GB 的 4070 被算成 36 GB）。
                            if (IsVirtualDisplay(dn)) continue;
                            if (seen.Contains(dn)) continue;   // 同一张卡在注册表里可能挂多个子键
                            seen.Add(dn);
                            GpuAdapter a = new GpuAdapter();
                            a.Name = dn;
                            object hw = sk.GetValue("HardwareInformation.qwMemorySize");
                            if (hw != null)
                            {
                                try { a.MemoryBytes = Convert.ToUInt64(hw); } catch { a.MemoryBytes = 0; }
                            }
                            if (a.MemoryBytes == 0)
                            {
                                // 有些驱动只给 32 位的 MemorySize（>4GB 不可靠），能读就读，读不准就当没有
                                object ms = sk.GetValue("HardwareInformation.MemorySize");
                                if (ms != null)
                                {
                                    try
                                    {
                                        byte[] b = ms as byte[];
                                        if (b != null && b.Length >= 4) a.MemoryBytes = BitConverter.ToUInt32(b, 0);
                                        else a.MemoryBytes = Convert.ToUInt64(ms);
                                    }
                                    catch { a.MemoryBytes = 0; }
                                }
                            }
                            list.Add(a);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 虚拟 / 间接显示适配器（IDD）。这些不是物理显卡：
        /// 常见的有 MuMu / 模拟器虚拟屏、GameViewer / 远程串流虚拟屏、OrayIddDriver（向日葵）、
        /// Parsec、Microsoft Basic Display 等。不过滤掉它们，显存与显卡名都会失真。
        /// </summary>
        public static bool IsVirtualDisplay(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string[] bad = new string[]
            {
                "Microsoft Basic", "Remote", "Virtual", "Indirect", "Idd", "Mirror",
                "Parsec", "Sunshine", "ToDesk", "GameViewer", "MuMu", "Oray",
                "USB Display", "DameWare", "Splashtop"
            };
            for (int i = 0; i < bad.Length; i++)
                if (name.IndexOf(bad[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---------------- 系统版本 ----------------

        public sealed class OsInfo
        {
            public string ProductName = "";
            public string DisplayVersion = "";
            public string Build = "";
            public string Describe()
            {
                string s = ProductName;
                if (!string.IsNullOrEmpty(DisplayVersion)) s += " " + DisplayVersion;
                if (!string.IsNullOrEmpty(Build)) s += " (Build " + Build + ")";
                return s.Trim();
            }
        }

        public static OsInfo GetOsInfo()
        {
            OsInfo o = new OsInfo();
            string p = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            o.ProductName = ReadRegistryString(Microsoft.Win32.Registry.LocalMachine, p, "ProductName");
            if (o.ProductName == null) o.ProductName = "";
            o.DisplayVersion = ReadRegistryString(Microsoft.Win32.Registry.LocalMachine, p, "DisplayVersion");
            if (o.DisplayVersion == null) o.DisplayVersion = "";
            o.Build = ReadRegistryString(Microsoft.Win32.Registry.LocalMachine, p, "CurrentBuild");
            if (o.Build == null) o.Build = "";

            // 兼容性说明：Win11 的 ProductName 仍然写着 "Windows 10"。
            // 用 Build 号纠正，避免面板上显示错系统名。
            int build;
            if (int.TryParse(o.Build, out build) && build >= 22000 && o.ProductName.IndexOf("Windows 10") >= 0)
                o.ProductName = o.ProductName.Replace("Windows 10", "Windows 11");
            return o;
        }

        /// <summary>把秒数说成人话：3天4小时 / 4小时12分 / 12分30秒。</summary>
        public static string HumanDuration(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) return "--";
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalDays >= 1) return string.Format("{0}天{1}小时", (int)t.TotalDays, t.Hours);
            if (t.TotalHours >= 1) return string.Format("{0}小时{1}分", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}分{1}秒", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0}秒", (int)t.TotalSeconds);
        }

        /// <summary>字节数说成人话（IEC 单位，保留 1 位小数）。</summary>
        public static string HumanBytes(double bytes)
        {
            if (double.IsNaN(bytes) || bytes < 0) return "--";
            string[] u = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            double v = bytes;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + u[i];
        }

        /// <summary>速率：字节/秒。</summary>
        public static string HumanRate(double bytesPerSec)
        {
            if (double.IsNaN(bytesPerSec)) return "--";
            return HumanBytes(bytesPerSec) + "/s";
        }
    }

    // ============================================================ PDH 封装
    //
    // PDH 是 Windows 自带的性能计数器接口，跨厂商、跨语言、免驱动。
    // 两个必须注意的点：
    //   1. 一律用 PdhAddEnglishCounter —— 中文系统上 PdhAddCounter 解析的是**本地化**
    //      计数器名，英文路径会找不到。这是本工程"跨语言通用"的关键。
    //   2. 通配符实例（GPU Engine(*) 这类）要用 PdhGetFormattedCounterArray 拿数组，
    //      返回值还要逐项检查 CStatus —— 某一项可能单独失败（进程刚退出）。

    internal sealed class PdhQuery : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x00000200;
        const uint PDH_MORE_DATA = 0x800007D2;
        const uint ERROR_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PDH_FMT_COUNTERVALUE_ITEM
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        // CharSet 绝不能省：不写就是 Ansi 版本（PdhGetFormattedCounterArrayA），
        // 返回的是 char*，却被下面按 LPWStr 读出来 → 实例名整片乱码
        // （"Realtek" 会变成 "敒污整⁫…"）。而实例名一乱，
        // 「GPU Engine 里找 engtype_3D」和「网络接口排除虚拟网卡」就全部失效，
        // 表现为 GPU 占用恒为 0、接口数虚高 —— 症状完全不像是编码问题。
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        static extern uint PdhCloseQuery(IntPtr query);

        IntPtr _query = IntPtr.Zero;
        readonly Dictionary<string, IntPtr> _counters = new Dictionary<string, IntPtr>();
        readonly List<string> _paths = new List<string>();

        public bool IsOpen { get { return _query != IntPtr.Zero; } }

        /// <summary>打开查询。任何一步失败都返回 false，不抛异常。</summary>
        public bool Open()
        {
            Close();
            IntPtr q;
            if (PdhOpenQuery(null, IntPtr.Zero, out q) != ERROR_SUCCESS) return false;
            _query = q;
            return true;
        }

        /// <summary>加一个计数器路径。加不上（系统没这个计数器）返回 false。</summary>
        public bool Add(string path)
        {
            if (_query == IntPtr.Zero) return false;
            if (_counters.ContainsKey(path)) return true;
            IntPtr c;
            uint r = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out c);
            if (r != ERROR_SUCCESS) return false;
            _counters[path] = c;
            _paths.Add(path);
            return true;
        }

        public bool Collect()
        {
            if (_query == IntPtr.Zero) return false;
            return PdhCollectQueryData(_query) == ERROR_SUCCESS;
        }

        /// <summary>单实例计数器的值。失败返回 NaN。</summary>
        public double GetValue(string path)
        {
            IntPtr c;
            if (!_counters.TryGetValue(path, out c)) return double.NaN;
            uint type;
            PDH_FMT_COUNTERVALUE v;
            if (PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE, out type, out v) != ERROR_SUCCESS) return double.NaN;
            if (v.CStatus != 0) return double.NaN;   // PDH_CSTATUS_VALID_DATA=0；其余都算无效
            return v.doubleValue;
        }

        /// <summary>
        /// 通配符实例计数器的值。返回 实例名 → 值；单项无效的会被跳过。
        /// 读取失败返回空字典（调用方按"读不到"处理）。
        /// </summary>
        public Dictionary<string, double> GetArray(string path)
        {
            Dictionary<string, double> result = new Dictionary<string, double>();
            IntPtr c;
            if (!_counters.TryGetValue(path, out c)) return result;

            uint size = 0;
            uint count = 0;
            uint r = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return result;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                r = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE, ref size, ref count, buf);
                if (r != ERROR_SUCCESS) return result;

                int itemSize = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM));
                IntPtr p = buf;
                for (uint i = 0; i < count; i++)
                {
                    PDH_FMT_COUNTERVALUE_ITEM item =
                        (PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(p, typeof(PDH_FMT_COUNTERVALUE_ITEM));
                    if (item.szName != null && item.FmtValue.CStatus == 0)
                        result[item.szName] = item.FmtValue.doubleValue;
                    p = new IntPtr(p.ToInt64() + itemSize);
                }
            }
            catch { /* 数组读取整体失败：当读不到 */ }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }

        public void Close()
        {
            if (_query != IntPtr.Zero)
            {
                try { PdhCloseQuery(_query); } catch { }
                _query = IntPtr.Zero;
            }
            _counters.Clear();
            _paths.Clear();
        }

        public void Dispose() { Close(); }
    }
}
