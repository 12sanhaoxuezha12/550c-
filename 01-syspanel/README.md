# SysPanel · 通用硬件监控面板

一个**在任意 Windows 机器上都能跑**的硬件监控面板：单个 exe，内置 HTTP 服务，浏览器打开看。

设计目标只有两条，其余都服从它们：

1. **通用** —— 同一份 exe 在 Intel 或 AMD 平台、NVIDIA / AMD / Intel 显卡上都能用；
   不装驱动、不依赖第三方库、不需要管理员权限、不写注册表、不联网（只监听本机回环）。
2. **不编数字** —— 读得到就显示，读不到就**整项不显示**，并在「诊断」里写明原因。
   绝不拿估算值冒充实测值。

## 快速开始

```bat
SysPanel.exe                  :: 起服务，默认 http://127.0.0.1:8765/
SysPanel.exe --open           :: 起服务并自动打开浏览器
SysPanel.exe --port 8791      :: 换端口
SysPanel.exe --host +         :: 监听所有网卡（同一局域网的手机/平板也能看）
SysPanel.exe --refresh 500    :: 刷新间隔（毫秒，最小 250）
SysPanel.exe --once           :: 采集一次，人读格式打印到控制台就退出（自检）
SysPanel.exe --json           :: 采集一次，输出 JSON 就退出（喂给别的程序）
```

停止：关窗口，或 Ctrl+C。

- **不需要管理员权限**（默认只绑 `127.0.0.1`）。
- 用 `--host +` 时 Windows 防火墙会弹窗询问，**允许**后别的设备才能访问；
  此时地址用本机内网 IP，例如 `http://192.168.1.20:8765/`。

## 界面

骨架与 GameHud「性能监测」面板（本机 8765）同构，配色数值也是照抄的：

    顶栏：标题 · ● 实时数据 · 主机 · 诊断 · 皮肤 · 时钟
    顶部横排 4 个 KPI：CPU 使用率 / GPU 使用率 / 内存已用·总量 / 显存已用·总量
    左列(1.5fr)                          右列(1fr)
      CPU 占用（大折线）                   CPU 详情：频率 / 温度 / 标称频率 / 逻辑核数
      GPU 使用率（大折线）                 GPU 详情：显存占用 / 3D 占用 / 显存总量 / 型号
      底部横排：进程 / 磁盘读写·网络 / 系统  内存详情：已用 / 占用率 / 总量 / 可用

- **读得到才显示**：右列与底部的整卡若一项都读不到 → 整卡隐藏、底部自动改列数，不留空洞；
  KPI 位置是固定的 4 格，读不到就显示 `--`（不估算、不补零）。
- **只保留一套皮肤**：面板（数值照抄 GameHud monitor.html）。终端（550C 琥珀 CRT）与浅色两套
  已按要求剔除 —— 界面上没有皮肤切换入口，CSS 里也不再有 `data-theme` 分支。
- 550C 开机片头默认播放，播完字形压暗 + 8px 模糊**常驻成水印**（在卡片之下，被毛玻璃柔化）；`?boot=0` 可跳过。
- 右上「诊断」展开**本机读不到的项及原因** —— 少一张卡时你能自己看出为什么。
- 折线是页面侧积累的 120 点历史（约 2 分钟），刚打开时从左往右长出来。

## 数据从哪来（全部是 Windows 自带接口）

| 指标 | 来源 | 通用性 |
|---|---|---|
| CPU 占用 | `GetSystemTimes` 差值 | 全平台。**故意不用** `% Processor Utility`（任务管理器口径）—— 部分机器上它会虚高（本机实测约 12 倍） |
| CPU 频率 | PDH `Processor Frequency`（有则用）否则 `% Processor Performance` × 注册表基准频率 | 多数可用；**AMD 的注册表基准常常是最大加速频率，所以读数偏高** |
| CPU 温度 | WMI `MSAcpi_ThermalZoneTemperature` | **多数桌面主板没实现这个类，读不到是常态** |
| CPU 功耗 | —— | Windows 没有免驱动的通用接口。**本工程不做估算，直接不提供** |
| 内存 | `GlobalMemoryStatusEx` | 全平台 |
| 显卡名称 / 显存总量 | 注册表 Display Class 的 `DriverDesc` + `HardwareInformation.qwMemorySize` | 全平台、64 位准确（WMI 的 `AdapterRAM` 是 32 位，超过 4 GB 会溢出） |
| GPU 3D 占用 | PDH `\GPU Engine(*)\Utilization Percentage` | Win10 1709+ / WDDM 2.0。**跨 NVIDIA / AMD / Intel 通用**，不需要 NVML / ADL |
| 显存占用 | PDH `\GPU Adapter Memory(*)\Dedicated Usage` | 同上 |
| GPU 温度 / 功耗 | —— | 免驱动读不到（NVML/ADL 需要额外适配），不显示 |
| 磁盘读写 / 活动 | PDH `\PhysicalDisk(_Total)\...` | 全平台 |
| 分区空间 | `DriveInfo` | 全平台 |
| 网络上下行 | PDH `\Network Interface(*)\...`（已过滤回环/隧道/虚拟网卡） | 全平台 |
| 开机时长 | `GetTickCount64` | 全平台 |
| 进程数 | `Process.GetProcesses()` | 全平台（受限令牌下会偏少，见下） |

