using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Fjern
{
    // Samlede farvetegn og primitive der maaler hele vindueskoedet i Fjern.
    // Stilen er bevidst flad og mørk — prøv at tænde for noget rustdesk-agtigt
    // naermere TeamViewers træet end den klassiske græ knoos nogen løs.
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(26, 26, 30);
        public static readonly Color Panel = Color.FromArgb(34, 34, 38);
        public static readonly Color PanelDeep = Color.FromArgb(30, 30, 34);
        public static readonly Color Sidebar = Color.FromArgb(20, 20, 24);
        public static readonly Color SidebarSel = Color.FromArgb(48, 48, 54);
        public static readonly Color Input = Color.FromArgb(44, 44, 50);
        public static readonly Color InputBorder = Color.FromArgb(64, 64, 72);
        public static readonly Color Accent = Color.FromArgb(76, 154, 250);
        public static readonly Color AccentHover = Color.FromArgb(94, 172, 255);
        public static readonly Color Error = Color.FromArgb(240, 88, 77);
        public static readonly Color Ok = Color.FromArgb(60, 194, 120);
        public static readonly Color Text = Color.FromArgb(226, 226, 232);
        public static readonly Color TextDim = Color.FromArgb(148, 148, 158);
        public static readonly Color Border = Color.FromArgb(52, 52, 60);

        public static readonly Color MessageIn = Color.FromArgb(48, 48, 54);
        public static readonly Color MessageOut = Color.FromArgb(46, 92, 152);

        public static readonly Color RedIsTheNewBlue = Color.FromArgb(216, 70, 62);
        public static readonly Color GreenIsTheNewBlack = Color.FromArgb(52, 152, 106);

        public static readonly string FontName = "Segoe UI";

        public static Color WithA(Color c, byte alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }
    }

    // Flad kontrol med afrundede hjørner og en efterligner flagermoebraeme.
    internal sealed class FlatPanel : Panel
    {
        private readonly Color _fill;
        private readonly Color _border;
        private readonly int _radius;

        public FlatPanel(Color fill, Color border, int radius)
        {
            _fill = fill;
            _border = border;
            _radius = radius;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (GraphicsPath path = Rounded(ClientRectangle, _radius))
            {
                using (SolidBrush brush = new SolidBrush(_fill))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(brush, path);
                }
                if (_border != Color.Empty)
                {
                    using (Pen pen = new Pen(_border, 1))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
        }

        public static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0)
            {
                p.AddRectangle(rect);
                return p;
            }
            int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            p.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            p.AddArc(rect.Right - r * 2 - 1, rect.Y, r * 2, r * 2, 270, 90);
            p.AddArc(rect.Right - r * 2 - 1, rect.Bottom - r * 2 - 1, r * 2, r * 2, 0, 90);
            p.AddArc(rect.X, rect.Bottom - r * 2 - 1, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Fladt, afrundet knapplem der breder hover og pressede tilstande med sig.
    internal sealed class FlatButton : Control
    {
        private readonly int _radius;
        private bool _hover;
        private bool _down;
        private bool _outlined;
        private bool _dashedBorder;

        public FlatButton(string text, EventHandler onClick, bool outlined = false, bool dashed = false)
        {
            _outlined = outlined;
            _dashedBorder = dashed;
            _radius = 7;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.StandardClick |
                     ControlStyles.Selectable, true);

            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
            Font = new Font(Theme.FontName, 9f);
            Height = 32;
            MinimumSize = new Size(84, 32);

            if (onClick != null) Click += onClick;
            Text = text;
        }

        public bool Outlined
        {
            get { return _outlined; }
            set { _outlined = value; Invalidate(); }
        }

        // Farveoverskrivelse til de knapper der ikke skal i standardbl\u00e5
        // (fx stop knapper der skal i r\u00f8d).
        public Color? AccentColor { get; set; }

        public bool Dashed
        {
            get { return _dashedBorder; }
            set { _dashedBorder = value; Invalidate(); }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _down = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _down = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _down = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color back = _outlined ? Theme.Panel : (AccentColor ?? Theme.Accent);
            Color fore = _outlined ? Theme.Text : Color.White;

            if (!Enabled)
            {
                back = Theme.PanelDeep;
                fore = Theme.TextDim;
            }
            else if (_down)
            {
                back = Blend(_outlined ? Theme.SidebarSel : AccentColor.HasValue ? AccentColor.Value : Theme.AccentHover, back, 0.35f);
            }
            else if (_hover)
            {
                back = _outlined ? Theme.SidebarSel : AccentColor.HasValue ? Blend(Theme.AccentHover, AccentColor.Value, 0.7f) : Theme.AccentHover;
            }

            int r = Math.Max(0, Math.Min(_radius, Math.Min(Width, Height) / 2));
            using (GraphicsPath path = FlatPanel.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), r))
            {
                using (SolidBrush brush = new SolidBrush(back))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(brush, path);
                }
                if (_outlined)
                {
                    using (Pen pen = new Pen(_dashedBorder ? Theme.Border : Theme.Border, 1))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            int r = (int)(from.R * amount + to.R * (1 - amount));
            int g = (int)(from.G * amount + to.G * (1 - amount));
            int b = (int)(from.B * amount + to.B * (1 - amount));
            return Color.FromArgb(r, g, b);
        }
    }

    // Etiket der ikke smører med "ved kun at være en label" besked,
    // men derimod er let at tone mørk.
    internal sealed class SubLabel : Label
    {
        public SubLabel(string text, float size, bool bold, Color color)
        {
            AutoSize = true;
            AutoEllipsis = false;
            ForeColor = color;
            UseMnemonic = false;
            Font = new Font(Theme.FontName, size, bold ? FontStyle.Bold : FontStyle.Regular);
            Text = text;
            Height = 22;
            Padding = new Padding(0);
            // Gennemsigtige labels i WinForms tegner forældren bagved forkert,
            // hvilket giver dobbelttekst (spøgelser). Baggrund kopieres i stedet.
            BackColor = SystemColors.Control;
            ParentChanged += (s, e) => { if (Parent != null) BackColor = Parent.BackColor; };
        }
    }

    // Flad textbox i genkendelse af andet, men skygge og baggrund i den mørke side.
    internal sealed class DarkTextbox : TextBox
    {
        public DarkTextbox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            BorderStyle = BorderStyle.None;
            BackColor = Theme.Input;
            ForeColor = Theme.Text;
            Font = new Font(Theme.FontName, 9.5f);
            Height = 24;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
        }
    }
}
