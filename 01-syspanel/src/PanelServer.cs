// PanelServer.cs —— 内嵌 HTTP 服务 + JSON 序列化。
//
// 三个设计决定，都有实测理由：
//   1. **不用 HttpListener，用 TcpListener 自己写最小 HTTP。**
//      HttpListener 走 http.sys：在受限令牌（沙箱 / 低权限账户）下 Start() 会直接抛
//      PlatformNotSupportedException（本机实测），而且监听局域网还要先
//      `netsh http add urlacl`。自己写这 100 行反而更稳、更通用、零系统配置。
//   2. **服务端定时采集，请求只读缓存。** 若每个请求触发一轮采集，开三个浏览器窗口
//      开销就 ×3，而 PDH 采集并不便宜。
//   3. **JSON 数字一律 InvariantCulture。** 中文/德文等区域的 ToString() 会把小数点
//      写成逗号，直接产出非法 JSON（{"value":3,4}）—— 本工程最难查的坑之一。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SysPanel
{
    internal static class Json
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>数字 → JSON。NaN/无穷大输出 null（JSON 没有这些字面量）。</summary>
        public static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Str(string s)
        {
            return "\"" + Escape(s) + "\"";
        }
    }

    public sealed class PanelServer : IDisposable
    {
        readonly Collector _collector = new Collector();
        readonly string _host;
        readonly int _port;
        readonly int _refreshMs;

        readonly object _lock = new object();
        string _cachedJson = "{\"cards\":[],\"diag\":[]}";
        DateTime _cachedAt = DateTime.MinValue;

        TcpListener _listener;
        Thread _acceptThread;
        Thread _collectThread;
        volatile bool _stop;

        public int Port { get { return _port; } }
        public string Host { get { return _host; } }
        public string Url
        {
            get
            {
                string shown = (_host == "+" || _host == "*" || _host == "0.0.0.0") ? "localhost" : _host;
                return "http://" + shown + ":" + _port + "/";
            }
        }

        public PanelServer(string host, int port, int refreshMs)
        {
            _host = host;
            _port = port;
            _refreshMs = refreshMs < 250 ? 250 : refreshMs;
        }

        public void Start()
        {
            CollectOnce();                      // 先填上首帧，避免第一个请求拿到空数组
            _collectThread = new Thread(CollectLoop);
            _collectThread.IsBackground = true;
            _collectThread.Start();

            _listener = new TcpListener(ResolveBindAddress(_host), _port);
            _listener.Start();

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
        }

        static IPAddress ResolveBindAddress(string host)
        {
            if (host == "+" || host == "*" || host == "0.0.0.0") return IPAddress.Any;
            if (host == "localhost") return IPAddress.Loopback;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return ip;
            return IPAddress.Loopback;
        }

        void CollectLoop()
        {
            while (!_stop)
            {
                try { CollectOnce(); }
                catch (Exception ex) { Console.Error.WriteLine("[collect] " + ex.Message); }
                int slept = 0;
                while (slept < _refreshMs && !_stop) { Thread.Sleep(50); slept += 50; }
            }
        }

        void CollectOnce()
        {
            string json = BuildJson();
            lock (_lock) { _cachedJson = json; _cachedAt = DateTime.Now; }
        }

        public string GetJson()
        {
            lock (_lock) { return _cachedJson; }
        }

        // ---------------------------------------------------------- JSON

        string BuildJson()
        {
            List<Card> cards = _collector.Collect();
            StringBuilder sb = new StringBuilder(8192);
            sb.Append("{\"host\":").Append(Json.Str(SafeHost()));
            sb.Append(",\"time\":").Append(Json.Str(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.Append(",\"uptimeSec\":").Append(Json.Num(Native.GetUptimeSeconds()));
            sb.Append(",\"refreshMs\":").Append(_refreshMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cards\":[");
            bool firstCard = true;
            for (int i = 0; i < cards.Count; i++)
            {
                if (!firstCard) sb.Append(',');
                firstCard = false;
                AppendCard(sb, cards[i]);
            }
            sb.Append("],\"diag\":[");
            AppendDiagnostics(sb, cards);
            sb.Append("]}");
            return sb.ToString();
        }

        static void AppendCard(StringBuilder sb, Card c)
        {
            sb.Append("{\"id\":").Append(Json.Str(c.Id));
            sb.Append(",\"title\":").Append(Json.Str(c.Title));
            sb.Append(",\"sub\":").Append(Json.Str(c.Sub));
            sb.Append(",\"metrics\":[");
            bool first = true;
            for (int i = 0; i < c.Metrics.Count; i++)
            {
                Metric m = c.Metrics[i];
                if (!m.Ok) continue;             // 读不到的指标不进 JSON —— 前端也就无从显示
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"key\":").Append(Json.Str(m.Key));
                sb.Append(",\"label\":").Append(Json.Str(m.Label));
                sb.Append(",\"text\":").Append(Json.Str(m.Text));
                sb.Append(",\"unit\":").Append(Json.Str(m.Unit));
                sb.Append(",\"value\":").Append(Json.Num(m.Value));
                sb.Append(",\"bar\":").Append(Json.Num(m.Bar));
                sb.Append('}');
            }
            sb.Append("],\"rows\":[");
            first = true;
            for (int i = 0; i < c.Rows.Count; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"label\":").Append(Json.Str(c.Rows[i].Label));
                sb.Append(",\"value\":").Append(Json.Str(c.Rows[i].Value));
                sb.Append('}');
            }
            sb.Append("]}");
        }

        /// <summary>
        /// 诊断：把所有"本机读不到"的指标连同原因一起报出去。
        /// 这是刻意设计的 —— 面板上少了一张卡时，用户能自己看到"为什么没有"，
        /// 而不是以为程序坏了。
        /// </summary>
        void AppendDiagnostics(StringBuilder sb, List<Card> cards)
        {
            bool first = true;
            for (int i = 0; i < cards.Count; i++)
            {
                Card c = cards[i];
                for (int j = 0; j < c.Metrics.Count; j++)
                {
                    Metric m = c.Metrics[j];
                    if (m.Ok) continue;
                    if (string.IsNullOrEmpty(m.Note)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"card\":").Append(Json.Str(c.Title));
                    sb.Append(",\"label\":").Append(Json.Str(m.Label));
                    sb.Append(",\"note\":").Append(Json.Str(m.Note));
                    sb.Append('}');
                }
            }
            for (int i = 0; i < _collector.Diagnostics.Count; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"card\":").Append(Json.Str("计数器"));
                sb.Append(",\"label\":").Append(Json.Str(""));
                sb.Append(",\"note\":").Append(Json.Str(_collector.Diagnostics[i]));
                sb.Append('}');
            }
        }

        static string SafeHost()
        {
            try { return Environment.MachineName; } catch { return "unknown"; }
        }

        // ---------------------------------------------------------- HTTP（手写最小实现）

        void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }                  // 监听器被关掉时跳出
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                // 关闭时最多等 2 秒把发送缓冲里的字节送出去（配合下面的 Shutdown(Send)）
                client.LingerState = new LingerOption(true, 2);
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    string requestLine = ReadRequestHead(ns);
                    if (requestLine == null) return;
                    string path = ParsePath(requestLine);

                    if (path == "/api/state") Send(ns, "application/json; charset=utf-8", GetJson());
                    else if (path == "/" || path == "/index.html") Send(ns, "text/html; charset=utf-8", GetPage());
                    else if (path == "/boot-550c.css") Send(ns, "text/css; charset=utf-8", GetAsset("boot-550c.css"));
                    else if (path == "/boot-550c.html") Send(ns, "text/html; charset=utf-8", GetAsset("boot-550c.html"));
                    else if (path == "/boot-550c.js") Send(ns, "application/javascript; charset=utf-8", GetAsset("boot-550c.js"));
                    else if (path == "/favicon.ico") Send(ns, "image/x-icon", "");
                    else SendStatus(ns, 404, "Not Found");

                    // 关键一步：先发 FIN 再关闭。直接 Close() 会丢掉内核发送缓冲里还没出去的
                    // 字节，客户端就拿到"Content-Length 不符的截断响应"而一直等 ——
                    // node 的 fetch(undici) 正是这样超时的，而裸 socket 只看得到半截 body。
                    try { ns.Flush(); } catch { }
                    try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                }
            }
            catch { /* 单个连接出错不能影响服务 */ }
        }

        /// <summary>读到请求头结束（\r\n\r\n）为止，返回请求行（"GET /x HTTP/1.1"）。</summary>
        static string ReadRequestHead(NetworkStream ns)
        {
            byte[] buf = new byte[2048];
            StringBuilder sb = new StringBuilder(1024);
            while (sb.Length < 16384)
            {
                int n;
                try { n = ns.Read(buf, 0, buf.Length); }
                catch { return null; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
            }
            string s = sb.ToString();
            int e = s.IndexOf("\r\n");
            if (e < 0) return s.Length > 0 ? s : null;
            return s.Substring(0, e);
        }

        static string ParsePath(string requestLine)
        {
            // "GET /api/state?x=1 HTTP/1.1"
            int sp1 = requestLine.IndexOf(' ');
            if (sp1 < 0) return "/";
            int sp2 = requestLine.IndexOf(' ', sp1 + 1);
            string target = sp2 > 0 ? requestLine.Substring(sp1 + 1, sp2 - sp1 - 1) : requestLine.Substring(sp1 + 1);
            int q = target.IndexOf('?');
            if (q >= 0) target = target.Substring(0, q);
            if (target.Length == 0) target = "/";
            return target;
        }

        static void Send(NetworkStream ns, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body == null ? "" : body);
            StringBuilder h = new StringBuilder(192);
            h.Append("HTTP/1.1 200 OK\r\n");
            h.Append("Content-Type: ").Append(contentType).Append("\r\n");
            h.Append("Content-Length: ").Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            // 面板数据必须实时：明确禁掉缓存，否则浏览器 304 会让数字"卡住"
            h.Append("Cache-Control: no-store, no-cache, must-revalidate\r\n");
            h.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(h.ToString());
            // 头与体一次写出：少一次系统调用，也避免"头已发、体还在缓冲"的中间态
            byte[] all = new byte[head.Length + payload.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, all, head.Length, payload.Length);
            ns.Write(all, 0, all.Length);
            ns.Flush();
        }

        static void SendStatus(NetworkStream ns, int code, string text)
        {
            string body = code + " " + text;
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + code + " " + text + "\r\n"
                        + "Content-Type: text/plain; charset=utf-8\r\n"
                        + "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                        + "Connection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(payload, 0, payload.Length);
            ns.Flush();
        }

        // ---------------------------------------------------------- 静态资源

        /// <summary>页面优先取 exe 同目录的 web\ 文件（方便直接改），取不到就用编译期内嵌的那份。</summary>
        string GetPage()
        {
            string fromDisk = TryReadWebFile("index.html");
            if (fromDisk != null) return fromDisk;
            return GetAsset("index.html");
        }

        string GetAsset(string name)
        {
            string fromDisk = TryReadWebFile(name);
            if (fromDisk != null) return fromDisk;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] names = asm.GetManifestResourceNames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream s = asm.GetManifestResourceStream(names[i]))
                        {
                            if (s == null) continue;
                            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                                return r.ReadToEnd();
                        }
                    }
                }
            }
            catch { }
            return "<!doctype html><meta charset=\"utf-8\"><h1>缺少 web/" + name + "</h1>"
                 + "<p>把它放到 exe 同目录的 web\\ 文件夹里，或用 build.bat 重新编译（会把页面内嵌进 exe）。</p>";
        }

        static string TryReadWebFile(string name)
        {
            try
            {
                string dir = AppDir();
                string p = Path.Combine(Path.Combine(dir, "web"), name);
                if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
                string p2 = Path.Combine(dir, name);          // 也认扁平的目录结构
                if (File.Exists(p2)) return File.ReadAllText(p2, Encoding.UTF8);
            }
            catch { }
            return null;
        }

        public static string AppDir()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p);
            }
            catch { }
            return Environment.CurrentDirectory;
        }

        public void Stop()
        {
            _stop = true;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }

        public void Dispose()
        {
            Stop();
            try { _collector.Dispose(); } catch { }
        }
    }
}
