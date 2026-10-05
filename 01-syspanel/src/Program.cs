// Program.cs —— 入口。
//
// 用法：
//   SysPanel.exe                     起服务，默认 http://127.0.0.1:8765/
//   SysPanel.exe --open               起服务并自动打开浏览器
//   SysPanel.exe --port 9000          换端口
//   SysPanel.exe --host +             监听所有网卡（局域网看；需要管理员或 netsh urlacl）
//   SysPanel.exe --refresh 500        刷新间隔（毫秒，最小 250）
//   SysPanel.exe --once               采集一次，人读格式打到控制台就退出（自检用）
//   SysPanel.exe --json               采集一次，JSON 打到标准输出就退出（喂给别的程序/管道）
//
// 退出：Ctrl+C，或关掉窗口。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SysPanel
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            // 中文不乱码。两种情况要分开处理：
            //   直接跑在控制台窗口 → 设 OutputEncoding（UTF-8 字节 + UTF-8 代码页）
            //   输出被重定向到文件 → 自建 UTF-8 writer；此时只设 OutputEncoding 不可靠
            //   （本机实测：重定向时 .NET 会按系统 ANSI(GBK) 写，读出来就是乱码）
            try
            {
                if (Console.IsOutputRedirected)
                {
                    StreamWriter w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                    w.AutoFlush = true;
                    Console.SetOut(w);
                    StreamWriter e = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
                    e.AutoFlush = true;
                    Console.SetError(e);
                }
                else
                {
                    Console.OutputEncoding = Encoding.UTF8;
                }
            }
            catch { }

            string host = "127.0.0.1";
            int port = 8765;
            int refresh = 1000;
            bool open = false;
            bool once = false;
            bool json = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--port":
                        if (i + 1 < args.Length) { int.TryParse(args[++i], out port); }
                        break;
                    case "--host":
                        if (i + 1 < args.Length) host = args[++i];
                        break;
                    case "--refresh":
                        if (i + 1 < args.Length) { int.TryParse(args[++i], out refresh); }
                        break;
                    case "--open":
                        open = true;
                        break;
                    case "--once":
                        once = true;
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintHelp();
                        return 0;
                }
            }
            if (port <= 0 || port > 65535) port = 8765;
            if (refresh < 250) refresh = 250;

            if (once || json)
            {
                using (Collector c = new Collector())
                {
                    // 采集两次：CPU 占用需要两个采样点的差值（与真实运行时的行为一致）
                    List<Card> cards = c.Collect();
                    if (!json)
                    {
                        Thread.Sleep(400);
                        cards = c.Collect();
                        PrintHuman(cards, c.Diagnostics);
                    }
                    else
                    {
                        Console.WriteLine(BuildJsonOnce(c, cards));
                    }
                }
                return 0;
            }

            // 端口被占用是很常见的事（这台机器上 8765 就躺着别的服务），
            // 与其让用户去查谁占了端口，不如自动往后找。
            PanelServer server = null;
            Exception lastError = null;
            for (int candidate = port; candidate <= port + 10 && server == null; candidate++)
            {
                PanelServer attempt = new PanelServer(host, candidate, refresh);
                try
                {
                    attempt.Start();
                    server = attempt;
                    if (candidate != port)
                        Console.WriteLine("端口 " + port + " 被占用，已改用 " + candidate + "。");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { attempt.Dispose(); } catch { }
                    if (candidate < port + 10)
                        Console.WriteLine("端口 " + candidate + " 不可用（" + ex.Message + "），试下一个…");
                }
            }
            if (server == null)
            {
                Console.Error.WriteLine("连续 11 个端口都起不来。最后一次错误：" +
                    (lastError == null ? "未知" : lastError.Message));
                if (host == "+" || host == "*")
                    Console.Error.WriteLine("提示：监听所有网卡时，Windows 防火墙会弹窗询问，允许后别的设备才能访问。");
                return 1;
            }

            Console.WriteLine("通用硬件监控面板");
            Console.WriteLine("  地址   " + server.Url);
            Console.WriteLine("  刷新   " + refresh + " ms（服务端采集，浏览器只读缓存）");
            Console.WriteLine("  数据   " + server.Url + "api/state");
            Console.WriteLine("  停止   Ctrl+C");
            Console.WriteLine();

            if (open)
            {
                try { System.Diagnostics.Process.Start(server.Url); }
                catch (Exception ex) { Console.WriteLine("打不开浏览器（自己去点地址吧）：" + ex.Message); }
            }

            ManualResetEvent wait = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                wait.Set();
            };
            wait.WaitOne();

            Console.WriteLine("正在停止…");
            server.Dispose();
            return 0;
        }

        static void PrintHelp()
        {
            Console.WriteLine("SysPanel —— 通用硬件监控面板（本机通用，不依赖任何第三方库）");
            Console.WriteLine();
            Console.WriteLine("  SysPanel.exe [--port N] [--host 127.0.0.1|+] [--refresh MS] [--open]");
            Console.WriteLine("  SysPanel.exe --once      采集一次，人读格式输出后退出");
            Console.WriteLine("  SysPanel.exe --json      采集一次，JSON 输出后退出");
        }

        // ---------------------------------------------------------- 自检输出

        static void PrintHuman(List<Card> cards, List<string> counterDiag)
        {
            Console.WriteLine("=== 采到的卡片（读不到整卡不显示） ===");
            for (int i = 0; i < cards.Count; i++)
            {
                Card c = cards[i];
                Console.WriteLine();
                Console.WriteLine("[" + c.Title + "]" + (c.Sub.Length > 0 ? "  " + c.Sub : ""));
                for (int j = 0; j < c.Metrics.Count; j++)
                {
                    Metric m = c.Metrics[j];
                    if (!m.Ok) continue;
                    Console.WriteLine("    " + Pad(m.Label, 10) + " " + m.Text);
                }
                for (int j = 0; j < c.Rows.Count; j++)
                    Console.WriteLine("      · " + Pad(c.Rows[j].Label, 12) + " " + c.Rows[j].Value);
            }

            Console.WriteLine();
            Console.WriteLine("=== 读不到的项（不显示，但要知道为什么） ===");
            bool any = false;
            for (int i = 0; i < cards.Count; i++)
            {
                for (int j = 0; j < cards[i].Metrics.Count; j++)
                {
                    Metric m = cards[i].Metrics[j];
                    if (m.Ok || string.IsNullOrEmpty(m.Note)) continue;
                    any = true;
                    Console.WriteLine("  " + cards[i].Title + " / " + m.Label + " —— " + m.Note);
                }
            }
            for (int i = 0; i < counterDiag.Count; i++)
            {
                any = true;
                Console.WriteLine("  " + counterDiag[i]);
            }
            if (!any) Console.WriteLine("  （没有，全部读到了）");
        }

        static string Pad(string s, int width)
        {
            if (s == null) s = "";
            int w = 0;
            for (int i = 0; i < s.Length; i++) w += (s[i] > 0x2E80) ? 2 : 1;   // 中文字符按 2 列算
            StringBuilder sb = new StringBuilder(s);
            while (w < width) { sb.Append(' '); w++; }
            return sb.ToString();
        }

        /// <summary>把采集结果序列化成与 /api/state 同构的 JSON（--json 模式用）。</summary>
        static string BuildJsonOnce(Collector c, List<Card> cards)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"host\":\"").Append(Json.Escape(SafeHost())).Append("\",");
            sb.Append("\"time\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"cards\":[");
            for (int i = 0; i < cards.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Card card = cards[i];
                sb.Append("{\"id\":\"").Append(Json.Escape(card.Id)).Append("\",");
                sb.Append("\"title\":\"").Append(Json.Escape(card.Title)).Append("\",");
                sb.Append("\"sub\":\"").Append(Json.Escape(card.Sub)).Append("\",");
                sb.Append("\"metrics\":[");
                bool first = true;
                for (int j = 0; j < card.Metrics.Count; j++)
                {
                    Metric m = card.Metrics[j];
                    if (!m.Ok) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"key\":\"").Append(Json.Escape(m.Key)).Append("\",");
                    sb.Append("\"label\":\"").Append(Json.Escape(m.Label)).Append("\",");
                    sb.Append("\"text\":\"").Append(Json.Escape(m.Text)).Append("\",");
                    sb.Append("\"value\":").Append(Json.Num(m.Value)).Append(',');
                    sb.Append("\"bar\":").Append(Json.Num(m.Bar)).Append('}');
                }
                sb.Append("],\"rows\":[");
                first = true;
                for (int j = 0; j < card.Rows.Count; j++)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"label\":\"").Append(Json.Escape(card.Rows[j].Label)).Append("\",");
                    sb.Append("\"value\":\"").Append(Json.Escape(card.Rows[j].Value)).Append("\"}");
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string SafeHost()
        {
            try { return Environment.MachineName; } catch { return "unknown"; }
        }
    }
}
