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
using System.Text;
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
        // 进度区（状态条下方）：ProgressBar + "第 x/y · 文件名 · 速度 · 剩余"
        private ProgressBar _progress;
        private Label _lblProgress, _lblProtocolErr;
        private Panel _progressPanel;
        private NotifyIcon _tray;
        private bool _reallyQuit = false;
        private volatile bool _loggedIn = false;
        // 引擎是否真的在跑自动备份。托盘气泡与状态条都要依据它说话，
        // 否则会出现"界面说在备份、实际没传"的骗人缺陷。
        private volatile bool _engineRunning = false;
        // 引擎 status 里给出的当天日志路径（日志按天分文件，名字每天变）
        private volatile string _currentLogFile = null;
        // 协议错误计数：界面必须能看见"协议坏了"，否则引擎静默不动而日志一片祥和
        private int _protocolErrCount = 0;
        // ui-log-*.log 的追加锁（AppendLog 在 UI 线程，但转存也可能被别处触发）
        private readonly object _uiLogSync = new object();
        // 用户拒绝 EULA：构造函数提前返回，窗口一显示就关掉，Application.Run 随之结束
        private bool _eulaDeclined = false;

        /// <summary>是否因用户拒绝 EULA 而放弃启动（Program 可据此跳过后续初始化）。</summary>
        public bool EulaDeclined { get { return _eulaDeclined; } }

        public MainForm(string dataDir)
        {
            _dataDir = dataDir;

            // 首次启动必须先过 EULA。放在最前面：用户不同意就绝不启动引擎、
            // 不建界面、不碰浏览器 —— 不能"先跑起来再问"。
            if (!EnsureEulaAccepted(dataDir))
            {
                _eulaDeclined = true;
                return;
            }

            _browser = new BrowserManager(dataDir, delegate(string m) { AppendLog(m); });
            BuildUi();
            WireEngine();

            // 启动引擎（后台）
            var t = new Thread(StartEngine);
            t.IsBackground = true;
            t.Start();
        }

        // ---------------- EULA（首次启动必须明示风险） ----------------
        // 为什么必须弹这个：本工具做的事情不是"官方支持的用法"，
        // 拿 CDP 去截网页版的 captcha/token 有可能被判定为违规。
        // 沉默地替用户承担这个风险是不诚实的，所以首次运行必须显式确认。
        public const string EulaFileName = "eula-accepted";

        public static string EulaText(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir))
                dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThunderPanSync");
            var sb = new StringBuilder();
            sb.AppendLine("在使用 ThunderPanSync 之前，请如实读完以下内容。");
            sb.AppendLine();
            sb.AppendLine("1) 工作原理与协议风险");
            sb.AppendLine("   本工具不是迅雷官方软件。它会在本机后台运行一个浏览器，");
            sb.AppendLine("   通过 Chrome DevTools Protocol（CDP）读取迅雷网页版页面里的");
            sb.AppendLine("   captcha 计算结果与登录 token（sessionid 等），再用这些凭证");
            sb.AppendLine("   调用迅雷云盘的网页接口上传文件。");
            sb.AppendLine("   这种自动化方式不在迅雷官方支持范围内，可能违反《迅雷用户协议》。");
            sb.AppendLine();
            sb.AppendLine("2) 账号风险");
            sb.AppendLine("   因为存在上述非官方自动化行为，你的迅雷账号存在被限速、");
            sb.AppendLine("   被限制功能、甚至被封禁的可能。这个风险由你自行承担。");
            sb.AppendLine();
            sb.AppendLine("3) 没有恢复功能（重要）");
            sb.AppendLine("   本工具只做「本地上传到云盘」这一个方向。");
            sb.AppendLine("   它没有下载/恢复功能，不能把云盘上的文件取回本地。");
            sb.AppendLine("   云盘不是备份的唯一副本，请务必保留你自己的本地原文件。");
            sb.AppendLine();
            sb.AppendLine("4) 本机安全风险：调试端口无认证");
            sb.AppendLine("   本工具会在本机启动一个带远程调试端口的浏览器（独立 profile），");
            sb.AppendLine("   默认端口 127.0.0.1:9222；若该端口已被其他程序占用，");
            sb.AppendLine("   会自动改用 9223-9233 中的空闲端口（实际端口记录在数据目录）。");
            sb.AppendLine("   该端口没有认证：任何能在本机运行的程序都可以连上去，");
            sb.AppendLine("   接管这个已登录迅雷账号的浏览器会话。");
            sb.AppendLine("   请勿在不受信任的电脑上使用本工具。");
            sb.AppendLine();
            sb.AppendLine("5) 退出与卸载");
            sb.AppendLine("   退出程序时只会终止「命令行里含本工具 profile 路径」的浏览器进程，");
            sb.AppendLine("   不会影响你自己日常使用的浏览器。");
            sb.AppendLine("   卸载时会询问是否删除配置目录，默认保留（选「否」）；");
            sb.AppendLine("   云盘上已经上传的文件不会因卸载而被删除。");
            sb.AppendLine();
            sb.AppendLine("6) 数据与日志");
            sb.AppendLine("   配置、登录态（独立浏览器 profile）、运行日志都存放在：");
            sb.AppendLine("   " + dataDir);
            sb.AppendLine("   日志按天分文件（sync-YYYY-MM-DD.log）。");
            sb.AppendLine();
            sb.AppendLine("点击「我已了解并承担风险」表示你已阅读、理解并接受以上全部内容。");
            return sb.ToString();
        }

        /// <summary>eula-accepted 不存在则弹模态对话框；返回 false 表示用户选择退出。</summary>
        public static bool EnsureEulaAccepted(string dataDir)
        {
            try
            {
                var mark = Path.Combine(dataDir, EulaFileName);
                if (File.Exists(mark)) return true;
                if (!ShowEulaDialog(dataDir)) return false;
                try
                {
                    Directory.CreateDirectory(dataDir);
                    var enc = new UTF8Encoding(true);   // 带 BOM，记事本打开不乱码
                    File.WriteAllText(mark,
                        "accepted " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " (eula v1)" + Environment.NewLine,
                        enc);
                }
                catch { }   // 写不进去也不能因此拦住用户（下次会再问一遍）
                return true;
            }
            catch
            {
                // fail-closed：EULA 检查本身出错时**不启动**。
                // 这是唯一正确的失败方向。原实现写的是"按已接受处理"（return true），
                // 意味着对话框构造一旦抛异常（字体/资源/权限问题），
                // 程序就会在用户从未看到任何风险披露的情况下直接开始上传 ——
                // 对一个明确披露"可能被封号"的工具，这不能接受。
                return false;
            }
        }

        private static bool ShowEulaDialog(string dataDir)
        {
            using (var f = new Form())
            {
                f.Text = "ThunderPanSync · 首次使用须知（请务必阅读）";
                f.Width = 760;
                f.Height = 620;
                f.MinimumSize = new Size(600, 480);
                f.StartPosition = FormStartPosition.CenterScreen;
                f.BackColor = Color.FromArgb(38, 40, 44);
                f.ForeColor = Color.Gainsboro;
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = true;

                var bottom = new Panel();
                bottom.Dock = DockStyle.Bottom;
                bottom.Height = 56;
                bottom.BackColor = Color.FromArgb(30, 32, 36);
                bottom.Padding = new Padding(12, 12, 12, 12);
                f.Controls.Add(bottom);

                var btnAccept = new Button();
                btnAccept.Text = "我已了解并承担风险";
                btnAccept.Width = 200;
                btnAccept.Height = 32;
                btnAccept.FlatStyle = FlatStyle.Flat;
                btnAccept.BackColor = Color.FromArgb(52, 56, 62);
                btnAccept.ForeColor = Color.Gainsboro;
                btnAccept.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 82);
                btnAccept.DialogResult = DialogResult.OK;
                btnAccept.Location = new Point(12, 12);
                bottom.Controls.Add(btnAccept);

                var btnQuit = new Button();
                btnQuit.Text = "退出";
                btnQuit.Width = 110;
                btnQuit.Height = 32;
                btnQuit.FlatStyle = FlatStyle.Flat;
                btnQuit.BackColor = Color.FromArgb(52, 56, 62);
                btnQuit.ForeColor = Color.Gainsboro;
                btnQuit.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 82);
                btnQuit.DialogResult = DialogResult.Cancel;
                btnQuit.Location = new Point(224, 12);
                bottom.Controls.Add(btnQuit);

                var txt = new TextBox();
                txt.Dock = DockStyle.Fill;
                txt.Multiline = true;
                txt.ReadOnly = true;
                txt.ScrollBars = ScrollBars.Vertical;
                txt.WordWrap = true;
                txt.BackColor = Color.FromArgb(22, 24, 28);
                txt.ForeColor = Color.FromArgb(214, 220, 228);
                txt.Font = new Font("Microsoft YaHei UI", 9.5F);
                txt.BorderStyle = BorderStyle.None;
                txt.Text = EulaText(dataDir);
                // 只读文本框里让内容从顶部开始显示
                txt.SelectionStart = 0;
                txt.SelectionLength = 0;
                f.Controls.Add(txt);

                // 关窗口 = 退出（不能默认当同意）
                f.AcceptButton = btnAccept;
                f.CancelButton = btnQuit;

                return f.ShowDialog() == DialogResult.OK;
            }
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

            // --- 进度条（状态条下方）---
            // WinForms 的 Dock 顺序是"后添加的更靠外"：Dock=Top 的控件，先加的在内侧。
            // 所以进度面板必须在状态条**之前** Add，才会排在状态条下面而不是上面。
            // （这两条都是实测出来的，不是猜的：写反了进度条会跑到窗口最顶端。）
            _progressPanel = new Panel();
            _progressPanel.Dock = DockStyle.Top;
            _progressPanel.Height = 40;
            _progressPanel.BackColor = Color.FromArgb(26, 28, 32);
            _progressPanel.Padding = new Padding(12, 4, 12, 6);
            Controls.Add(_progressPanel);

            _lblProgress = new Label();
            _lblProgress.Dock = DockStyle.Fill;
            _lblProgress.Text = "空闲（同步时显示：第 x/y 个 · 文件名 · 速度 · 剩余）";
            _lblProgress.ForeColor = Color.Gray;
            _lblProgress.Font = new Font("Microsoft YaHei UI", 9F);
            _lblProgress.TextAlign = ContentAlignment.MiddleLeft;
            _progressPanel.Controls.Add(_lblProgress);

            _progress = new ProgressBar();
            _progress.Dock = DockStyle.Bottom;
            _progress.Height = 10;
            _progress.Minimum = 0;
            _progress.Maximum = 1000;
            _progress.Value = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            // 必须在 Label(Fill) 之后添加：Fill 先占位，Bottom 才缩到 10px；
            // 反过来 Bottom 先占位、Fill 后撑满会压住进度条（实测重叠）。
            _progressPanel.Controls.Add(_progress);

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
            // 协议错误可见计数：默认隐藏，非 0 才显示。
            // 之前 protocol-error 事件在 C# 端根本没有分支，协议坏了界面完全无感。
            _lblProtocolErr = MakeStatusLabel("协议错误 0", Color.Orange);
            _lblProtocolErr.Visible = false;

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
            flow.Controls.Add(Spacer(18));
            flow.Controls.Add(_lblProtocolErr);
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
                // 日志已改为按天分文件（sync-YYYY-MM-DD.log），原来的固定名 sync.log
                // 是历史遗留、不再更新 —— 打开它等于给用户看一个死文件。
                // 优先用引擎 status 里给出的当天 logFile；拿不到再退回扫描目录。
                var f = _currentLogFile;
                if (string.IsNullOrEmpty(f) || !File.Exists(f))
                {
                    f = FindNewestLogFile();
                }
                if (!string.IsNullOrEmpty(f) && File.Exists(f))
                {
                    try { Process.Start("notepad.exe", "\"" + f + "\""); }
                    catch (Exception ex) { MessageBox.Show("打开日志失败：" + ex.Message, "ThunderPanSync"); }
                }
                else
                {
                    MessageBox.Show("还没有日志文件。日志目录：" + _dataDir, "ThunderPanSync");
                }
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
                // debug 级别来自 stderr（引擎原始日志），不进 UI，避免刷屏。
                // 但 EngineHost 已把它落盘到 engine-stderr.log，所以没丢。
                if (level == "debug") return;
                AppendLog(msg);
            };
            _host.OnRunComplete += delegate(Dictionary<string, object> s)
            {
                UiInvoke(delegate
                {
                    // 一轮结束：把进度条收回到"空闲"，否则会停在 100% 骗人
                    SetProgressIdle("空闲（上一轮已结束）");
                    RefreshStatus();
                });
            };
            // 进度事件很频繁：只更新控件，绝不 AppendLog（否则日志区被刷屏，
            // 前 800 行裁剪会把真正的根因挤掉——这正是本条要修的问题）。
            _host.OnProgress += delegate(Dictionary<string, object> d)
            {
                UiInvoke(delegate { ApplyProgress(d); });
            };
            _host.OnProtocolError += delegate(string raw)
            {
                UiInvoke(delegate
                {
                    _protocolErrCount++;
                    _lblProtocolErr.Visible = true;
                    _lblProtocolErr.Text = "协议错误 " + _protocolErrCount;
                    AppendLog("[协议错误] 引擎收到无法解析的行：" + raw);
                });
            };
            _host.OnFatal += delegate(string m) { UiFatal(m); };
        }

        // ---------------- 进度显示 ----------------
        // 字段名严格照 plan.md WP1：phase/index/total/name/size/bytesDone/bytesTotal/elapsedMs/etaMs
        private void ApplyProgress(Dictionary<string, object> d)
        {
            if (d == null || _progress == null) return;
            string phase = GetStr(d, "phase", "uploading");
            int idx = GetInt(d, "index", 0);
            int total = GetInt(d, "total", 0);
            string name = GetStr(d, "name", "");
            long bytesDone = GetLong(d, "bytesDone", 0);
            long bytesTotal = GetLong(d, "bytesTotal", 0);
            long elapsedMs = GetLong(d, "elapsedMs", 0);
            long etaMs = GetLong(d, "etaMs", -1);

            if (phase == "done")
            {
                // "完成"这个词必须与实际结果一致。
                // done 事件里 total = uploaded + failed，若整轮全失败，
                // 原实现会显示"完成 · 共 10 个文件"——纯属说谎。
                int up = GetInt(d, "uploaded", -1);
                int bad = GetInt(d, "failed", -1);
                if (up < 0 || bad < 0)
                {
                    // 老引擎没给这两个字段，退回中性措辞，不硬说"完成"
                    SetProgressIdle("本轮结束 · 共 " + total + " 个文件");
                }
                else if (bad == 0)
                {
                    SetProgressIdle("完成 · 成功 " + up + " 个文件");
                }
                else if (up == 0)
                {
                    _lblProgress.Text = "本轮失败 " + bad + " 个文件（详见下方失败列表）";
                    _lblProgress.ForeColor = Color.Salmon;
                }
                else
                {
                    _lblProgress.Text = "部分成功 · 成功 " + up + "，失败 " + bad
                        + "（详见下方失败列表）";
                    _lblProgress.ForeColor = Color.Gold;
                }
                if (bad == 0)
                {
                    try { _progress.Value = 1000; } catch { }
                    _lblProgress.ForeColor = Color.LightGreen;
                }
                else if (bad < 0)
                {
                    // 老引擎缺字段：不知道成败，就保持中性，既不说成功也不报假失败
                    try { _progress.Value = 0; } catch { }
                }
                else
                {
                    // 有失败时进度条不能停在"看起来快完成"的位置，也不能归零装作没事。
                    // 设成一个明确的"未完成"值并把颜色标红，避免"满格后归零=成功"的误读。
                    try { _progress.Value = Math.Min(_progress.Maximum, 999); } catch { }
                    try
                    {
                        _progress.ForeColor = Color.Salmon;
                    }
                    catch { }
                }
                return;
            }
            if (phase == "scanning")
            {
                _progress.Value = 0;
                _lblProgress.ForeColor = Color.Gainsboro;
                _lblProgress.Text = "正在扫描…" + (total > 0 ? ("（" + total + " 个文件）") : "");
                return;
            }

            // 总体进度 = 已完成的文件数 + 当前文件内的百分比
            double pct = 0;
            if (total > 0)
            {
                double inFile = bytesTotal > 0 ? ((double)bytesDone / (double)bytesTotal) : 0;
                if (inFile < 0) inFile = 0;
                if (inFile > 1) inFile = 1;
                double doneFiles = Math.Max(0, idx - 1);
                pct = (doneFiles + inFile) / (double)total;
            }
            else if (bytesTotal > 0)
            {
                pct = (double)bytesDone / (double)bytesTotal;
            }
            if (pct < 0) pct = 0;
            if (pct > 1) pct = 1;
            try { _progress.Value = (int)Math.Round(pct * 1000); } catch { }

            string speed = "";
            if (elapsedMs > 500 && bytesDone > 0)
            {
                double bps = (double)bytesDone * 1000.0 / (double)elapsedMs;
                speed = FormatSize((long)bps) + "/s";
            }

            var sb = new StringBuilder();
            sb.Append("第 ");
            sb.Append(idx > 0 ? idx : 0);
            sb.Append("/");
            sb.Append(total);
            if (!string.IsNullOrEmpty(name)) { sb.Append("  ·  "); sb.Append(name); }
            if (bytesTotal > 0)
            {
                sb.Append("  ·  ");
                sb.Append(FormatSize(bytesDone));
                sb.Append(" / ");
                sb.Append(FormatSize(bytesTotal));
            }
            if (speed.Length > 0) { sb.Append("  ·  "); sb.Append(speed); }
            if (etaMs > 0)
            {
                sb.Append("  ·  剩余 ");
                sb.Append(FormatEta(etaMs));
            }

            _lblProgress.ForeColor = Color.Gainsboro;
            _lblProgress.Text = sb.ToString();
        }

        private void SetProgressIdle(string text)
        {
            if (_progress == null) return;
            try { _progress.Value = 0; } catch { }
            if (_lblProgress != null)
            {
                // 只有"真的没事"时才用灰色。带失败信息时保持调用方设的告警色，
                // 否则会出现"进度满格→归零→灰色中性文本"的成功错觉（实测复现过）。
                bool looksLikeFailure = !string.IsNullOrEmpty(text)
                    && (text.IndexOf("失败") >= 0 || text.IndexOf("未完成") >= 0);
                if (!looksLikeFailure) _lblProgress.ForeColor = Color.Gray;
                _lblProgress.Text = string.IsNullOrEmpty(text) ? "空闲" : text;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";
        }

        private static string FormatEta(long ms)
        {
            if (ms < 1000) return "0 秒";
            long sec = ms / 1000;
            if (sec < 60) return sec + " 秒";
            if (sec < 3600) return (sec / 60) + " 分 " + (sec % 60) + " 秒";
            return (sec / 3600) + " 小时 " + ((sec % 3600) / 60) + " 分";
        }

        private void UiFatal(string msg)
        {
            // 错误提示必须带引擎自己的话（stderr 尾部）。只显示"引擎进程已退出"
            // 等于什么都没说，用户和我们都无从查起。
            string detail = msg;
            try { detail = _host.BuildFatalDetail(msg, 100); } catch { }
            UiInvoke(delegate
            {
                _lblState.Text = "● 引擎异常";
                _lblState.ForeColor = Color.Salmon;
                AppendLog("[错误] " + detail);
                SetProgressIdle("已停止（引擎异常）");
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
                    // 裁剪前必须先把被裁掉的部分落盘。
                    // 原来的实现直接"砍掉最前 300 行"，而启动过程与第一次失败的原因
                    // 恰好就在最前面——用户和我们事后都查不到根因，这是日志最大的缺陷。
                    var cur = _txtLog.Lines;
                    int cut = 300;
                    if (cut > cur.Length) cut = cur.Length;
                    var dropped = new string[cut];
                    Array.Copy(cur, 0, dropped, 0, cut);
                    AppendUiLogArchive(dropped);

                    var keep = new string[cur.Length - cut];
                    Array.Copy(cur, cut, keep, 0, keep.Length);
                    _txtLog.Lines = keep;
                }
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            });
        }

        // 把被裁掉的日志行追加到 %APPDATA%\ThunderPanSync\ui-log-YYYY-MM-DD.log
        // 按天分文件，保证"界面上被挤掉的根因"在磁盘上永远查得到。
        private void AppendUiLogArchive(string[] lines)
        {
            if (lines == null || lines.Length == 0) return;
            try
            {
                var file = Path.Combine(_dataDir, "ui-log-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                lock (_uiLogSync)
                {
                    bool isNew = !File.Exists(file);
                    // 新文件带 BOM（notepad 兼容），追加时绝不能再写 BOM，
                    // 否则每追加一次文件中间就多一个 \uFEFF。
                    var enc = new UTF8Encoding(isNew);
                    using (var w = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), enc))
                    {
                        w.WriteLine("===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 界面日志裁剪转存（被挤出的 " + lines.Length + " 行）=====");
                        for (int i = 0; i < lines.Length; i++) w.WriteLine(lines[i]);
                    }
                }
            }
            catch { }   // 转存失败绝不能拖垮界面
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

                    // 熔断态必须单独显示。
                    // circuitOpen / pausedReason / consecutiveFailures 早就由 getStatus() 返回，
                    // 但界面一个字段都没读 —— 熔断期间 running 仍为 true，
                    // 于是显示绿色「● 备份中」，而引擎只是每 5 分钟打一条"已熔断"的 warn。
                    // 这是"界面说谎"的同类复发，比进度条严重得多。
                    bool circuitOpen = st.ContainsKey("circuitOpen")
                        && Convert.ToBoolean(st["circuitOpen"]);
                    int consecFail = GetInt(st, "consecutiveFailures", 0);

                    // 状态显示必须诚实。四态：
                    //   已熔断   (红) —— 连续失败已暂停自动重试，必须刺眼
                    //   备份中   (绿) —— 引擎在跑且未熔断
                    //   未在备份 (红) —— 有目录但没跑
                    //   未配置   (黄) —— 还没有监视目录
                    bool hasDirs = _lvDirs.Items.Count > 0;
                    if (circuitOpen)
                    {
                        _lblState.Text = "● 已暂停（连续失败 " + consecFail
                            + " 轮，点「立即同步」重试）";
                        _lblState.ForeColor = Color.Salmon;
                    }
                    else if (running)
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

                    // 记住当天日志路径，供"打开日志文件"按钮使用。
                    // 日志按天分文件后，写死 "sync.log" 会打开一个不再更新的旧文件。
                    var lf = GetStr(st, "logFile", "");
                    if (!string.IsNullOrEmpty(lf)) _currentLogFile = lf;
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
            if (d == null || !d.ContainsKey(k) || d[k] == null) return def;
            return Convert.ToString(d[k]);
        }

        // 找最新的日志文件：优先 sync-YYYY-MM-DD.log（按天分文件后的命名），
        // 找不到再退回任何 sync*.log，避免"打开日志"指向历史遗留的死文件。
        private string FindNewestLogFile()
        {
            try
            {
                if (!Directory.Exists(_dataDir)) return null;
                var today = Path.Combine(_dataDir, "sync-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (File.Exists(today)) return today;

                var cands = Directory.GetFiles(_dataDir, "sync*.log");
                if (cands.Length == 0) return null;

                string newest = null;
                DateTime newestTime = DateTime.MinValue;
                for (int i = 0; i < cands.Length; i++)
                {
                    try
                    {
                        var t = File.GetLastWriteTime(cands[i]);
                        if (t > newestTime) { newestTime = t; newest = cands[i]; }
                    }
                    catch { }
                }
                return newest;
            }
            catch { return null; }
        }
        private static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return def;
            try { return Convert.ToInt32(d[k]); } catch { return def; }
        }
        // 字节数与 etaMs 可能超过 int 范围（大文件/长剩余时间），必须按 long 取
        private static long GetLong(Dictionary<string, object> d, string k, long def)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return def;
            try { return Convert.ToInt64(d[k]); } catch { return def; }
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
            if (_eulaDeclined) return;      // 拒绝 EULA：控件全部不存在，绝不能碰
            LoadAutoStart();
            // 首次启动若配置里有文件夹，自动刷新一次
            RefreshStatus();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 用户拒绝 EULA（构造函数已提前返回，界面没建）→ 立刻关窗，
            // Application.Run 随即返回，进程退出。这是"退出"按钮真正生效的地方。
            if (_eulaDeclined)
            {
                // _reallyQuit 必须先置位：万一将来有人把 EULA 检查挪到 BuildUi 之后，
                // FormClosing 里的"点 X 最小化到托盘"会把这次 Close() 取消掉，
                // 于是程序挂在一个空窗口上永远不退出。
                _reallyQuit = true;
                try { Close(); } catch { }
                return;
            }
            // --autostart 时直接进托盘（不弹窗）
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a == "--autostart") { WindowState = FormWindowState.Minimized; Hide(); break; }
            }
        }
    }
}
