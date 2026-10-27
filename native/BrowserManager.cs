// BrowserManager.cs -- 浏览器生命周期管理（用户看不见它）
//
// 为什么必须由应用自己管浏览器：
//   captcha 由网页版的 JS 实时计算（算法分片动态下发，无法离线复刻 —— 已有实测）。
//   所以引擎必须有一个真实的、已登录的 Edge 在后台跑。
//   但用户不该关心这件事：他们只该看到"请登录一次"。
//
// 状态机：
//   1. 探测调试端口上有无已登录的 pan.xunlei.com → 有：完全静默，什么都不做
//   2. 没有 → 启动 Edge（独立 profile，带调试端口），显示窗口让用户登录
//   3. 登录成功后 → 把窗口最小化（不杀进程，引擎还要用它截 captcha）
//
// 独立 profile 是刻意的：不碰用户日常浏览器的登录态，避免"把你的 Edge 踢下线"，
// 也避免和用户正在用的 Edge 抢 profile 锁（实测会 "Target closed" 失败）。
//
// ---------------- 调试端口（WP3 安全加固） ----------------
// CDP 的 --remote-debugging-port 没有认证：本机任何程序连上去就能读这个浏览器的
// 登录态（cookie / 截请求头）。这是协议固有特性，无法加认证，只能：
//   ① 不把端口暴露给"别人的浏览器/别人的服务"：连之前先验明身份
//      （/json/version 的 Browser 必须以 Edg/ 或 Chrome/ 开头，
//        且 /json/list 里真有 pan.xunlei.com 页面，否则视为端口被冒充，拒绝连接）
//   ② 默认端口仍是 9222；但若 9222 已被本应用以外的程序占用，就回退到
//      9223..9233 里第一个空闲端口，并把**实际端口**写进
//      %APPDATA%\ThunderPanSync\cdp-port.txt，供 engine/cdp.js 读取
//      （engine 侧读不到文件时回退 9222，保证向后兼容）
//
// C# 5 语法限制（系统 csc 只支持到 C#5）：不能用插值字符串、?. 、元组、out var。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ThunderPanSync
{
    public class BrowserManager
    {
        // 默认调试端口。只在"没人占用"时使用，实际端口见 CdpPort。
        private const int DefaultPort = 9222;
        private const int FallbackPortFirst = 9223;
        private const int FallbackPortLast = 9233;
        private const string PortFileName = "cdp-port.txt";

        private readonly string _profileDir;
        private readonly string _dataDir;
        private readonly string _portFile;
        private readonly Action<string> _log;
        private Process _edgeProc;

        // 端口解析只做一次：解析完就固定，避免探测期间端口跳变
        private readonly object _portLock = new object();
        private int _port;
        private bool _portResolved;

        public BrowserManager(string dataDir, Action<string> log)
        {
            _profileDir = Path.Combine(dataDir, "browser-profile");
            _dataDir = dataDir;
            _portFile = Path.Combine(dataDir, PortFileName);
            _log = log;
            _port = 0;                  // 0 = 还没有浏览器；真正端口在启动后从 DevToolsActivePort 读
        }

        /// <summary>实际使用的调试端口（首次访问时解析，并写入 cdp-port.txt）。</summary>
        public int CdpPort
        {
            get { EnsurePortResolved(); return _port; }
        }

        /// <summary>实际调试端点，如 http://127.0.0.1:9222。</summary>
        public string CdpBaseUrl
        {
            get { return "http://127.0.0.1:" + CdpPort; }
        }

        // ---------------- 端口解析 / 回退 ----------------

        /// <summary>
        /// 探测结果。Reachable=端口上有 HTTP 响应；IsBrowser=响应看起来是真的 Edge/Chrome；
        /// HasPanPage=/json/list 里有 pan.xunlei.com 页面；WebSocketDebuggerUrl=用于验证所有权。
        /// </summary>
        private class EndpointInfo
        {
            public bool Reachable;
            public bool IsBrowser;
            public string Browser = "";
            public bool HasPanPage;
            public string WebSocketDebuggerUrl = "";
        }

        private void EnsurePortResolved()
        {
            if (_portResolved) return;
            lock (_portLock)
            {
                if (_portResolved) return;
                int chosen = 0;
                try { chosen = ResolvePort(); } catch { chosen = 0; }
                _port = chosen;
                _portResolved = true;
                if (chosen > 0)
                {
                    // engine 侧与界面侧必须看到同一个端口
                    WritePortFile(chosen);
                }
                else
                {
                    // 没有可沿用的浏览器：删掉上一次遗留的端口文件，免得引擎去连一个已死的端口
                    try { if (File.Exists(_portFile)) File.Delete(_portFile); } catch { }
                }
            }
        }

        /// <summary>
        /// 只做一件事：本应用 profile 的浏览器还活着，就沿用它的端口。
        /// 返回 0 = 没有可复用的浏览器，启动时用 --remote-debugging-port=0 让浏览器随机分配。
        ///
        /// 为什么不再用固定 9222 / 9223-9233（M7，评审认定）：
        ///   固定端口可被猜、可被抢占冒充 —— 那条攻击链实测能造成"静默丢数据"
        ///   （别人在 9222 上放一个假 CDP，本应用连上去后会把"云端已有该文件"当真的）。
        ///   随机化之后，端口只存在于 browser-profile\DevToolsActivePort 与 cdp-port.txt 里。
        /// </summary>
        private int ResolvePort()
        {
            int ownPort;
            string ownWs;
            ReadDevToolsActivePort(out ownPort, out ownWs);

            if (ownPort > 0)
            {
                var own = ProbePort(ownPort);
                // 所有权证据（DevToolsActivePort 里的 browser uuid 与 /json/version 一致）
                // 或者它已经在提供 pan.xunlei.com 页面 —— 两种情况都可以放心复用
                if (own.IsBrowser && (IsOwnEndpoint(own, ownWs) || own.HasPanPage))
                {
                    _log("沿用本应用上次使用的调试端口 " + ownPort);
                    return ownPort;
                }
                return 0;
            }

            // ===== 升级迁移：兼容 M7 之前的旧浏览器 =====
            //
            // M7 之前浏览器是用固定 9222（回退 9223-9233）启动的，那种浏览器**不会**
            // 写 browser-profile\DevToolsActivePort。用户升级本程序时，旧浏览器往往
            // 还在后台跑着（已登录、有 pan 页面），但上面的分支因为读不到
            // DevToolsActivePort 直接返回 0 ⇒ 我们会**再启动一个**浏览器，同时
            // EnsurePortResolved 又把 cdp-port.txt 删掉 ⇒ 引擎回退 9222 连上那个**旧的**，
            // 于是"界面控制新浏览器、引擎操作旧浏览器"，上传必然失败。
            // 实测症状：upload-runonce 两个文件全部 "无法进入目标文件夹"（PAGE）。
            //
            // 处理：探测旧固定端口；只有它确实是本应用 profile 的浏览器、且已经在
            // 提供 pan 页面时才沿用（没有 uuid 证据，所以必须有 pan 页面才算，
            // 避免把别人占住 9222 的浏览器认成自己的）。
            var legacy = ProbePort(DefaultPort);
            if (legacy.IsBrowser && legacy.HasPanPage)
            {
                _log("沿用一个旧版本（固定端口）启动的浏览器：" + DefaultPort
                    + "。建议彻底退出本程序后删除 " + _profileDir + " 下的浏览器再重开，"
                    + "即可切到随机端口模式。");
                return DefaultPort;
            }
            return 0;
        }

        /// <summary>
        /// 启动后读 profile 的 DevToolsActivePort 拿真实端口（--remote-debugging-port=0 时由
        /// 浏览器随机分配并写进这个文件），再写 cdp-port.txt 供引擎读取。读不到返回 0。
        /// </summary>
        private int AdoptLaunchedPort()
        {
            for (int i = 0; i < 60; i++)       // 最多约 12 秒
            {
                int p;
                string ws;
                ReadDevToolsActivePort(out p, out ws);
                if (p > 0)
                {
                    _port = p;
                    _portResolved = true;
                    WritePortFile(p);
                    _log("调试端口 " + p + "（浏览器随机分配，仅 127.0.0.1）");
                    return p;
                }
                Thread.Sleep(200);
            }
            return 0;
        }

        private static string Describe(EndpointInfo info)
        {
            if (!info.Reachable) return "无响应";
            var b = info.Browser;
            if (b == null || b.Length == 0) b = "非浏览器响应";
            return info.HasPanPage ? b + "，有 pan 页面" : b + "，无 pan 页面";
        }

        /// <summary>
        /// 端口上的 CDP 端点是不是"本应用 profile 启动的那个浏览器"。
        /// 依据：Edge/Chrome 会把 /devtools/browser/<uuid> 写进 profile 的 DevToolsActivePort，
        /// 而 /json/version 的 webSocketDebuggerUrl 以同一个 uuid 结尾。
        /// 端口被别的浏览器/别的工具抢走后，uuid 必然不同 → 不算本应用所有。
        /// </summary>
        private static bool IsOwnEndpoint(EndpointInfo info, string ownWs)
        {
            if (info == null) return false;
            if (ownWs == null || ownWs.Length == 0) return false;
            var actual = info.WebSocketDebuggerUrl;
            if (actual == null || actual.Length == 0) return false;
            return actual.EndsWith(ownWs, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 读 profile 里的 DevToolsActivePort：第 1 行是实际端口，第 2 行是 /devtools/browser/&lt;uuid&gt;。
        /// 读不到时 port=0、wsPath=null（当作"没有所有权证据"）。
        /// </summary>
        private void ReadDevToolsActivePort(out int port, out string wsPath)
        {
            port = 0;
            wsPath = null;
            try
            {
                var f = Path.Combine(_profileDir, "DevToolsActivePort");
                if (!File.Exists(f)) return;
                var lines = File.ReadAllLines(f);
                if (lines.Length < 2) return;
                int p;
                if (!int.TryParse(lines[0].Trim(), out p)) return;
                if (p <= 0 || p > 65535) return;
                var ws = lines[1].Trim();
                if (ws.Length == 0) return;
                port = p;
                wsPath = ws;
            }
            catch { }
        }

        /// <summary>把实际端口写给 engine 侧（engine/cdp.js 读这个文件）。</summary>
        private void WritePortFile(int port)
        {
            try
            {
                Directory.CreateDirectory(_dataDir);
                // 无 BOM 的 UTF-8：Node 侧直接 parseInt，避免 BOM 带来脏字符
                File.WriteAllText(_portFile, port.ToString() + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 写不进去不是致命错误：engine 侧会回退 9222，只是换端口时会对不上
                _log("写入 " + PortFileName + " 失败：" + ex.Message);
            }
        }

        // ---------------- 探测 ----------------

        /// <summary>探测某个端口上的 CDP 端点（1.5 秒超时，失败即视为没有服务）。</summary>
        private EndpointInfo ProbePort(int port)
        {
            var info = new EndpointInfo();
            try
            {
                using (var hc = new HttpClient())
                {
                    hc.Timeout = TimeSpan.FromMilliseconds(1500);
                    var ver = hc.GetStringAsync("http://127.0.0.1:" + port + "/json/version").Result;
                    info.Reachable = true;
                    if (!string.IsNullOrEmpty(ver))
                    {
                        var d = TryDeserializeDict(ver);
                        if (d != null)
                        {
                            info.Browser = DictStr(d, "Browser");
                            info.WebSocketDebuggerUrl = DictStr(d, "webSocketDebuggerUrl");
                        }
                        // 防冒充第 1 层：Browser 必须是真浏览器（Edg/ 或 Chrome/）。
                        // 只看"返回非空"会把任意本机 HTTP 服务当成浏览器 —— 这是评审确认的缺陷。
                        info.IsBrowser = LooksLikeRealBrowser(info.Browser);
                        if (info.IsBrowser) info.HasPanPage = FindPanPageWsUrl(hc, port) != null;
                    }
                }
            }
            catch { info.Reachable = false; }
            return info;
        }

        private static bool LooksLikeRealBrowser(string browser)
        {
            if (string.IsNullOrEmpty(browser)) return false;
            return browser.StartsWith("Edg/", StringComparison.OrdinalIgnoreCase)
                || browser.StartsWith("Chrome/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否有"本应用该连的浏览器"：真 Edge/Chrome 并且确实开着 pan.xunlei.com。
        /// 两个条件缺一不可 —— 否则视为端口被他人占用，不连。
        /// </summary>
        public bool IsDebugPortAlive()
        {
            var info = ProbePort(CdpPort);
            return info.IsBrowser && info.HasPanPage;
        }

        /// <summary>
        /// 浏览器是否已登录迅雷云盘。
        /// 必须真查 sessionid cookie —— 只看 URL 是否含 /login 会误判：
        /// 迅雷网页版登出后常停在 pan.xunlei.com 空列表页，URL 不含 /login，
        /// 于是按钮显示"已登录 ✓"、状态条绿色，而引擎实际每次都在鉴权失败。
        /// 这是评审确认过的"界面撒谎"缺陷。
        /// </summary>
        public bool HasLogin()
        {
            try
            {
                var wsUrl = FindPanTargetWs();
                if (wsUrl == null) return false;

                using (var ws = new System.Net.WebSockets.ClientWebSocket())
                {
                    var cts = new System.Threading.CancellationTokenSource(5000);
                    try
                    {
                        ws.ConnectAsync(new Uri(wsUrl), cts.Token).Wait(5000);
                        if (ws.State != System.Net.WebSockets.WebSocketState.Open) return false;

                        // 用 CDP 的 Network.getCookies 拿真实 cookie
                        var reqJson = "{\"id\":1,\"method\":\"Network.getCookies\",\"params\":{\"urls\":[\"https://pan.xunlei.com\"]}}";
                        var bytes = Encoding.UTF8.GetBytes(reqJson);
                        ws.SendAsync(new ArraySegment<byte>(bytes),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token).Wait(3000);

                        var buf = new byte[16 * 1024];
                        var seg = new ArraySegment<byte>(buf);
                        var task = ws.ReceiveAsync(seg, cts.Token);
                        if (!task.Wait(4000)) return false;
                        var resp = Encoding.UTF8.GetString(buf, 0, task.Result.Count);

                        // 有 sessionid 且非空 => 已登录
                        if (resp.IndexOf("\"sessionid\"", StringComparison.OrdinalIgnoreCase) >= 0
                            && resp.IndexOf("cs0", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                        return false;
                    }
                    catch { return false; }
                }
            }
            catch { return false; }
        }

        private string FindPanTargetWs()
        {
            try
            {
                using (var hc = new HttpClient())
                {
                    hc.Timeout = TimeSpan.FromSeconds(3);
                    return FindPanPageWsUrl(hc, CdpPort);
                }
            }
            catch { }
            return null;
        }

        /// <summary>在 /json/list 里找 url 含 pan.xunlei.com 的 page，返回它的调试 ws 地址。</summary>
        private static string FindPanPageWsUrl(HttpClient hc, int port)
        {
            try
            {
                var json = hc.GetStringAsync("http://127.0.0.1:" + port + "/json/list").Result;
                var arr = new JavaScriptSerializer().Deserialize<object[]>(json);
                if (arr == null) return null;
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    var type = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "";
                    var url = d.ContainsKey("url") ? Convert.ToString(d["url"]) : "";
                    if (type == "page" && url != null
                        && url.IndexOf("pan.xunlei.com", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return d.ContainsKey("webSocketDebuggerUrl")
                            ? Convert.ToString(d["webSocketDebuggerUrl"]) : null;
                    }
                }
            }
            catch { }
            return null;
        }

        private static Dictionary<string, object> TryDeserializeDict(string json)
        {
            try
            {
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch { return null; }
        }

        private static string DictStr(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return "";
            return Convert.ToString(d[key]);
        }

        // ---------------- 找 Edge ----------------
        public string FindEdge()
        {
            var cands = new string[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return null;
        }

        // ---------------- 启动 ----------------
        /// <summary>
        /// 确保浏览器可用。返回 true 表示可以直接用（已登录）。
        /// showWindow=false 时以最小化方式启动（用户选择"完全隐藏"）。
        /// </summary>
        public bool Ensure(bool showWindow)
        {
            int port = CdpPort;         // 0 = 还没有浏览器（等下随机分配端口）

            if (port > 0)
            {
            var info = ProbePort(port);

            if (info.IsBrowser)
            {
                // 端口上是真浏览器。但要先确认它不是"别人家的"：
                // 既没有本应用的所有权证据、也没有 pan 页面 → 不碰它。
                int ownPort;
                string ownWs;
                ReadDevToolsActivePort(out ownPort, out ownWs);
                if (!info.HasPanPage && !IsOwnEndpoint(info, ownWs))
                {
                    _log("拒绝连接：端口 " + port + " 上的浏览器不是本应用启动的（无 pan.xunlei.com 页面），"
                        + "请关闭占用该端口的程序后重试");
                    return false;
                }

                if (HasLogin()) { _log("浏览器已就绪（已登录），调试端口 " + port); return true; }
                _log("浏览器已启动（端口 " + port + "），但还没登录迅雷云盘");
                return false;
            }

            if (info.Reachable)
            {
                // 有东西在应答，但不是 Edge/Chrome —— 绝不连（防冒充）
                _log("拒绝连接：端口 " + port + " 上不是浏览器（" + Describe(info) + "），可能已被其他程序占用");
                return false;
            }
            }

            var exe = FindEdge();
            if (exe == null)
            {
                _log("找不到 Edge 或 Chrome，无法继续。请先安装 Microsoft Edge。");
                return false;
            }

            Directory.CreateDirectory(_profileDir);
            _log("正在启动浏览器…（端口由浏览器随机分配，只绑 127.0.0.1）");

            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments =
                // M7：--remote-debugging-port=0 让浏览器自己挑一个空闲端口（随机），
                // 之后从 browser-profile\DevToolsActivePort 读真实端口。固定端口可被猜、
                // 可被抢占冒充（评审实测该链能造成静默丢数据）。
                // ⚠ 永远不要加 --remote-debugging-address：那会把调试端口绑到
                //   局域网/全网卡上，等于把"本机风险"升级成"整个网络风险"。
                "--remote-debugging-port=0 " +
                "--user-data-dir=\"" + _profileDir + "\" " +
                "--no-first-run --no-default-browser-check " +
                "--disable-blink-features=AutomationControlled " +
                "https://pan.xunlei.com";
            psi.UseShellExecute = false;

            // 关键：隐藏启动 = 最小化 + 不抢焦点。窗口仍存在（用户登录时需要），
            // 但不会突然弹到用户面前。登录完成后由调用方再最小化一次。
            psi.WindowStyle = showWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized;

            try
            {
                _edgeProc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log("启动浏览器失败：" + ex.Message);
                return false;
            }

            // 随机端口：先读 DevToolsActivePort 拿到实际端口（最多等约 12 秒）
            if (AdoptLaunchedPort() <= 0)
            {
                _log("错误：浏览器已启动，但读不到它的调试端口（DevToolsActivePort 缺失或不可解析），无法连接");
                return false;
            }

            // 再等端口真正可用（最多 30 秒）。要求：真浏览器 + pan 页面都在（防冒充校验同时满足）
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                if (IsDebugPortAlive()) break;
            }
            if (!IsDebugPortAlive())
            {
                _log("浏览器启动超时（调试端口 " + _port + " 上没有检测到 pan.xunlei.com 页面）");
                return false;
            }

            _log("浏览器已启动，等待登录");
            return HasLogin();
        }

        /// <summary>
        /// 退出时清理本应用启动的浏览器进程。
        ///
        /// 为什么必须做：之前 BrowserManager 全文没有任何 Kill，
        /// 用户每次退出都会留下一个带 --remote-debugging-port=9222 的 msedge.exe 常驻。
        /// 实测确认过：应用退出后仍有 3 个带 9222 的 Edge 在跑。
        /// 危害：① 9222 无认证，任何本机程序都能接管这个已登录会话
        ///       ② 卸载时 profile 被锁，数据目录删不干净
        /// 安全约束：只杀命令行里同时含本应用的 user-data-dir 和 remote-debugging-port 的进程，
        ///          绝不碰用户自己的 Edge。
        /// 注意：这里的 profile 路径匹配与端口无关（换端口不影响本方法的判断）。
        /// </summary>
        public void ShutdownBrowser()
        {
            try
            {
                // 1) 优先按我们记录的进程号收掉整棵进程树
                if (_edgeProc != null)
                {
                    try
                    {
                        if (!_edgeProc.HasExited) _edgeProc.Kill();
                    }
                    catch { }
                    _edgeProc = null;
                }

                // 2) 兜底：Edge 会派生出多个子进程，父进程死了子进程可能还在。
                //    只杀"命令行里带我们的 profile 目录"的那些，绝不泛杀 msedge.exe。
                var marker = _profileDir;
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "wmic";
                psi.Arguments = "process where \"name='msedge.exe' or name='chrome.exe'\" get ProcessId,CommandLine /format:list";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;

                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);

                    int pid = 0;
                    string cmd = null;
                    foreach (var rawLine in outp.Split('\n'))
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith("CommandLine=", StringComparison.OrdinalIgnoreCase))
                        {
                            cmd = line.Substring("CommandLine=".Length);
                        }
                        else if (line.StartsWith("ProcessId=", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line.Substring("ProcessId=".Length).Trim(), out pid);
                            if (pid > 0 && cmd != null && cmd.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    using (var victim = System.Diagnostics.Process.GetProcessById(pid))
                                    {
                                        victim.Kill();
                                    }
                                }
                                catch { }
                            }
                            cmd = null;
                            pid = 0;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>浏览器是否由本应用启动（用于判断退出时要不要清理）。</summary>
        public bool OwnsBrowser { get { return _edgeProc != null; } }

        /// <summary>登录完成后把窗口收起来（不杀进程，引擎还要用）。</summary>
        public void HideWindow()
        {
            try
            {
                if (_edgeProc != null && !_edgeProc.HasExited)
                {
                    var h = _edgeProc.MainWindowHandle;
                    if (h != IntPtr.Zero) ShowWindow(h, 6 /* SW_MINIMIZE */);
                }
            }
            catch { }
        }

        /// <summary>把浏览器窗口叫出来（用户需要重新登录时）。</summary>
        public void ShowWindow()
        {
            try
            {
                if (_edgeProc != null && !_edgeProc.HasExited)
                {
                    var h = _edgeProc.MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        ShowWindow(h, 9 /* SW_RESTORE */);
                        SetForegroundWindow(h);
                    }
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
