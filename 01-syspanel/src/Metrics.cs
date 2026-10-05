// Metrics.cs —— 采集层。
//
// 核心约定（整个工程的"通用"就建立在这上面）：
//   * 每个指标都是一个独立的探针，自己知道自己读没读到；
//   * 读不到就标 ok=false 并写明原因，**绝不用估算值冒充实测值**；
//   * 一张卡片里没有任何指标可用 → 整张卡不出现在输出里（前端自然不渲染）；
//   * 任何异常都不得打断整轮采集。
//
// 数据来源优先级：Windows 通用接口（PDH / GetSystemTimes / 注册表） > 厂商 SDK（暂未用）
// 说明：CPU 温度走 WMI MSAcpi_ThermalZoneTemperature —— 这是系统里唯一的免驱动温度源，
// 但大量桌面主板根本不实现它，所以实际上经常读不到。这是 Windows 的现状，不是 bug。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SysPanel
{
    // ============================================================ 数据模型

    public sealed class Metric
    {
        public string Key = "";
        public string Label = "";
        public string Text = "--";
        public double Value = double.NaN;
        public string Unit = "";
        /// <summary>0-100 的进度条数值；NaN = 这一项不适合画条。</summary>
        public double Bar = double.NaN;
        public bool Ok = false;
        public string Note = "";
    }

    public sealed class Row
    {
        public string Label = "";
        public string Value = "";
        public Row() { }
        public Row(string l, string v) { Label = l; Value = v; }
    }

    public sealed class Card
    {
        public string Id = "";
        public string Title = "";
        public string Sub = "";
        public List<Metric> Metrics = new List<Metric>();
        public List<Row> Rows = new List<Row>();

        /// <summary>
        /// 这张卡有没有内容值得显示：有读到值的指标，或者有行内容。
        /// （进程卡只有 Rows、没有 Metrics，所以不能只看 Metrics。）
        /// </summary>
        public bool AnyOk()
        {
            for (int i = 0; i < Metrics.Count; i++) if (Metrics[i].Ok) return true;
            return Rows.Count > 0;
        }
    }

    // ============================================================ 采集器

    public sealed class Collector : IDisposable
    {
        // ---- PDH 计数器路径。全部是英文路径（PdhAddEnglishCounter 解析），中文系统同样可用。
        const string C_CPU_PERF = @"\Processor Information(_Total)\% Processor Performance";
        const string C_CPU_FREQ = @"\Processor Information(_Total)\Processor Frequency";
        const string C_GPU_ENGINE = @"\GPU Engine(*)\Utilization Percentage";
        const string C_GPU_MEM = @"\GPU Adapter Memory(*)\Dedicated Usage";
        const string C_DISK_READ = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
        const string C_DISK_WRITE = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
        const string C_DISK_TIME = @"\PhysicalDisk(_Total)\% Disk Time";
        const string C_NET_RX = @"\Network Interface(*)\Bytes Received/sec";
        const string C_NET_TX = @"\Network Interface(*)\Bytes Sent/sec";

        readonly PdhQuery _pdh = new PdhQuery();

        // CPU 占用需要两次采样之间的差值
        ulong _lastIdle;
        ulong _lastTotal;
        bool _hasCpuBase;

        // CPU 温度：WMI 查询慢（首次 1-3 秒），所以放后台低频刷新，主循环只读缓存
        double _tempC = double.NaN;
        string _tempNote = "尚未探测";
        DateTime _tempNextUtc = DateTime.MinValue;
        int _tempRunning = 0;

        // GPU 静态信息（注册表，读一次就够）
        List<Native.GpuAdapter> _adapters;
        double _nominalMhz = double.NaN;

        // 进程数：每轮都问一次太贵，5 秒一次
        int _procCount = -1;
        DateTime _procCountNextUtc = DateTime.MinValue;

        public List<string> Diagnostics = new List<string>();

        public Collector()
        {
            _nominalMhz = Native.GetNominalCpuMhz();
            bool opened = _pdh.Open();
            if (!opened) Diagnostics.Add("PDH 打开失败（pdh.dll 不可用？）—— 所有基于性能计数器的指标都读不到。");
            else
            {
                // 加不上的计数器单独记账，方便在诊断里说清楚"为什么这张卡没出来"
                AddCounter(C_CPU_PERF, "CPU 频率");
                _pdh.Add(C_CPU_FREQ);   // 可选增强：多数机器没这个计数器，拿不到很正常，不写进诊断免得刷屏
                AddCounter(C_GPU_ENGINE, "GPU 占用");
                AddCounter(C_GPU_MEM, "显存占用");
                AddCounter(C_DISK_READ, "磁盘读取");
                AddCounter(C_DISK_WRITE, "磁盘写入");
                AddCounter(C_DISK_TIME, "磁盘活动");
                AddCounter(C_NET_RX, "网络接收");
                AddCounter(C_NET_TX, "网络发送");
            }
            try { _adapters = Native.GetGpuAdapters(); }
            catch { _adapters = new List<Native.GpuAdapter>(); }
        }

        void AddCounter(string path, string label)
        {
            if (!_pdh.Add(path)) Diagnostics.Add(label + "：计数器 " + path + " 在本机不存在，该项不显示。");
        }

        // ---------------------------------------------------------- 主入口

        /// <summary>采一轮。Cards 里只会出现"有内容"的卡。</summary>
        public List<Card> Collect()
        {
            Diagnostics.Clear();
            _pdh.Collect();

            List<Card> cards = new List<Card>();
            AddIfAny(cards, BuildSystemCard());
            AddIfAny(cards, BuildCpuCard());
            AddIfAny(cards, BuildMemoryCard());
            AddIfAny(cards, BuildGpuCard());
            AddIfAny(cards, BuildDiskCard());
            AddIfAny(cards, BuildNetworkCard());
            AddIfAny(cards, BuildProcCard());
            return cards;
        }

        static void AddIfAny(List<Card> list, Card c)
        {
            if (c != null && c.AnyOk()) list.Add(c);
        }

        // ---------------------------------------------------------- 系统

        Card BuildSystemCard()
        {
            Card c = new Card();
            c.Id = "system";
            c.Title = "系统";
            Native.OsInfo os = Native.GetOsInfo();
            c.Sub = os.Describe();

            if (_procCount < 0)
            {
                // 进程枚举只在到期时做（GetProcesses 在大机器上要几十毫秒）
                if (DateTime.UtcNow >= _procCountNextUtc)
                {
                    try { _procCount = Process.GetProcesses().Length; }
                    catch { _procCount = -1; }
                    _procCountNextUtc = DateTime.UtcNow.AddSeconds(5);
                }
            }

            Metric up = new Metric();
            up.Key = "sys.uptime"; up.Label = "开机时长";
            double secs = Native.GetUptimeSeconds();
            up.Ok = secs >= 0;
            up.Text = Native.HumanDuration(secs);
            c.Metrics.Add(up);

            Metric pc = new Metric();
            pc.Key = "sys.procs"; pc.Label = "进程数";
            pc.Ok = _procCount >= 0;
            pc.Value = _procCount;
            pc.Text = _procCount >= 0 ? _procCount.ToString() : "--";
            c.Metrics.Add(pc);

            Metric cores = new Metric();
            cores.Key = "sys.cores"; cores.Label = "逻辑核";
            cores.Ok = true;
            cores.Value = Environment.ProcessorCount;
            cores.Text = Environment.ProcessorCount.ToString();
            c.Metrics.Add(cores);

            c.Rows.Add(new Row("主机名", SafeHostName()));
            c.Rows.Add(new Row("系统", os.Describe()));
            c.Rows.Add(new Row("运行时", ".NET Framework " + Environment.Version.ToString()));
            return c;
        }

        static string SafeHostName()
        {
            try { return Environment.MachineName; } catch { return "--"; }
        }

        // ---------------------------------------------------------- CPU

        Card BuildCpuCard()
        {
            Card c = new Card();
            c.Id = "cpu";
            c.Title = "CPU";

            string name = Native.ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
            if (!string.IsNullOrEmpty(name)) c.Sub = name.Trim();

            // ---- 占用：GetSystemTimes 差值（与内核口径一致） ----
            Metric load = new Metric();
            load.Key = "cpu.load"; load.Label = "占用"; load.Unit = "%";
            ulong idle, total;
            if (Native.TryGetCpuTimes(out idle, out total))
            {
                if (_hasCpuBase)
                {
                    double dIdle = (double)(idle - _lastIdle);
                    double dTotal = (double)(total - _lastTotal);
                    if (dTotal > 0)
                    {
                        double pct = 100.0 * (dTotal - dIdle) / dTotal;
                        if (pct < 0) pct = 0;
                        if (pct > 100) pct = 100;
                        load.Ok = true;
                        load.Value = pct;
                        load.Bar = pct;
                        load.Text = pct.ToString("0.0") + " %";
                    }
                }
                _lastIdle = idle; _lastTotal = total; _hasCpuBase = true;
            }
            if (!load.Ok && !_hasCpuBase) load.Note = "GetSystemTimes 不可用";
            else if (!load.Ok) load.Note = "第一次采样，需要两个点才能算差值（下一轮就有值）";
            c.Metrics.Add(load);

            // ---- 频率：% Processor Performance × 标称频率 ----
            Metric freq = new Metric();
            freq.Key = "cpu.freq"; freq.Label = "频率";
            // 优先用系统直接给的 MHz（部分 Windows/驱动提供），拿不到再退回
            // "% Processor Performance × 注册表基准频率"。
            // 注意基准频率在 AMD 上往往是"最大加速频率"而不是基础频率，
            // 所以算出来的值可能偏高 —— 页面上标的是"参考值"。
            double directMhz = _pdh.GetValue(C_CPU_FREQ);
            double perf = _pdh.GetValue(C_CPU_PERF);
            if (!double.IsNaN(directMhz) && directMhz >= 100 && directMhz <= 20000)
            {
                freq.Ok = true;
                freq.Value = directMhz;
                freq.Text = (directMhz / 1000.0).ToString("0.00") + " GHz";
                freq.Note = "来源：计数器 Processor Frequency（系统直接给出）";
            }
            else if (!double.IsNaN(perf) && !double.IsNaN(_nominalMhz) && _nominalMhz > 0)
            {
                double mhz = _nominalMhz * perf / 100.0;
                freq.Ok = true;
                freq.Value = mhz;
                freq.Text = (mhz / 1000.0).ToString("0.00") + " GHz";
                freq.Note = "注册表基准 " + (_nominalMhz / 1000.0).ToString("0.00") + " GHz × " + perf.ToString("0") + "%（AMD 的基准常为最大加速频率，读数可能偏高）";
            }
            else
            {
                freq.Note = double.IsNaN(perf) ? "计数器 % Processor Performance 读不到" : "基准频率读不到";
            }
            c.Metrics.Add(freq);

            // ---- 温度：WMI，后台低频刷新 ----
            StartTemperatureRefreshIfDue();
            Metric temp = new Metric();
            temp.Key = "cpu.temp"; temp.Label = "温度"; temp.Unit = "°C";
            if (!double.IsNaN(_tempC))
            {
                temp.Ok = true;
                temp.Value = _tempC;
                temp.Text = _tempC.ToString("0") + " °C";
                temp.Note = _tempNote;
            }
            else
            {
                temp.Note = _tempNote;
            }
            c.Metrics.Add(temp);

            // ---- 功耗：Windows 没有免驱动的通用接口，本工程**不做估算**，直接不提供 ----
            // （用户要求：读得到就有，读不到就整项不要。所以这里连探针都不建。）

            c.Rows.Add(new Row("标称频率", double.IsNaN(_nominalMhz) ? "--" : (_nominalMhz / 1000.0).ToString("0.00") + " GHz"));
            c.Rows.Add(new Row("逻辑核数", Environment.ProcessorCount.ToString()));
            return c;
        }

        void StartTemperatureRefreshIfDue()
        {
            if (DateTime.UtcNow < _tempNextUtc) return;
            if (Interlocked.CompareExchange(ref _tempRunning, 1, 0) != 0) return;   // 已有一次在跑
            _tempNextUtc = DateTime.UtcNow.AddSeconds(20);
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { RefreshTemperature(); }
                catch { _tempNote = "温度探测异常"; }
                finally { Interlocked.Exchange(ref _tempRunning, 0); }
            });
        }

        /// <summary>
        /// 读 ACPI 热区温度。注意：这个类在**很多**桌面主板上根本不存在，
        /// 于是 WMI 查询会抛 ManagementException —— 那是正常结果，不是错误。
        /// </summary>
        void RefreshTemperature()
        {
            try
            {
                System.Management.ManagementScope scope = new System.Management.ManagementScope(@"root\WMI");
                scope.Connect();
                System.Management.ObjectQuery q =
                    new System.Management.ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(scope, q))
                {
                    s.Options.Timeout = TimeSpan.FromSeconds(6);
                    double best = double.NaN;
                    foreach (System.Management.ManagementBaseObject mo in s.Get())
                    {
                        object v = mo["CurrentTemperature"];
                        if (v == null) continue;
                        double raw = Convert.ToDouble(v);            // 单位：十分之一开尔文
                        double celsius = raw / 10.0 - 273.15;
                        if (celsius > -50 && celsius < 150)
                        {
                            // 多个热区时取最高的那个（通常是 CPU 附近）
                            if (double.IsNaN(best) || celsius > best) best = celsius;
                        }
                    }
                    if (double.IsNaN(best))
                    {
                        _tempC = double.NaN;
                        _tempNote = "MSAcpi_ThermalZoneTemperature 没有返回有效热区 —— 本机主板未实现该 WMI 类";
                    }
                    else
                    {
                        _tempC = best;
                        _tempNote = "来源：WMI MSAcpi_ThermalZoneTemperature（ACPI 热区，非核心温度，通常偏低）";
                    }
                }
            }
            catch (Exception ex)
            {
                _tempC = double.NaN;
                _tempNote = "本机读不到 CPU 温度（" + ex.GetType().Name + "）：Windows 没有免驱动的通用温度接口";
            }
        }

        // ---------------------------------------------------------- 内存

        Card BuildMemoryCard()
        {
            Card c = new Card();
            c.Id = "mem";
            c.Title = "内存";

            ulong total, avail;
            double loadPct;
            if (!Native.TryGetMemory(out total, out avail, out loadPct))
            {
                c.Sub = "GlobalMemoryStatusEx 不可用";
                return c;
            }
            c.Sub = Native.HumanBytes(total) + " 物理内存";

            Metric used = new Metric();
            used.Key = "mem.used"; used.Label = "已用";
            used.Ok = true;
            used.Value = loadPct;
            used.Bar = loadPct;
            used.Text = Native.HumanBytes(total - avail);
            c.Metrics.Add(used);

            Metric pct = new Metric();
            pct.Key = "mem.pct"; pct.Label = "占用率"; pct.Unit = "%";
            pct.Ok = true; pct.Value = loadPct; pct.Bar = loadPct;
            pct.Text = loadPct.ToString("0.0") + " %";
            c.Metrics.Add(pct);

            Metric av = new Metric();
            av.Key = "mem.avail"; av.Label = "可用";
            av.Ok = true; av.Value = avail;
            av.Text = Native.HumanBytes(avail);
            c.Metrics.Add(av);

            c.Rows.Add(new Row("物理总量", Native.HumanBytes(total)));
            c.Rows.Add(new Row("可用", Native.HumanBytes(avail)));
            return c;
        }

        // ---------------------------------------------------------- GPU

        Card BuildGpuCard()
        {
            Card c = new Card();
            c.Id = "gpu";
            c.Title = "GPU";

            // 名称与显存总量走注册表（64 位，跨 NVIDIA/AMD/Intel）
            string names = "";
            ulong totalVram = 0;
            if (_adapters != null)
            {
                for (int i = 0; i < _adapters.Count; i++)
                {
                    string n = _adapters[i].Name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (names.Length > 0) names += " + ";
                    names += n;
                    // 取最大值而不是求和：多卡/核显机器上求和没有意义，
                    // 分母应当是"主显卡的显存"。单卡机器上这就是它的容量。
                    if (_adapters[i].MemoryBytes > totalVram) totalVram = _adapters[i].MemoryBytes;
                }
            }
            c.Sub = names.Length > 0 ? names : "未识别到显示适配器";

            // ---- 3D 引擎占用：把所有 engtype_3D 实例的占用加起来（clamp 到 100） ----
            // 实例名形如 pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D
            Metric gpuLoad = new Metric();
            gpuLoad.Key = "gpu.load"; gpuLoad.Label = "3D 占用"; gpuLoad.Unit = "%";
            Dictionary<string, double> engines = _pdh.GetArray(C_GPU_ENGINE);
            if (engines.Count > 0)
            {
                double sum3d = 0;
                double maxAny = 0;
                int n3d = 0;
                foreach (KeyValuePair<string, double> kv in engines)
                {
                    string t = ExtractEngType(kv.Key);
                    if (t == "3D")
                    {
                        sum3d += kv.Value;
                        n3d++;
                    }
                    if (kv.Value > maxAny) maxAny = kv.Value;
                }
                if (n3d > 0)
                {
                    if (sum3d < 0) sum3d = 0;
                    if (sum3d > 100) sum3d = 100;
                    gpuLoad.Ok = true;
                    gpuLoad.Value = sum3d;
                    gpuLoad.Bar = sum3d;
                    gpuLoad.Text = sum3d.ToString("0.0") + " %";
                    gpuLoad.Note = "汇总 " + n3d + " 个 3D 引擎实例；当前最高单引擎 " + maxAny.ToString("0.0") + "%";
                }
                else
                {
                    // 计数器在、但此刻没有 3D 引擎实例 → 真的就是 0，不是"读不到"
                    gpuLoad.Ok = true;
                    gpuLoad.Value = 0;
                    gpuLoad.Bar = 0;
                    gpuLoad.Text = "0.0 %";
                    gpuLoad.Note = "计数器可用，当前没有 3D 引擎实例（无 GPU 活动）";
                }
            }
            else
            {
                gpuLoad.Note = "计数器 GPU Engine 读不到（需要 Win10 1709+ 与 WDDM 2.0 驱动）";
            }
            c.Metrics.Add(gpuLoad);

            // ---- 显存占用 ----
            Metric vram = new Metric();
            vram.Key = "gpu.vram"; vram.Label = "显存";
            Dictionary<string, double> mem = _pdh.GetArray(C_GPU_MEM);
            if (mem.Count > 0)
            {
                double sum = 0;
                foreach (KeyValuePair<string, double> kv in mem) sum += kv.Value;
                if (sum < 0) sum = 0;
                vram.Ok = true;
                vram.Value = sum;
                if (totalVram > 0)
                {
                    double bar = 100.0 * sum / (double)totalVram;
                    if (bar > 100) bar = 100;
                    vram.Bar = bar;
                    vram.Text = Native.HumanBytes(sum) + " / " + Native.HumanBytes((double)totalVram);
                }
                else
                {
                    vram.Text = Native.HumanBytes(sum);
                }
                vram.Note = "汇总 " + mem.Count + " 个适配器实例";
            }
            else
            {
                vram.Note = "计数器 GPU Adapter Memory 读不到";
            }
            c.Metrics.Add(vram);

            if (totalVram > 0)
                c.Rows.Add(new Row("显存总量", Native.HumanBytes((double)totalVram) + "（注册表 HardwareInformation.qwMemorySize）"));
            c.Rows.Add(new Row("显卡", names.Length > 0 ? names : "--"));
            return c;
        }

        /// <summary>从 GPU Engine 实例名里取出引擎类型：...engtype_3D → 3D</summary>
        static string ExtractEngType(string instance)
        {
            int i = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string t = instance.Substring(i + 8);
            int u = t.IndexOf('_');
            if (u > 0) t = t.Substring(0, u);
            return t;
        }

        // ---------------------------------------------------------- 磁盘

        Card BuildDiskCard()
        {
            Card c = new Card();
            c.Id = "disk";
            c.Title = "磁盘";

            double read = _pdh.GetValue(C_DISK_READ);
            double write = _pdh.GetValue(C_DISK_WRITE);
            double busy = _pdh.GetValue(C_DISK_TIME);

            Metric mRead = new Metric();
            mRead.Key = "disk.read"; mRead.Label = "读取";
            if (!double.IsNaN(read)) { mRead.Ok = true; mRead.Value = read; mRead.Text = Native.HumanRate(read); }
            else mRead.Note = "计数器读不到";
            c.Metrics.Add(mRead);

            Metric mWrite = new Metric();
            mWrite.Key = "disk.write"; mWrite.Label = "写入";
            if (!double.IsNaN(write)) { mWrite.Ok = true; mWrite.Value = write; mWrite.Text = Native.HumanRate(write); }
            else mWrite.Note = "计数器读不到";
            c.Metrics.Add(mWrite);

            Metric mBusy = new Metric();
            mBusy.Key = "disk.busy"; mBusy.Label = "活动时间"; mBusy.Unit = "%";
            if (!double.IsNaN(busy))
            {
                double b = busy;
                if (b < 0) b = 0;
                if (b > 100) b = 100;      // 多队列磁盘上这个计数器会超过 100，截断显示
                mBusy.Ok = true; mBusy.Value = b; mBusy.Bar = b;
                mBusy.Text = b.ToString("0.0") + " %";
            }
            else mBusy.Note = "计数器读不到";
            c.Metrics.Add(mBusy);

            // 分区剩余空间（这部分一定读得到，属于每个固定盘）
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo d = drives[i];
                    try
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        if (!d.IsReady) continue;
                        double totalGb = d.TotalSize / 1073741824.0;
                        double freeGb = d.TotalFreeSpace / 1073741824.0;
                        double usedPct = d.TotalSize > 0 ? 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize : 0;
                        c.Rows.Add(new Row(
                            d.Name.TrimEnd('\\'),
                            string.Format("可用 {0:0.0} GB / 共 {1:0.0} GB（{2:0}% 已用）", freeGb, totalGb, usedPct)));
                    }
                    catch { /* 单个分区读不到就跳过 */ }
                }
            }
            catch { }

            return c;
        }

        // ---------------------------------------------------------- 进程

        sealed class ProcInfo
        {
            public string Name = "";
            public int Pid;
            public double CpuPercent = double.NaN;
            public long WorkingSet;
        }

        sealed class ProcSample
        {
            public TimeSpan Cpu;
            public long WorkingSet;
        }

        readonly Dictionary<int, ProcSample> _lastProcs = new Dictionary<int, ProcSample>();
        readonly List<Row> _procRows = new List<Row>();
        DateTime _procNextUtc = DateTime.MinValue;
        DateTime _procLastSampleUtc = DateTime.MinValue;
        int _procTotal = -1;

        /// <summary>
        /// 进程卡。**刻意降频到 3 秒一次**：枚举 + 每进程取 CPU 时间/工作集，
        /// 在几百个进程的机器上要几十毫秒，挂在 1 秒的采集循环里会白烧 CPU。
        /// CPU 占用用两次采样的 TotalProcessorTime 差值算，口径与任务管理器接近；
        /// 首次采样没有基线，那一轮显示 "--"（不猜）。
        /// </summary>
        Card BuildProcCard()
        {
            Card c = new Card();
            c.Id = "proc";
            c.Title = "进程";

            if (DateTime.UtcNow >= _procNextUtc) RefreshProcs();

            c.Rows.AddRange(_procRows);
            c.Sub = _procTotal >= 0
                ? (_procTotal + " 个进程 · 按 CPU 排序，每 3 秒刷新")
                : "进程枚举不可用";
            return c;
        }

        void RefreshProcs()
        {
            DateTime now = DateTime.UtcNow;
            _procNextUtc = now.AddSeconds(3);
            double elapsedMs = _procLastSampleUtc == DateTime.MinValue
                ? 0 : (now - _procLastSampleUtc).TotalMilliseconds;
            _procLastSampleUtc = now;

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return; }
            _procTotal = procs.Length;

            int ncpu = Environment.ProcessorCount;
            List<ProcInfo> list = new List<ProcInfo>();
            Dictionary<int, ProcSample> fresh = new Dictionary<int, ProcSample>();

            for (int i = 0; i < procs.Length; i++)
            {
                Process p = procs[i];
                try
                {
                    TimeSpan cpu = p.TotalProcessorTime;
                    long ws = p.WorkingSet64;
                    int pid = p.Id;

                    ProcInfo info = new ProcInfo();
                    info.Name = p.ProcessName;
                    info.Pid = pid;
                    info.WorkingSet = ws;

                    ProcSample prev;
                    if (elapsedMs > 50 && _lastProcs.TryGetValue(pid, out prev))
                    {
                        double delta = (cpu - prev.Cpu).TotalMilliseconds;
                        if (delta >= 0)
                        {
                            double pct = 100.0 * delta / (elapsedMs * ncpu);
                            if (pct > 100) pct = 100;
                            info.CpuPercent = pct;
                        }
                    }
                    list.Add(info);

                    ProcSample s = new ProcSample();
                    s.Cpu = cpu;
                    s.WorkingSet = ws;
                    fresh[pid] = s;
                }
                catch { /* 系统进程、已退出的进程、受保护的进程：跳过 */ }
                finally { try { p.Dispose(); } catch { } }
            }

            // 字典整体换新：否则退出的进程会永远留在里面，越积越多
            _lastProcs.Clear();
            foreach (KeyValuePair<int, ProcSample> kv in fresh) _lastProcs[kv.Key] = kv.Value;

            list.Sort(delegate(ProcInfo a, ProcInfo b)
            {
                double ca = double.IsNaN(a.CpuPercent) ? -1 : a.CpuPercent;
                double cb = double.IsNaN(b.CpuPercent) ? -1 : b.CpuPercent;
                if (cb != ca) return cb.CompareTo(ca);
                return b.WorkingSet.CompareTo(a.WorkingSet);
            });

            _procRows.Clear();
            int take = list.Count < 8 ? list.Count : 8;
            for (int i = 0; i < take; i++)
            {
                ProcInfo pi = list[i];
                string cpuText = double.IsNaN(pi.CpuPercent) ? "--" : pi.CpuPercent.ToString("0.0") + "%";
                _procRows.Add(new Row(
                    Shorten(pi.Name, 20),
                    cpuText + "  ·  " + Native.HumanBytes(pi.WorkingSet)));
            }
        }

        static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "--";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        // ---------------------------------------------------------- 网络

        Card BuildNetworkCard()
        {
            Card c = new Card();
            c.Id = "net";
            c.Title = "网络";

            Dictionary<string, double> rx = _pdh.GetArray(C_NET_RX);
            Dictionary<string, double> tx = _pdh.GetArray(C_NET_TX);

            _ifaceNames.Clear();
            double sumRx = SumInterfaces(rx, out _lastInterfaceCount, _ifaceNames);
            double sumTx = SumInterfaces(tx, out _lastInterfaceCount2, null);
            bool haveRx = rx.Count > 0;
            bool haveTx = tx.Count > 0;

            Metric mRx = new Metric();
            mRx.Key = "net.rx"; mRx.Label = "下行";
            if (haveRx) { mRx.Ok = true; mRx.Value = sumRx; mRx.Text = Native.HumanRate(sumRx); }
            else mRx.Note = "计数器 Network Interface 读不到";
            c.Metrics.Add(mRx);

            Metric mTx = new Metric();
            mTx.Key = "net.tx"; mTx.Label = "上行";
            if (haveTx) { mTx.Ok = true; mTx.Value = sumTx; mTx.Text = Native.HumanRate(sumTx); }
            else mTx.Note = "计数器 Network Interface 读不到";
            c.Metrics.Add(mTx);

            // 把实际参与汇总的接口名列出来 —— 用户一眼就能看出是不是混进了虚拟网卡
            if (_ifaceNames.Count > 0)
            {
                string joined = string.Join(" / ", _ifaceNames.ToArray());
                if (joined.Length > 78) joined = joined.Substring(0, 78) + "…";
                c.Sub = _ifaceNames.Count + " 个接口：" + joined;
            }
            else
            {
                c.Sub = "无活动接口";
            }
            return c;
        }

        int _lastInterfaceCount = 0;
        int _lastInterfaceCount2 = 0;
        readonly List<string> _ifaceNames = new List<string>();

        /// <summary>
        /// 汇总物理网卡。必须把回环、隧道、伪接口排掉 ——
        /// 否则 "下行 40 MB/s" 里会混进本机内部流量，看的人会以为在下载东西。
        /// </summary>
        static double SumInterfaces(Dictionary<string, double> values, out int count, List<string> names)
        {
            double sum = 0;
            count = 0;
            foreach (KeyValuePair<string, double> kv in values)
            {
                string n = kv.Key;
                if (IsVirtualInterface(n)) continue;
                if (kv.Value < 0) continue;
                sum += kv.Value;
                count++;
                if (names != null && !names.Contains(n)) names.Add(n);
            }
            return sum;
        }

        static bool IsVirtualInterface(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string[] bad = new string[]
            {
                "Loopback", "isatap", "Teredo", "Pseudo-Interface", "6to4", "IP-HTTPS", "WAN Miniport",
                // 虚拟网卡：不是物理链路，混进来会让"下行速率"变成假象
                "vEthernet", "VMware", "VirtualBox", "Hyper-V", "Tailscale", "ZeroTier",
                "Bluetooth", "WSL", "TAP-", "OpenVPN", "WireGuard"
            };
            for (int i = 0; i < bad.Length; i++)
                if (name.IndexOf(bad[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public void Dispose()
        {
            try { _pdh.Dispose(); } catch { }
        }
    }
}
