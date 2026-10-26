// UiPreview.cs -- 视觉层自检窗口（开发期工具，不参与产品构建）
//
// 作用：把 Theme / Drawing / Widgets / DarkForm 的所有视觉元素摆在一屏里，
// 用肉眼确认配色、圆角、进度条、状态胶囊真的像设计的那样。
// 纯自绘的东西没法靠单元测试验证好看不好看，只能看。
//
// 编译（不参与 build-native.ps1）：
//   $csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
//   & $csc /nologo /target:winexe /win32manifest:native\app.manifest /out:"$env:TEMP\preview.exe" `
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll `
//     native\Theme.cs native\Drawing.cs native\Widgets.cs native\DarkForm.cs native\UiPreview.cs
using System;
using System.Drawing;
using System.Windows.Forms;

namespace ThunderPanSync
{
    internal class PreviewForm : DarkForm
    {
        private StatusPill _pill;
        private FlatProgress _prog;
        private Label _lblProg, _lblHint;
        private Panel _progPanel;
        private SegmentedTabs _tabs;
        private TextBox _log;

        public PreviewForm()
        {
            Text = "ThunderPanSync · 视觉层自检";
            ClientSize = new Size(920, 700);
            AutoScaleMode = AutoScaleMode.None;
            // DarkForm 只自绘顶部 TitleBarPx 高的标题栏，内容区必须自己让出来
            // （和 MainForm 一样）。原来漏了这行 ⇒ 顶部 34px 被内容盖住，标题栏/标签条验证不了。
            Padding = new Padding(0, TitleBarPx, 0, 0);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;

            // ---- Footer（先 Add Fill 的不行，这里按 SPEC 的 Dock 顺序：Fill 最先）----
            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(14), Theme.S(8));
            content.BackColor = Theme.BgWindow;
            Controls.Add(content);

            BuildFooter();
            BuildProgress();

            // ---- Header ----
            var header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Theme.S(58);
            header.BackColor = Theme.BgBar;
            header.Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(14), Theme.S(6));
            Controls.Add(header);
            Controls.SetChildIndex(header, 0);

            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = false;
            flow.BackColor = Theme.BgBar;
            header.Controls.Add(flow);

            _pill = new StatusPill();
            _pill.PillState = StatusPill.PillKind.Running;
            _pill.Text = "正在备份";
            _pill.Margin = new Padding(0, 0, Theme.S(16), 0);
            flow.Controls.Add(_pill);

            flow.Controls.Add(Chip("文件夹", "3"));
            flow.Controls.Add(Chip("已上传", "1,284"));
            flow.Controls.Add(Chip("失败", "0"));
            flow.Controls.Add(Chip("远程", "已就绪"));

            // ---- Content ----
            _tabs = new SegmentedTabs();
            _tabs.Tabs = new string[] { "备份", "设置" };
            _tabs.Dock = DockStyle.Top;
            content.Controls.Add(_tabs);

            var body = new Panel();
            body.Dock = DockStyle.Fill;
            body.BackColor = Theme.BgWindow;
            content.Controls.Add(body);
            content.Controls.SetChildIndex(body, 0);

            var card = new CardPanel();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(Theme.S(12));
            body.Controls.Add(card);

            var title = new Label();
            title.Text = "最近活动";
            title.Dock = DockStyle.Top;
            title.Height = Theme.S(24);
            title.Font = Theme.FontSubtitle;
            title.ForeColor = Theme.TextSecondary;
            title.BackColor = Theme.BgSurface;
            card.Controls.Add(title);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Theme.BgInset;
            _log.ForeColor = Theme.TextSecondary;
            _log.Font = Theme.FontMonoZh;
            _log.Text = "[20:31:02] 开始备份 7 个文件\r\n"
                + "[20:31:03] 已进入 \"ThunderPanSync\"\r\n"
                + "[20:31:05] 上传 季度报告.pdf (2.4 MB)\r\n"
                + "[20:31:08] ✓ 季度报告.pdf 上传成功（云端已核实）\r\n"
                + "[20:31:09] 跳过 空文件.txt（0 字节）\r\n"
                + "[20:31:12] 上传 设计稿.psd (48.2 MB)\r\n"
                + "[20:31:41] ✓ 设计稿.psd 上传成功（云端已核实）\r\n"
                + "\r\n"
                + "深色滚动条验证：右下角滚动条应该是深灰而不是亮白。\r\n"
                + "如果它是亮白的，说明 SetWindowTheme 没生效。\r\n";
            card.Controls.Add(_log);
            card.Controls.SetChildIndex(_log, 0);
            _log.Height = 200;

            Shown += delegate
            {
                // 滚动条深色化必须在 Handle 建好之后
                Draw.TryDarkScrollbars(_log);
            };
        }

        private void BuildFooter()
        {
            var footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(58);
            footer.BackColor = Theme.BgBar;
            footer.Padding = new Padding(Theme.S(14), Theme.S(12), Theme.S(14), Theme.S(12));
            Controls.Add(footer);
            Controls.SetChildIndex(footer, 0);

            var f = new FlowLayoutPanel();
            f.Dock = DockStyle.Fill;
            f.FlowDirection = FlowDirection.LeftToRight;
            f.WrapContents = false;
            f.BackColor = Theme.BgBar;
            footer.Controls.Add(f);

            f.Controls.Add(Btn("开始自动备份", FlatButton.Kind.Primary, 132));
            f.Controls.Add(Btn("现在备份一次", FlatButton.Kind.Secondary, 116));
            f.Controls.Add(Btn("取消本轮", FlatButton.Kind.Secondary, 96));
            f.Controls.Add(Btn("打开日志", FlatButton.Kind.Ghost, 96));
            f.Controls.Add(Btn("退出", FlatButton.Kind.Danger, 76));
        }

        private void BuildProgress()
        {
            _progPanel = new Panel();
            _progPanel.Dock = DockStyle.Top;
            _progPanel.Height = Theme.S(38);
            _progPanel.BackColor = Theme.BgBar;
            _progPanel.Padding = new Padding(Theme.S(14), Theme.S(4), Theme.S(14), Theme.S(8));
            Controls.Add(_progPanel);
            Controls.SetChildIndex(_progPanel, 0);

            _lblProg = new Label();
            _lblProg.Text = "第 3/7 个 · 季度报告.pdf · 已传 240 MB / 1.8 GB · 已用 2:10";
            _lblProg.Dock = DockStyle.Top;
            _lblProg.Height = Theme.S(18);
            _lblProg.Font = Theme.FontBody;
            _lblProg.ForeColor = Theme.TextPrimary;
            _lblProg.BackColor = Theme.BgBar;
            _progPanel.Controls.Add(_lblProg);

            _prog = new FlatProgress();
            _prog.Dock = DockStyle.Bottom;
            _prog.Height = Theme.S(8);
            _prog.Ratio = 0.43;
            _prog.FillColor = Theme.Ok;
            _progPanel.Controls.Add(_prog);
        }

        private static StatChip Chip(string label, string val)
        {
            var c = new StatChip();
            c.Label = label;
            c.Value = val;
            c.Margin = new Padding(0, Theme.S(2), Theme.S(16), 0);
            return c;
        }

        private static FlatButton Btn(string text, FlatButton.Kind kind, int w)
        {
            var b = new FlatButton();
            b.Text = text;
            b.ButtonKind = kind;
            b.Width = Theme.S(w);
            b.Margin = new Padding(0, 0, Theme.S(8), 0);
            return b;
        }
    }

    static class UiPreview
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // DPI 缩放必须在任何控件创建前初始化
            Theme.InitScale(Draw.GetDpiScale(null));
            Application.Run(new PreviewForm());
        }
    }
}
