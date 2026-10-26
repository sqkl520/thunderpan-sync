// Theme.cs -- 统一视觉令牌（颜色 / 字号 / 间距 / 圆角）
//
// 存在的理由：
//   原来 MainForm 里散落着 Color.FromArgb(38,40,44)、Color.Gainsboro 这类字面值，
//   一共 40 多处，改一个配色要全文搜索替换，而且很容易改漏导致深浅不一致。
//   全部集中到这里之后，"换一套配色"只需要改这一个文件。
//
// 设计约束（写死在这里，避免后人重踩）：
//   1. 只用 System.Drawing，不引第三方库（产物要维持 50KB 级）
//   2. 颜色一律给精确 RGB，不用 Color.Gainsboro 这类命名色
//      —— 命名色的饱和度是 .NET 拍脑袋定的，在深色背景上刺眼
//   3. 正文对比度必须 >= 4.5:1（WCAG AA），大字号 >= 3:1
//   4. 高 DPI：所有尺寸都乘以 S() 缩放因子，不要直接用像素
using System;
using System.Drawing;

namespace ThunderPanSync
{
    internal static class Theme
    {
        // ---------------- DPI 缩放 ----------------
        //
        // 为什么不让 WinForms 自动缩放：AutoScaleMode=Dpi 在 .NET Framework 里
        // 对 Font 的自适应很不稳定，混合 Dpi/Font 模式会出现"一部分控件放大、
        // 另一部分没放大"。这里改成自己做：启动时量一次，之后所有尺寸走 S()。
        private static float _scale = 0f;

        internal static void InitScale(float scale)
        {
            if (scale > 0.5f && scale < 4.0f) _scale = scale;
        }

        /// <summary>逻辑像素 -> 实际像素（按当前 DPI 缩放）。</summary>
        internal static int S(int logical)
        {
            if (_scale <= 0f) return logical;
            return (int)Math.Round(logical * _scale);
        }

        internal static float Scale { get { return _scale <= 0f ? 1f : _scale; } }

        // ---------------- 配色：背景层级 ----------------
        // 层级从深到浅：窗口底色 < 卡片 < 输入框凹陷 < 悬浮
        internal static readonly Color BgWindow = Color.FromArgb(24, 26, 30);   // 窗口最底层
        internal static readonly Color BgSurface = Color.FromArgb(32, 35, 40);  // 卡片 / 面板
        internal static readonly Color BgSurfaceAlt = Color.FromArgb(38, 42, 48); // 次级面板 / 表头
        internal static readonly Color BgInset = Color.FromArgb(20, 22, 26);    // 输入框 / 日志凹陷
        internal static readonly Color BgHover = Color.FromArgb(52, 57, 65);    // 悬浮
        internal static readonly Color BgActive = Color.FromArgb(64, 70, 80);   // 按下
        internal static readonly Color BgBar = Color.FromArgb(28, 31, 36);      // 状态条 / 底栏

        // ---------------- 配色：文字层级 ----------------
        internal static readonly Color TextPrimary = Color.FromArgb(235, 238, 242);   // 主要
        internal static readonly Color TextSecondary = Color.FromArgb(168, 176, 188); // 次要
        // 弱化文字。原值 #7C8490 在 BgSurfaceAlt(#262A30) 上只有 3.82:1，低于 WCAG AA 的 4.5:1
        // （表头 / 设置页提示 / 空闲文案都在用它，属于"承载信息"的文字）。
        // #8E96A4 是同一色相（冷灰蓝）整体提亮 18 阶的结果：
        //   BgSurfaceAlt 4.84:1（原 3.82）BgSurface 5.29（4.17）BgBar 5.55（4.38）BgWindow 5.85（4.61）
        // 注意 BgHover 上仍是 3.90:1 —— 那是悬浮态背景，弱化文字不用它，故不因此再提亮。
        internal static readonly Color TextTertiary = Color.FromArgb(142, 150, 164);  // 弱化
        internal static readonly Color TextDisabled = Color.FromArgb(92, 98, 108);    // 禁用
        internal static readonly Color TextOnAccent = Color.FromArgb(255, 255, 255);  // 强调色上的字

        // ---------------- 配色：语义色 ----------------
        internal static readonly Color Ok = Color.FromArgb(74, 194, 122);       // 成功 / 备份中
        internal static readonly Color Warn = Color.FromArgb(230, 176, 68);     // 警告 / 未配置
        internal static readonly Color Danger = Color.FromArgb(226, 96, 88);    // 危险 / 未备份
        internal static readonly Color Info = Color.FromArgb(88, 156, 226);     // 信息 / 进行中
        internal static readonly Color Accent = Color.FromArgb(72, 138, 220);   // 主按钮

        // 语义色的深色底（做胶囊背景用，饱和度压低，不刺眼）
        internal static readonly Color OkBg = Color.FromArgb(30, 52, 40);
        internal static readonly Color WarnBg = Color.FromArgb(56, 46, 26);
        internal static readonly Color DangerBg = Color.FromArgb(56, 32, 30);
        internal static readonly Color InfoBg = Color.FromArgb(26, 42, 62);

