// DarkForm.cs -- 深色窗框基类（自绘标题栏）
//
// 为什么必须自绘标题栏（这是本文件存在的唯一理由，实测数据在下面）：
//
//   Win10 19045 上的实测结果（像素采样标题栏平均亮度，非推测）：
//     baseline                L=250  #BCDEFF  LIGHT
//     DwmSetWindowAttribute(20) hr=0    L=250  #BCDEFF  LIGHT   ← 返回值成功，视觉完全没变
//     DwmSetWindowAttribute(19) hr=-2147024809（E_INVALIDARG）
//     DwmSetWindowAttribute(35) hr=-2147024809  ← CAPTION_COLOR 是 Win11 22000+ 才有的
//     DwmSetWindowAttribute(36) hr=-2147024809  ← BORDER_COLOR 同上
//
//   也就是说：在 Win10 上，系统标题栏永远是浅色的。一个自称深色的窗口，
//   最上面顶着一条纯白横条（亮度 250，是全窗最亮的元素），"套壳感"一半来自这里。
//
//   官方文档只保证 Win11 22000+ 支持。所以不能靠 API，只能自绘。
//
// 自绘标题栏的代价（必须自己实现，少一个用户就会骂）：
//   * 拖动窗口（WM_NCHITTEST 返回 HTCAPTION）
//   * 双击最大化/还原
//   * 最小化 / 最大化 / 关闭 三个按钮的 hover 与按下态
//   * 边缘 8px 的缩放热区（FormBorderStyle=None 后系统不再提供）
//   * 最大化时不能盖住任务栏（要用 WorkingArea 而不是 Screen.Bounds）
//   * 高 DPI 下按钮尺寸与热区要跟着缩放
//   * Aero Snap（拖到屏幕边缘吸附 / 双击标题栏）要保留
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ThunderPanSync
{
    /// <summary>自绘深色标题栏的窗口基类。Win10/Win11 上都能得到一致的深色外观。</summary>
    // 必须是 public：MainForm 是 public，C# 不允许基类的可访问性低于派生类
    // （error CS0060）。编译时踩过一次。
    public class DarkForm : Form
    {
        // ---------------- Win32 ----------------
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_GETMINMAXINFO = 0x0024;

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // ---------------- 外观常量 ----------------
        private const int TitleBarHeight = 34;
        private const int ResizeBorder = 6;     // FormBorderStyle=None 后自己实现缩放热区
        private const int BtnWidth = 46;

        private enum HotBtn { None, Minimize, Maximize, Close }

        private HotBtn _hot = HotBtn.None;
        private bool _closeDown, _minDown, _maxDown;

        private Rectangle _rcMin, _rcMax, _rcClose;

        public DarkForm()
        {
            // FormBorderStyle=None 是自绘标题栏的前提；否则系统会画自己的标题栏。
            FormBorderStyle = FormBorderStyle.None;
            // 保留系统阴影与可缩放边框（WS_THICKFRAME 由下面重写 CreateParams 补上）
            BackColor = Theme.BgWindow;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
        }

        // FormBorderStyle=None 会去掉 WS_THICKFRAME / WS_CAPTION，
        // 结果是没有窗口阴影、也不能拖动边缘缩放。这里手工补回来：
        //   WS_THICKFRAME (0x00040000) -> 恢复可缩放 + DWM 阴影
        //   WS_MINIMIZEBOX/WS_MAXIMIZEBOX -> 任务栏右键菜单与最大化动画正常
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00040000;  // WS_THICKFRAME
                cp.Style |= 0x00020000;  // WS_MINIMIZEBOX
                cp.Style |= 0x00010000;  // WS_MAXIMIZEBOX
                return cp;
            }
        }

        /// <summary>标题栏高度（已按 DPI 缩放）。子类布局可用它算内容区偏移。</summary>
        protected int TitleBarPx { get { return Theme.S(TitleBarHeight); } }

        private void LayoutButtons()
        {
            int h = TitleBarPx;
            int bw = Theme.S(BtnWidth);
            int right = ClientSize.Width;
            _rcClose = new Rectangle(right - bw, 0, bw, h);
            _rcMax = new Rectangle(right - bw * 2, 0, bw, h);
            _rcMin = new Rectangle(right - bw * 3, 0, bw, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutButtons();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutButtons();
            // 顺带试一下 DWM 深色（Win11 上有效，Win10 上静默失败，无害）。
            // 自绘标题栏已经覆盖了标题区域，这个调用只是为了 Win11 上的边框/阴影也变深。
            try { Draw.TryDarkTitleBar(this); } catch (Exception) { }
        }

        // ---------------- 绘制 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            DrawTitleBar(g);
        }

        private void DrawTitleBar(Graphics g)
        {
            int h = TitleBarPx;
            int w = ClientSize.Width;
            if (h <= 0 || w <= 0) return;

            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(Theme.BgBar))
            {
                g.FillRectangle(b, 0, 0, w, h);
            }
            // 标题栏底部一条 1px 分隔线，比阴影更轻，且在各种 DPI 下稳定
            using (var p = new Pen(Theme.Divider))
            {
                g.DrawLine(p, 0, h - 1, w, h - 1);
            }

            // 标题文字
            int iconW = Theme.S(28);
            var textRect = new Rectangle(iconW, 0, w - iconW - Theme.S(BtnWidth * 3 + 8), h);
            Draw.TextLeft(g, Text, Theme.FontBody, textRect, Theme.TextSecondary);

            DrawCaptionButton(g, _rcMin, HotBtn.Minimize, _minDown);
            DrawCaptionButton(g, _rcMax, HotBtn.Maximize, _maxDown);
            DrawCaptionButton(g, _rcClose, HotBtn.Close, _closeDown);
        }

        private void DrawCaptionButton(Graphics g, Rectangle r, HotBtn kind, bool down)
        {
            bool hot = (_hot == kind);
            if (hot)
            {
                Color bg = down ? Theme.BgActive
                    : (kind == HotBtn.Close ? Color.FromArgb(196, 62, 54) : Theme.BgHover);
                using (var b = new SolidBrush(bg))
                {
                    g.FillRectangle(b, r);
                }
            }
            Color fg = (hot && kind == HotBtn.Close) ? Color.White : Theme.TextSecondary;
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            int s = Theme.S(5);

            using (var p = new Pen(fg, Math.Max(1f, Theme.S(1) * 1.0f)))
            {
                switch (kind)
                {
                    case HotBtn.Minimize:
                        g.DrawLine(p, cx - s, cy + s / 2, cx + s, cy + s / 2);
                        break;
                    case HotBtn.Maximize:
                        if (WindowState == FormWindowState.Maximized)
                        {
                            // 还原图标：两个错开的方框
                            g.DrawRectangle(p, cx - s, cy - s + 1, s * 2 - 2, s * 2 - 2);
                            g.DrawRectangle(p, cx - s + 2, cy - s - 1, s * 2 - 2, s * 2 - 2);
                        }
                        else
                        {
                            g.DrawRectangle(p, cx - s, cy - s, s * 2, s * 2);
                        }
                        break;
                    case HotBtn.Close:
                        g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                        g.DrawLine(p, cx - s, cy + s, cx + s, cy - s);
                        break;
                }
            }
        }

        // ---------------- 命中测试 ----------------
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                // 先让系统给出默认判断（它只认客户区/非客户区）
                // 我们自己改写：边缘给缩放码，标题栏给 HTCAPTION
                int lp = m.LParam.ToInt32();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                Point p = PointToClient(new Point(sx, sy));

                if (WindowState != FormWindowState.Maximized)
                {
                    int rb = Theme.S(ResizeBorder);
                    bool left = p.X < rb;
                    bool right = p.X >= ClientSize.Width - rb;
                    bool top = p.Y < rb;
                    bool bottom = p.Y >= ClientSize.Height - rb;

                    if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
                    if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                    if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                    if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                    if (left) { m.Result = (IntPtr)HTLEFT; return; }
                    if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                    if (top) { m.Result = (IntPtr)HTTOP; return; }
                    if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }
                }

                // 标题栏（按钮区除外）：交给系统当标题栏 → 拖动、双击最大化、Aero Snap 全都免费
                if (p.Y >= 0 && p.Y < TitleBarPx && p.X < ClientSize.Width - Theme.S(BtnWidth * 3))
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }
                m.Result = (IntPtr)HTCLIENT;
                return;
            }

            if (m.Msg == WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                // 最大化时不能盖住任务栏：把 ptMaxSize/ptMaxPosition 限制到工作区。
                // 不做这一步，FormBorderStyle=None 的窗口最大化会盖住任务栏，
                // 用户没法点开始菜单（这是自绘窗框最容易漏掉的坑）。
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(Handle, 2 /*MONITOR_DEFAULTTONEAREST*/);
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(mon, ref mi))
                {
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    // 最小尺寸用窗体自己的 MinimumSize，不要在这里写死。
                    //
                    // 原来这里是 Theme.S(640) x Theme.S(420)，实测会**覆盖**
                    // MainForm.MinimumSize（820x600）—— 向真实窗口发 WM_GETMINMAXINFO
                    // 读回 ptMinTrackSize 恒为 640x420，不管 MinimumSize 设多少。
                    // 结果窗口能被拖到比设计更小，布局挤坏。
                    // 正确做法：有 MinimumSize 就尊重它，没有才用兜底值。
                    if (MinimumSize.Width > 0 && MinimumSize.Height > 0)
                    {
                        mmi.ptMinTrackSize.X = MinimumSize.Width;
                        mmi.ptMinTrackSize.Y = MinimumSize.Height;
                    }
                    else
                    {
                        mmi.ptMinTrackSize.X = Theme.S(640);
                        mmi.ptMinTrackSize.Y = Theme.S(420);
                    }
                    Marshal.StructureToPtr(mmi, m.LParam, true);
                }
                return;
            }

            base.WndProc(ref m);
        }

        // ---------------- 鼠标 ----------------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            HotBtn h = HitButton(e.Location);
            if (h != _hot) { _hot = h; Invalidate(new Rectangle(0, 0, ClientSize.Width, TitleBarPx)); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != HotBtn.None) { _hot = HotBtn.None; Invalidate(new Rectangle(0, 0, ClientSize.Width, TitleBarPx)); }
            base.OnMouseLeave(e);
        }

        private HotBtn HitButton(Point p)
        {
            if (_rcClose.Contains(p)) return HotBtn.Close;
            if (_rcMax.Contains(p)) return HotBtn.Maximize;
            if (_rcMin.Contains(p)) return HotBtn.Minimize;
            return HotBtn.None;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                HotBtn h = HitButton(e.Location);
                if (h == HotBtn.Close) _closeDown = true;
                else if (h == HotBtn.Minimize) _minDown = true;
                else if (h == HotBtn.Maximize) _maxDown = true;
                if (h != HotBtn.None) Invalidate(new Rectangle(0, 0, ClientSize.Width, TitleBarPx));
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                HotBtn h = HitButton(e.Location);
                bool wasClose = _closeDown, wasMin = _minDown, wasMax = _maxDown;
                _closeDown = _minDown = _maxDown = false;

                if (wasClose && h == HotBtn.Close) { Close(); return; }
                if (wasMin && h == HotBtn.Minimize) { WindowState = FormWindowState.Minimized; Invalidate(); return; }
                if (wasMax && h == HotBtn.Maximize) { ToggleMaximize(); return; }
                Invalidate(new Rectangle(0, 0, ClientSize.Width, TitleBarPx));
            }
            base.OnMouseUp(e);
        }

        private void ToggleMaximize()
        {
            WindowState = (WindowState == FormWindowState.Maximized)
                ? FormWindowState.Normal : FormWindowState.Maximized;
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            // 双击标题栏最大化（WinForms 在 HTCAPTION 下其实已经由系统处理，
            // 但 FormBorderStyle=None 时行为不稳定，这里显式兜一层）
            Point p = PointToClient(Cursor.Position);
            if (p.Y >= 0 && p.Y < TitleBarPx && p.X < ClientSize.Width - Theme.S(BtnWidth * 3))
            {
                ToggleMaximize();
                return;
            }
            base.OnDoubleClick(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 整窗铺深色底：FormBorderStyle=None 后没有系统背景可依赖
            using (var b = new SolidBrush(Theme.BgWindow))
            {
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }
    }
}
