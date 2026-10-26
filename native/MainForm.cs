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
    // 继承 DarkForm 而不是 Form：
    //   Win10 上系统标题栏无法变深（DwmSetWindowAttribute 返回成功但视觉无效，已像素级实测），
    //   唯一可靠办法是 FormBorderStyle=None + 自绘标题栏（拖动/最大化/关闭/边缘缩放/Aero Snap
    //   都在 DarkForm 里实现了）。详见 DarkForm.cs 顶部注释与 docs/UI-SPEC.md。
    public class MainForm : DarkForm
    {
        private readonly EngineHost _host = new EngineHost();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly string _dataDir;
        private readonly BrowserManager _browser;

        // 控件
        // 标签页用自绘 SegmentedTabs + 两个 Panel，不用原生 TabControl
        // （原生 TabControl 的页体背景永远是浅色 #F0F0F0，无法改，见 BuildUi 里的说明）
        // 标签页用自绘 SegmentedTabs + 两个 Panel，不用原生 TabControl
        // （原生 TabControl 的页体背景永远是浅色 #F0F0F0，无法改）
        private SegmentedTabs _tabStrip;
        private Panel _pageHost, _pageMain, _pageSettings;
        private DarkListView _lvDirs;
        private Button _btnAddDir, _btnDelDir;
        private TextBox _txtLog;
        private DarkListView _lvFail;
        private Button _btnSync, _btnStart, _btnStop, _btnSave, _btnOpenLog;
        // 设置页（DECISIONS §3.6）：只有"云盘目录名"还是文本框，其余三项改 ComboBox ——
        // 用文本框让用户手输"多久找一次"既容易输错，也无法表达推荐值。
        private TextBox _txtRemoteName;
        private FlatCombo _cboInterval, _cboSettle, _cboMaxSize;
        private CheckBox _chkAutoStart;
        // 状态条（DECISIONS §3.5）：TableLayoutPanel + 定宽列。实测 FlowLayoutPanel 在
        // 同一组文本下抖动 156px，绝对坐标重叠 38px，只有表格稳定。
        private Panel _statusBar, _honestyBar, _progressRow, _footer;
        private TableLayoutPanel _statusGrid;
        private StatusPill _pill;
        private StatChip _chipDirs, _chipUploaded, _chipFailed, _chipLogin;
        private Label _lblProtocolErr, _lblHonesty, _lblHonestyTail, _lblProgress;
        private FlatProgress _flatProgress;
        private Panel _failPanel;
        private Label _lblFail, _lblFailSummary;
        // ---- 唯一状态机（DECISIONS §五 A-1）----
        // 界面状态只有一个来源：RefreshUiState()。任何地方都不许直接改 _pill 的文字，
        // 否则"熔断显示绿色"这类说谎缺陷会以另一种形式复发。
        private UiState _uiState = UiState.Ready;
        // 只有 UiFatal 能置 true —— 启动期慢绝不等于异常（历史假告警就是从这里来的）
        private bool _engineFatal = false;
        // 只有 StartEngine 成功等到 ready 后才能置 true：它是 Error 态的唯一闸门
        private volatile bool _engineReady = false;
        // 只有 progress 的 done 分支能写；OnRunComplete 必须尊重它（A-2 的覆盖 bug）
        private string _lastRoundFatalCode = null;
        private DateTime _lastStatusAt = DateTime.MinValue;
        // A-4：1 秒 Timer 驱动秒表/相对时间/状态过期；10 秒 Timer 轮询 status
        private System.Windows.Forms.Timer _tickTimer, _pollTimer;
        // 本轮开始时间（秒表用）；_roundActive 为 false 时不显示"已用"
        private DateTime _roundStartedAt = DateTime.MinValue;
        private bool _roundActive = false;
        // 失败列表内容签名（A-5：只比数量会在"修好 A、坏掉 B"时不刷新）
        private string _failSig = "";
        // ---- 状态机附属字段 ----
        private bool _haveStatus = false;
        private bool _stRunning = false;
        private bool _stCircuitOpen = false;
        private int _stDirsCount = 0;
        private int _stConsecFail = 0;
        private int _stUploadedTotal = 0;
        private int _stFailedTotal = 0;
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
                f.Width = Theme.S(760);
                f.Height = Theme.S(620);
                f.MinimumSize = new Size(Theme.S(600), Theme.S(480));
                f.StartPosition = FormStartPosition.CenterScreen;
                f.BackColor = Theme.BgSurface;
                f.ForeColor = Theme.TextPrimary;
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = true;

                var bottom = new Panel();
                bottom.Dock = DockStyle.Bottom;
                bottom.Height = Theme.S(56);
                bottom.BackColor = Theme.BgBar;
                // EULA 底栏 padY 10：内容 36 >= 原生 Button 32（ADDENDUM 第 5 条）
                bottom.Padding = new Padding(Theme.S(12), Theme.S(10), Theme.S(12), Theme.S(10));
                f.Controls.Add(bottom);

                var btnAccept = new Button();
                btnAccept.Text = "我已了解并承担风险";
                btnAccept.Width = Theme.S(200);
                btnAccept.Height = Theme.S(32);
                btnAccept.FlatStyle = FlatStyle.Flat;
                btnAccept.BackColor = Theme.BgSurfaceAlt;
                btnAccept.ForeColor = Theme.TextPrimary;
                btnAccept.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 82);
                btnAccept.DialogResult = DialogResult.OK;
                btnAccept.Location = new Point(Theme.S(12), Theme.S(10));
                bottom.Controls.Add(btnAccept);

                var btnQuit = new Button();
                btnQuit.Text = "退出";
                btnQuit.Width = Theme.S(110);
                btnQuit.Height = Theme.S(32);
                btnQuit.FlatStyle = FlatStyle.Flat;
                btnQuit.BackColor = Theme.BgSurfaceAlt;
                btnQuit.ForeColor = Theme.TextPrimary;
                btnQuit.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 82);
                btnQuit.DialogResult = DialogResult.Cancel;
                btnQuit.Location = new Point(Theme.S(224), Theme.S(10));
                bottom.Controls.Add(btnQuit);

                var txt = new TextBox();
                txt.Dock = DockStyle.Fill;
                txt.Multiline = true;
                txt.ReadOnly = true;
                txt.ScrollBars = ScrollBars.Vertical;
                txt.WordWrap = true;
                txt.BackColor = Theme.BgInset;
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
        //
        // 五条栏的最终像素（DECISIONS §3.2 + AGENT-A-ADDENDUM）：
        //   标题栏 34（DarkForm 自绘） / 状态条 48 / 诚实性常驻条 24 / 进度行 36 /
        //   标签条 36 / 页体 Fill / 底栏 52
        // 全部走 Theme.S()：窗口在 150% 下放大 1.5 倍，栏高不跟着放大就会把文字裁掉。
        private void BuildUi()
        {
            Text = "ThunderPanSync · 迅雷云盘自动备份";
            // DarkForm 的隐形边框吃 14px（7px/边）⇒ 1000×760 的客户区是 986×746。
            Width = Theme.S(1000);
            Height = Theme.S(760);
            MinimumSize = new Size(Theme.S(940), Theme.S(700));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.BgWindow;
            ForeColor = Theme.TextPrimary;

            // DarkForm 只自绘了顶部 TitleBarPx 高的标题栏，内容区要自己让出来。
            // 用 Padding 而不是逐个控件算 Y 偏移：Dock 布局会自动遵守它。
            Padding = new Padding(0, TitleBarPx, 0, 0);
            AutoScaleMode = AutoScaleMode.None;

            BuildStatusBar();
            BuildHonestyBar();
            BuildProgressRow();
            BuildTabsAndPages();
            BuildFooter();

            // ---- 显式钉 Z 序（不靠 Add 顺序猜）----
            // WinForms 的 Dock 从 Controls.Count-1 往 0 处理：索引越大越靠外缘，
            // 所以 Dock=Fill 必须占索引 0。Add 顺序写错了不报错，只表现为控件跑到
            // 奇怪的位置或被遮住（实测踩过：Fill 抢了 Tab 上方的空间）。
            // 禁止前台置顶调用 —— 对 Dock=Top 控件实测是"往下推"，净零/有害。
            Controls.SetChildIndex(_pageHost, 0);
            Controls.SetChildIndex(_tabStrip, 1);
            Controls.SetChildIndex(_progressRow, 2);
            Controls.SetChildIndex(_honestyBar, 3);
            Controls.SetChildIndex(_statusBar, 4);
            Controls.SetChildIndex(_footer, 5);

            _btnSync.Click += delegate { DoPrimary(); };
            _btnStart.Click += delegate { DoSimple("start", "自动备份已启动"); };
            _btnStop.Click += delegate { DoSimple("stop", "自动备份已停止"); };
            _btnOpenLog.Click += delegate { OpenLogFile(); };

            BuildTray();
            WireFormEvents();
            StartTimers();
            RefreshUiState();
        }

        /// <summary>状态条：TableLayoutPanel（五列定宽）+ 内嵌在底部的 8px 进度条。</summary>
        private void BuildStatusBar()
        {
            _statusBar = new Panel();
            _statusBar.Dock = DockStyle.Top;
            _statusBar.Height = Theme.S(48);
            _statusBar.BackColor = Theme.BgBar;
            // 48 必须同时装下"pill/chip 上排"和"8px 进度条"（DECISIONS §3.4 要求内嵌）。
            // 上排真实需要 = StatusPill 26 + margin 4 = 30，进度条 8 ⇒ 38；
            // 若按 ADDENDUM 给 (8,8) 垂直内边距，内容只剩 32 < 38，上排就被裁 6px。
            // 所以垂直内边距取 (2,0)：内容 46 ≥ 38，两档 DPI 都过。
            _statusBar.Padding = new Padding(Theme.S(12), Theme.S(2), Theme.S(12), 0);
            Controls.Add(_statusBar);

            _statusGrid = new TableLayoutPanel();
            _statusGrid.Dock = DockStyle.Fill;
            _statusGrid.BackColor = Theme.BgBar;
            _statusGrid.ColumnCount = 5;
            _statusGrid.RowCount = 1;
            // 有滚动条就吃掉 17px 高度（DPI Agent 实测）——状态条绝不允许出现滚动条
            _statusGrid.AutoScroll = false;
            _statusGrid.Margin = new Padding(0);
            _statusGrid.Padding = new Padding(0);
            _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(280)));
            _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(150)));
            _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(150)));
            _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(110)));
            _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            // 先加表格（Fill，索引 0）再加进度条（Bottom，索引 1）：
            // 同一容器内也是索引大的先被 Dock 处理，Bottom 必须先占位，否则被 Fill 覆盖。
            _statusBar.Controls.Add(_statusGrid);

            _pill = new StatusPill();
            _pill.Anchor = AnchorStyles.Left;
            _pill.Margin = new Padding(0, 0, Theme.S(8), 0);
            _pill.PinnedWidth = Theme.S(272);   // B 的控件自约束：Fit() 不再改写宽度（150% 下更稳）
            _pill.PillState = StatusPill.PillKind.Unknown;
            _pill.Text = "启动中";
            _statusGrid.Controls.Add(_pill, 0, 0);

            _chipDirs = new StatChip();
            _chipDirs.Label = "文件夹";
            _chipDirs.Value = "0";
            _chipDirs.Anchor = AnchorStyles.Left;
            _chipDirs.Margin = new Padding(0, 0, Theme.S(8), 0);
            _chipDirs.PinnedWidth = Theme.S(142);
            _statusGrid.Controls.Add(_chipDirs, 1, 0);

            _chipUploaded = new StatChip();
            _chipUploaded.Label = "已上传";
            _chipUploaded.Value = "0";
            _chipUploaded.ValueColor = Theme.Ok;
            _chipUploaded.Anchor = AnchorStyles.Left;
            _chipUploaded.Margin = new Padding(0, 0, Theme.S(8), 0);
            _chipUploaded.PinnedWidth = Theme.S(142);
            _statusGrid.Controls.Add(_chipUploaded, 2, 0);

            _chipFailed = new StatChip();
            _chipFailed.Label = "失败";
            _chipFailed.Value = "0";
            _chipFailed.ValueColor = Theme.Danger;
            _chipFailed.Anchor = AnchorStyles.Left;
            _chipFailed.Margin = new Padding(0, 0, Theme.S(8), 0);
            _chipFailed.PinnedWidth = Theme.S(102);
            _statusGrid.Controls.Add(_chipFailed, 3, 0);

            // 「已登录」降级为状态信息，不再是一个常驻按钮（DECISIONS §3.8）
            var right = new FlowLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.FlowDirection = FlowDirection.LeftToRight;
            right.WrapContents = false;
            right.AutoScroll = false;
            right.Margin = new Padding(0);
            right.BackColor = Theme.BgBar;
            _statusGrid.Controls.Add(right, 4, 0);

            _chipLogin = new StatChip();
            _chipLogin.Label = "登录";
            _chipLogin.Value = "未登录";
            _chipLogin.ValueColor = Theme.Warn;
            _chipLogin.Margin = new Padding(0, 0, Theme.S(12), 0);
            _chipLogin.PinnedWidth = Theme.S(130);
            right.Controls.Add(_chipLogin);

            // 协议错误可见计数：默认隐藏，非 0 才显示。之前协议坏了界面完全无感。
            _lblProtocolErr = new Label();
            _lblProtocolErr.Text = "协议错误 0";
            _lblProtocolErr.AutoSize = true;
            _lblProtocolErr.ForeColor = Theme.Warn;
            _lblProtocolErr.Margin = new Padding(0, Theme.S(6), 0, 0);
            _lblProtocolErr.Visible = false;
            // 无障碍（必修 2）：自绘控件拿得到焦点但不画焦点框，键盘 Tab 会停在
            // "看不见的落点"上。这里把它们移出 Tab 序列：用户只在 FlatButton /
            // TextBox / ComboBox 之间移动，每个都有可见焦点。标签条用方向键切换。
            _pill.TabStop = false;
            _chipDirs.TabStop = false;
            _chipUploaded.TabStop = false;
            _chipFailed.TabStop = false;
            _chipLogin.TabStop = false;
            right.Controls.Add(_lblProtocolErr);

            _flatProgress = new FlatProgress();
            _flatProgress.Dock = DockStyle.Bottom;
            _flatProgress.Height = Theme.S(8);
            _flatProgress.Ratio = 0;
            // 换掉原生 ProgressBar 会丢语义，这里补上可访问性（屏幕阅读器要读得到）
            _flatProgress.AccessibleRole = AccessibleRole.ProgressBar;
            _flatProgress.AccessibleName = "本轮备份进度";
            _statusBar.Controls.Add(_flatProgress);
        }

        /// <summary>诚实性常驻条（A-7）：只上传、不能下载或恢复 —— 常驻、不可关闭。</summary>
        private void BuildHonestyBar()
        {
            _honestyBar = new Panel();
            _honestyBar.Dock = DockStyle.Top;
            _honestyBar.Height = Theme.S(24);
            _honestyBar.BackColor = Theme.BgBar;
            _honestyBar.Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0);
            Controls.Add(_honestyBar);

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = false;
            flow.AutoScroll = false;
            flow.Margin = new Padding(0);
            flow.BackColor = Theme.BgBar;
            _honestyBar.Controls.Add(flow);

            _lblHonesty = new Label();
            _lblHonesty.Text = "只上传，不能下载或恢复";
            _lblHonesty.AutoSize = true;
            _lblHonesty.Font = new Font("Microsoft YaHei UI", 9F);
            // 关键短语用 Warn：BgBar #1C1F24 上实测 8.39:1，过 AA
            _lblHonesty.ForeColor = Theme.Warn;
            _lblHonesty.Margin = new Padding(0, Theme.S(3), 0, 0);
            flow.Controls.Add(_lblHonesty);

            _lblHonestyTail = new Label();
            _lblHonestyTail.Text = " · 云端不是唯一副本，本地文件请自行保留";
            _lblHonestyTail.AutoSize = true;
            _lblHonestyTail.Font = new Font("Microsoft YaHei UI", 9F);
            // 正文 TextSecondary：同一背景 7.56:1
            _lblHonestyTail.ForeColor = Theme.TextSecondary;
            _lblHonestyTail.Margin = new Padding(0, Theme.S(3), 0, 0);
            flow.Controls.Add(_lblHonestyTail);
        }

        /// <summary>进度行（36）：只放说明文字。进度条本体已内嵌进状态条（§3.4）。</summary>
        private void BuildProgressRow()
        {
            _progressRow = new Panel();
            _progressRow.Dock = DockStyle.Top;
            _progressRow.Height = Theme.S(36);
            _progressRow.BackColor = Theme.BgBar;
            // padY 3：内容 30 ≥ 需要 28（label S(20)）（ADDENDUM 第 4 条）
            _progressRow.Padding = new Padding(Theme.S(12), Theme.S(3), Theme.S(12), Theme.S(3));
            Controls.Add(_progressRow);

            _lblProgress = new Label();
            _lblProgress.Dock = DockStyle.Fill;
            _lblProgress.Text = "空闲";
            _lblProgress.ForeColor = Theme.TextTertiary;
            _lblProgress.Font = new Font("Microsoft YaHei UI", 9F);
            _lblProgress.TextAlign = ContentAlignment.MiddleLeft;
            _lblProgress.AutoEllipsis = true;
            _progressRow.Controls.Add(_lblProgress);
        }

        /// <summary>标签条 + 页体宿主。</summary>
        private void BuildTabsAndPages()
        {
            // 不用 TabControl：它的页体背景由系统视觉样式绘制，永远是 #F0F0F0，
            // TabPages[i].BackColor 设了也不生效（实测）。深色窗口里那块白底
            // 是"廉价感"的主要来源，且无法用任何属性消除。
            _tabStrip = new SegmentedTabs();
            _tabStrip.Tabs = new string[] { "备份", "设置" };
            _tabStrip.Dock = DockStyle.Top;
            _tabStrip.Height = Theme.S(36);
            _tabStrip.SelectedIndexChanged += delegate { SwitchTab(); };
            // 无障碍（必修 1）：SegmentedTabs 目前不处理按键（全项目 OnKeyDown 0 处），
            // 键盘用户无法切到「设置」页。B 会在控件内部修，这里先绕过，保证可用。
            // 该控件不画焦点框，故移出 Tab 序列（TabStop=false），可用鼠标点选后按方向键。
            _tabStrip.TabStop = false;
            _tabStrip.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Right && _tabStrip.SelectedIndex < _tabStrip.Tabs.Length - 1)
                { _tabStrip.SelectedIndex++; e.Handled = true; }
                else if (e.KeyCode == Keys.Left && _tabStrip.SelectedIndex > 0)
                { _tabStrip.SelectedIndex--; e.Handled = true; }
                else if (e.KeyCode == Keys.Space)
                { _tabStrip.SelectedIndex = (_tabStrip.SelectedIndex == 0) ? 1 : 0; e.Handled = true; }
            };
            Controls.Add(_tabStrip);

            _pageHost = new Panel();
            _pageHost.Dock = DockStyle.Fill;
            _pageHost.BackColor = Theme.BgSurface;
            Controls.Add(_pageHost);

            BuildMainTab();
            BuildSettingsTab();
            SwitchTab();
        }

        /// <summary>底栏：高度 52，padY 8（FlatButton 自带 S(34) 高，10 会裁）。</summary>
        private void BuildFooter()
        {
            _footer = new Panel();
            _footer.Dock = DockStyle.Bottom;
            _footer.Height = Theme.S(52);
            _footer.BackColor = Theme.BgBar;
            _footer.Padding = new Padding(Theme.S(12), Theme.S(8), Theme.S(12), Theme.S(8));
            Controls.Add(_footer);

            _btnSync = MakeButton("立即同步一次", 130);
            _btnStart = MakeButton("开始自动备份", 130);
            _btnStop = MakeButton("停止", 80);
            _btnOpenLog = MakeButton("打开日志文件", 120);

            var bflow = new FlowLayoutPanel();
            bflow.Dock = DockStyle.Fill;
            bflow.FlowDirection = FlowDirection.LeftToRight;
            bflow.WrapContents = false;
            bflow.AutoScroll = false;
            bflow.Margin = new Padding(0);
            bflow.BackColor = Theme.BgBar;
            bflow.Controls.Add(_btnSync);
            bflow.Controls.Add(_btnStart);
            bflow.Controls.Add(_btnStop);
            bflow.Controls.Add(_btnOpenLog);
            _footer.Controls.Add(bflow);
        }

        /// <summary>切换标签页：只改两个 Panel 的可见性，不重建控件（重建会丢状态）。</summary>
        private void SwitchTab()
        {
            if (_pageMain == null || _pageSettings == null) return;
            bool main = (_tabStrip.SelectedIndex == 0);
            _pageMain.Visible = main;
            _pageSettings.Visible = !main;
            // 用索引而不是前台置顶：可见的那个放到索引 1（更靠外），另一个留在 0。
            if (main) _pageHost.Controls.SetChildIndex(_pageMain, 1);
            else _pageHost.Controls.SetChildIndex(_pageSettings, 1);
        }

        private void BuildMainTab()
        {
            _pageMain = new Panel();
            _pageMain.Dock = DockStyle.Fill;
            _pageMain.BackColor = Theme.BgSurface;
            _pageHost.Controls.Add(_pageMain);
            Panel page = _pageMain;

            // 文件夹列表标签（高 26）
            var lbl = new Label();
            lbl.Text = "监视的文件夹（里面的新文件会自动上传到迅雷云盘）：";
            lbl.Dock = DockStyle.Top;
            lbl.Height = Theme.S(26);
            lbl.ForeColor = Theme.TextPrimary;
            lbl.AutoEllipsis = true;
            page.Controls.Add(lbl);

            _lvDirs = new DarkListView();
            _lvDirs.Dock = DockStyle.Top;
            _lvDirs.Height = Theme.S(132);
            _lvDirs.View = View.Details;
            _lvDirs.FullRowSelect = true;
            _lvDirs.BackColor = Theme.BgSurfaceAlt;
            _lvDirs.Columns.Add("文件夹路径", 700);
            _lvDirs.FitColumns();
            _lvDirs.TabStop = false;   // 列表靠鼠标选；不自绘焦点框，不进 Tab 序列
            page.Controls.Add(_lvDirs);

            // 添加 / 移除按钮行（高 42，padY 3 ⇒ 内容 36 ≥ FlatButton 34）
            var dirBtns = new Panel();
            dirBtns.Dock = DockStyle.Top;
            dirBtns.Height = Theme.S(42);
            dirBtns.Padding = new Padding(0, Theme.S(3), 0, Theme.S(3));
            dirBtns.BackColor = Theme.BgSurface;
            page.Controls.Add(dirBtns);

            _btnAddDir = MakeButton("添加文件夹…", 120);
            _btnDelDir = MakeButton("移除选中", 110);
            var dirFlow = new FlowLayoutPanel();
            dirFlow.Dock = DockStyle.Fill;
            dirFlow.FlowDirection = FlowDirection.LeftToRight;
            dirFlow.WrapContents = false;
            dirFlow.AutoScroll = false;
            dirFlow.Margin = new Padding(0);
            dirFlow.BackColor = Theme.BgSurface;
            dirFlow.Controls.Add(_btnAddDir);
            dirFlow.Controls.Add(_btnDelDir);
            dirBtns.Controls.Add(dirFlow);

            _btnAddDir.Click += delegate { AddDir(); };
            _btnDelDir.Click += delegate { DelDir(); };

            // 失败区：Bottom，三档 0 / 44 / 156（A4）。0 = 没有失败（整块隐藏），
            // 44 = 只显示一行摘要（不再让列表抢走日志的高度），156 = 标签 24 + 列表 132。
            _failPanel = new Panel();
            _failPanel.Dock = DockStyle.Bottom;
            _failPanel.Visible = false;
            _failPanel.BackColor = Theme.BgSurface;
            page.Controls.Add(_failPanel);

            _lblFail = new Label();
            _lblFail.Text = "失败的文件（原因在这里，修好后点「立即同步一次」会重试）：";
            _lblFail.Dock = DockStyle.Top;
            _lblFail.Height = Theme.S(24);
            _lblFail.ForeColor = Theme.TextPrimary;
            _lblFail.AutoEllipsis = true;
            _failPanel.Controls.Add(_lblFail);

            _lblFailSummary = new Label();
            _lblFailSummary.Dock = DockStyle.Top;
            _lblFailSummary.Height = Theme.S(20);
            _lblFailSummary.ForeColor = Theme.TextSecondary;
            _lblFailSummary.AutoEllipsis = true;
            _lblFailSummary.Visible = false;
            _failPanel.Controls.Add(_lblFailSummary);

            _lvFail = new DarkListView();
            _lvFail.Dock = DockStyle.Fill;
            _lvFail.View = View.Details;
            _lvFail.FullRowSelect = true;
            _lvFail.GridLines = false;
            _lvFail.BackColor = Theme.BgSurfaceAlt;
            _lvFail.ForeColor = Theme.TextPrimary;
            // ToolTipText 之前设了但从不显示：ShowItemToolTips 默认 false（B-2）
            _lvFail.ShowItemToolTips = true;
            _lvFail.Columns.Add("文件", 240);
            _lvFail.Columns.Add("次数", 50);
            _lvFail.Columns.Add("原因", 420);
            _lvFail.FitColumns();
            _lvFail.TabStop = false;
            _failPanel.Controls.Add(_lvFail);

            // 日志（Fill）
            var logPanel = new Panel();
            logPanel.Dock = DockStyle.Fill;
            logPanel.BackColor = Theme.BgSurface;
            page.Controls.Add(logPanel);

            var lblLog = new Label();
            lblLog.Text = "运行日志：";
            lblLog.Dock = DockStyle.Top;
            lblLog.Height = Theme.S(24);
            lblLog.ForeColor = Theme.TextPrimary;
            logPanel.Controls.Add(lblLog);

            _txtLog = new TextBox();
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Theme.BgInset;
            _txtLog.ForeColor = Theme.TextSecondary;
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.BorderStyle = BorderStyle.None;
            logPanel.Controls.Add(_txtLog);
            // A-9：日志一显示就全文选中会整块变选中蓝（白像素 ×4）
            _txtLog.Select(0, 0);

            // 页体内部 Z 序（DECISIONS §3.3）：Fill 占 0，Bottom 其次，Top 越靠前索引越大
            page.Controls.SetChildIndex(logPanel, 0);
            page.Controls.SetChildIndex(_failPanel, 1);
            page.Controls.SetChildIndex(dirBtns, 2);
            _pageMain.Controls.SetChildIndex(_lvDirs, 3);
            _pageMain.Controls.SetChildIndex(lbl, 4);
        }


        // ---------------- 小工具 ----------------

        // ---------------- 引擎 ----------------
        private void StartEngine()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var host = Path.Combine(baseDir, "engine", "host.js");

                // node.exe 查找顺序（P1-b）：
                //   1. <exe目录>\runtime\node.exe        正常安装结构
                //   2. <exe目录>\..\publish\runtime\...  开发时从 native\ 直接跑 exe
                //   3. D:\Nodejs\node.exe                本机开发环境
                //   4. PATH 里逐个目录找 node.exe
                // 原来只认第 1 条，于是从 native\ 跑 dev build 必然是
                // 「引擎异常：找不到 node.exe」+ 写 ui-fatal.log（实测 3 次）。
                var tried = new List<string>();
                string node = null;
                var cand = new List<string>();
                cand.Add(Path.Combine(baseDir, "runtime", "node.exe"));
                cand.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "publish", "runtime", "node.exe")));
                cand.Add(@"D:\Nodejs\node.exe");
                string pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    string[] dirs = pathEnv.Split(';');
                    for (int k = 0; k < dirs.Length; k++)
                    {
                        string dk = dirs[k];
                        if (string.IsNullOrEmpty(dk)) continue;
                        try { cand.Add(Path.Combine(dk.Trim(), "node.exe")); }
                        catch (Exception) { }
                    }
                }
                for (int k = 0; k < cand.Count; k++)
                {
                    string c = cand[k];
                    if (string.IsNullOrEmpty(c)) continue;
                    tried.Add(c);
                    try { if (File.Exists(c)) { node = c; break; } }
                    catch (Exception) { }
                }

                if (node == null)
                {
                    string msg = "找不到 node.exe，已尝试：" + string.Join(" | ", tried.ToArray());
                    UiFatal(msg);
                    return;
                }
                if (!File.Exists(host))
                {
                    UiFatal("找不到 host.js：" + host);
                    return;
                }
                AppendLog("引擎使用的 node：" + node);

                _host.Start(node, host, _dataDir);

                // 等 ready。
                //
                // 为什么不是"固定等 10 秒然后判死"：
                // 实测踩到过 —— 在 LZMA 压 105 MB node.exe 的同时启动应用，
                // host.js 没能在 10 秒内吐出 ready，界面就被判成"引擎异常 /
                // 已停止（引擎异常）"，而且【永久停在这个假告警上】；实际上引擎
                // 几秒后就正常就绪了。用户的机器会同时跑杀毒扫描、Windows 更新、
                // 压缩包，这不是罕见场景。
                //
                // 新策略：前 15 秒只"等"，不报警；之后如果仍然没 ready，
                // 就明确显示"启动较慢"（黄色，不是红色异常），继续等；
                // 只有连进程都死了，才是真异常（那条路径由 OnFatal 负责）。
                // 真异常的信息比"超时"精确得多，没必要用超时去猜。
                bool slowNotified = false;
                for (int i = 0; i < 1200 && !_host.IsReady; i++)
                {
                    Thread.Sleep(100);
                    if (i == 150 && !_host.IsReady)
                    {
                        slowNotified = true;
                        UiInvoke(delegate
                        {
                            // 状态文字统一由 RefreshUiState 决定（A-1：唯一入口）
                            RefreshUiState();
                            SetProgressIdle("引擎启动较慢（机器繁忙时正常），请稍候…");
                        });
                    }
                }

                if (!_host.IsReady)
                {
                    // 走到这里说明等了 120 秒：进程还在但一直不就绪，按异常处理。
                    UiFatal("引擎启动超时（120 秒无响应）");
                    return;
                }
                if (slowNotified)
                {
                    AppendLog("引擎启动较慢，现已就绪（机器繁忙时属正常现象）");
                }

                // 引擎就绪是 Error 态的唯一闸门（A-1：启动期绝不判 Error）
                _engineReady = true;
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
                    // A-2（阻断级缺陷）：这里原来无条件 SetProgressIdle("空闲（上一轮已结束）")。
                    // 事件顺序是 progress(done) → run-complete，两者都走 UiInvoke/BeginInvoke（FIFO 保证覆盖），
                    // 于是 ApplyProgress 刚写好的失败文案被这句抹掉：致命轮显示成空闲，成功轮的文案同样被抹。
                    // 现在只在"本轮没有致命错误"时才把进度行收回到空闲。
                    if (string.IsNullOrEmpty(_lastRoundFatalCode))
                        SetProgressIdle("空闲（上一轮已结束）");
                    RefreshUiState();
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


        /// <summary>把引擎的错误码翻成用户能看懂的一句话。</summary>
        private static string FatalCodeText(string code)
        {
            if (code == "BROWSER") return "后台浏览器没有运行或被关掉了";
            if (code == "AUTH") return "迅雷登录失效了，请重新登录";
            if (code == "QUOTA") return "云盘空间不足或文件数超限";
            if (code == "NETWORK") return "网络连不上云端";
            if (code == "PAGE") return "云盘网页结构可能变了";
            if (code == "DISK") return "读不到本地文件（被占用或没有权限）";
            if (code == "UNKNOWN") return "原因未知（已写入日志）";
            return "原因未知（" + code + "，已写入日志）";
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


        // 把引擎返回的 failedFiles 渲染成列表（文件 / 次数 / 原因）

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
        // 字节数与 etaMs 可能超过 int 范围（大文件 / 时间跨度大），必须按 long 取
        private static long GetLong(Dictionary<string, object> d, string k, long def)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return def;
            try { return Convert.ToInt64(d[k]); } catch { return def; }
        }

        // ---------------- 操作 ----------------

        private void DoSync()
        {
            _btnSync.Enabled = false;
            AppendLog("正在开始一轮上传…");
            var t = new Thread(delegate()
            {
                var d = _host.CallData("runOnce", null, 300000);
                UiInvoke(delegate
                {
                    _btnSync.Enabled = true;
                    if (d == null)
                    {
                        AppendLog("[错误] 同步失败或无响应");
                    }
                    else
                    {
                        // A-6：这是与 ApplyProgress 无关的第二条播报路径。
                        // 致命轮（fatalCode 非空但 failed=0）时这里曾播报成功，必须同样看 fatalCode。
                        int scanned = GetInt(d, "scanned", 0);
                        int up = GetInt(d, "uploaded", 0);
                        int bad = GetInt(d, "failed", 0);
                        int skip = GetInt(d, "skipped", 0);
                        string fatal = GetStr(d, "fatalCode", null);
                        if (!string.IsNullOrEmpty(fatal))
                        {
                            AppendLog("本轮失败：" + FatalCodeText(fatal)
                                + "（扫描 " + scanned + "，上传 " + up + "，失败 " + bad + "，跳过 " + skip + "）");
                        }
                        else if (bad > 0)
                        {
                            AppendLog("本轮结束但有失败：扫描 " + scanned + "，上传 " + up
                                + "，失败 " + bad + "，跳过 " + skip + "（原因见下方失败列表）");
                        }
                        else
                        {
                            AppendLog("本轮结束：扫描 " + scanned + "，上传 " + up
                                + "，失败 " + bad + "，跳过 " + skip);
                        }
                    }
                    RefreshUiState();
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
            _lvDirs.FitColumns();
            _lvDirs.RefreshColumns();
            _stDirsCount = _lvDirs.Items.Count;
            if (_chipDirs != null) _chipDirs.Value = _stDirsCount.ToString();
            RefreshUiState();
            SaveSettingsSilent();
        }

        private void DelDir()
        {
            if (_lvDirs.SelectedItems.Count == 0) return;
            foreach (ListViewItem it in _lvDirs.SelectedItems) _lvDirs.Items.Remove(it);
            _stDirsCount = _lvDirs.Items.Count;
            if (_chipDirs != null) _chipDirs.Value = _stDirsCount.ToString();
            RefreshUiState();
            SaveSettingsSilent();
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
        private void BuildSettingsTab()
        {
            _pageSettings = new Panel();
            _pageSettings.Dock = DockStyle.Fill;
            _pageSettings.BackColor = Theme.BgSurface;
            _pageSettings.AutoScroll = true;
            _pageHost.Controls.Add(_pageSettings);

            // 用 TableLayoutPanel（2 列：标签 / 控件）替掉所有
            // "Location = new Point(20, y)" + "y += 24/40" 的手算：
            // 9.5pt 行高在 100/150/200% 是 19/25/35px，y+=24 在 ≥150% 必然重叠（200% 叠 11px）。
            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Top;
            // Dock=Top 且 AutoSize=false 时【必须显式给 Height】，否则 TableLayoutPanel
            // 保留自己的默认高度（约 100px），只露出前 2 行 —— 实测踩过：设置页只显示
            // "云盘文件夹名字"和"多久找一次新文件"，后 3 项（改完多久后才上传 /
            // 单个文件最大 / 开机自启）连同"保存设置"全部被裁掉，
            // 看起来像"设置项没做完"，实际是布局漏了一行 Height。
            grid.AutoSize = false;
            grid.AutoSizeMode = AutoSizeMode.GrowOnly;
            grid.ColumnCount = 2;
            grid.RowCount = 7;
            grid.BackColor = Theme.BgSurface;
            grid.Padding = new Padding(0, Theme.S(8), 0, Theme.S(8));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(320)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // Height 必须等于"行高之和 + 上下 Padding"，否则行会被压缩或拉伸：
            //   6×S(52) + 1×S(56) = S(368)，加 Padding 2×S(8) = S(16) ⇒ S(384)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(52)));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(56)));
            grid.Height = Theme.S(384);
            _pageSettings.Controls.Add(grid);

            _txtRemoteName = new TextBox();
            StyleInput(_txtRemoteName);
            AddSettingRow(grid, 0, "云盘文件夹名字（云端目录）：", _txtRemoteName);

            _cboInterval = MakeCombo(new ComboOption[] {
                new ComboOption("每分钟找一次", "60"),
                new ComboOption("每 5 分钟找一次", "300"),
                new ComboOption("每 15 分钟找一次", "900"),
                new ComboOption("每 30 分钟找一次", "1800"),
                new ComboOption("每小时找一次", "3600") });
            AddSettingRow(grid, 1, "多久找一次新文件：", _cboInterval);

            _cboSettle = MakeCombo(new ComboOption[] {
                new ComboOption("改完 5 秒后才上传", "5"),
                new ComboOption("改完 15 秒后才上传", "15"),
                new ComboOption("改完 30 秒后才上传", "30"),
                new ComboOption("改完 1 分钟后才上传", "60"),
                new ComboOption("改完 3 分钟后才上传", "180") });
            AddSettingRow(grid, 2, "文件改完多久后才上传：", _cboSettle);

            _cboMaxSize = MakeCombo(new ComboOption[] {
                new ComboOption("最大 100 MB", "100"),
                new ComboOption("最大 500 MB", "500"),
                new ComboOption("最大 1 GB", "1024"),
                new ComboOption("最大 2 GB", "2048"),
                new ComboOption("最大 4 GB", "4096") });
            AddSettingRow(grid, 3, "单个文件最大：", _cboMaxSize);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机时自动启动";
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.ForeColor = Theme.TextPrimary;
            _chkAutoStart.Margin = new Padding(0, Theme.S(6), 0, 0);
            AddSettingRow(grid, 4, "启动项：", _chkAutoStart);

            _btnSave = MakeButton("保存设置", 120);
            _btnSave.Click += delegate { SaveSettings(); };
            AddSettingRow(grid, 5, "", _btnSave);

            var hint = new Label();
            hint.Text = "备份不会删除本地文件。日志与配置存放在：" + _dataDir;
            hint.Dock = DockStyle.Fill;
            hint.AutoEllipsis = true;
            hint.ForeColor = Theme.TextTertiary;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Margin = new Padding(0);
            grid.Controls.Add(hint, 0, 6);
            grid.SetColumnSpan(hint, 2);

            // 页脚占位：TableLayoutPanel 是 Top，其余空间留给背景，不要额外控件
            _pageSettings.Controls.SetChildIndex(grid, 0);
        }

        private Button MakeButton(string text, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Width = Theme.S(w);
            // FlatButton 自带 S(34) 高（Widgets.cs L78）。用原生 Button 时也要 34，
            // 否则 52 高的底栏在 150% 下会把文字底边裁掉（ADDENDUM：需要 34/51）。
            b.Height = Theme.S(34);
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Theme.BgSurfaceAlt;
            b.ForeColor = Theme.TextPrimary;
            b.FlatAppearance.BorderColor = Theme.BorderStrong;
            b.Margin = new Padding(0, 0, Theme.S(8), 0);
            return b;
        }

        /// <summary>登录态变化只影响状态条副信息与主操作按钮，不改运行状态。</summary>
        private void UpdateLoginUi()
        {
            RefreshUiState();
        }

        private static void AddSettingRow(TableLayoutPanel grid, int row, string label, Control ctl)
        {
            if (!string.IsNullOrEmpty(label))
            {
                var l = new Label();
                l.Text = label;
                l.AutoSize = true;
                l.ForeColor = Theme.TextPrimary;
                l.TextAlign = ContentAlignment.MiddleLeft;
                l.Margin = new Padding(0, Theme.S(14), Theme.S(12), 0);
                grid.Controls.Add(l, 0, row);
            }
            ctl.Anchor = AnchorStyles.Left;
            if (ctl is FlatCombo || ctl is ComboBox || ctl is Button)
            {
                ctl.Width = ctl is Button ? ctl.Width : Theme.S(320);
            }
            else if (ctl is TextBox)
            {
                ctl.Width = Theme.S(320);
            }
            if (ctl.Margin.Top < Theme.S(8)) ctl.Margin = new Padding(0, Theme.S(12), 0, 0);
            grid.Controls.Add(ctl, 1, row);
        }

        private void StyleInput(TextBox t)
        {
            t.BackColor = Theme.BgSurfaceAlt;
            t.ForeColor = Theme.TextPrimary;
            t.BorderStyle = BorderStyle.FixedSingle;
        }

        /// <summary>下拉选择：自绘的 FlatCombo（原生 ComboBox 的白边框/箭头无解，见 FlatCombo.cs）。
        private FlatCombo MakeCombo(ComboOption[] items)
        {
            var c = new FlatCombo();
            c.Width = Theme.S(320);
            int n = items == null ? 0 : items.Length;
            var texts = new string[n];
            var values = new string[n];
            for (int i = 0; i < n; i++)
            {
                texts[i] = items[i].Text;
                values[i] = items[i].Value;
            }
            c.SetItems(texts, values);
            if (n > 0) c.SelectedIndex = 0;
            return c;
        }

        /// <summary>设置项 -> 配置字典（全部从 ComboBox 读，不再 parseInt 文本框）。</summary>
        private Dictionary<string, object> CollectSettings()
        {
            var dirs = new List<string>();
            foreach (ListViewItem it in _lvDirs.Items) dirs.Add(it.Text);

            var cfg = new Dictionary<string, object>();
            cfg["watchDirs"] = dirs.ToArray();
            string rn = _txtRemoteName == null ? "" : _txtRemoteName.Text.Trim();
            cfg["remoteFolderName"] = string.IsNullOrEmpty(rn) ? "ThunderPanSync" : rn;

            int iv = 60, st = 5, mx = 2048;
            string s;
            s = ComboValue(_cboInterval);
            if (s != null) { try { iv = Math.Max(10, int.Parse(s)); } catch { } }
            s = ComboValue(_cboSettle);
            if (s != null) { try { st = Math.Max(0, int.Parse(s)); } catch { } }
            s = ComboValue(_cboMaxSize);
            if (s != null) { try { mx = Math.Max(1, int.Parse(s)); } catch { } }
            cfg["intervalMs"] = iv * 1000;
            cfg["settleMs"] = st * 1000;
            cfg["maxSizeMB"] = mx;
            return cfg;
        }

        private static string ComboValue(FlatCombo c)
        {
            if (c == null) return null;
            string v = c.SelectedValue;
            return string.IsNullOrEmpty(v) ? null : v;   // 空 = 没选中，调用方按"保持原值"处理
        }
        // ================= 状态机（DECISIONS §五 A-1）=================
        //
        // 唯一入口是 RefreshUiState()：任何地方都不许直接改 _pill 的文字，
        // 否则"熔断显示绿色"这类说谎缺陷会以另一种形式复发。
        //
        // 两条硬规则：
        //   1) Error 只有两个来源 —— _engineFatal，或"引擎已 ready 但 20 秒没有 status"。
        //      启动期绝不判 Error：历史上"启动慢 10 秒就报引擎异常"是纯假告警。
        //   2) Paused 必须在 Running 之前判 —— 熔断期 running 仍是 true，
        //      先判 running 就会把"已暂停"显示成绿色"正在上传"。
        private void RefreshUiState()
        {
            UiState s;
            bool stale = _haveStatus && AgeMs(_lastStatusAt) > 20000;
            if (_engineFatal) s = UiState.Error;
            else if (!_haveStatus && _engineReady && AgeMs(_lastStatusAt) > 20000) s = UiState.Error;
            else if (!_haveStatus) s = _engineRunning ? UiState.Running : UiState.Ready;
            else if (_stCircuitOpen) s = UiState.Paused;
            else if (_stDirsCount == 0) s = UiState.NotConfigured;
            else if (_stRunning) s = UiState.Running;
            else s = UiState.Ready;
            _uiState = s;

            string text;
            Color c;
            StatusPill.PillKind k;
            if (s == UiState.Error)
            {
                text = _engineFatal ? "引擎异常" : "状态不可用";
                c = Theme.Danger;
                k = StatusPill.PillKind.Error;
            }
            else if (s == UiState.Paused)
            {
                text = "已暂停（连续失败 " + _stConsecFail + " 轮）";
                c = Theme.Danger;
                k = StatusPill.PillKind.Paused;
            }
            else if (s == UiState.NotConfigured)
            {
                text = "还没有添加文件夹";
                c = Theme.Warn;
                k = StatusPill.PillKind.NotConfigured;
            }
            else if (s == UiState.Running)
            {
                text = "正在上传";
                c = Theme.Ok;
                k = StatusPill.PillKind.Running;
            }
            else
            {
                text = "待命（没在上传）";
                c = Theme.Warn;
                k = StatusPill.PillKind.Configured;
            }

            // 状态过期：不冒充"最新的"，把最后更新时刻说清楚
            if (s != UiState.Error && stale)
            {
                text = "状态不可用（最后更新 " + _lastStatusAt.ToString("HH:mm") + "）";
                c = Theme.Danger;
                k = StatusPill.PillKind.Error;
            }

            if (_pill != null)
            {
                _pill.PillState = k;
                _pill.Text = text;
                _pill.ForeColor = c;
            }

            // 进度行只在真正有轮次在跑时出现（A-7 的 24px 就是从这里支付的）
            if (_progressRow != null)
            {
                // 致命轮结束后 _roundActive 已被 done 分支置 false，若只看它，进度行会立刻隐藏，
            // 刚写进去的「本轮失败 · 原因」用户根本看不见（只有日志里有一行）。
            // 所以致命轮保留进度行；_lastRoundFatalCode 在下一次 scanning 时清空。
            bool showProgress = (s == UiState.Running) || _roundActive
                || !string.IsNullOrEmpty(_lastRoundFatalCode);
                if (_progressRow.Visible != showProgress) _progressRow.Visible = showProgress;
            }

            // 诚实性常驻条：只在 Running 态降级为单色（那正是用户最可能
            // 误以为"备份=可恢复"的时刻，其余态加强提示）
            if (_lblHonesty != null)
            {
                bool running = (s == UiState.Running);
                _lblHonesty.ForeColor = running ? Theme.TextSecondary : Theme.Warn;
                _lblHonestyTail.ForeColor = Theme.TextSecondary;
            }

            ResolvePrimaryAction();
        }

        private static long AgeMs(DateTime t)
        {
            if (t == DateTime.MinValue) return long.MaxValue;
            long ms = (long)(DateTime.Now - t).TotalMilliseconds;
            return ms < 0 ? 0 : ms;
        }

        /// <summary>主操作按钮的唯一判定点（DECISIONS §3.8：登录态只影响按钮，不改状态）。</summary>
        private void ResolvePrimaryAction()
        {
            if (_btnSync == null) return;
            bool loggedIn = _loggedIn;
            if (!loggedIn)
            {
                _btnSync.Text = "登录迅雷云盘";
                _btnSync.Enabled = true;
                _btnStart.Enabled = false;
                _chipLogin.Value = "未登录";
                _chipLogin.ValueColor = Theme.Warn;
            }
            else
            {
                _btnSync.Text = "立即同步一次";
                _btnSync.Enabled = (_uiState != UiState.Error);
                _btnStart.Enabled = (_stDirsCount > 0);
                _chipLogin.Value = "已登录";
                _chipLogin.ValueColor = Theme.Ok;
            }
        }

        private void DoPrimary()
        {
            if (!_loggedIn) { DoLogin(false); return; }
            DoSync();
        }

        // ================= 定时器（A-4）=================
        private void StartTimers()
        {
            _tickTimer = new System.Windows.Forms.Timer();
            _tickTimer.Interval = 1000;
            _tickTimer.Tick += delegate { OnTick(); };
            _tickTimer.Start();

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 10000;
            _pollTimer.Tick += delegate { RefreshStatus(); };
            _pollTimer.Start();
        }

        /// <summary>1 秒：秒表、相对时间、状态过期检测。</summary>
        private void OnTick()
        {
            if (_roundActive && _roundStartedAt != DateTime.MinValue && _lblProgress != null)
            {
                // 只在该行还显示着进度文本时补秒表，避免把 done 分支的文案覆盖掉
                string cur = _lblProgress.Text;
                int at = cur.LastIndexOf("  ·  已用 ");
                if (at >= 0) cur = cur.Substring(0, at);
                _lblProgress.Text = cur + "  ·  已用 " + FormatElapsed(DateTime.Now - _roundStartedAt);
            }
            // 状态过期是"时间引起的变化"，没有 status 帧推不动它，必须靠 tick
            RefreshUiState();
        }

        private static string FormatElapsed(TimeSpan ts)
        {
            if (ts.TotalSeconds < 0) ts = TimeSpan.Zero;
            int total = (int)ts.TotalSeconds;
            int m = total / 60;
            int s = total % 60;
            return m + ":" + (s < 10 ? "0" + s : s.ToString());
        }

        // ================= 进度（A-3：纯文件个数；不显示速率、不显示推算时间）=================
        private void ApplyProgress(Dictionary<string, object> d)
        {
            if (d == null || _flatProgress == null) return;
            string phase = GetStr(d, "phase", "uploading");
            int idx = GetInt(d, "index", 0);
            int total = GetInt(d, "total", 0);
            string name = GetStr(d, "name", "");
            long bytesDone = GetLong(d, "bytesDone", 0);
            long bytesTotal = GetLong(d, "bytesTotal", 0);

            if (phase == "done")
            {
                // 致命轮与"真的没文件"在 uploaded/failed 上完全同形（都是 0/0），
                // 只有 fatalCode 能区分。这里必须先把 fatalCode 记下来给
                // OnRunComplete 用（A-2 的覆盖 bug 就死在没这个字段上）。
                string fatalCode = GetStr(d, "fatalCode", null);
                _lastRoundFatalCode = fatalCode;
                _roundActive = false;
                _roundStartedAt = DateTime.MinValue;
                int up = GetInt(d, "uploaded", -1);
                int bad = GetInt(d, "failed", -1);

                if (!string.IsNullOrEmpty(fatalCode))
                {
                    _flatProgress.Indeterminate = false;
                    _flatProgress.Ratio = 0;
                    _flatProgress.FillColor = Theme.Danger;
                    _lblProgress.ForeColor = Theme.Danger;
                    _lblProgress.Text = "本轮失败 · " + FatalCodeText(fatalCode);
                }
                else if (up < 0 || bad < 0)
                {
                    SetProgressIdle("本轮已结束 · 共 " + total + " 个文件");
                }
                else if (bad == 0)
                {
                    _flatProgress.Indeterminate = false;
                    _flatProgress.Ratio = bad == 0 && up == 0 && total == 0 ? 0 : 1;
                    _flatProgress.FillColor = Theme.Ok;
                    _lblProgress.ForeColor = Theme.Ok;
                    _lblProgress.Text = up == 0
                        ? "本轮已结束 · 没有需要上传的新文件"
                        : "本轮已结束 · 成功上传 " + up + " 个文件";
                }
                else
                {
                    _flatProgress.Indeterminate = false;
                    _flatProgress.Ratio = up > 0 ? 1 : 0;
                    _flatProgress.FillColor = Theme.Danger;
                    _lblProgress.ForeColor = up > 0 ? Theme.Warn : Theme.Danger;
                    _lblProgress.Text = up > 0
                        ? "部分成功 · 成功 " + up + "，失败 " + bad + "（原因见下方失败列表）"
                        : "本轮失败 " + bad + " 个文件（原因见下方失败列表）";
                }
                RefreshUiState();
                return;
            }

            if (phase == "scanning")
            {
                // 新一轮开始：清掉上一轮的致命标记，否则 showProgress 会让进度行永久占着 36px
                _lastRoundFatalCode = null;
                SetProgressIdle("正在扫描…" + (total > 0 ? ("（" + total + " 个文件）") : ""));
                RefreshUiState();
                return;
            }

            // 上传中：进度 = 已完成文件数 / 总文件数（纯文件个数，不再把
            // "文件数"和"文件内字节比"相加 —— 那是不同单位相加，会跳格）。
            _roundActive = true;
            if (_roundStartedAt == DateTime.MinValue) _roundStartedAt = DateTime.Now;

            int doneFiles = idx > 0 ? idx - 1 : 0;
            if (doneFiles > total) doneFiles = total;
            if (total > 0)
            {
                _flatProgress.Indeterminate = false;
                _flatProgress.Ratio = (double)doneFiles / (double)total;
            }
            else
            {
                // 总数未知：用不确定态流动，绝不假装百分比
                _flatProgress.Indeterminate = true;
            }
            _flatProgress.FillColor = Theme.Info;

            var sb = new StringBuilder();
            sb.Append("第 ");
            sb.Append(idx > 0 ? idx : 1);
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
            if (_roundStartedAt != DateTime.MinValue)
            {
                sb.Append("  ·  已用 ");
                sb.Append(FormatElapsed(DateTime.Now - _roundStartedAt));
            }
            _lblProgress.ForeColor = Theme.TextPrimary;
            _lblProgress.Text = sb.ToString();
            RefreshUiState();
        }

        private void SetProgressIdle(string text)
        {
            if (_flatProgress == null) return;
            _flatProgress.Indeterminate = false;
            _flatProgress.Ratio = 0;
            _flatProgress.FillColor = Theme.Info;
            if (_lblProgress != null)
            {
                // 只有"真的没事"时才用灰色：带失败信息时保持调用方设的告警色，
                // 否则会出现"满格→归零→灰色中性文本"的成功错觉（实测复现过）。
                bool looksLikeFailure = !string.IsNullOrEmpty(text)
                    && (text.IndexOf("失败") >= 0 || text.IndexOf("未完成") >= 0);
                if (!looksLikeFailure) _lblProgress.ForeColor = Theme.TextTertiary;
                _lblProgress.Text = string.IsNullOrEmpty(text) ? "空闲" : text;
            }
        }

        private void UiFatal(string msg)
        {
            // 诊断：把"谁判了致命错误 + 调用栈"写进 ui-fatal.log。
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] UiFatal: " + msg);
                sb.AppendLine(Environment.StackTrace);
                File.AppendAllText(Path.Combine(_dataDir, "ui-fatal.log"), sb.ToString());
            }
            catch (Exception) { }

            string detail = msg;
            try { detail = _host.BuildFatalDetail(msg, 100); } catch { }
            UiInvoke(delegate
            {
                _engineFatal = true;
                _engineRunning = false;
                AppendLog("[错误] " + detail);
                SetProgressIdle("已停止（引擎异常）");
                RefreshUiState();
            });
        }

        // ================= 状态渲染（ApplyStatus）=================
        private void ApplyStatus(Dictionary<string, object> d)
        {
            try
            {
                var cfg = d.ContainsKey("config") ? d["config"] as Dictionary<string, object> : null;
                var st = d.ContainsKey("status") ? d["status"] as Dictionary<string, object> : null;

                if (cfg != null)
                {
                    // A-9：回填会吃掉用户正在输入的内容 —— 焦点在谁身上就不动谁
                    if (_txtRemoteName != null && !_txtRemoteName.Focused)
                        _txtRemoteName.Text = GetStr(cfg, "remoteFolderName", "ThunderPanSync");
                    if (_cboInterval != null && !_cboInterval.Focused)
                        SelectComboValue(_cboInterval, (GetInt(cfg, "intervalMs", 60000) / 1000).ToString(), " 分钟");
                    if (_cboSettle != null && !_cboSettle.Focused)
                        SelectComboValue(_cboSettle, (GetInt(cfg, "settleMs", 5000) / 1000).ToString(), " 秒");
                    if (_cboMaxSize != null && !_cboMaxSize.Focused)
                        SelectComboValue(_cboMaxSize, GetInt(cfg, "maxSizeMB", 2048).ToString(), " MB");

                    // 文件夹列表：内容变了才重建（重建会丢选中、也会闪）
                    var want = new List<string>();
                    object wd;
                    if (cfg.TryGetValue("watchDirs", out wd) && wd is object[])
                    {
                        foreach (var x in (object[])wd) want.Add(Convert.ToString(x));
                    }
                    bool same = (want.Count == _lvDirs.Items.Count);
                    if (same)
                    {
                        for (int i = 0; i < want.Count; i++)
                        {
                            if (_lvDirs.Items[i].Text != want[i]) { same = false; break; }
                        }
                    }
                    if (!same)
                    {
                        _lvDirs.BeginUpdate();
                        try
                        {
                            _lvDirs.Items.Clear();
                            for (int i = 0; i < want.Count; i++) _lvDirs.Items.Add(new ListViewItem(want[i]));
                        }
                        finally { _lvDirs.EndUpdate(); }
                        _lvDirs.FitColumns();
            _lvDirs.RefreshColumns();
                    }
                    _stDirsCount = _lvDirs.Items.Count;
                    _chipDirs.Value = _stDirsCount.ToString();
                }

                if (st != null)
                {
                    _haveStatus = true;
                    _lastStatusAt = DateTime.Now;
                    _stRunning = st.ContainsKey("running") && Convert.ToBoolean(st["running"]);
                    _stCircuitOpen = st.ContainsKey("circuitOpen") && Convert.ToBoolean(st["circuitOpen"]);
                    _stConsecFail = GetInt(st, "consecutiveFailures", 0);
                    // _engineRunning 的语义是"真的在自动备份"：熔断期 running 仍为 true，
                    // 但那时并没有在上传，托盘/状态条都不能说"备份仍在运行"。
                    _engineRunning = _stRunning && !_stCircuitOpen;

                    _stUploadedTotal = GetInt(st, "uploadedTotal", 0);
                    _stFailedTotal = GetInt(st, "failedTotal", 0);
                    _chipUploaded.Value = _stUploadedTotal.ToString();
                    _chipFailed.Value = _stFailedTotal.ToString();

                    FillFailedList(st);

                    var lf = GetStr(st, "logFile", "");
                    if (!string.IsNullOrEmpty(lf)) _currentLogFile = lf;
                }

                if (_lvDirs != null)
                {
                    _stDirsCount = _lvDirs.Items.Count;
                    _chipDirs.Value = _stDirsCount.ToString();
                }
                RefreshUiState();
            }
            catch { }
        }

        /// <summary>下拉框选中指定值；不在推荐列表里时追加一项（绝不静默取整）。</summary>
        private static void SelectComboValue(FlatCombo cbo, string value, string unit)
        {
            if (cbo == null) return;
            for (int i = 0; i < cbo.Count; i++)
            {
                if (cbo.ValueAt(i) == value)
                {
                    if (cbo.SelectedIndex != i) cbo.SelectedIndex = i;
                    return;
                }
            }
            // 非预设值（例如 3000 MB）：追加一项并选中，让用户看见真实配置
            cbo.AddItem(value + unit + "（当前设置，不在推荐值里）", value);
            cbo.SelectedIndex = cbo.Count - 1;
        }

        /// <summary>失败列表：按"内容签名"比较，不再只比数量（A-5）。</summary>
        private void FillFailedList(Dictionary<string, object> st)
        {
            try
            {
                object raw;
                object[] arr = null;
                if (st != null && st.TryGetValue("failedFiles", out raw)) arr = raw as object[];

                var sig = new StringBuilder();
                int n = arr == null ? 0 : arr.Length;
                for (int i = 0; i < n; i++)
                {
                    var e = arr[i] as Dictionary<string, object>;
                    if (e == null) continue;
                    sig.Append(GetStr(e, "file", "")).Append('\u0001');
                    sig.Append(GetInt(e, "count", 0)).Append('\u0001');
                    sig.Append(GetStr(e, "error", "")).Append('\u0002');
                }
                string s = sig.ToString();
                if (s == _failSig) { UpdateFailPanelHeight(n); return; }
                _failSig = s;

                _lvFail.BeginUpdate();
                try
                {
                    _lvFail.Items.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var e = arr[i] as Dictionary<string, object>;
                        if (e == null) continue;
                        var full = GetStr(e, "file", "");
                        var nameOnly = full;
                        int slash = full.LastIndexOf('\\');
                        if (slash >= 0 && slash + 1 < full.Length) nameOnly = full.Substring(slash + 1);
                        if (nameOnly.Length == 0) nameOnly = full;

                        var it = new ListViewItem(nameOnly);
                        it.ToolTipText = full;
                        it.SubItems.Add(GetInt(e, "count", 0).ToString());
                        it.SubItems.Add(GetStr(e, "error", "(无原因)"));
                        _lvFail.Items.Add(it);
                    }
                }
                finally { _lvFail.EndUpdate(); }
                _lvFail.FitColumns();
                _lvFail.RefreshColumns();   // 加行让滚动条出现不触发 OnResize，列宽会停在旧值
            _lvFail.TabStop = false;
                UpdateFailPanelHeight(n);
            }
            catch { }
        }

        /// <summary>失败区三档：0（无失败）/ 44（只有摘要一行）/ 156（标签+列表）。</summary>
        private void UpdateFailPanelHeight(int n)
        {
            if (_failPanel == null) return;
            if (n <= 0)
            {
                _failPanel.Visible = false;
                int failH = 0;
                _failPanel.Height = failH;
                _lblFailSummary.Visible = false;
                _lvFail.Visible = false;
                return;
            }
            _failPanel.Visible = true;
            // failedFiles 只返回前 50 个，标题要说清"总共多少"
            if (_stFailedTotal > n)
                _lblFail.Text = "失败 " + _stFailedTotal + " 个，下面只显示前 " + n + " 个（原因见右列）：";

            else
                _lblFail.Text = "失败的文件（原因在这里，修好后点「立即同步一次」会重试）：";

            if (n <= 2)
            {
                // 一两个失败不值得占用日志的高度：只显示一行摘要
                _lvFail.Visible = false;
                _lblFailSummary.Visible = true;
                _lblFailSummary.Text = "共 " + n + " 个：" + (_lvFail.Items.Count > 0 ? _lvFail.Items[0].Text : "") + " 等";
                _failPanel.Height = Theme.S(44);
            }
            else
            {
                _lvFail.Visible = true;
                _lblFailSummary.Visible = false;
                _failPanel.Height = Theme.S(156);
            }
        }

        // ================= 托盘 / 表单事件 =================
        private void BuildTray()
        {
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
        }

        private void WireFormEvents()
        {
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                // 点 X = 最小化到托盘，不退出（备份继续跑）
                if (!_reallyQuit)
                {
                    e.Cancel = true;
                    Hide();
                    // 这句必须说实话：只有真在备份时才说"备份仍在后台运行"
                    if (_engineRunning)
                        _tray.ShowBalloonTip(2000, "ThunderPanSync", "已最小化到托盘，备份仍在后台运行", ToolTipIcon.Info);
                    else
                        _tray.ShowBalloonTip(4000, "ThunderPanSync",
                            "已最小化到托盘。注意：当前没有在备份", ToolTipIcon.Warning);
                }
            };
        }

        private void OpenLogFile()
        {
            // 日志按天分文件，打开写死的 sync.log 等于给用户看一个死文件
            var f = _currentLogFile;
            if (string.IsNullOrEmpty(f) || !File.Exists(f)) f = FindNewestLogFile();
            if (!string.IsNullOrEmpty(f) && File.Exists(f))
            {
                try { Process.Start("notepad.exe", "\"" + f + "\""); }
                catch (Exception ex) { MessageBox.Show("打开日志失败：" + ex.Message, "ThunderPanSync"); }
            }
            else
            {
                MessageBox.Show("还没有日志文件。日志目录：" + _dataDir, "ThunderPanSync");
            }
        }

        // ================= 工作区夹取（DECISIONS §3.1）=================
        // MinimumSize 必须乘 Theme.S()，否则高 DPI 下不变大（DarkForm 的
        // WM_GETMINMAXINFO 是原样直传 this.MinimumSize）；但 150% 下
        // Theme.S(940)=1410 可能超出屏幕，所以还要夹到工作区。
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ClampToWorkingArea();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_DPICHANGED：运行时换屏 / 改缩放必须重算，否则最小尺寸留在旧档
            if (m.Msg == 0x02E0) ClampToWorkingArea();
        }

        private void ClampToWorkingArea()
        {
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;   // 物理像素
                int w = Math.Min(Theme.S(940), wa.Width);
                int h = Math.Min(Theme.S(700), wa.Height);
                var want = new Size(w, h);
                if (MinimumSize != want) MinimumSize = want;
                if (Width < w || Height < h)
                    Size = new Size(Math.Max(Width, w), Math.Max(Height, h));
            }
            catch { }
        }

        // P3：两个 WinForms Timer 原来从不 Stop/Dispose。窗体 Dispose 之后它们仍可能
        // 再 tick 一次，RefreshUiState() 就会碰已释放的控件（跨线程/已释放句柄的隐患）。
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tickTimer != null) { try { _tickTimer.Stop(); _tickTimer.Dispose(); } catch (Exception) { } }
                if (_pollTimer != null) { try { _pollTimer.Stop(); _pollTimer.Dispose(); } catch (Exception) { } }
            }
            base.Dispose(disposing);
        }
}

    }
namespace ThunderPanSync
{
    /// <summary>
    /// 界面状态（DECISIONS §五 A-1）。顺序是文档序，不是优先级 ——
    /// 判定链只有 MainForm.RefreshUiState() 一处。
    /// </summary>
    internal enum UiState
    {
        Error,
        Paused,
        NotConfigured,
        Running,
        Ready
    }

    /// <summary>ComboBox 的一项：显示文本 + 实际取值（字符串，便于回填比较）。</summary>
    internal sealed class ComboOption
    {
        public readonly string Text;
        public readonly string Value;
        public ComboOption(string text, string value) { Text = text; Value = value; }
        public override string ToString() { return Text; }
    }
}

