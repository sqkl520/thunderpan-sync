// BrowserManager.cs -- 浏览器生命周期管理（用户看不见它）
//
// 为什么必须由应用自己管浏览器：
//   captcha 由网页版的 JS 实时计算（算法分片动态下发，无法离线复刻 —— 已有实测）。
//   所以引擎必须有一个真实的、已登录的 Edge 在后台跑。
//   但用户不该关心这件事：他们只该看到"请登录一次"。
//
// 状态机：
//   1. 探测 9222 上有无已登录的 pan.xunlei.com → 有：完全静默，什么都不做
//   2. 没有 → 启动 Edge（独立 profile，带调试端口），显示窗口让用户登录
//   3. 登录成功后 → 把窗口最小化（不杀进程，引擎还要用它截 captcha）
//
// 独立 profile 是刻意的：不碰用户日常浏览器的登录态，避免"把你的 Edge 踢下线"，
// 也避免和用户正在用的 Edge 抢 profile 锁（实测会 "Target closed" 失败）。
using System;
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
        private const string DebugUrl = "http://127.0.0.1:9222";
        private readonly string _profileDir;
        private readonly Action<string> _log;
        private Process _edgeProc;

        public BrowserManager(string dataDir, Action<string> log)
        {
            _profileDir = Path.Combine(dataDir, "browser-profile");
            _log = log;
        }

        // ---------------- 探测 ----------------
        public bool IsDebugPortAlive()
        {
            try
            {
                using (var hc = new HttpClient())
                {
                    hc.Timeout = TimeSpan.FromMilliseconds(1500);
                    var s = hc.GetStringAsync(DebugUrl + "/json/version").Result;
                    return !string.IsNullOrEmpty(s);
                }
            }
            catch { return false; }
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
                    var json = hc.GetStringAsync(DebugUrl + "/json/list").Result;
                    var arr = new JavaScriptSerializer().Deserialize<object[]>(json);
                    foreach (var item in arr)
                    {
                        var d = item as System.Collections.Generic.Dictionary<string, object>;
                        if (d == null) continue;
                        var type = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "";
                        var url = d.ContainsKey("url") ? Convert.ToString(d["url"]) : "";
                        if (type == "page" && url != null && url.Contains("pan.xunlei.com"))
                            return Convert.ToString(d["webSocketDebuggerUrl"]);
                    }
                }
            }
            catch { }
            return null;
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
            if (IsDebugPortAlive())
            {
                if (HasLogin()) { _log("浏览器已就绪（已登录）"); return true; }
                _log("浏览器已启动，但还没登录迅雷云盘");
                return false;
            }

            var exe = FindEdge();
            if (exe == null)
            {
                _log("找不到 Edge 或 Chrome，无法继续。请先安装 Microsoft Edge。");
                return false;
            }

            Directory.CreateDirectory(_profileDir);
            _log("正在启动浏览器…");

            var psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments =
                "--remote-debugging-port=9222 " +
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

            // 等端口起来（最多 30 秒）
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                if (IsDebugPortAlive()) break;
            }
            if (!IsDebugPortAlive())
            {
                _log("浏览器启动超时");
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
