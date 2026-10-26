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

        // 主动关闭标志。
        //
        // 为什么必须有：ReadStdout 在 stdout 关闭后会无条件 RaiseFatal("引擎进程已退出")。
        // 于是【每次正常退出应用】都会触发一次"● 引擎异常"——Shutdown() 发完 quit
        // 之后 host 正常退出，stdout 关闭，就被当成崩溃。实测在界面上的表现是：
        // 状态条写着"尚未配置（请添加文件夹）"，下面的进度条却写着
        // "已停止（引擎异常）"，两条互相矛盾，而且那句告警是假的。
        private volatile bool _closing = false;

        // stderr 落盘：debug 级别的引擎原始日志不进 UI（会刷屏），但绝不能丢——
        // 崩溃时的最后几行几乎总是唯一线索。OnFatal 时把末尾若干行附在错误里。
        private readonly object _stderrSync = new object();
        private string _stderrFile;
        private StreamWriter _stderrWriter;
        private const long StderrFileMaxBytes = 4L * 1024 * 1024;

        // UI 回调（在后台线程触发，订阅者需自行 Invoke 到 UI 线程）
        public event Action<string, string, string> OnLog;        // level, msg, time
        public event Action<Dictionary<string, object>> OnRunComplete;
        public event Action<Dictionary<string, object>> OnProgress;  // 进度事件（字段见 plan WP1）
        public event Action<string> OnProtocolError;              // host.js 收到无法解析的行
        public event Action<string> OnFatal;                      // stderr 或进程退出

        public bool IsReady { get { return _ready; } }

        /// <summary>启动 node 宿主。nodeExe 与 hostScript 由调用方给出（安装目录定位）。</summary>
        public void Start(string nodeExe, string hostScript, string dataDir)
        {
            _stderrFile = Path.Combine(dataDir, "engine-stderr.log");
            PrepareStderrFile();

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
            // 重新启动时清掉关闭标志，否则新进程真的崩了也不会报异常。
            _closing = false;
            _ready = false;
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
            // 主动 Shutdown 导致的退出不是异常。不判断 _closing 的话，
            // 每次正常关闭应用都会在界面上留一条假的"引擎异常"告警。
            if (!_closing) RaiseFatal("引擎进程已退出");
        }

        private void ReadStderr()
        {
            try
            {
                string line;
                while ((line = _proc.StandardError.ReadLine()) != null)
                {
                    // 原始行先落盘，再转发给 UI。UI 侧会过滤掉 debug，
                    // 所以"UI 看不到"与"查不到"必须是两件事。
                    WriteStderrLine(line);
                    // stderr 是引擎的原始日志，转发给 UI 但不参与协议
                    var h = OnLog;
                    if (h != null) h("debug", line, DateTime.Now.ToString("o"));
                }
            }
            catch { }
        }

        // ---------------- stderr 落盘（错误分类用） ----------------

        // 每次启动轮转：上一次会话的 engine-stderr.log 改名为 .prev，
        // 这样"这次为什么崩"与"上次为什么崩"都能查，又不至于无限增长。
        private void PrepareStderrFile()
        {
            try
            {
                if (_stderrFile == null) return;
                lock (_stderrSync)
                {
                    if (_stderrWriter != null) { try { _stderrWriter.Flush(); _stderrWriter.Close(); } catch { } _stderrWriter = null; }
                    try
                    {
                        if (File.Exists(_stderrFile))
                        {
                            var prev = _stderrFile + ".prev";
                            try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                            File.Move(_stderrFile, prev);
                        }
                    }
                    catch { }
                    var dir = Path.GetDirectoryName(_stderrFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _stderrWriter = new StreamWriter(new FileStream(_stderrFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    _stderrWriter.AutoFlush = true;
                }
            }
            catch { }
        }

        /// <summary>把一行 stderr 追加进 engine-stderr.log（超过上限则截断重开）。</summary>
        private void WriteStderrLine(string line)
        {
            if (line == null) return;
            try
            {
                lock (_stderrSync)
                {
                    if (_stderrWriter == null) return;
                    _stderrWriter.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
                    if (_stderrWriter.BaseStream.Length > StderrFileMaxBytes)
                    {
                        // 太大就重开（不保留旧的：最新的一定是排障最需要的）
                        try { _stderrWriter.Flush(); _stderrWriter.Close(); } catch { }
                        _stderrWriter = null;
                        try
                        {
                            File.Delete(_stderrFile);
                            _stderrWriter = new StreamWriter(new FileStream(_stderrFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                            _stderrWriter.AutoFlush = true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>读 engine-stderr.log 的最后 n 行（文件不存在或读失败返回空数组）。</summary>
        public string[] ReadStderrTail(int n)
        {
            var list = new List<string>();
            if (n <= 0) return list.ToArray();
            try
            {
                lock (_stderrSync)
                {
                    if (_stderrWriter != null) { try { _stderrWriter.Flush(); } catch { } }
                    if (_stderrFile == null || !File.Exists(_stderrFile)) return list.ToArray();
                    // 共享读：引擎还在写的时候也要能读
                    using (var fs = new FileStream(_stderrFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string l;
                        while ((l = sr.ReadLine()) != null)
                        {
                            list.Add(l);
                            if (list.Count > n * 4) list.RemoveRange(0, list.Count - n * 2);
                        }
                    }
                }
            }
            catch { }
            if (list.Count > n) list.RemoveRange(0, list.Count - n);
            return list.ToArray();
        }

        private void CloseStderrFile()
        {
            try
            {
                lock (_stderrSync)
                {
                    if (_stderrWriter != null) { try { _stderrWriter.Flush(); _stderrWriter.Close(); } catch { } _stderrWriter = null; }
                }
            }
            catch { }
        }

        /// <summary>
        /// 生成带 stderr 尾部的错误详情：最近 100 行原始 stderr 附在消息后面。
        /// 只报"引擎进程已退出"对排障毫无价值，必须带上它退出前说的话。
        /// </summary>
        public string BuildFatalDetail(string msg, int tailLines)
        {
            var tail = ReadStderrTail(tailLines <= 0 ? 100 : tailLines);
            if (tail.Length == 0) return msg;
            var sb = new StringBuilder();
            sb.Append(msg);
            sb.Append(Environment.NewLine);
            sb.Append("---- engine-stderr.log 最后 " + tail.Length + " 行 ----");
            sb.Append(Environment.NewLine);
            sb.Append("文件: " + (_stderrFile == null ? "(未知)" : _stderrFile));
            for (int i = 0; i < tail.Length; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append(tail[i]);
            }
            return sb.ToString();
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
                // 进度：高频事件，只抛给 UI 更新控件，不写日志（否则日志区被刷屏）
                if (ev == "progress" && data != null)
                {
                    var h = OnProgress;
                    if (h != null) h(data);
                    return;
                }
                // host.js 收到无法解析的行时上报；之前 C# 端没有任何分支，
                // 于是协议损坏是完全静默的——界面看起来"一切正常但不动"。
                if (ev == "protocol-error")
                {
                    string raw = data != null ? Str(data, "raw") : "";
                    if (raw.Length == 0) raw = Trunc(line, 200);
                    // host.js 还会给 reason（为什么坏）：带上它，否则用户只看到
                    // "有一行说不清"，却不知道是 JSON 语法错还是结构不是对象。
                    string reason = data != null ? Str(data, "reason") : "";
                    string msg = raw;
                    if (reason.Length > 0) msg = raw + "  (原因: " + reason + ")";
                    var h = OnProtocolError;
                    if (h != null) h(msg);
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
            // 必须先置位再发 quit：读线程可能在 quit 之后立刻看到 EOF，
            // 晚一步就会先抛出"引擎进程已退出"再被这里覆盖，信号已经发给 UI 了。
            _closing = true;
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
            CloseStderrFile();
        }
    }
}