        // ---------------- 边框 / 分隔 ----------------
        internal static readonly Color Border = Color.FromArgb(56, 62, 72);
        internal static readonly Color BorderStrong = Color.FromArgb(74, 82, 94);
        internal static readonly Color Divider = Color.FromArgb(44, 49, 57);
        internal static readonly Color FocusRing = Color.FromArgb(96, 164, 236);

        // ---------------- 圆角 ----------------
        internal const int RadiusCard = 6;
        internal const int RadiusControl = 4;
        internal const int RadiusPill = 9;

        // ---------------- 间距（4px 基准） ----------------
        internal const int Sp1 = 4;
        internal const int Sp2 = 8;
        internal const int Sp3 = 12;
        internal const int Sp4 = 16;
        internal const int Sp5 = 20;
        internal const int Sp6 = 24;
        internal const int Sp8 = 32;

        // ---------------- 字号（逻辑像素，用 S() 缩放） ----------------
        private static Font _fontTitle, _fontSubtitle, _fontBody, _fontSmall, _fontMono;
        private static Font _fontMonoZh;

        /// <summary>标题（窗口内的一级标题）。</summary>
        internal static Font FontTitle
        {
            get
            {
                if (_fontTitle == null) _fontTitle = MakeFont("Microsoft YaHei UI", 12.5f, FontStyle.Regular);
                return _fontTitle;
            }
        }

        internal static Font FontSubtitle
        {
            get
            {
                if (_fontSubtitle == null) _fontSubtitle = MakeFont("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
                return _fontSubtitle;
            }
        }

        /// <summary>正文（默认 UI 字体）。</summary>
        internal static Font FontBody
        {
            get
            {
                if (_fontBody == null) _fontBody = MakeFont("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
                return _fontBody;
            }
        }

        internal static Font FontSmall
        {
            get
            {
                if (_fontSmall == null) _fontSmall = MakeFont("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
                return _fontSmall;
            }
        }

        /// <summary>等宽（日志用）。Consolas 对中文会 fallback 到雅黑，混排可接受。</summary>
        internal static Font FontMono
        {
            get
            {
                if (_fontMono == null) _fontMono = MakeFont("Consolas", 9f, FontStyle.Regular);
                return _fontMono;
            }
        }

        /// <summary>中文优先的等宽（日志里中文多时用它，避免 Consolas fallback 的高低不齐）。</summary>
        internal static Font FontMonoZh
        {
            get
            {
                if (_fontMonoZh == null) _fontMonoZh = MakeFont("Microsoft YaHei UI", 9f, FontStyle.Regular);
                return _fontMonoZh;
            }
        }

        private static Font MakeFont(string family, float size, FontStyle style)
        {
            // 这里**绝对不能**再乘 Scale。
            //
            // GraphicsUnit.Point 的字号天生就是 DPI 无关的：1pt = 1/72 英寸，
            // GDI+ 会按 DC 的 DPI 自己换算成像素。再乘一次 Scale 就是二次放大。
            // 红队实测（120/144/192 DPI）：
            //   应得 20.1px -> 实际 25.1px (1.25x)
            //   应得 24.1px -> 实际 36.2px (1.50x)
            //   应得 32.2px -> 实际 64.3px (2.00x)
            // 而控件高度走 Theme.S() 只放大一次，于是 150% 下文字会溢出被裁。
            //
            // 唯一需要 Scale 的是"用像素表示的东西"（高度/间距/圆角），
            // 字号交给系统换算即可。
            try { return new Font(family, size, style, GraphicsUnit.Point); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point); }
        }

        // ---------------- 字体可用性探测 ----------------
        internal static bool HasFont(string name)
        {
            try
            {
                using (var f = new FontFamily(name)) { return true; }
            }
            catch { return false; }
        }

        /// <summary>可用的中文 UI 字体（按优先级挑一个装了的）。</summary>
        internal static string UiFontFamily()
        {
            string[] cands = new string[] {
                "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma",
            };
            foreach (var c in cands) { if (HasFont(c)) return c; }
            return FontFamily.GenericSansSerif.Name;
        }

        // ---------------- 工具方法 ----------------
        /// <summary>按比例混合两个颜色（t=0 取 a，t=1 取 b）。用于做 hover/active 过渡色。</summary>
        internal static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>把颜色提亮（amount>0）或压暗（amount<0），范围 -1..1。</summary>
        internal static Color Shade(Color c, double amount)
        {
            if (amount >= 0) return Mix(c, Color.White, amount);
            return Mix(c, Color.Black, -amount);
        }

        /// <summary>
        /// WCAG 相对亮度。用于自检对比度 —— 这个函数只在开发期被调用，
        /// 但它保证"改了配色还能算一下够不够亮"这件事有工具可依。
        /// </summary>
        internal static double Luminance(Color c)
        {
            double r = Channel(c.R), g = Channel(c.G), b = Channel(c.B);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>两个颜色的 WCAG 对比度（1..21）。正文要求 >= 4.5。</summary>
        internal static double Contrast(Color fg, Color bg)
        {
            double l1 = Luminance(fg), l2 = Luminance(bg);
            if (l1 < l2) { double t = l1; l1 = l2; l2 = t; }
            return (l1 + 0.05) / (l2 + 0.05);
        }
    }
}
