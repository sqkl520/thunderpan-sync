// FlatCombo.cs -- 完全自绘的下拉选择控件（替代原生 ComboBox）
//
// 为什么必须自绘（实测，不是猜）：
//   Win10 19045 上 ComboBox 的**边框和右侧下拉箭头**由系统视觉样式绘制，
//   BackColor / FlatStyle.Flat 都管不到，`SetWindowTheme(h,"DarkMode_Explorer",null)`
//   也完全无效。Lead 用真实像素（BitBlt 取窗口 DC / PrintWindow）对比了三种状态：
//       as-is / DarkMode_Explorer / no-theme
//       三种都是 borderWhite T=320 B=320 L=30 R=30 corner=#FFFFFF，完全相同；
//   下拉箭头处采样到 #F0F0F0（经典浅色视觉风格面色）。
//   而且 `Form.DrawToBitmap()` 测出来的"白像素 3494 → 0"是**假象**（它不忠实渲染主题子控件），
//   所以本文件的验收也坚持用真实像素（见 D:\build-tmp 下的探针）。
//
// 设计取舍：
//   * 不继承 ComboBox（视觉样式画在它的非客户区里，接管不了），直接继承 Control 全自绘；
//   * 下拉列表用一个无边框 Form（不用 ListBox：ListBox 又是另一个吃系统样式的控件）；
//   * 键盘可达是硬要求（审计出的阻断级缺陷）：Tab 能进入、Space/Enter/Alt+Down 打开、
//     打开时 Up/Down/Enter/Esc、关闭时 Up/Down 直接改选中项。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThunderPanSync
{
    internal class FlatCombo : Control
    {
        private string[] _texts = new string[0];
        private string[] _values = new string[0];
        private int _selected = -1;
        private bool _hover;
        private ComboPopup _popup;
        private int _closedTick;

        public event EventHandler SelectedIndexChanged;

        public FlatCombo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = Theme.BgSurfaceAlt;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            Height = Theme.S(30);      // 与原来 ComboBox 的 30px 一致，避免设置页布局重排
            Width = Theme.S(320);
            Cursor = Cursors.Hand;
            TabStop = true;            // 键盘用户必须能 Tab 进来
            AccessibleRole = AccessibleRole.ComboBox;
            AccessibleName = "下拉选择";
        }

        // ---------------- 数据 ----------------

        public int Count { get { return _texts.Length; } }

        /// <summary>第 i 项的实际取值（越界返回 ""）。给"按值回填"的调用方用。</summary>
        public string ValueAt(int i)
        {
            if (i < 0 || i >= _values.Length) return "";
            return _values[i];
        }

        /// <summary>第 i 项的显示文本（越界返回 ""）。</summary>
        public string TextAt(int i)
        {
            if (i < 0 || i >= _texts.Length) return "";
            return _texts[i];
        }

        public void SetItems(string[] texts, string[] values)
        {
            if (texts == null) texts = new string[0];
            if (values == null) values = new string[0];
            int n = Math.Min(texts.Length, values.Length);
            _texts = new string[n];
            _values = new string[n];
            for (int i = 0; i < n; i++)
            {
                _texts[i] = texts[i] == null ? "" : texts[i];
                _values[i] = values[i] == null ? "" : values[i];
            }
            if (_selected >= n) _selected = n - 1;
            if (_selected < 0 && n > 0) _selected = 0;
            Invalidate();
        }

        public void AddItem(string text, string value)
        {
            int n = _texts.Length;
            var nt = new string[n + 1];
            var nv = new string[n + 1];
            for (int i = 0; i < n; i++) { nt[i] = _texts[i]; nv[i] = _values[i]; }
            nt[n] = text == null ? "" : text;
            nv[n] = value == null ? "" : value;
            _texts = nt;
            _values = nv;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (_texts.Length > 0 && v >= _texts.Length) v = _texts.Length - 1;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>选中项的实际取值；没有选中/越界时返回 ""（调用方据此判空）。</summary>
        public string SelectedValue
        {
            get
            {
                if (_selected < 0 || _selected >= _values.Length) return "";
                return _values[_selected];
            }
            set
            {
                string want = value == null ? "" : value;
                for (int i = 0; i < _values.Length; i++)
                {
                    if (_values[i] == want) { SelectedIndex = i; return; }
                }
                SelectedIndex = -1;   // 未命中：清空选择，不猜、不静默取整
            }
        }

        public string SelectedText
        {
            get
            {
                if (_selected < 0 || _selected >= _texts.Length) return "";
                return _texts[_selected];
            }
        }

        // 屏幕阅读器：读得出"这是一个下拉框"和当前值（原生 ComboBox 就有这两样）
        private sealed class ComboAccessibleObject : Control.ControlAccessibleObject
        {
            private readonly FlatCombo _owner;

            public ComboAccessibleObject(FlatCombo owner) : base(owner) { _owner = owner; }

            public override string Value { get { return _owner.SelectedText; } }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ComboAccessibleObject(this);
        }

        // ---------------- 交互状态 ----------------

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            // 刚因为"点了控件本身"把弹出层关掉的话，这一次点击不要再打开（防抖 250ms）
            if (Environment.TickCount - _closedTick < 250)
            {
                base.OnMouseDown(e);
                return;
            }
            if (IsOpen) ClosePopup(false, false);
            else OpenPopup();
            base.OnMouseDown(e);
        }

        public bool IsOpen { get { return _popup != null && _popup.Visible; } }

        // 箭头键/回车必须进 OnKeyDown，不能被容器当成"对话框导航键"截走
        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right
                || k == Keys.Enter || k == Keys.Space || k == Keys.Escape) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!Enabled) { base.OnKeyDown(e); return; }

            bool alt = (e.Modifiers & Keys.Alt) != 0;
            if (IsOpen)
            {
                // 打开时键盘交给弹出层（它有焦点），这里只兜底
                base.OnKeyDown(e);
                return;
            }

            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter
                || e.KeyCode == Keys.F4 || (alt && e.KeyCode == Keys.Down))
            {
                OpenPopup();
                e.Handled = true;
                return;
            }
            // 关闭状态下 Up/Down 直接改选中项（不用先打开列表）
            if (e.KeyCode == Keys.Up) { MoveSelection(-1); e.Handled = true; return; }
            if (e.KeyCode == Keys.Down) { MoveSelection(1); e.Handled = true; return; }

            base.OnKeyDown(e);
        }

        private void MoveSelection(int step)
        {
            if (_texts.Length == 0) return;
            int next = _selected + step;
            if (next < 0) next = _texts.Length - 1;          // 环绕：键到头再按就回到另一端
            if (next >= _texts.Length) next = 0;
            SelectedIndex = next;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        // ---------------- 下拉列表 ----------------

        private void OpenPopup()
        {
            if (_texts.Length == 0) return;
            ClosePopup(false, false);

            var p = new ComboPopup(this, _texts, _selected);
            _popup = p;
            Rectangle owner = RectangleToScreen(ClientRectangle);
            int itemH = Theme.S(26);
            int h = _texts.Length * itemH + 2;         // 上下各 1px 边框
            int w = owner.Width;
            Screen scr = Screen.FromControl(this);
            int y = owner.Bottom;
            if (y + h > scr.WorkingArea.Bottom) y = owner.Top - h;   // 下方放不下就翻到上方
            p.Bounds = new Rectangle(owner.Left, y, w, h);

            Form host = FindForm();
            if (host != null) p.Show(host); else p.Show();
            p.Activate();
            Invalidate();       // 边框换成"打开"色
        }

        /// <summary>
        /// 关闭弹出层；commit=true 时把弹出层的键盘游标落到选中项上。
        /// refocus=true 时把焦点收回本控件：键盘/鼠标选完必须还能接着用键盘。
        /// 用户点别处导致的关闭（OnDeactivate）要传 false，否则会把焦点抢回来。
        /// </summary>
        internal void ClosePopup(bool commit, bool refocus)
        {
            var p = _popup;
            _popup = null;
            _closedTick = Environment.TickCount;
            int pick = -1;
            if (p != null)
            {
                pick = p.ResultIndex;
                try { p.Close(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
            }
            if (commit && pick >= 0) SelectedIndex = pick;   // SelectedIndex 自己会触发事件
            if (refocus) { try { Focus(); } catch (Exception) { } }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClosePopup(false, false);
            base.Dispose(disposing);
        }

        // ---------------- 绘制 ----------------

        private Color BorderColor()
        {
            if (!Enabled) return Theme.Border;
            if (IsOpen) return Theme.FocusRing;
            if (_hover || Focused) return Theme.Mix(Theme.BorderStrong, Theme.TextPrimary, 0.25);
            return Theme.BorderStrong;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            // 父容器底色（圆角外要露出背景）
            Color parentBg = Parent != null ? Parent.BackColor : Theme.BgSurface;
            using (var pb = new SolidBrush(parentBg)) g.FillRectangle(pb, ClientRectangle);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Theme.S(Theme.RadiusControl);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Draw.RoundRect(r, radius))
            {
                using (var b = new SolidBrush(Theme.BgSurfaceAlt)) g.FillPath(b, path);
                using (var p = new Pen(BorderColor())) g.DrawPath(p, path);
            }

            // 右侧自绘三角（不用字符/emoji：字体会 fallback 到别的字形，尺寸也控不住）
            int arrowW = Theme.S(9);
            int arrowH = Theme.S(5);
            int arrowRight = Width - Theme.S(10);
            Color arrowColor = (!Enabled) ? Theme.TextDisabled
                : ((_hover || Focused) ? Theme.TextPrimary : Theme.TextSecondary);
            int ax = arrowRight - arrowW;
            int ay = (Height - arrowH) / 2;
            using (var b = new SolidBrush(arrowColor))
            {
                Point[] tri = new Point[] {
                    new Point(ax, ay),
                    new Point(ax + arrowW, ay),
                    new Point(ax + arrowW / 2, ay + arrowH)
                };
                g.FillPolygon(b, tri);
            }

            // 文本（超长省略号，右边留出箭头的空间）
            int padL = Theme.S(8);
            int textRight = arrowRight - arrowW - Theme.S(6);
            var textRect = new Rectangle(padL, 0, Math.Max(0, textRight - padL), Height);
            Color fg = Enabled ? Theme.TextPrimary : Theme.TextDisabled;
            TextRenderer.DrawText(g, SelectedText, Font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // 焦点环：2px，画在圆角内侧（键盘用户必须看得见自己在哪）
            if (Focused && Enabled)
            {
                using (var p = new Pen(Theme.FocusRing, 2f))
                {
                    using (var path = Draw.RoundRect(new Rectangle(2, 2, Width - 5, Height - 5), Math.Max(1, radius - 1)))
                    {
                        g.DrawPath(p, path);
                    }
                }
            }
        }

        // ===========================================================
        //  ComboPopup -- 无边框弹出列表（自绘，不用 ListBox）
        // ===========================================================
        //
        // 为什么不用 ListBox：ListBox 也是吃系统视觉样式的控件（表头/滚动条/边框），
        // 会重演 ComboBox 那套"设主题无效 + 白边"。这里左手画到底，样式只有一条来源。
        private sealed class ComboPopup : Form
        {
            private readonly FlatCombo _owner;
            private readonly string[] _texts;
            private readonly int _itemH;
            private int _cursor;
            private int _hover = -1;

            public ComboPopup(FlatCombo owner, string[] texts, int selected)
            {
                _owner = owner;
                _texts = texts;
                _cursor = selected < 0 ? 0 : selected;
                _itemH = Theme.S(26);

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.BgSurfaceAlt;
                ForeColor = Theme.TextPrimary;
                Font = Theme.FontBody;
                KeyPreview = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint, true);
                DoubleBuffered = true;
            }

            public int ResultIndex { get { return _cursor; } }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                // 点到别处 / 切窗口：按"取消"关掉，不偷偷改选中项
                _owner.ClosePopup(false, false);   // 点别处：取消，且不抢焦点
            }

            // 真键盘：走 Application 泵 → PreProcessMessage → ProcessDialogKey
            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (HandleKey(keyData)) return true;
                return base.ProcessDialogKey(keyData);
            }

            // 直接投递到窗口的 WM_KEYDOWN 不经过 PreProcessMessage，会落到 OnKeyDown。
            // 两条路都接上，键盘行为不取决于"消息从哪儿来"（探针直接 SendMessage 也能测）。
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (HandleKey(e.KeyData)) { e.Handled = true; return; }
                base.OnKeyDown(e);
            }

            private bool HandleKey(Keys keyData)
            {
                Keys k = keyData & Keys.KeyCode;
                bool alt = (keyData & Keys.Alt) != 0;
                if (k == Keys.Up)
                {
                    if (alt) { _owner.ClosePopup(false, true); } else { MoveCursor(-1); }
                    return true;
                }
                if (k == Keys.Down) { MoveCursor(1); return true; }
                if (k == Keys.Enter) { _owner.ClosePopup(true, true); return true; }
                if (k == Keys.Escape) { _owner.ClosePopup(false, true); return true; }
                if (k == Keys.F4) { _owner.ClosePopup(false, true); return true; }
                if (k == Keys.Tab) { _owner.ClosePopup(false, true); return true; }
                return false;
            }

            private void MoveCursor(int step)
            {
                int n = _texts.Length;
                if (n == 0) return;
                _cursor += step;
                if (_cursor < 0) _cursor = n - 1;
                if (_cursor >= n) _cursor = 0;
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int i = ItemAt(e.Y);
                if (i != _hover) { _hover = i; Invalidate(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                int i = ItemAt(e.Y);
                if (i >= 0) { _cursor = i; _owner.ClosePopup(true, true); }
                base.OnMouseDown(e);
            }

            private int ItemAt(int y)
            {
                int i = (y - 1) / _itemH;
                if (i < 0 || i >= _texts.Length) return -1;
                return i;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;
                using (var b = new SolidBrush(Theme.BgSurfaceAlt)) g.FillRectangle(b, ClientRectangle);

                for (int i = 0; i < _texts.Length; i++)
                {
                    var r = new Rectangle(1, 1 + i * _itemH, Width - 2, _itemH);
                    Color bg = (i == _cursor) ? Theme.InfoBg
                        : ((i == _hover) ? Theme.BgHover : Theme.BgSurfaceAlt);
                    using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, _texts[i], Font,
                        new Rectangle(r.X + Theme.S(8), r.Y, r.Width - Theme.S(16), r.Height),
                        Theme.TextPrimary,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }

                using (var p = new Pen(Theme.BorderStrong))
                {
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                }
            }
        }
    }
}