**跨语言的关键一点**：所有 PDH 路径都用 `PdhAddEnglishCounter` 注册。
中文系统上 `PdhAddCounter` 解析的是**本地化**计数器名，英文路径会直接找不到。

## 本机实测（AMD Ryzen 5 5600 + RTX 4070 + Windows 11 23H2 22631）

```
[系统]  Windows 11 Pro 23H2 (Build 22631)      开机时长 5小时21分 · 进程数 36 · 逻辑核 12
[CPU]   AMD Ryzen 5 5600 6-Core Processor      占用 2.0 % · 频率 4.70 GHz
[内存]  31.9 GB 物理内存                        已用 13.2 GB · 占用率 41.4 % · 可用 18.7 GB
[GPU]   NVIDIA GeForce RTX 4070                3D 占用 0.0 % · 显存 2.9 GB / 12.0 GB
[磁盘]                                         读 21.7 KB/s · 写 498.5 KB/s · 活动 0.7 %
                                               C: 可用 22.3/150.1 GB · D: 145.3/802.9 GB · F: 755.6/900 GB · G: 638.2/1007.7 GB
[网络]  4 个活动接口                             下行 178.0 KB/s · 上行 8.0 KB/s

读不到的项：
  CPU / 温度 —— 本机读不到 CPU 温度（ManagementException）：Windows 没有免驱动的通用温度接口
```

## 已知限制（如实列出）

1. **CPU 温度**在绝大多数桌面机上读不到。这是 Windows 的现状：唯一的免驱动温度源是 ACPI 热区，
   而主板厂商普遍不实现它。想读核心温度只能装内核驱动（HWiNFO / 游戏加加那条路），本工程不为它引入驱动。
2. **CPU 频率偏高**：注册表里的 "~MHz" 在 AMD 上通常是最大加速频率而非基础频率（本机 4.69 GHz，
   而 5600 的规格是 4.4 GHz boost）。所以这是**参考值**，不是精确测量。
3. **GPU 显存比例在多显卡机器上会失真**：分子是**所有适配器实例之和**，分母取**最大的那张卡**。
   核显 + 独显的机器上，比例会偏大。
4. **GPU 温度 / 功耗不显示**（需要 NVML / ADL 之类的厂商 SDK）。
5. **进程数在受限权限下会偏少**：本机自检（跑在 DSH 沙箱里）只数到 36 个进程，正常桌面会有上百个。
   你自己双击运行时是准确的。
6. **网络"活动接口"计数可能仍包含未识别的虚拟网卡**（已过滤 vEthernet / VMware / VirtualBox /
   Hyper-V / Tailscale / ZeroTier / 回环 / 隧道等常见项，但不能穷举）。
7. **没有帧率（FPS）**：不依赖 RTSS / 抓屏，所以不提供。这是刻意的 —— 别为了一个数字给系统装钩子。
8. **页面渲染可以自己核对**（本机已打通无头 Edge 截图，见 `tools/shot.ps1`）。两个坑：
   ① `--user-data-dir` / `--screenshot` 的路径**必须自己加引号** —— `Start-Process -ArgumentList`
   不替含空格的路径加引号，`D:\AI WORK\...` 会被拆成两个参数，浏览器报
   `Multiple targets are not supported in headless mode`；
   ② DSH 的 workspace-write 沙箱会拒绝 Edge 给自身缓存目录做 ACL 授权
   （`Failed to grant sandbox access ... 0x5 拒绝访问`），浏览器能起但渲染不出来，
   需要在放宽文件权限的情况下运行。另外**务必保留超时保护**：
   没有超时的 headless 在本机上空转烧过 1002 秒 CPU。
9. **开机时长用的是 `GetTickCount64`**，它不包含休眠时间（与任务管理器口径一致）。

## 编译

```bat
build.bat
```

