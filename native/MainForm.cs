// MainForm.cs -- 原生 WinForms 主窗口（不是浏览器，是真 Windows 控件）
//
// 界面结构（简洁单窗口 + 托盘）：
//   顶部：状态条（运行状态 / 文件夹数 / 已上传 / 失败 / 远程目录）
//   中部：监视文件夹列表（ListView）+ 增删按钮
//   下部：日志（只读 TextBox，滚动）
//   底部：操作按钮（立即同步 / 开始 / 停止 / 保存配置 / 打开日志）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace ThunderPanSync
{
    public class MainForm : Form
    {
        private readonly EngineHost _host = new EngineHost();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly string _dataDir;
        private readonly BrowserManager _browser;

        // 控件
        private TabControl _tabs;
        private ListView _lvDirs;
        private Button _btnAddDir, _btnDelDir;
        private TextBox _txtLog;
        private ListView _lvFail;
        private Button _btnSync, _btnStart, _btnStop, _btnSave, _btnOpenLog, _btnLogin;
        private TextBox _txtRemoteName, _txtInterval, _txtSettle, _txtMaxSize;
        private CheckBox _chkAutoStart;
        private Label _lblState, _lblDirs, _lblUploaded, _lblFailed, _lblRemote;
        private NotifyIcon _tray;
        private bool _reallyQuit = false;
        private volatile bool _loggedIn = false;
        // 引擎是否真的在跑自动备份。托盘气泡与状态条都要依据它说话，
        // 否则会出现"界面说在备份、实际没传"的骗人缺陷。
        private volatile bool _engineRunning = false;

        public MainForm(string dataDir)
        {
            _dataDir = dataDir;
            _browser = new BrowserManager(dataDir, delegate(string m) { AppendLog(m); });
            BuildUi();
            WireEngine();

            // 启动引擎（后台）
            var t = new Thread(StartEngine);
            t.IsBackground = true;
            t.Start();
        }

        // ---------------- UI 构建 ----------------
        private void BuildUi()
        {
            Text = "ThunderPanSync · 迅雷云盘自动备份";
            Width = 1000;
            Height = 720;
            MinimumSize = new Size(820, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(38, 40, 44);
            ForeColor = Color.Gainsboro;

            // --- 状态条 ---
            var status = new Panel();
            status.Dock = DockStyle.Top;
            status.Height = 44;
            status.BackColor = Color.FromArgb(30, 32, 36);
            status.Padding = new Padding(12, 8, 12, 8);
            Controls.Add(status);

            _lblState = MakeStatusLabel("● 启动中", Color.Gold);
            _lblDirs = MakeStatusLabel("文件夹 0", Color.Gainsboro);
            _lblUploaded = MakeStatusLabel("已上传 0", Color.LightGreen);
            _lblFailed = MakeStatusLabel("失败 0", Color.Salmon);
            _lblRemote = MakeStatusLabel("远程 待创建", Color.Gainsboro);

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.Controls.Add(_lblState);
            flow.Controls.Add(Spacer(18));
            flow.Controls.Add(_lblDirs);
            flow.Controls.Add(Spacer(18));
            flow.Controls.Add(_lblUploaded);
            flow.Controls.Add(Spacer(18));
            flow.Controls.Add(_lblFailed);
            flow.Controls.Add(Spacer(18));
            flow.Controls.Add(_lblRemote);
            status.Controls.Add(flow);

            // --- 标签页 ---
            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            Controls.Add(_tabs);
            _tabs.BringToFront();

            BuildMainTab();
            BuildSettingsTab();

            // --- 底部按钮条 ---
            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.BackColor = Color.FromArgb(30, 32, 36);
            bottom.Padding = new Padding(12, 10, 12, 10);
            Controls.Add(bottom);

            _btnSync = MakeButton("立即同步一次", 130);
            _btnStart = MakeButton("开始自动备份", 130);
            _btnStop = MakeButton("停止", 80);
            _btnLogin = MakeButton("需要登录迅雷云盘", 150);
            _btnOpenLog = MakeButton("打开日志文件", 120);

            var bflow = new FlowLayoutPanel();
            bflow.Dock = DockStyle.Fill;
            bflow.FlowDirection = FlowDirection.LeftToRight;
            bflow.WrapContents = false;
            bflow.Controls.Add(_btnSync);
            bflow.Controls.Add(_btnStart);
            bflow.Controls.Add(_btnStop);
            bflow.Controls.Add(_btnLogin);
            bflow.Controls.Add(_btnOpenLog);
            bottom.Controls.Add(bflow);

            _btnSync.Click += delegate { DoSync(); };
            _btnStart.Click += delegate { DoSimple("start", "自动备份已启动"); };
            _btnStop.Click += delegate { DoSimple("stop", "自动备份已停止"); };
            _btnLogin.Click += delegate { DoLogin(false); };
            _btnOpenLog.Click += delegate
            {
                var f = Path.Combine(_dataDir, "sync.log");
                if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\"");
                else MessageBox.Show("还没有日志文件。", "ThunderPanSync");
            };

            // --- 托盘 ---
            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "ThunderPanSync";
            _tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
            menu.Items.Add("立即同步一次", null, delegate { DoSync(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { QuitReally(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowFromTray(); };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                // 点 X = 最小化到托盘，不退出（备份继续跑）
                if (!_reallyQuit)
                {
                    e.Cancel = true;
                    Hide();
                    // 这句必须说实话：只有在真正备份时才说"备份仍在运行"。
                    // 之前无条件说这句，而引擎其实从没启动过 —— 评审抓出的"骗人"缺陷。
                    if (_engineRunning)
                        _tray.ShowBalloonTip(2000, "ThunderPanSync", "已最小化到托盘，备份仍在后台运行", ToolTipIcon.Info);
                    else
                        _tray.ShowBalloonTip(4000, "ThunderPanSync",
                            "已最小化到托盘。注意：当前没有在备份，请点「开始自动备份」", ToolTipIcon.Warning);
                }
            };
        }

        private void BuildMainTab()
        {
            var page = new TabPage("备份");
            page.BackColor = Color.FromArgb(38, 40, 44);
            _tabs.TabPages.Add(page);

            // 文件夹列表
            var lbl = new Label();
            lbl.Text = "监视的文件夹（里面的文件会自动备份到迅雷云盘）：";
            lbl.Dock = DockStyle.Top;
            lbl.Height = 26;
            lbl.ForeColor = Color.Gainsboro;
            page.Controls.Add(lbl);

            _lvDirs = new ListView();
            _lvDirs.Dock = DockStyle.Top;
            _lvDirs.Height = 150;
            _lvDirs.View = View.Details;
            _lvDirs.FullRowSelect = true;
            _lvDirs.BackColor = Color.FromArgb(28, 30, 34);
            _lvDirs.ForeColor = Color.Gainsboro;
            _lvDirs.Columns.Add("文件夹路径", 700);
            page.Controls.Add(_lvDirs);

            var dirBtns = new Panel();
            dirBtns.Dock = DockStyle.Top;
            dirBtns.Height = 42;
            dirBtns.Padding = new Padding(0, 6, 0, 6);
            page.Controls.Add(dirBtns);

            _btnAddDir = MakeButton("添加文件夹…", 120);
            _btnDelDir = MakeButton("移除选中", 100);
            _btnAddDir.Location = new Point(0, 6);
            _btnDelDir.Location = new Point(128, 6);
            dirBtns.Controls.Add(_btnAddDir);
            dirBtns.Controls.Add(_btnDelDir);

            _btnAddDir.Click += delegate { AddDir(); };
            _btnDelDir.Click += delegate { DelDir(); };

            // 日志 + 失败列表（左右分栏）
            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 260;
            split.BackColor = Color.FromArgb(50, 54, 60);
            page.Controls.Add(split);

            var logPanel = new Panel();
            logPanel.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(logPanel);

            var lblLog = new Label();
            lblLog.Text = "运行日志：";
            lblLog.Dock = DockStyle.Top;
            lblLog.Height = 24;
            lblLog.ForeColor = Color.Gainsboro;
            logPanel.Controls.Add(lblLog);

            _txtLog = new TextBox();
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(22, 24, 28);
            _txtLog.ForeColor = Color.FromArgb(180, 200, 220);
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.BorderStyle = BorderStyle.None;
            logPanel.Controls.Add(_txtLog);
            _txtLog.BringToFront();
            lblLog.BringToFront();

            // 失败文件列表：引擎早就通过 status 返回了 failedFiles（含原因），
            // 但原界面只把 failedTotal 显示成一个数字，用户永远看不到"哪个文件、为什么失败"。
            var failPanel = new Panel();
            failPanel.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(failPanel);

            var lblFail = new Label();
            lblFail.Text = "失败的文件（原因在这里，修好后点「立即同步一次」会重试）：";
            lblFail.Dock = DockStyle.Top;
            lblFail.Height = 24;
            lblFail.ForeColor = Color.Gainsboro;
            failPanel.Controls.Add(lblFail);

            _lvFail = new ListView();
            _lvFail.Dock = DockStyle.Fill;
            _lvFail.View = View.Details;
            _lvFail.FullRowSelect = true;
            _lvFail.GridLines = false;
            _lvFail.BackColor = Color.FromArgb(28, 30, 34);
            _lvFail.ForeColor = Color.Gainsboro;
            _lvFail.Columns.Add("文件", 240);
            _lvFail.Columns.Add("次数", 50);
            _lvFail.Columns.Add("原因", 420);
            failPanel.Controls.Add(_lvFail);
            _lvFail.BringToFront();
            lblFail.BringToFront();

            // 让上面的 Dock=Top 控件按顺序占位
            dirBtns.BringToFront();
            _lvDirs.BringToFront();
            lbl.BringToFront();
        }

        private void BuildSettingsTab()
        {
            var page = new TabPage("设置");
            page.BackColor = Color.FromArgb(38, 40, 44);
            _tabs.TabPages.Add(page);

            var y = 20;
            page.Controls.Add(MakeLabel("远程文件夹名（云盘里的目录名）：", 20, y)); y += 24;
            _txtRemoteName = MakeInput(20, y, 300); page.Controls.Add(_txtRemoteName); y += 40;

            page.Controls.Add(MakeLabel("检查间隔（秒，最小 10）：", 20, y)); y += 24;
            _txtInterval = MakeInput(20, y, 120); page.Controls.Add(_txtInterval); y += 40;

            page.Controls.Add(MakeLabel("文件稳定判定（秒，避免传半截文件）：", 20, y)); y += 24;
            _txtSettle = MakeInput(20, y, 120); page.Controls.Add(_txtSettle); y += 40;

            page.Controls.Add(MakeLabel("单文件大小上限（MB）：", 20, y)); y += 24;
            _txtMaxSize = MakeInput(20, y, 120); page.Controls.Add(_txtMaxSize); y += 40;

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机时自动启动";
            _chkAutoStart.Location = new Point(20, y);
            _chkAutoStart.Width = 300;
            _chkAutoStart.ForeColor = Color.Gainsboro;
            page.Controls.Add(_chkAutoStart); y += 40;

            _btnSave = MakeButton("保存设置", 120);
            _btnSave.Location = new Point(20, y);
            page.Controls.Add(_btnSave);
            _btnSave.Click += delegate { SaveSettings(); };

            var hint = MakeLabel("备份不会删除本地文件。日志与配置存放在：" + _dataDir, 20, y + 50);
            hint.Width = 900;
            hint.ForeColor = Color.Gray;
            page.Controls.Add(hint);
        }

        // ---------------- 小工具 ----------------
        private Label MakeStatusLabel(string text, Color c)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = c;
            l.Margin = new Padding(0, 6, 0, 0);
            l.Font = new Font("Microsoft YaHei UI", 9.5F);
            return l;
        }
        private Control Spacer(int w)
        {
            var p = new Panel();
            p.Width = w;
            p.Height = 1;
            return p;
        }
        private Button MakeButton(string text, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Width = w;
            b.Height = 30;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(52, 56, 62);
            b.ForeColor = Color.Gainsboro;
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 82);
            b.Margin = new Padding(0, 0, 8, 0);
            return b;
        }
        private Label MakeLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.ForeColor = Color.Gainsboro;
            return l;
        }
        private TextBox MakeInput(int x, int y, int w)
        {
            var t = new TextBox();
            t.Location = new Point(x, y);
            t.Width = w;
            t.BackColor = Color.FromArgb(28, 30, 34);
            t.ForeColor = Color.Gainsboro;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        // ---------------- 引擎 ----------------
        private void StartEngine()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var node = Path.Combine(baseDir, "runtime", "node.exe");
                var host = Path.Combine(baseDir, "engine", "host.js");

                if (!File.Exists(node)) { UiFatal("找不到 node.exe：" + node); return; }
                if (!File.Exists(host)) { UiFatal("找不到 host.js：" + host); return; }

                _host.Start(node, host, _dataDir);

                // 等 ready（最多 10 秒）
                for (int i = 0; i < 100 && !_host.IsReady; i++) Thread.Sleep(100);

                if (!_host.IsReady) { UiFatal("引擎启动超时"); return; }

                // 不显示"● 就绪"——那会让人以为在备份。
                // 真实状态由 RefreshStatus 根据 running 判定（未在备份时是红色）。
                RefreshStatus();

                // 浏览器与登录态：放在引擎就绪之后做，避免拖慢界面
                InitBrowser();
            }
            catch (Exception ex)
            {
                UiFatal("启动引擎失败：" + ex.Message);
            }
        }

        // ---------------- 浏览器 / 登录 ----------------
        private void InitBrowser()
        {
            var t = new Thread(delegate()
            {
                try
                {
                    if (_browser.IsDebugPortAlive() && _browser.HasLogin())
                    {
                        _loggedIn = true;
                        _browser.HideWindow();
                        AppendLog("浏览器已就绪（用现有登录状态，后台静默运行）");
                        UiInvoke(delegate { UpdateLoginUi(); });
                        return;
                    }

                    if (_browser.IsDebugPortAlive())
                    {
                        // 浏览器在跑但没登录：不主动弹窗，提示用户点按钮
                        AppendLog("浏览器已启动，但还没登录迅雷云盘 —— 请点「需要登录迅雷云盘」");
                        UiInvoke(delegate { UpdateLoginUi(); });
                        return;
                    }

                    // 首次使用：静默启动（最小化），然后提示
                    AppendLog("首次使用：正在后台准备浏览器…");
                    bool ok = _browser.Ensure(false);
                    _loggedIn = ok;
                    if (ok)
                    {
                        _browser.HideWindow();
                        AppendLog("登录状态确认，后台运行中");
                    }
                    else
                    {
                        AppendLog("需要登录一次迅雷云盘（只需一次，之后一直有效）");
                    }
                    UiInvoke(delegate { UpdateLoginUi(); });
                }
                catch (Exception ex)
                {
                    AppendLog("浏览器初始化失败：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoLogin(bool silent)
        {
            var t = new Thread(delegate()
            {
                try
                {
                    if (!_browser.IsDebugPortAlive())
                    {
                        AppendLog("正在启动浏览器…");
                        _browser.Ensure(true);
                    }
                    _browser.ShowWindow();
                    AppendLog("已打开登录窗口，请用迅雷账号登录（扫码或密码均可）");
                    AppendLog("登录成功后本窗口会自动更新状态");

                    // 轮询等登录完成（最多 5 分钟）
                    for (int i = 0; i < 150; i++)
                    {
                        Thread.Sleep(2000);
                        if (_browser.HasLogin())
                        {
                            _loggedIn = true;
                            _browser.HideWindow();
                            AppendLog("✓ 登录成功，浏览器已收起到后台");
                            UiInvoke(delegate { UpdateLoginUi(); });
                            return;
                        }
                    }
                    AppendLog("等待登录超时，可以再点一次按钮");
                }
                catch (Exception ex)
                {
                    AppendLog("登录流程出错：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void UpdateLoginUi()
        {
            // 注意：这里不再改 _lblState。运行状态只能由 running 决定，
            // 登录状态只属于按钮。两者混用会出现"已登录✓ + 绿色就绪"但实际没备份的误导。
            if (_loggedIn)
            {
                _btnLogin.Text = "已登录 ✓";
                _btnLogin.BackColor = Color.FromArgb(52, 56, 62);
                _btnLogin.Enabled = true;   // 仍可点击（重新登录）
            }
            else
            {
                _btnLogin.Text = "需要登录迅雷云盘";
                _btnLogin.BackColor = Color.FromArgb(180, 120, 30);
            }
        }

        private void WireEngine()
        {
            _host.OnLog += delegate(string level, string msg, string time)
            {
                // debug 级别来自 stderr（引擎原始日志），不进 UI，避免重复
                if (level == "debug") return;
                AppendLog(msg);
            };
            _host.OnRunComplete += delegate(Dictionary<string, object> s)
            {
                UiInvoke(delegate { RefreshStatus(); });
            };
            _host.OnFatal += delegate(string m) { UiFatal(m); };
        }

        private void UiFatal(string msg)
        {
            UiInvoke(delegate
            {
                _lblState.Text = "● 引擎异常";
                _lblState.ForeColor = Color.Salmon;
                AppendLog("[错误] " + msg);
            });
        }

        private void UiInvoke(Action a)
        {
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        private void AppendLog(string msg)
        {
            UiInvoke(delegate
            {
                _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
                if (_txtLog.Lines.Length > 800)
                {
                    var keep = new string[_txtLog.Lines.Length - 300];
                    Array.Copy(_txtLog.Lines, 300, keep, 0, keep.Length);
                    _txtLog.Lines = keep;
                }
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            });
        }

        private void RefreshStatus()
        {
            var t = new Thread(delegate()
            {
                var d = _host.CallData("status", null, 8000);
                if (d == null) return;
                UiInvoke(delegate { ApplyStatus(d); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ApplyStatus(Dictionary<string, object> d)
        {
            try
            {
                var cfg = d.ContainsKey("config") ? d["config"] as Dictionary<string, object> : null;
                var st = d.ContainsKey("status") ? d["status"] as Dictionary<string, object> : null;

                if (cfg != null)
                {
                    _txtRemoteName.Text = GetStr(cfg, "remoteFolderName", "ThunderPanSync");
                    _txtInterval.Text = (GetInt(cfg, "intervalMs", 60000) / 1000).ToString();
                    _txtSettle.Text = (GetInt(cfg, "settleMs", 5000) / 1000).ToString();
                    _txtMaxSize.Text = GetInt(cfg, "maxSizeMB", 2048).ToString();

                    // 文件夹列表
                    _lvDirs.Items.Clear();
                    object wd;
                    if (cfg.TryGetValue("watchDirs", out wd) && wd is object[])
                    {
                        foreach (var x in (object[])wd)
                        {
                            var it = new ListViewItem(Convert.ToString(x));
                            _lvDirs.Items.Add(it);
                        }
                    }
                }

                if (st != null)
                {
                    bool running = st.ContainsKey("running") && Convert.ToBoolean(st["running"]);
                    _engineRunning = running;

                    // 状态显示必须诚实。三态而非两态：
                    //   备份中   (绿) —— 引擎在跑
                    //   未在备份 (红) —— 有目录但没跑，这是最危险的静默状态，必须刺眼
                    //   未配置   (黄) —— 还没有监视目录
                    bool hasDirs = _lvDirs.Items.Count > 0;
                    if (running)
                    {
                        _lblState.Text = "● 备份中";
                        _lblState.ForeColor = Color.LightGreen;
                    }
                    else if (hasDirs)
                    {
                        // 用户已经有目录却不跑了：必须明确告诉他"现在没在备份"
                        _lblState.Text = "● 未在备份（点「开始自动备份」）";
                        _lblState.ForeColor = Color.Salmon;
                    }
                    else
                    {
                        _lblState.Text = "● 尚未配置（请添加文件夹）";
                        _lblState.ForeColor = Color.Gold;
                    }

                    int total = 0;
                    object up;
                    if (st.TryGetValue("uploadedTotal", out up)) { try { total = Convert.ToInt32(up); } catch { } }
                    _lblUploaded.Text = "已上传 " + total;

                    int failed = 0;
                    object ff;
                    if (st.TryGetValue("failedTotal", out ff)) { try { failed = Convert.ToInt32(ff); } catch { } }
                    _lblFailed.Text = "失败 " + failed;

                    var rem = GetStr(st, "remoteFolderId", "");
                    _lblRemote.Text = string.IsNullOrEmpty(rem) ? "远程 待创建" : "远程 " + GetStr(st, "remoteFolderName", "");

                    // 渲染失败列表：引擎一直有这些数据，之前界面从不显示，
                    // 用户只看到"失败 3"却不知道是哪三个、为什么。
                    FillFailedList(st);
                }

                _lblDirs.Text = "文件夹 " + _lvDirs.Items.Count;
            }
            catch { }
        }

        // 把引擎返回的 failedFiles 渲染成列表（文件 / 次数 / 原因）
        private void FillFailedList(Dictionary<string, object> st)
        {
            try
            {
                if (st == null || !st.ContainsKey("failedFiles"))
                {
                    if (_lvFail.Items.Count > 0) _lvFail.Items.Clear();
                    return;
                }
                var arr = st["failedFiles"] as object[];
                if (arr == null)
                {
                    if (_lvFail.Items.Count > 0) _lvFail.Items.Clear();
                    return;
                }

                // 避免每次状态刷新都重建（会闪、会丢选中）
                if (_lvFail.Items.Count == arr.Length) return;

                _lvFail.BeginUpdate();
                try
                {
                    _lvFail.Items.Clear();
                    foreach (var o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        var full = GetStr(d, "file", "");
                        var nameOnly = full;
                        int slash = full.LastIndexOf('\\');
                        if (slash >= 0 && slash + 1 < full.Length) nameOnly = full.Substring(slash + 1);
                        if (nameOnly.Length == 0) nameOnly = full;

                        var it = new ListViewItem(nameOnly);
                        it.ToolTipText = full;
                        it.SubItems.Add(GetInt(d, "count", 0).ToString());
                        it.SubItems.Add(GetStr(d, "error", "(无原因)"));
                        _lvFail.Items.Add(it);
                    }
                }
                finally { _lvFail.EndUpdate(); }
            }
            catch { }
        }

        private static string GetStr(Dictionary<string, object> d, string k, string def)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return def;            return Convert.ToString(d[k]);
        }
        private static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return def;
            try { return Convert.ToInt32(d[k]); } catch { return def; }
        }

        // ---------------- 操作 ----------------
        private void DoSync()
        {
            _btnSync.Enabled = false;
            AppendLog("开始同步…");
            var t = new Thread(delegate()
            {
                var d = _host.CallData("runOnce", null, 300000);
                UiInvoke(delegate
                {
                    _btnSync.Enabled = true;
                    if (d == null) { AppendLog("[错误] 同步失败或无响应"); }
                    else
                    {
                        AppendLog("完成：扫描 " + GetInt(d, "scanned", 0) +
                                  "，上传 " + GetInt(d, "uploaded", 0) +
                                  "，失败 " + GetInt(d, "failed", 0) +
                                  "，跳过 " + GetInt(d, "skipped", 0));
                    }
                    RefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoSimple(string cmd, string okMsg)
        {
            var t = new Thread(delegate()
            {
                var d = _host.CallData(cmd, null, 8000);
                UiInvoke(delegate
                {
                    AppendLog(d != null ? okMsg : ("操作失败：" + cmd));
                    RefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AddDir()
        {
            var dlg = new FolderBrowserDialog();
            dlg.Description = "选择要自动备份到迅雷云盘的文件夹";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            foreach (ListViewItem it in _lvDirs.Items)
            {
                if (string.Equals(it.Text, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)) return;
            }
            _lvDirs.Items.Add(new ListViewItem(dlg.SelectedPath));
            _lblDirs.Text = "文件夹 " + _lvDirs.Items.Count;
            SaveSettingsSilent();
        }

        private void DelDir()
        {
            if (_lvDirs.SelectedItems.Count == 0) return;
            foreach (ListViewItem it in _lvDirs.SelectedItems) _lvDirs.Items.Remove(it);
            _lblDirs.Text = "文件夹 " + _lvDirs.Items.Count;
            SaveSettingsSilent();
        }

        private Dictionary<string, object> CollectSettings()
        {
            var dirs = new List<string>();
            foreach (ListViewItem it in _lvDirs.Items) dirs.Add(it.Text);

            var cfg = new Dictionary<string, object>();
            cfg["watchDirs"] = dirs.ToArray();
            cfg["remoteFolderName"] = string.IsNullOrEmpty(_txtRemoteName.Text.Trim()) ? "ThunderPanSync" : _txtRemoteName.Text.Trim();
            int iv = 60, st = 5, mx = 2048;
            try { iv = Math.Max(10, int.Parse(_txtInterval.Text)); } catch { }
            try { st = Math.Max(0, int.Parse(_txtSettle.Text)); } catch { }
            try { mx = Math.Max(1, int.Parse(_txtMaxSize.Text)); } catch { }
            cfg["intervalMs"] = iv * 1000;
            cfg["settleMs"] = st * 1000;
            cfg["maxSizeMB"] = mx;
            return cfg;
        }

        private void SaveSettingsSilent()
        {
            var cfg = CollectSettings();
            var t = new Thread(delegate()
            {
                var p = new Dictionary<string, object>();
                p["params"] = cfg;
                // host.js 的 saveConfig 期望 params 直接是配置对象
                _host.CallData("saveConfig", cfg, 8000);
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SaveSettings()
        {
            SaveSettingsSilent();
            ApplyAutoStart();
            AppendLog("设置已保存");
            RefreshStatus();
        }

        private void ApplyAutoStart()
        {
            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (_chkAutoStart.Checked)
                    key.SetValue("ThunderPanSync", "\"" + Application.ExecutablePath + "\" --autostart");
                else if (key.GetValue("ThunderPanSync") != null)
                    key.DeleteValue("ThunderPanSync", false);
                key.Close();
            }
            catch (Exception ex) { AppendLog("设置开机自启失败：" + ex.Message); }
        }

        private void LoadAutoStart()
        {
            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false);
                if (key != null)
                {
                    _chkAutoStart.Checked = key.GetValue("ThunderPanSync") != null;
                    key.Close();
                }
            }
            catch { }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        public void QuitReally()
        {
            _reallyQuit = true;
            try { _host.Shutdown(); } catch { }
            // 关键：收掉我们自己启动的浏览器。
            // 不清理的后果（已实测）：应用退出后仍留下带 9222 调试端口的 Edge，
            // 该端口无认证，任何本机程序都能接管这个已登录会话。
            try { _browser.ShutdownBrowser(); } catch { }
            if (_tray != null) { _tray.Visible = false; }
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadAutoStart();
            // 首次启动若配置里有文件夹，自动刷新一次
            RefreshStatus();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // --autostart 时直接进托盘（不弹窗）
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a == "--autostart") { WindowState = FormWindowState.Minimized; Hide(); break; }
            }
        }
    }
}
