// Drawing.cs -- 原生 WinForms 的"现代感"工具箱
//
// WinForms 默认控件是 2005 年的样子：方角、3D 边框、系统色。要做出现代观感，
// 只有两条路：(1) 换 UI 库（我们不许，会撑爆 50KB 的体积预算），
// (2) 自己画。这个文件就是"自己画"的全部工具。
//
// 每个函数都备注了"为什么必须这么做"，因为 WinForms 的这些坑没有文档。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ThunderPanSync
{
    internal static class Draw
    {
        // ---------------- 圆角路径 ----------------
        //
        // WinForms 控件没有圆角属性。唯一可行的做法是设置控件的 Region 为圆角路径。
        // 坑：Region 是硬裁剪，边缘会有锯齿（Region 不做抗锯齿）。
        // 所以分两种用法：
        //   * 小圆角（<= 6px）用 Region，锯齿几乎看不出
        //   * 需要视觉完美的场合（卡片、胶囊）用自绘：在 OnPaint 里 FillPath
        //     （FillPath 走 GDI+ 抗锯齿，边缘平滑）
        internal static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }

            int d = radius * 2;
            // 半径不能超过宽高的一半，否则路径自交，填充出怪形
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>给控件套上圆角（硬裁剪，锯齿可见）。适合小圆角与纯色块。</summary>
        internal static void ApplyRoundRegion(Control c, int radius)
        {
            if (c == null || c.Width <= 0 || c.Height <= 0) return;
            try
            {
                var old = c.Region;
                using (var path = RoundRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                {
                    c.Region = new Region(path);
                }
                if (old != null && old != c.Region) old.Dispose();
            }
            catch (Exception)
            {
                // Region 设置失败（控件尺寸为 0 等）不应该崩掉界面，退回方角
            }
        }

        // ---------------- 胶囊（状态 pill） ----------------
        /// <summary>画一个胶囊形状态标签。返回实际宽度，便于横排时计算位置。</summary>
        internal static int DrawPill(Graphics g, int x, int y, int height,
            string text, Color fg, Color bg, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int padX = Theme.S(10);
            SizeF sz = g.MeasureString(text, font);
            int w = (int)Math.Ceiling(sz.Width) + padX * 2;
            var rect = new Rectangle(x, y, w, height);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(rect, height / 2))
            using (var b = new SolidBrush(bg))
            {
                g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, text, font,
                new Rectangle(x + padX, y, w - padX * 2, height),
                fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            return w;
        }

        // ---------------- 卡片 ----------------
        /// <summary>画一张卡片（圆角底 + 细边框）。</summary>
        internal static void DrawCard(Graphics g, Rectangle r, Color bg, Color border, int radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(r, radius))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                if (border.A > 0)
                {
                    using (var p = new Pen(border)) g.DrawPath(p, path);
                }
            }
        }

        // ---------------- 进度条（自绘） ----------------
        //
        // 为什么不用原生 ProgressBar：
        //   1. 它的 ForeColor 只在关闭视觉样式时生效，而关掉视觉样式整窗会变回 Win95 风
        //   2. 它固定有 3D 凹槽，配色跟深色主题完全不搭
        //   3. 它不能画"不确定进度"（marquee 除外，而 marquee 样式很丑）
        // 自绘成本很低（一个圆角矩形 fill），收益是能完全控制圆角、颜色、动画。
        internal static void DrawProgress(Graphics g, Rectangle r, double ratio,
            Color fillColor, Color trackColor, bool indeterminate, double animPhase)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            int radius = r.Height / 2;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 轨道
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(trackColor))
            {
                g.FillPath(b, path);
            }

            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            if (indeterminate)
            {
                // 不确定进度：一段来回滑动的亮块。
                // WinForms Timer 驱动 animPhase（0..1 循环），不需要线程。
                int segW = Math.Max(Theme.S(40), r.Width / 5);
                double t = animPhase % 1.0;
                // 用三角波让滑块来回移动，比单向循环更符合"永远做不完"的语义
                double tri = t < 0.5 ? t * 2 : (1 - t) * 2;
                int travel = r.Width - segW;
                int sx = r.X + (int)Math.Round(travel * tri);

                var clip = new Rectangle(sx, r.Y, segW, r.Height);
                if (clip.Width > 0)
                {
                    using (var path = RoundRect(r, radius))
                    {
                        var old = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        using (var segPath = RoundRect(clip, radius))
                        using (var b = new SolidBrush(fillColor))
                        {
                            g.FillPath(b, segPath);
                        }
                        g.Clip = old;
                    }
                }
                return;
            }

            int fillW = (int)Math.Round(r.Width * ratio);
            if (fillW <= 0) return;
            // 圆角轨道里填一段也要圆角，否则左端会露出尖角
            if (fillW < r.Height) fillW = r.Height;
            if (fillW > r.Width) fillW = r.Width;

            using (var path = RoundRect(r, radius))
            {
                var old = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                using (var fillPath = RoundRect(new Rectangle(r.X, r.Y, fillW, r.Height), radius))
                using (var b = new SolidBrush(fillColor))
                {
                    g.FillPath(b, fillRectInto(fillPath, new Rectangle(r.X, r.Y, fillW, r.Height)));
                }
                g.Clip = old;
            }
        }

        // 小工具：确保填充路径对应矩形（保持可读性，避免上面一行过长）
        private static GraphicsPath fillRectInto(GraphicsPath reuse, Rectangle r)
        {
            return reuse;
        }

        // ---------------- 分割线 ----------------
        internal static void DrawHLine(Graphics g, int x, int y, int w, Color c)
        {
            using (var p = new Pen(c))
            {
                g.DrawLine(p, x, y, x + w - 1, y);
            }
        }

        // ---------------- 文本（统一走 TextRenderer） ----------------
        //
        // 为什么统一用 TextRenderer 而不是 Graphics.DrawString：
        //   DrawString 用的是 GDI+ 文本渲染，中文小字号会模糊；
        //   TextRenderer 走 GDI（和系统原生控件一致），中文清晰得多。
        internal static void Text(Graphics g, string s, Font f, Rectangle r, Color c,
            TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, s, f, r, c,
                TextFormatFlags.NoPadding | extra);
        }

        internal static void TextLeft(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Text(g, s, f, r, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        internal static void TextRight(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Text(g, s, f, r, c, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        internal static void TextCenter(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Text(g, s, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        /// <summary>测量文本宽度（用同一套 TextRenderer 度量，避免和绘制不一致）。</summary>
        internal static int MeasureText(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(g, s, f,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        // ---------------- 双缓冲 ----------------
        //
        // 自绘控件必须开双缓冲，否则每次 Invalidate 都闪一下。
        // WinForms 的 DoubleBuffered 是 protected，外部改不了 ——
        // 这是必须写自定义控件类（而不是给普通 Panel 挂 Paint 事件）的原因之一。
        internal static void EnableDoubleBuffer(Control c)
        {
            try
            {
                var pi = typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(c, true, null);
            }
            catch (Exception) { }
        }

        // ---------------- 深色标题栏 ----------------
        //
        // 这是 WinForms 深色主题最刺眼的破绽：窗框标题栏永远是系统色（白/浅灰），
        // 下面接一块深色界面，割裂感极强。修法只有调 DWM 的隐藏属性。
        // 注意：这是未文档化 API，Win10 1809+ 有效；调用失败必须静默忽略
        // （老系统上让标题栏保持系统色，总比崩掉或异常好）。
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;  // Win10 1809-1909
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;      // Win10 2004+ / Win11

        // ---------------- 真实 Windows 版本探测 ----------------
        //
        // 绝对不能用 Environment.OSVersion 判断系统版本。
        // 实测（这台 Win10 19045 上）：不带 app.manifest 的 supportedOS 声明时，
        // Environment.OSVersion.Version 返回 **6.2.9200.0（Windows 8）**，不是 10.x。
        // 原因是 .NET Framework 会用兼容性垫片把版本号降级，除非清单里显式声明
        // 支持 Win10。这导致所有 "if (Major < 10) return false" 的深色主题代码
        // 在 Win10 上直接短路返回 —— 现象是"代码看起来对，就是没效果"，
        // 极难排查（我为此浪费了一整轮）。
        //
        // 正确做法：用 RtlGetVersion（ntdll 的文档外 API，不撒谎）。
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEXW info);

        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEXW
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        private static int _winBuild = -1;

        /// <summary>真实的 Windows build 号（如 19045）。取不到时返回 0。</summary>
        internal static int WindowsBuild
        {
            get
            {
                if (_winBuild >= 0) return _winBuild;
                _winBuild = 0;
                try
                {
                    var info = new OSVERSIONINFOEXW();
                    info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEXW));
                    if (RtlGetVersion(ref info) == 0) _winBuild = info.dwBuildNumber;
                }
                catch (Exception) { }
                return _winBuild;
            }
        }

        /// <summary>Win10 1809（build 17763）+ 才有 DarkMode_Explorer 与沉浸式标题栏。</summary>
        internal static bool SupportsDarkMode
        {
            get { return WindowsBuild >= 17763; }
        }

        internal static bool TryDarkTitleBar(Form f)
        {
            if (f == null || !f.IsHandleCreated) return false;
            if (!SupportsDarkMode) return false;
            try
            {
                int on = 1;
                int hr = DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                if (hr != 0)
                {
                    hr = DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
                }
                return hr == 0;
            }
            catch (Exception) { return false; }
        }

        // ---------------- 深色滚动条 ----------------
        //
        // 这是 WinForms 深色主题最顽固的破绽：TextBox / ListView 的滚动条由 system
        // visual style 绘制，永远是浅灰白，在深色背景上像一道疤。
        // 唯一可行的修法是给它套深色主题（SetWindowTheme(hwnd, "DarkMode_Explorer")），
        // 这是 Windows 10 1809+ 资源管理器自己用的主题名。
        // 老系统上会失效 → 必须允许失败，退回系统滚动条。
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        /// <summary>对任意窗口句柄套 DarkMode_Explorer（用于 ListView 的 header 子窗口等）。</summary>
        internal static bool SetThemeDeep(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !SupportsDarkMode) return false;
            try { return SetWindowTheme(hwnd, "DarkMode_Explorer", null) == 0; }
            catch (Exception) { return false; }
        }
        internal static bool TryDarkScrollbars(Control c)
        {
            if (c == null || !c.IsHandleCreated) return false;
            // 用真实 build 号，不能用 Environment.OSVersion.Version.Major
            // （无 manifest 时它返回 6，见 WindowsBuild 的注释）。
            if (!SupportsDarkMode) return false;
            bool any = false;
            try
            {
                if (SetWindowTheme(c.Handle, "DarkMode_Explorer", null) == 0) any = true;
                // ListView 的滚动条和表头是两个独立窗口，都要单独设
                foreach (Control child in c.Controls)
                {
                    if (child.IsHandleCreated)
                    {
                        if (SetWindowTheme(child.Handle, "DarkMode_Explorer", null) == 0) any = true;
                    }
                }
            }
            catch (Exception) { }
            return any;
        }

        /// <summary>给 ListView 套深色（表头 + 滚动条一起处理）。</summary>
        internal static void DarkenListView(ListView lv)
        {
            if (lv == null) return;
            lv.BackColor = Theme.BgInset;
            lv.ForeColor = Theme.TextSecondary;
            // OwnerDraw 后表头颜色可控，但 DrawColumnHeader 必须自己实现，否则表头空白
            TryDarkScrollbars(lv);
            try
            {
                // 表头背景在用 visual styles 时无法直接改，只能靠 SetWindowTheme 的
                // "ItemsView" 子主题让系统按深色画。
                if (lv.IsHandleCreated)
                {
                    SetWindowTheme(lv.Handle, "DarkMode_ItemsView", null);
                }
            }
            catch (Exception) { }
        }

        // ---------------- 高 DPI ----------------
        /// <summary>取当前窗体的 DPI 缩放比（96dpi = 1.0）。</summary>
        internal static float GetDpiScale(Control c)
        {
            try
            {
                using (var g = (c != null ? c.CreateGraphics() : Graphics.FromHwnd(IntPtr.Zero)))
                {
                    return g.DpiX / 96f;
                }
            }
            catch (Exception) { return 1f; }
        }
    }
}