只用系统自带的 `csc.exe`（.NET Framework 4.x）：**不需要装 SDK、不需要 NuGet、没有任何第三方引用**。
`build.bat` 会把 `web\index.html` 一起内嵌进 exe，所以单文件拷走也能跑；
但如果 exe 旁边的 `web\` 目录存在，**优先读磁盘上的那份** —— 这样改页面不用重编译。

源码只有 4 个文件：

| 文件 | 内容 |
|---|---|
| `src/Native.cs` | P/Invoke 与 PDH 封装（内存、CPU 时间、注册表、显示器适配器、PDH 查询） |
| `src/Metrics.cs` | 采集层：每个指标一个探针，自己知道读没读到、为什么没读到 |
| `src/PanelServer.cs` | 手写最小 HTTP 服务 + JSON 序列化 + 静态资源 |
| `src/Program.cs` | 命令行入口、`--once` 自检输出 |

## 验证工具

| 脚本 | 作用 |
|---|---|
| `tools/check-panel.mjs` | 对跑起来的服务做端到端检查（HTTP 状态、JSON 合法性、无 NaN、无"逗号小数点"、静态资源、接口名没乱码、GPU 占用是真实值） |
| `tools/check-page-js.mjs` | 对页面做静态检查（内联 JS 语法、DOM id 齐全、脚本引用的 id 都存在、标签平衡） |
| `tools/shot.ps1` | 用无头 Edge 给页面截图（自己核对界面用；沙箱下需放宽文件权限，文件头写了两个坑） |

```sh
node tools/check-panel.mjs http://127.0.0.1:8765
node tools/check-page-js.mjs web/index.html
```

## 踩过的坑（改代码前先看）

1. **`HttpListener` 在受限令牌下直接不可用**：`Start()` 抛 `PlatformNotSupportedException`
   （它走 http.sys），而且局域网监听还要先 `netsh http add urlacl`。
   现已改成 `TcpListener` 自己写最小 HTTP —— 纯用户态，零系统配置。
2. **`TcpClient.Close()` 会丢掉内核发送缓冲里没发完的字节**，客户端拿到
   "Content-Length 不符的截断响应"就一直等（node 的 fetch 正是这样超时的）。
   现在发送后显式 `Shutdown(SocketShutdown.Send)` 再关闭，并设了 `LingerState`。
3. **JSON 数字必须 `InvariantCulture`**：某些区域的 `ToString()` 把小数点写成逗号，
   直接产出非法 JSON（`{"value":3,4}`）。`tools/check-panel.mjs` 里专门有一条断言盯这个。
4. **PDH 通配符实例要逐项查 `CStatus`**：某个实例可能单独失败（进程刚退出），
   整组数据里混进一个无效值会让汇总结果看起来"莫名其妙"。
5. **注册表里的"显卡"包含虚拟显示适配器**（模拟器虚拟屏、串流虚拟屏、远程桌面驱动）。
   不过滤的话，一张 12 GB 的 4070 会被算成 36 GB（本机实测踩到）。
6. **WMI 首次查询要 1-3 秒**：温度探测放在后台线程、低频刷新，主采集循环只读缓存。
7. **别用 `% Processor Utility` 当默认口径**：本机实测它会虚高约 12 倍。
8. **`PdhGetFormattedCounterArray` 必须写 `CharSet = CharSet.Unicode`**：不写就等于调 Ansi 版本
   （`PdhGetFormattedCounterArrayA`），返回 `char*` 却按 `LPWStr` 读出来 → 实例名整片乱码
   （`Realtek` 会显示成 `敒污整⁫…`）。
   **它的症状完全不像编码问题**，这才是最坑的地方：实例名一乱，
   「在 GPU Engine 里找 `engtype_3D`」就永远匹配不上，于是 **GPU 占用恒显示 `0.0 %`**；
   「网络接口排除虚拟网卡」也整体失效，接口数虚高。
   本机就是踩了这个坑：修好之后 GPU 占用从假的 `0.0 %` 变成真实值 `17.5 %`。
   **教训：看到"某个指标一直是 0"时，先怀疑取数路径，别急着相信它是真的没负载。**
9. **`.bat` 必须写成纯 ASCII**：cmd 按**控制台代码页**（中文系统 936）读 .bat，而不是 UTF-8。
   UTF-8 的中文注释会让字节边界错位，**把命令行都拆断**，症状是：
   `'/nologo' is not recognized as an internal or external command`、
   `'el.exe' is not recognized as an internal or external command`。
   **加 BOM 也救不了**（cmd 会把 BOM 当成一条命令去执行）。
   而 C# 源码里的中文完全没问题 —— 因为 `csc` 被 `/codepage:65001` 显式告知了编码。
10. **`.ps1` 必须存成 UTF-8 with BOM**：PowerShell 5.1 读**无 BOM** 的 .ps1 时按 ANSI 解释，
    中文会破坏语法（本机实测：`shot.ps1` 报"字符串缺少终止符"，明明肉眼看着没问题）。
11. **打包时 exe 正在运行会失败**：`Compress-Archive` 需要独占读，正在跑的程序会报
    `文件"…SysPanel.exe"正由另一进程使用，因此该进程无法访问此文件`。
    用 .NET 自己写 zip 条目即可绕过 ——
    `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)`，
    或者先把程序停掉再打包。

## 许可

这份代码和这个仓库里其他原创部分一样，按 MIT 使用。
550C 开机片头的来源与署名见同目录 `web/` 内注释，或
`550c-boot-ports/CREDITS.md`（动画原稿 Voidpoket，DSH 插件工程 Ziyang Song，均为 MIT）。
