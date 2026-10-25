// EngineHost.cs -- C# 侧的引擎通道（JSON-RPC over stdio）
//
// C# 与 Node 的边界就这一个文件。设计上刻意做得很薄：
//   - 启动 node host.js 子进程，RedirectStandardInput/Output
//   - 主线程只发请求、收响应；事件（日志）通过回调抛到 UI 线程
//   - 子进程随本进程退出（Kill 掉），不会留孤儿进程
//
// C# 5 语法限制（系统 csc 只支持到 C#5）：不能用插值字符串、?. 、元组、out var。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ThunderPanSync
{
    public class EngineHost
    {
        private Process _proc;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _sync = new object();
        private int _nextId = 0;
        private readonly Dictionary<int, ManualResetEventSlim> _waiters = new Dictionary<int, ManualResetEventSlim>();
        private readonly Dictionary<int, string> _results = new Dictionary<int, string>();
        private volatile bool _ready = false;

        // UI 回调（在后台线程触发，订阅者需自行 Invoke 到 UI 线程）
        public event Action<string, string, string> OnLog;        // level, msg, time
        public event Action<Dictionary<string, object>> OnRunComplete;
        public event Action<string> OnFatal;                      // stderr 或进程退出

        public bool IsReady { get { return _ready; } }

        /// <summary>启动 node 宿主。nodeExe 与 hostScript 由调用方给出（安装目录定位）。</summary>
        public void Start(string nodeExe, string hostScript, string dataDir)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = nodeExe;
            psi.Arguments = "\"" + hostScript + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = Path.GetDirectoryName(hostScript);

            // 数据目录通过环境变量传给宿主（安装目录只读，数据必须落到 %APPDATA%）
            psi.EnvironmentVariables["PANSYNC_DATA_DIR"] = dataDir;

            _proc = new Process();
            _proc.StartInfo = psi;
            _proc.Start();

            var tOut = new Thread(ReadStdout);
            tOut.IsBackground = true;
            tOut.Start();

            var tErr = new Thread(ReadStderr);
            tErr.IsBackground = true;
            tErr.Start();
        }

        private void ReadStdout()
        {
            try
            {
                string line;
                while ((line = _proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try { Dispatch(line); }
                    catch (Exception ex) { RaiseFatal("解析失败: " + ex.Message + " | " + Trunc(line, 200)); }
                }
            }
            catch (Exception ex) { RaiseFatal("stdout 读失败: " + ex.Message); }
            RaiseFatal("引擎进程已退出");
        }

        private void ReadStderr()
        {
            try
            {
                string line;
                while ((line = _proc.StandardError.ReadLine()) != null)
                {
                    // stderr 是引擎的原始日志，转发给 UI 但不参与协议
                    var h = OnLog;
                    if (h != null) h("debug", line, DateTime.Now.ToString("o"));
                }
            }
            catch { }
        }

        private void Dispatch(string line)
        {
            var o = _json.Deserialize<Dictionary<string, object>>(line);
            if (o == null) return;

            // 事件（无 id）
            if (o.ContainsKey("event"))
            {
                var ev = Convert.ToString(o["event"]);
                var data = o.ContainsKey("data") ? o["data"] as Dictionary<string, object> : null;

                if (ev == "ready") { _ready = true; return; }

                if (ev == "log" && data != null)
                {
                    var h = OnLog;
                    if (h != null)
                    {
                        h(Str(data, "level"), Str(data, "msg"), Str(data, "time"));
                    }
                    return;
                }
                if (ev == "run-complete" && data != null)
                {
                    var h = OnRunComplete;
                    if (h != null) h(data);
                    return;
                }
                return;
            }

            // 响应（有 id）
            if (o.ContainsKey("id"))
            {
                int id = 0;
                try { id = Convert.ToInt32(o["id"]); } catch { return; }
                lock (_sync)
                {
                    _results[id] = line;
                    ManualResetEventSlim ev2;
                    if (_waiters.TryGetValue(id, out ev2)) ev2.Set();
                }
            }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return "";
            return Convert.ToString(d[key]);
        }

        private static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        private void RaiseFatal(string msg)
        {
            var h = OnFatal;
            if (h != null) h(msg);
        }

        /// <summary>同步调用一条命令，返回原始 JSON 响应行（超时返回 null）。</summary>
        public string Call(string cmd, Dictionary<string, object> prms, int timeoutMs)
        {
            int id;
            var ev = new ManualResetEventSlim(false);
            lock (_sync)
            {
                id = ++_nextId;
                _waiters[id] = ev;
            }

            var req = new Dictionary<string, object>();
            req["id"] = id;
            req["cmd"] = cmd;
            if (prms != null) req["params"] = prms;

            try
            {
                _proc.StandardInput.WriteLine(_json.Serialize(req));
                _proc.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                lock (_sync) { _waiters.Remove(id); }
                RaiseFatal("写入引擎失败: " + ex.Message);
                return null;
            }

            bool got = ev.Wait(timeoutMs);
            lock (_sync)
            {
                _waiters.Remove(id);
                string raw;
                if (got && _results.TryGetValue(id, out raw))
                {
                    _results.Remove(id);
                    return raw;
                }
            }
            return null;
        }

        /// <summary>调用并把 data 解成字典；失败返回 null。</summary>
        public Dictionary<string, object> CallData(string cmd, Dictionary<string, object> prms, int timeoutMs)
        {
            var raw = Call(cmd, prms, timeoutMs);
            if (raw == null) return null;
            try
            {
                var o = _json.Deserialize<Dictionary<string, object>>(raw);
                if (o == null) return null;
                bool ok = o.ContainsKey("ok") && Convert.ToBoolean(o["ok"]);
                if (!ok) return null;
                return o.ContainsKey("data") ? o["data"] as Dictionary<string, object> : null;
            }
            catch { return null; }
        }

        public void Shutdown()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    try
                    {
                        _proc.StandardInput.WriteLine("{\"id\":99999,\"cmd\":\"quit\"}");
                        _proc.StandardInput.Flush();
                    }
                    catch { }
                    if (!_proc.WaitForExit(2000))
                    {
                        try { _proc.Kill(); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
