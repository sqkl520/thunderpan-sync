// Widgets.cs -- 自绘控件集合
//
// 为什么必须自己写控件类，而不是给原生控件挂事件：
//   1. 双缓冲（DoubleBuffered）是 protected，外部改不了 → 不双缓冲会闪
//   2. 原生控件（Button/ListView/ProgressBar）的内部绘制无法完全接管，
//      想让它们圆角 + 深色，只能 OwnerDraw 或整套自绘
//   3. 原生控件的 AccessibleRole 太笼统，自绘后可以精确设置
//
// 每个控件都实现了 AccessibleName/Role，保证屏幕阅读器可用。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThunderPanSync
{
    // ===========================================================
    //  CardPanel -- 圆角卡片容器
    // ===========================================================
    // 目前仅 UiPreview.cs（开发期自检）使用；产品代码里 0 引用（保留原因同 FlatButton）。
    internal class CardPanel : Panel
    {
        public Color CardBack = Theme.BgSurface;
        public Color CardBorder = Theme.Border;
        public int Radius = Theme.RadiusCard;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.BgWindow;   // 卡片外的底色，靠自绘覆盖
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // 先铺窗体底色（圆角外侧要露出底色）
            using (var bg = new SolidBrush(Theme.BgWindow))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.DrawCard(g, r, CardBack, CardBorder, Theme.S(Radius));
            base.OnPaint(e);
        }
    }

    // ===========================================================
    //  FlatButton -- 扁平按钮（圆角 / hover / 按下 / 禁用 / 主次区分）
    // ===========================================================
    // 目前仅 UiPreview.cs（开发期自检）使用；产品 MainForm 用原生 Button（MakeButton）。
    // 保留原因：这是唯一能单独看 4 种按钮 Kind 视觉的工具，删了自检窗口编译不过。
    internal class FlatButton : Button
    {
        public enum Kind { Primary, Secondary, Danger, Ghost }

        private Kind _kind = Kind.Secondary;
        private bool _hover, _down;

        public Kind ButtonKind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        /// <summary>紧凑模式（工具栏里的小按钮，高度更小、内边距更窄）。</summary>
        public bool Compact { get; set; }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            Cursor = Cursors.Hand;
            Height = Theme.S(34);
            DoubleBuffered = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        private void ResolveColors(out Color bg, out Color fg, out Color border)
        {
            bool en = Enabled;
            if (!en)
            {
                bg = Theme.BgSurfaceAlt; fg = Theme.TextDisabled; border = Theme.Border;
                return;
            }

            switch (_kind)
            {
                case Kind.Primary:
                    bg = _down ? Theme.Shade(Theme.Accent, -0.18)
                        : (_hover ? Theme.Shade(Theme.Accent, 0.12) : Theme.Accent);
                    fg = Theme.TextOnAccent;
                    border = Color.Transparent;
                    break;
                case Kind.Danger:
                    bg = _down ? Theme.Shade(Theme.Danger, -0.18)
                        : (_hover ? Theme.Shade(Theme.Danger, 0.12) : Theme.Danger);
                    fg = Theme.TextOnAccent;
                    border = Color.Transparent;
                    break;
                case Kind.Ghost:
                    // 幽灵按钮：平时只有文字，hover 才出现底色（用于次要操作）
                    bg = _down ? Theme.BgActive : (_hover ? Theme.BgHover : Color.Transparent);
                    fg = _hover ? Theme.TextPrimary : Theme.TextSecondary;
                    border = Color.Transparent;
                    break;
                default: // Secondary
                    bg = _down ? Theme.BgActive : (_hover ? Theme.BgHover : Theme.BgSurfaceAlt);
                    fg = _hover ? Theme.TextPrimary : Theme.TextPrimary;
                    border = _hover ? Theme.BorderStrong : Theme.Border;
                    break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color bg, fg, border;
            ResolveColors(out bg, out fg, out border);

            // 父容器底色（透明效果靠它）
            Color parentBg = Parent != null ? Parent.BackColor : Theme.BgSurface;
            using (var pb = new SolidBrush(parentBg)) g.FillRectangle(pb, ClientRectangle);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Theme.S(Theme.RadiusControl);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Draw.RoundRect(r, radius))
            {
                if (bg.A > 0)
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                }
                if (border.A > 0)
                {
                    using (var p = new Pen(border)) g.DrawPath(p, path);
                }
            }

            // 有焦点时画一圈 focus ring（键盘用户能看见自己在哪）
            if (Focused && Enabled)
            {
                using (var p = new Pen(Theme.FocusRing, 1.5f))
                {
                    using (var path = Draw.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), radius))
                    {
                        g.DrawPath(p, path);
                    }
                }
            }

            int padX = Theme.S(Compact ? 8 : 14);
            var textRect = new Rectangle(padX, 0, Width - padX * 2, Height);
            Draw.TextCenter(g, Text, Font, textRect, fg);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // ===========================================================
    //  FlatProgress -- 自绘进度条（支持确定/不确定、圆角、语义色）
    // ===========================================================
    internal class FlatProgress : Control
    {
        private double _ratio;
        private bool _indeterminate;
        private double _phase;
        private readonly Timer _anim;
        private Color _fill = Theme.Ok;

        public FlatProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.BgBar;
            Height = Theme.S(8);
            // 换掉原生 ProgressBar 之后，"这是个进度条"的语义也跟着丢了，
            // 屏幕阅读器会把它当成无名面板。这里补回角色（值见 PercentDone/AccessibleObject）。
            AccessibleRole = AccessibleRole.ProgressBar;
            _anim = new Timer();
            _anim.Interval = 33;   // ~30fps，足够平滑且不吃 CPU
            _anim.Tick += delegate
            {
                _phase += 0.012;
                if (_phase > 1.0) _phase -= 1.0;
                Invalidate();
            };
        }

        public double Ratio
        {
            get { return _ratio; }
            set
            {
                double v = value;
                // NaN/Infinity 必须先挡：NaN < 0 和 NaN > 1 都是 false，会原样漏进绘制层，
                // 而 bytesTotal=0 时 pct = bytesDone/0 正好产出 NaN（Infinity 同理）。
                // 绘制层再兜一次（Draw.DrawProgress），这里是第一道闸。
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                if (Math.Abs(v - _ratio) < 0.0005) return;
                _ratio = v;
                Invalidate();
            }
        }

        /// <summary>完成百分比（0..100）。由 Ratio 派生，供屏幕阅读器读；写它等价于写 Ratio。</summary>
        public int PercentDone
        {
            get
            {
                int p = (int)Math.Round(_ratio * 100.0);
                if (p < 0) p = 0;
                if (p > 100) p = 100;
                return p;
            }
            set
            {
                int p = value;
                if (p < 0) p = 0;
                if (p > 100) p = 100;
                Ratio = p / 100.0;
            }
        }

        // 自绘控件没有原生可访问值：屏幕阅读器只能拿到"进度条"这个角色，读不到百分比。
        // WinForms 原生 ProgressBar 的 AccessibleObject.Value 就是 "37%" 这种字符串，这里照做。
        private sealed class ProgressAccessibleObject : Control.ControlAccessibleObject
        {
            private readonly FlatProgress _owner;

            public ProgressAccessibleObject(FlatProgress owner) : base(owner) { _owner = owner; }

            public override string Value { get { return _owner.PercentDone.ToString() + "%"; } }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ProgressAccessibleObject(this);
        }

        public bool Indeterminate
        {
            get { return _indeterminate; }
            set
            {
                if (_indeterminate == value) return;
                _indeterminate = value;
                if (value) { _phase = 0; _anim.Start(); }
                else { _anim.Stop(); }
                Invalidate();
            }
        }

        public Color FillColor
        {
            get { return _fill; }
            set { if (_fill != value) { _fill = value; Invalidate(); } }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _anim.Stop(); _anim.Dispose(); } catch (Exception) { } }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgBar))
            {
                g.FillRectangle(b, ClientRectangle);
            }
            var track = new Rectangle(0, 0, Width, Height);
            // 轨道色：填充色的极暗版本，视觉上属于同一族
            Color trackColor = Theme.Mix(Theme.BgInset, _fill, 0.12);
            Draw.DrawProgress(g, track, _ratio, _fill, trackColor, _indeterminate, _phase);
        }
    }

    // ===========================================================
    //  StatusPill -- 状态胶囊（颜色 + 图标形状 + 文字，三重编码）
    // ===========================================================
    //
    // 为什么不能只靠颜色：色盲用户（约 8% 男性）分不清红/绿。
    // 所以状态同时用【形状】和【文字】表达，颜色只是第三重冗余。
    internal class StatusPill : Control
    {
        public enum PillKind { Unknown, Configured, Running, Paused, Error, NotConfigured }

        private PillKind _state = PillKind.Unknown;
        private string _text = "";
        private Timer _blink;
        private bool _blinkOn = true;
        private int _pinnedWidth;

        public StatusPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.BgBar;
            ForeColor = Theme.TextPrimary;   // 未显式设置时的兜底（自检窗口直接用这个）
            Font = Theme.FontBody;
            Height = Theme.S(26);
            _blink = new Timer();
            _blink.Interval = 700;
            _blink.Tick += delegate
            {
                // 备份中时让指示点缓慢呼吸，用户扫一眼就知道"还活着"
                _blinkOn = !_blinkOn;
                Invalidate();
            };
        }

        public PillKind PillState
        {
            get { return _state; }
            set { if (_state != value) { _state = value; SyncBlink(); Invalidate(); } }
        }

        /// <summary>
        /// 锁定宽度（&gt;0 生效）：Fit() 不再按文本改写 Width。
        /// 默认 0 = 保持原来的"按文本自动计算"行为。
        /// 用途：状态栏里几个胶囊要等宽对齐，而文字长度会随状态变（"备份中"/"已暂停"）。
        /// </summary>
        public int PinnedWidth
        {
            get { return _pinnedWidth; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v == _pinnedWidth) return;
                _pinnedWidth = v;
                if (_pinnedWidth > 0)
                {
                    if (Width != _pinnedWidth) Width = _pinnedWidth;   // 立刻生效，不等下一次 Fit
                }
                else
                {
                    Fit();   // 解除锁定：回到按文本算的宽度
                }
            }
        }

        public override string Text
        {
            get { return _text; }
            set { _text = value == null ? "" : value; Fit(); Invalidate(); }
        }

        private void SyncBlink()
        {
            if (_state == PillKind.Running) { _blinkOn = true; _blink.Start(); }
            else { _blink.Stop(); _blinkOn = true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _blink.Stop(); _blink.Dispose(); } catch (Exception) { } }
            base.Dispose(disposing);
        }

        private void Fit()
        {
            // 锁宽优先：调用方显式指定了宽度就别再用文本长度盖掉它
            if (_pinnedWidth > 0)
            {
                if (Width != _pinnedWidth) Width = _pinnedWidth;
                return;
            }
            // 同 StatChip.Fit：不能用 CreateGraphics（构造期 Handle 未建，
            // 早期版本因此让胶囊宽度为 0，整个控件看不见）。
            int w = TextRenderer.MeasureText(_text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(46);
            if (w < Theme.S(72)) w = Theme.S(72);
            if (Width != w) Width = w;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Fit();
        }

        private Color StateColor()
        {
            switch (_state)
            {
                case PillKind.Running: return Theme.Ok;
                case PillKind.NotConfigured: return Theme.Warn;
                case PillKind.Configured: return Theme.TextTertiary;  // 待命 = 中性色（红色只留给"需要关注"）
                case PillKind.Paused: return Theme.Warn;
                case PillKind.Error: return Theme.Danger;
                default: return Theme.TextTertiary;
            }
        }

        private Color StateBg()
        {
            switch (_state)
            {
                case PillKind.Running: return Theme.OkBg;
                case PillKind.NotConfigured: return Theme.WarnBg;
                case PillKind.Configured: return Theme.BgSurfaceAlt;
                case PillKind.Paused: return Theme.WarnBg;
                case PillKind.Error: return Theme.DangerBg;
                default: return Theme.BgSurfaceAlt;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgBar))
            {
                g.FillRectangle(b, ClientRectangle);
            }

            Color c = StateColor();
            Color bgc = StateBg();
            int h = Height;
            int radius = h / 2;
            var rect = new Rectangle(0, 0, Width - 1, h - 1);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Draw.RoundRect(rect, radius))
            using (var b = new SolidBrush(bgc))
            {
                g.FillPath(b, path);
            }

            // 指示图标：形状随状态变（不只靠颜色）
            int iconSize = Theme.S(8);
            int ix = Theme.S(10);
            int iy = (h - iconSize) / 2;
            Color iconColor = (_state == PillKind.Running && !_blinkOn)
                ? Theme.Mix(c, bgc, 0.5)   // 呼吸效果：变暗
                : c;

            using (var b = new SolidBrush(iconColor))
            using (var pen = new Pen(iconColor, Math.Max(1.5f, Theme.S(2))))
            {
                switch (_state)
                {
                    case PillKind.Running:
                        // 实心圆 = 正在运行
                        g.FillEllipse(b, ix, iy, iconSize, iconSize);
                        break;
                    case PillKind.Configured:
                        // 空心圆 = 待命（文件夹配好了、登录了、只是没开始备份）。
                        // 原来画实心三角 + 红色，把完全正常的状态渲染成"出错了"（必修 12）。
                        g.DrawEllipse(pen, ix, iy, iconSize, iconSize);
                        break;
                    case PillKind.Error:
                        // 叉号 = 出错。这两个状态颜色相同（都是 Theme.Danger，语义上都属危险），
                        // 灰度下**像素值完全一样**，所以区别只能靠形状（WCAG 1.4.1 不只靠颜色）：
                        // 实心三角 vs 两条交叉斜线，8px 尺寸下轮廓不同，肉眼与色盲工具都能分开。
                        {
                            // 1.5px 的细笔：8px 见方的图标用 2px 笔画会把叉号糊成一团
                            int inset = Math.Max(1, iconSize / 8);
                            using (var xp = new Pen(iconColor, Math.Max(1.2f, Theme.S(1))))
                            {
                                g.DrawLine(xp, ix + inset, iy + inset, ix + iconSize - inset, iy + iconSize - inset);
                                g.DrawLine(xp, ix + iconSize - inset, iy + inset, ix + inset, iy + iconSize - inset);
                            }
                        }
                        break;
                    case PillKind.Paused:
                        // 两条竖线 = 暂停
                        {
                            int wBar = Math.Max(2, iconSize / 3);
                            g.FillRectangle(b, ix, iy, wBar, iconSize);
                            g.FillRectangle(b, ix + iconSize - wBar, iy, wBar, iconSize);
                        }
                        break;
                    case PillKind.NotConfigured:
                        // 空心方块 = 还没设置
                        g.DrawRectangle(pen, ix, iy, iconSize, iconSize);
                        break;
                    default:
                        g.DrawEllipse(pen, ix, iy, iconSize, iconSize);
                        break;
                }
            }

            var textRect = new Rectangle(ix + iconSize + Theme.S(8), 0, Width - (ix + iconSize + Theme.S(8)) - Theme.S(10), h);
            // 文字色跟随 ForeColor：调用方（MainForm）一直按状态设 ForeColor，但这里写死成
            // Theme.TextPrimary，于是那次赋值是死代码，颜色永远是"正常文字色"（必修 12）
            Draw.TextLeft(g, _text, Font, textRect, ForeColor);
        }
    }

    // ===========================================================
    //  StatChip -- 指标胶囊（"文件夹 3" 这种，数字比标签更醒目）
    // ===========================================================
    internal class StatChip : Control
    {
        private string _label = "";
        private string _value = "0";
        private Color _valueColor = Theme.TextPrimary;
        private int _pinnedWidth;

        public StatChip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.BgBar;
            Font = Theme.FontSmall;
            Height = Theme.S(24);
        }

        public string Label
        {
            get { return _label; }
            set { _label = value == null ? "" : value; Fit(); Invalidate(); }
        }

        public string Value
        {
            get { return _value; }
            set { _value = value == null ? "0" : value; Fit(); Invalidate(); }
        }

        public Color ValueColor
        {
            get { return _valueColor; }
            set { _valueColor = value; Invalidate(); }
        }

        /// <summary>
        /// 锁定宽度（&gt;0 生效）：Fit() 不再按文本改写 Width。
        /// 默认 0 = 保持原来的"按文本自动计算"行为。用途同 StatusPill：指标胶囊等宽对齐，
        /// 而 "文件夹 3" → "文件夹 12" 会让自动宽度跳一下。
        /// </summary>
        public int PinnedWidth
        {
            get { return _pinnedWidth; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v == _pinnedWidth) return;
                _pinnedWidth = v;
                if (_pinnedWidth > 0)
                {
                    if (Width != _pinnedWidth) Width = _pinnedWidth;
                }
                else
                {
                    Fit();
                }
            }
        }

        private void Fit()
        {
            // 锁宽优先（同 StatusPill.Fit）
            if (_pinnedWidth > 0)
            {
                if (Width != _pinnedWidth) Width = _pinnedWidth;
                return;
            }
            // 构造期 Handle 还没建，CreateGraphics 会失败 —— 但不能直接 return，
            // 否则 Width 永远是 0，放进 FlowLayoutPanel 后控件彻底看不见
            // （实测踩过：4 个指标胶囊全部消失，只剩状态胶囊）。
            // 所以这里用 TextRenderer.MeasureText 的静态重载，它不需要 Handle。
            int w = MeasureStatic(_label, Font) + MeasureStatic(_value, Theme.FontSubtitle) + Theme.S(20);
            if (w < Theme.S(40)) w = Theme.S(40);
            if (Width != w) Width = w;
        }

        // 不依赖 Handle 的文本测量（构造期也能用）
        private static int MeasureStatic(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Fit();   // Handle 建好后按真实字体再量一次（DPI 缩放后可能更宽）
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgBar))
            {
                g.FillRectangle(b, ClientRectangle);
            }

            int x = 0;
            // 标签（次要色，小字）
            if (_label.Length > 0)
            {
                int lw = Draw.MeasureText(g, _label, Font);
                Draw.TextLeft(g, _label, Font, new Rectangle(x, 0, lw, Height), Theme.TextTertiary);
                x += lw + Theme.S(6);
            }
            // 数值（主要色，稍大，等宽感）
            int vx = x;
            int vw = Width - vx;
            Draw.TextLeft(g, _value, Theme.FontSubtitle, new Rectangle(vx, 0, vw, Height), _valueColor);
        }
    }

    // ===========================================================
    //  FlatTabControl -- 去掉 WinForms 那个丑陋的 Tab
    // ===========================================================
    //
    // 原生 TabControl 在深色主题下的问题：
    //   1. Tab 页边缘有系统白边（无法通过属性去掉）
    //   2. Tab 头用的是系统视觉样式，和自定义配色打架
    //   3. 无法自定义 Tab 头的 hover/选中样式
    // 所以这里完全自绘 Tab 头，TabPage 用 Panel 代替。
    internal class SegmentedTabs : Control
    {
        private string[] _tabs = new string[0];
        private int _selected = 0;
        private int _hover = -1;

        public event EventHandler SelectedIndexChanged;

        public SegmentedTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.BgBar;
            Font = Theme.FontBody;
            Height = Theme.S(36);
            Cursor = Cursors.Hand;
        }

        public string[] Tabs
        {
            get { return _tabs; }
            set { _tabs = value == null ? new string[0] : value; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (_tabs.Length > 0 && v >= _tabs.Length) v = _tabs.Length - 1;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        // 绘制与命中测试**必须共用同一个宽度函数**。
        //
        // 红队实测过这个 bug：OnPaint 用 Draw.MeasureText(g, ...)（走 Graphics/GDI），
        // TabAt 用静态 TextRenderer.MeasureText（走屏幕 DC），两者宽度差约 9px/字符串。
        // 结果 tab[0] 画在 0..61 但可点到 0..70；点视觉上属于「设置」的位置会切到「备份」。
        //
        // 这是同一根源第二次出事（第一次是 CreateGraphics 导致 Width=0），
        // 所以固化成一条规则：**同一控件的测量只能有一个来源**。
        private int TabWidth(int i)
        {
            return TextRenderer.MeasureText(_tabs[i], Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(18) * 2;
        }

        private int TabAt(int x)
        {
            if (_tabs.Length == 0) return -1;
            int cx = 0;
            for (int i = 0; i < _tabs.Length; i++)
            {
                int w = TabWidth(i);
                if (x >= cx && x < cx + w) return i;
                cx += w;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = TabAt(e.X);
            if (i != _hover) { _hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = TabAt(e.X);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.BgBar)) g.FillRectangle(b, ClientRectangle);

            int padX = Theme.S(18);
            int cx = 0;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            for (int i = 0; i < _tabs.Length; i++)
            {
                int w = TabWidth(i);
                var r = new Rectangle(cx, 0, w, Height);
                bool sel = (i == _selected);

                if (sel)
                {
                    // 选中：底部一条强调色下划线（比整块高亮更克制）
                    using (var p = new Pen(Theme.Accent, Math.Max(2f, Theme.S(2))))
                    {
                        g.DrawLine(p, cx + padX / 2, Height - 2, cx + w - padX / 2, Height - 2);
                    }
                }
                else if (i == _hover)
                {
                    var hr = new Rectangle(cx, Theme.S(4), w, Height - Theme.S(8));
                    using (var path = Draw.RoundRect(hr, Theme.S(Theme.RadiusControl)))
                    using (var b = new SolidBrush(Theme.BgHover))
                    {
                        g.FillPath(b, path);
                    }
                }

                Color tc = sel ? Theme.TextPrimary : (i == _hover ? Theme.TextSecondary : Theme.TextTertiary);
                Draw.TextCenter(g, _tabs[i], Font, r, tc);
                cx += w;
            }

            // 底部分隔线
            Draw.DrawHLine(g, 0, Height - 1, Width, Theme.Divider);
        }
    }
// ===========================================================
    //  DarkListView -- 深色 ListView（表头 / 滚动条 / 斑马纹 / 行高）
    // ===========================================================
    //
    // 为什么必须自绘（实测）：
    //   1. 表头背景由 Header 子窗口按系统主题绘制。即使 OwnerDraw=true，
    //      当列宽总和小于控件宽度时右侧仍露出白色填充块 #FFFFFF。
    //      必须在 Handle 建好后对 header 窗口单独 SetWindowTheme 才能消掉。
    //   2. 行高没有公开属性。唯一手段是挂一个 ImageSize.Height=N 的 SmallImageList。
    //   3. 滚动条同样要吃 DarkMode_Explorer，否则是刺眼的白条。
    internal class DarkListView : ListView
    {
        private const int LVM_GETHEADER = 0x101F;
        private const int LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036;
        private const int LVS_EX_DOUBLEBUFFER = 0x00010000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct CRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr h, out CRect r);

        public DarkListView()
        {
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;              // 失焦时仍显示选中，否则点别处选中项就看不见了
            MultiSelect = false;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;   // 本应用的列表不需要排序
            BorderStyle = BorderStyle.None;                 // 去掉 Win95 的 3D 边框
            OwnerDraw = true;
            BackColor = Theme.BgInset;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            // ListView 默认不显示 item tooltip：MainForm._lvFail 给行设了 ToolTipText
            // （失败原因往往比列宽长），但全项目没有一处打开这个开关，
            // 所以那些提示从来没显示过。这里一次覆盖 _lvDirs / _lvFail 两个列表。
            ShowItemToolTips = true;

            var il = new ImageList();
            il.ImageSize = new Size(1, Theme.S(26));    // 用 ImageSize 间接把行高拉到 26px
            il.ColorDepth = ColorDepth.Depth32Bit;
            SmallImageList = il;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // ListView 会忽略 SetStyle(OptimizedDoubleBuffer)，只能设扩展样式
            try { SendMessage(Handle, LVM_SETEXTENDEDLISTVIEWSTYLE, (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)LVS_EX_DOUBLEBUFFER); }
            catch (Exception) { }
            ApplyDarkChrome();
            FitColumns();   // 此时已有真实尺寸，列宽才能算对
        }

        /// <summary>给本体、表头子窗口、滚动条都套深色主题。</summary>
        public void ApplyDarkChrome()
        {
            if (!IsHandleCreated) return;
            Draw.TryDarkScrollbars(this);
            try
            {
                IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) Draw.SetThemeDeep(header);
            }
            catch (Exception) { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitColumns();
        }

        /// <summary>
        /// 让最后一列吃掉剩余宽度，使列宽总和 == 表头客户区宽度。
        ///
        /// 这不是美观问题，是消除"表头右侧白块"的根因：
        /// 列宽总和小于控件宽度时，右侧那段填充区由 Header 子窗口用系统浅色绘制，
        /// OwnerDraw 覆盖不到，SetWindowTheme 也只是把白变成深灰（仍有色块）。
        /// 列宽填满后根本不产生填充区。实测有效。
        ///
        /// 历史 bug（Lead 实测）：原来这里写 `avail = ClientSize.Width`，又在滚动条出现时
        /// `avail -= Theme.S(18)`。而 **ClientSize.Width 在有垂直滚动条时已经排除了滚动条**，
        /// 等于同一个 18px 扣了两次：12 项时列宽 951、右侧 18px 白块（324 个白像素）。
        /// 现在改成取表头子窗口自己的客户区宽度，绕开"ClientSize 含不含滚动条"的歧义。
        /// </summary>
        public void FitColumns()
        {
            if (Columns.Count == 0) return;
            int avail = ClientSize.Width;      // 客户区宽度：出现垂直滚动条时**已经**排除了滚动条
            if (avail <= 0) return;

            // 再用表头子窗口的客户区宽度兜一层（表头比客户区更窄时以表头为准）。
            //
            // 为什么不能直接拿表头宽当目标（Lead 原方案，我用探针实测过）：
            // 出现垂直滚动条时，表头窗口**不会**跟着变窄——实测 客户区 969 / 表头 986，
            // 多出的 17px 是滚动条上方那条。列画到 986 会被滚动条盖住，
            // 同时 ListView 认为内容比可视区宽 → **冒出横向滚动条**（探针实测 hscroll=True）。
            // 所以目标取两者较小值：无滚动条时 = 986（表头填满），有滚动条时 = 969。
            if (IsHandleCreated)
            {
                IntPtr header = IntPtr.Zero;
                try { header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception) { }
                if (header != IntPtr.Zero)
                {
                    CRect rc;
                    if (GetClientRect(header, out rc))
                    {
                        int hw = rc.Right - rc.Left;
                        if (hw > 0 && hw < avail) avail = hw;
                    }
                }
            }

            int others = 0;
            for (int i = 0; i < Columns.Count - 1; i++) others += Columns[i].Width;
            int last = avail - others;
            // 幂等短路：算出来的宽度和当前一样就**不要写**。
            // 写列宽会走 LVM_SETCOLUMNWIDTH，ListView 每次都重算滚动条并重绘，
            // 10 秒一次的定时刷新里反复写会闪。宽度没变时直接返回，零副作用。
            if (last > Theme.S(40) && Columns[Columns.Count - 1].Width != last)
            {
                Columns[Columns.Count - 1].Width = last;
            }
        }

        /// <summary>
        /// 重新按当前宽度分配列宽（幂等，可以随便反复调用）。
        /// 为什么需要它：给 Items 加行导致垂直滚动条出现/消失时，ListView **不会**触发
        /// OnResize（滚动条是子窗口，控件外框尺寸没变），列宽就会停在旧值上。
        /// 调用方在 FillFailedList / 文件夹列表赋值之后调一次即可；
        /// 定时刷新（如 10 秒一次的 ApplyStatus）里每次都调也安全：宽度没变时不发任何消息。
        /// </summary>
        public void RefreshColumns()
        {
            FitColumns();
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.BgSurfaceAlt))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }
            using (var p = new Pen(Theme.Divider))
            {
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
            var r = e.Bounds;
            r.X += Theme.S(8);
            r.Width -= Theme.S(10);
            Draw.TextLeft(e.Graphics, e.Header.Text, Theme.FontSmall, r, Theme.TextSecondary);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            // 留空：背景全部在 DrawSubItem 里画，同一块填两次会闪
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            bool selected = e.Item.Selected;
            Color bg = selected ? Theme.InfoBg
                : (((e.ItemIndex & 1) == 1) ? Theme.BgSurfaceAlt : Theme.BgInset);
            using (var b = new SolidBrush(bg))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }
            if (e.ColumnIndex == 0 && !selected)
            {
                using (var b = new SolidBrush(Theme.Divider))
                {
                    e.Graphics.FillRectangle(b, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Width, 1);
                }
            }

            var r = e.Bounds;
            r.X += Theme.S(8);
            r.Width -= Theme.S(10);

            Color fg = selected ? Theme.TextPrimary
                : (e.ColumnIndex == 0 ? Theme.TextSecondary : Theme.TextTertiary);
            Draw.TextLeft(e.Graphics, e.SubItem.Text, Theme.FontBody, r, fg);
        }
    }
}