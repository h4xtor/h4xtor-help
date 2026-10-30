using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Fjern
{
    // Hovedvinduet: et dashboard i stil med RustDesk/TeamViewer.
    //
    // · Venstre sidebjælke med logo, navigation og dit rum/adgangskode.
    // · Indholdsområde der skifter mellem siderne (Del/Fjern, Filer, Chat, Log, Indstillinger).
    // · Statuslinje i bunden med forbindelsesstatus og kopier-knapper.
    //
    // Alle sessioners vinduer (ShareForm/ViewerForm) åbnes ovenpå dette vindue.
    internal sealed class MainForm : Form
    {
        private readonly PictureBox _brandIcon;
        private readonly Panel _sidebar;
        private readonly Panel _navHolder;
        private readonly List<NavButton> _navButtons = new List<NavButton>();

        // start direkte paa Hjaelp-siden (bruges af "Fjern.exe help")
        public bool StartHelpPage;

        // sat af Program nr. der er CLI-argumenter vs. ren start
        public bool CliHasArgs;

        private SubLabel _shareCardTitle;
        private SubLabel _viewCardTitle;
        private string _cliMode;

        private ListBox _logList;
        private SubLabel _statusText;
        private SubLabel _roomLabel;
        private SubLabel _passLabel;

        private Panel _pages;
        private Panel _pageShare;
        private Panel _pageFiles;
        private Panel _pageChat;
        private Panel _pageSettings;

        private FlatButton _btnShareStart;
        private FlatButton _btnViewConnect;
        private RawTextBox _txHostRelay;
        private RawTextBox _txHostPort;
        private RawTextBox _txHostRoom;
        private RawTextBox _txHostPass;
        private RawTextBox _txViewRelay;
        private RawTextBox _txViewPort;
        private RawTextBox _txViewRoom;
        private RawTextBox _txViewPass;
        private RawTextBox _txViewDirectHost;
        private RawTextBox _txViewDirectPort;

        private SessionBase _activeSession;
        private Settings _appSettings;
        private string _savedRoom;
        private string _savedPass;

        // Rolleskift ved build (csc /define:FJERN_ADMIN):
        // admin -> styr kortet + opkald; klient -> del + vis kode.
#if FJERN_ADMIN
        public const bool AdminRole = true;
#else
        public const bool AdminRole = false;
#endif

        public MainForm()
        {
            _appSettings = Settings.Load();

            // Basisvinduet er dækker af hovedtemaets baggrund.
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font(Theme.FontName, 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Text = AdminRole ? "Fjern Admin - h4xtor-help" : "Fjern - Hj\u00e6lp med din pc";
            MinimumSize = new Size(880, 640);
            Size = new Size(980, 680);

            _savedRoom = _appSettings.Get("HostRoom", SettingsHelper.RandomRoom());
            _savedPass = _appSettings.Get("HostPass", SettingsHelper.RandomPass());

            SuspendLayout();

            // ---------------------------------------------------------- sidebjælke
            _sidebar = new Panel();
            _sidebar.BackColor = Theme.Sidebar;
            _sidebar.Dock = DockStyle.Left;
            _sidebar.Width = 220;
            Controls.Add(_sidebar);

            Panel brand = new Panel();
            brand.BackColor = Theme.Sidebar;
            brand.Dock = DockStyle.Top;
            brand.Height = 96;
            brand.Padding = new Padding(16, 18, 12, 0);
            _sidebar.Controls.Add(brand);

            _brandIcon = new PictureBox();
            _brandIcon.Location = new Point(16, 14);
            _brandIcon.Size = new Size(36, 36);
            _brandIcon.BackColor = Theme.Accent;
            _brandIcon.SizeMode = PictureBoxSizeMode.Zoom;
            brand.Controls.Add(_brandIcon);

            SubLabel brandTitle = new SubLabel("Fjern", 17, true, Theme.Text);
            brandTitle.Location = new Point(62, 14);
            brand.Controls.Add(brandTitle);

            SubLabel brandSub = new SubLabel("skærmdeling til venner", 7.5f, false, Theme.TextDim);
            brandSub.Location = new Point(62, 40);
            brand.Controls.Add(brandSub);

            _navHolder = new Panel();
            _navHolder.BackColor = Theme.Sidebar;
            _navHolder.Dock = DockStyle.Top;
            _navHolder.Height = 4 + 7*38 + 4;  // 7 nav-knapper a 38 px + luft
            _navHolder.Padding = new Padding(10, 4, 10, 4);
            _sidebar.Controls.Add(_navHolder);
            // sidebjælke ventiler — navigation øverst, status nederst
            _navHolder.BringToFront();

            Panel sbTail = new Panel();
            sbTail.BackColor = Theme.Sidebar;
            sbTail.Dock = DockStyle.Bottom;
            sbTail.Height = 118;
            _sidebar.Controls.Add(sbTail);
            sbTail.BringToFront();

            SubLabel sideTail = new SubLabel("Dit rum", 7.5f, false, Theme.TextDim);
            sideTail.Location = new Point(16, 8);
            sbTail.Controls.Add(sideTail);

            _roomLabel = new SubLabel("rum  " + _savedRoom, 10, true, Theme.Text);
            _roomLabel.Location = new Point(16, 28);
            sbTail.Controls.Add(_roomLabel);

            _passLabel = new SubLabel("kode  " + _savedPass, 10, true, Theme.Text);
            _passLabel.Location = new Point(16, 50);
            sbTail.Controls.Add(_passLabel);

            FlatButton copyRoom = new FlatButton("Kopier rum", delegate { Copy(_roomLabel.Text.Replace("rum  ", "")); }, false);
            copyRoom.SetBounds(14, 74, 92, 26);
            sbTail.Controls.Add(copyRoom);

            FlatButton copyPass = new FlatButton("Kopier kode", delegate { Copy(_passLabel.Text.Replace("kode  ", "")); }, false);
            copyPass.SetBounds(110, 74, 92, 26);
            sbTail.Controls.Add(copyPass);

            // ------------------------------------------------------------- indhold
            _pages = new Panel();
            _pages.BackColor = Theme.Bg;
            _pages.Dock = DockStyle.Fill;
            _pages.Padding = new Padding(18, 14, 18, 8);
            _pages.AutoScroll = true;
            Controls.Add(_pages);
            _pages.BringToFront();

            Panel statusStrip = new Panel();
            statusStrip.BackColor = Theme.PanelDeep;
            statusStrip.Dock = DockStyle.Bottom;
            statusStrip.Height = 26;
            Controls.Add(statusStrip);
            statusStrip.BringToFront();

            _statusText = new SubLabel("klar - opret en session for at starte", 8, false, Theme.TextDim);
            _statusText.Location = new Point(12, 6);
            statusStrip.Controls.Add(_statusText);

            ClickHelper hint = new ClickHelper("Fjern bruger en ekstern relay for at forbinde. Se README",
                delegate { ShowPage("share"); });
            hint.Location = new Point(statusStrip.Width - 240, 6);
            statusStrip.Controls.Add(hint);

            ResumeLayout();

            AddNavItem("Del sk\u00e6rm", "Del sk\u00e6rm med en ven (v\u00e6rt)", delegate { ShowPage("share"); });
#if FJERN_ADMIN
            AddNavItem("Fjernstyring", "Styring af en vens pc (som seer)", delegate { ShowPage("share"); });
#endif
            AddNavItem("Filer", "Overf\u00f8r filer til og fra din ven", delegate { ShowPage("files"); });
            AddNavItem("Chat", "Beskeder mellem host og seer", delegate { ShowPage("chat"); });
            AddNavItem("Log", "Seneste h\u00e6ndelser", delegate { ShowPage("log"); });
            AddNavItem("Indstillinger", "Port, mappe og overf\u00f8ringshastighed", delegate { ShowPage("settings"); });
            AddNavItem("Hj\u00e6lp", "Kort forklaring af sikkerhed", delegate { ShowPage("help"); });

            BuildSharePage();
            BuildFilesPage();
            BuildChatPage();
            BuildSettingsPage();
            BuildLogPage();
            BuildHelpPage();

            ShowPage("share");

            FormClosing += OnClosing;
            Shown += delegate
            {
                // ikon der tegnes med GDI+
                DrawBrandIcon();
                Status("klar - opret en session eller vent p\u00e5 en ven");
                if (_cliMode == "share") RunCliShare();
                else if (_cliMode == "view") RunCliView();
#if FJERN_ADMIN
                else
                {
                    // admin: \u00e5bn direkte p\u00e5 Fjernstyring
                    ShowPage("share");
                    Status("indtast rum + kode fra din ven og tryk Forbind");
                }
#else
                else if (!CliHasArgs && _appSettings.GetBool("AutoShare", true))
                {
                    // vennens klient: \u00e5bn, start deling automatisk, vis kode
                    StartShare();
                }
#endif
            };
        }

        // Til genveje: Fjern.exe share <server> <port> <rum> <kode>
        private void RunCliShare()
        {
            _txHostRelay.Text = _cliDirect ? "" : _cliHost;
            _txHostPort.Text = _cliPort.ToString();
            _txHostRoom.Text = _cliRoom;
            _txHostPass.Text = _cliPass;
            StartShare();
        }

        private void RunCliView()
        {
            if (_cliDirect)
            {
                _txViewRelay.Text = "";
                _txViewDirectHost.Text = _cliHost;
                _txViewDirectPort.Text = (_cliPort + 1).ToString();
            }
            else
            {
                _txViewRelay.Text = _cliHost;
                _txViewPort.Text = _cliPort.ToString();
            }
            _txViewRoom.Text = _cliRoom;
            _txViewPass.Text = _cliPass;
            StartView();
        }

        private string _cliHost;
        private int _cliPort;
        private string _cliRoom;
        private string _cliPass;
        private bool _cliDirect;

        public MainForm(string mode, string host, int port, string room, string password, bool direct)
            : this()
        {
            _cliMode = mode;
            _cliHost = host;
            _cliPort = port;
            _cliRoom = room;
            _cliPass = password;
            _cliDirect = direct;
        }

        // efter erstesidebygning: ga direkte til Hjaelp naar flaget er sat
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (StartHelpPage) { ShowPage("help"); StartHelpPage = false; }
        }

        private sealed class ClickHelper : Label
        {
            private readonly Action _action;

            public ClickHelper(string text, Action action)
            {
                _action = action;
                AutoSize = true;
                ForeColor = Theme.TextDim;
                Font = new Font(Theme.FontName, 7f);
                Text = text;
                Cursor = Cursors.Hand;
                Click += delegate { _action(); };
            }
        }

        private void DrawBrandIcon()
        {
            using (Bitmap bmp = new Bitmap(36, 36))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Theme.Accent);
                    using (Pen pen = new Pen(Color.White, 2.2f))
                    {
                        g.DrawArc(pen, 8, 10, 20, 16, 200, 140);
                        g.DrawLine(pen, 12, 22, 9, 28);
                        g.DrawLine(pen, 24, 22, 27, 28);
                    }
                }
                _brandIcon.Image = (Image)bmp.Clone();
            }
        }

        // ------------------------------------------------------------- navigation

        private void AddNavItem(string title, string hint, Action show)
        {
            NavButton nav = new NavButton(title, hint, show);
            nav.Dock = DockStyle.Top;
            nav.Height = 38;
            _navHolder.Controls.Add(nav);
            _navButtons.Add(nav);
            if (_navButtons.Count == 1) { nav.Highlight(true); }  // forsiden lyser fra start
            // Dock=Top stabler bagfra: send knapperne bagfra-til-front,
            // sa den foerrest tilfjede ender oeverst
            for (int i = _navButtons.Count - 1; i >= 0; i--) { _navButtons[i].SendToBack(); }

            // highlight: kun den valgte nav-knap lyser op
            nav.Click += delegate
            {
                foreach (NavButton other in _navButtons) other.Highlight(false);
                nav.Highlight(true);
            };
        }

        private sealed class NavButton : Control
        {
            private bool _highlight;
            private readonly string _hint;
            private readonly Action _show;
            private bool _hover;

            public NavButton(string title, string hint, Action show)
            {
                _hint = hint;
                _show = show;
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint, true);
                BackColor = Theme.Sidebar;
                ForeColor = Theme.Text;
                Font = new Font(Theme.FontName, 10f);
                Cursor = Cursors.Hand;
                Text = title;
                Click += delegate { _show(); };
            }

            public void Highlight(bool value)
            {
                _highlight = value;
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
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Color back = Theme.Sidebar;
                if (_highlight) back = Theme.SidebarSel;
                else if (_hover) back = Color.FromArgb(32, 32, 38);

                using (SolidBrush brush = new SolidBrush(back))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }

                Color fore = _highlight ? Theme.Text : Theme.TextDim;
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new Rectangle(10, 0, Width - 20, Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }

        private void ShowPage(string name)
        {
            SuspendLayout();

            _pageShare.Visible = name == "share";
            _pageFiles.Visible = name == "files";
            _pageChat.Visible = name == "chat";
            _pageSettings.Visible = name == "settings";

            Panel show = null;
            if (name == "share") show = _pageShare;
            else if (name == "files") show = _pageFiles;
            else if (name == "chat") show = _pageChat;
            else if (name == "settings") show = _pageSettings;
            else if (name == "log") show = _logPage;
            else if (name == "help") show = _helpPage;

            if (show != null) show.BringToFront();
            ResumeLayout();
            Status(show == _logPage ? "seneste h\u00e6ndelser" : "klar");
        }

        private Panel _logPage;
        private Panel _helpPage;
        private ListBox _chatList;
        private TextBox _chatInput;
        private FlatButton _btnChatSend;
        private SessionBase _chatSession;

        // -------------------------------------------------------------- deling

        private void BuildSharePage()
        {
            _pageShare = new Panel();
            _pageShare.BackColor = Theme.Bg;
            _pageShare.Dock = DockStyle.Fill;
            _pageShare.AutoScroll = true;
            _pages.Controls.Add(_pageShare);

            FlowCard shareCard = new FlowCard("Del sk\u00e6rm fra denne pc", "V\u00e6rten - lad en ven se og styre");
            shareCard.SetBounds(20, 16, 420, 340);
            _pageShare.Controls.Add(shareCard);
            _shareCardTitle = shareCard.Title;

            _txHostRelay = NewField(shareCard, "Relay-server (tom = direkte LAN)",
                _appSettings.Get("HostRelay", SettingsHelper.RelayForFriends(_appSettings)), 0);
            _txHostPort = NewField(shareCard, "Relay-port", _appSettings.Get("Port", "6767"), 1).AsNumber();
            _txHostRoom = NewField(shareCard, "Rum (ID)", _savedRoom, 2);
            _txHostPass = NewField(shareCard, "Adgangskode", _savedPass, 3);

            _btnShareStart = new FlatButton("Start delingen", delegate { StartShare(); }, false);
            _btnShareStart.SetBounds(20, 280, 160, 32);
            shareCard.Controls.Add(_btnShareStart);

            FlatButton btnNewCode = new FlatButton("Gener\u00e9r nyt ID", delegate
            {
                _savedRoom = SettingsHelper.RandomRoom();
                _savedPass = SettingsHelper.RandomPass();
                _txHostRoom.Text = _savedRoom;
                _txHostPass.Text = _savedPass;
                _roomLabel.Text = "rum  " + _savedRoom;
                _passLabel.Text = "kode  " + _savedPass;
            }, true);
            btnNewCode.SetBounds(200, 280, 140, 32);
            shareCard.Controls.Add(btnNewCode);

            FlowCard viewCard = new FlowCard("Styr en veninds pc", "Seer - kobl videre til et rum");
            viewCard.SetBounds(460, 16, 420, 430);
#if FJERN_ADMIN
            _pageShare.Controls.Add(viewCard);
#endif
            _viewCardTitle = viewCard.Title;

            _txViewRelay = NewField(viewCard, "Relay-server (tom = direkte)",
                _appSettings.Get("HostRelay", SettingsHelper.RelayForFriends(_appSettings)), 0);
            _txViewPort = NewField(viewCard, "Relay-port", _appSettings.Get("Port", "6767"), 1).AsNumber();
            _txViewRoom = NewField(viewCard, "Rum (ID)", _appSettings.Get("LastRoom", ""), 2);
            _txViewPass = NewField(viewCard, "Adgangskode", _appSettings.Get("LastPass", ""), 3);
            _txViewDirectHost = NewField(viewCard, "Direkte: din vens IP", _appSettings.Get("LastDirect", ""), 4);
            _txViewDirectPort = NewField(viewCard, "Direkte: port (v\u00e6rts = port+1)", _appSettings.Get("LastDirectPort", "6768"), 5).AsNumber();

            _btnViewConnect = new FlatButton("Forbind", delegate { StartView(); }, false);
            _btnViewConnect.SetBounds(20, 384, 160, 32);
            viewCard.Controls.Add(_btnViewConnect);
        }

        private sealed class FlowCard : Panel
        {
            private readonly SubLabel _title;
            private readonly SubLabel _sub;

            public FlowCard(string title, string sub)
            {
                BackColor = Theme.Panel;
                _title = new SubLabel(title, 13, true, Theme.Text);
                _title.Location = new Point(18, 16);
                Controls.Add(_title);
                _sub = new SubLabel(sub, 8, false, Theme.TextDim);
                _sub.Location = new Point(18, 42);
                Controls.Add(_sub);

                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.UserPaint, true);
            }

            public SubLabel Title { get { return _title; } }
            public SubLabel Sub { get { return _sub; } }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (Pen pen = new Pen(Theme.Border, 1))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
                    Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                    int radius = 10;
                    path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
                    path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
                    path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
                    path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
                    path.CloseFigure();
                    e.Graphics.DrawPath(pen, path);
                    path.Dispose();
                }
            }
        }

        private sealed class LabeledField : Panel
        {
            public SubLabel Label;

            public LabeledField(string text)
            {
                BackColor = Theme.Panel;
                Label = new SubLabel(text, 7.5f, false, Theme.TextDim);
                Label.SetBounds(0, 0, 200, 14);
                Controls.Add(Label);
            }
        }

        private sealed class RawTextBox : TextBox
        {
            public RawTextBox AsNumber()
            {
                // .NET 4's TextBox kr\u00e6ver masken via KeyPress hvis vi vil beholde det fladt.
                KeyPress += delegate(object sender, KeyPressEventArgs e)
                {
                    if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
                };
                return this;
            }
        }

        private RawTextBox NewField(FlowCard card, string title, string value, int row)
        {
            LabeledField field = new LabeledField(title);
            field.SetBounds(20, 62 + row * 52, 240, 14);
            card.Controls.Add(field);

            RawTextBox box = new RawTextBox();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Theme.Input;
            box.ForeColor = Theme.Text;
            box.Font = new Font(Theme.FontName, 9.5f);
            box.Text = value;
            box.SetBounds(20, 62 + row * 52 + 16, 380, 24);
            card.Controls.Add(box);
            return box;
        }

        // ------------------------------------------------------------------ filer

        private TextBox _txFilesTarget;
        private ListBox _fileList;
        private SessionBase _filesSession;

        private void BuildFilesPage()
        {
            _pageFiles = new Panel();
            _pageFiles.BackColor = Theme.Bg;
            _pageFiles.Dock = DockStyle.Fill;
            _pageFiles.AutoScroll = true;
            _pages.Controls.Add(_pageFiles);

            FlowCard card = new FlowCard("Filoverf\u00f8rsel", "Send via en aktiv session");
            card.SetBounds(20, 16, 800, 460);
            _pageFiles.Controls.Add(card);

            SubLabel source = new SubLabel("Kilder - v\u00e6lg filer", 9, true, Theme.Text);
            source.Location = new Point(20, 62);
            card.Controls.Add(source);

            FlatButton browse = new FlatButton("Tilføj filer", delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Multiselect = true;
                    dialog.Title = "V\u00e6lg filer";
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        foreach (string file in dialog.FileNames)
                        {
                            _fileList.Items.Add(file);
                        }
                        Status("tilf\u00f8jede " + dialog.FileNames.Length + " filer til k\u00f8en");
                    }
                }
            }, true);
            browse.SetBounds(560, 58, 110, 26);
            card.Controls.Add(browse);

            FlatButton clear = new FlatButton("Ryd", delegate { _fileList.Items.Clear(); }, true);
            clear.SetBounds(676, 58, 68, 26);
            card.Controls.Add(clear);

            _fileList = new ListBox();
            _fileList.BorderStyle = BorderStyle.FixedSingle;
            _fileList.BackColor = Theme.Input;
            _fileList.ForeColor = Theme.Text;
            _fileList.Font = new Font(Theme.FontName, 9f);
            _fileList.SetBounds(20, 92, 724, 180);
            card.Controls.Add(_fileList);

            SubLabel dest = new SubLabel("M\u00e5lmappe p\u00e5 modtagersiden", 9, true, Theme.Text);
            dest.Location = new Point(20, 284);
            card.Controls.Add(dest);

            _txFilesTarget = new TextBox();
            _txFilesTarget.BorderStyle = BorderStyle.FixedSingle;
            _txFilesTarget.BackColor = Theme.Input;
            _txFilesTarget.ForeColor = Theme.Text;
            _txFilesTarget.Font = new Font(Theme.FontName, 9.5f);
            _txFilesTarget.Text = _appSettings.Get("IncomingFolder",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Fjern");
            _txFilesTarget.SetBounds(20, 306, 500, 24);
            card.Controls.Add(_txFilesTarget);

            SubLabel info = new SubLabel(".session og adgangskode er det samme som p\u00e5 Deling-fanen", 7.5f, false, Theme.TextDim);
            info.Location = new Point(20, 340);
            card.Controls.Add(info);

            FlatButton send = new FlatButton("Send filer", delegate
            {
                if (_fileList.Items.Count == 0)
                {
                    Status("tilf\u00f8j filer f\u00f8rst");
                    return;
                }
                if (_filesSession == null || !_filesSession.Running)
                {
                    Status("ingen aktiv session - start deling eller forbind f\u00f8rst");
                    return;
                }
                string[] paths = new string[_fileList.Items.Count];
                for (int i = 0; i < _fileList.Items.Count; i++) paths[i] = _fileList.Items[i].ToString();

                ViewerSession viewer = _filesSession as ViewerSession;
                if (viewer != null) viewer.SendFiles(paths);
                Status("sender " + paths.Length + " filer");
            }, false);
            send.SetBounds(20, 368, 160, 32);
            card.Controls.Add(send);
        }

        private void BuildChatPage()
        {
            _pageChat = new Panel();
            _pageChat.BackColor = Theme.Bg;
            _pageChat.Dock = DockStyle.Fill;
            _pages.Controls.Add(_pageChat);

            FlowCard card = new FlowCard("Chat", "Beskeder via den krypterede tunnel");
            card.SetBounds(20, 16, 760, 440);
            _pageChat.Controls.Add(card);

            _chatList = new ListBox();
            _chatList.BorderStyle = BorderStyle.FixedSingle;
            _chatList.BackColor = Theme.Input;
            _chatList.ForeColor = Theme.Text;
            _chatList.Font = new Font(Theme.FontName, 9f);
            _chatList.SetBounds(20, 62, 700, 290);
            card.Controls.Add(_chatList);

            _chatInput = new TextBox();
            _chatInput.BorderStyle = BorderStyle.FixedSingle;
            _chatInput.BackColor = Theme.Input;
            _chatInput.ForeColor = Theme.Text;
            _chatInput.Font = new Font(Theme.FontName, 9.5f);
            _chatInput.SetBounds(20, 364, 580, 24);
            card.Controls.Add(_chatInput);

            _btnChatSend = new FlatButton("Send", delegate { SendChat(); }, false);
            _btnChatSend.SetBounds(610, 362, 110, 28);
            card.Controls.Add(_btnChatSend);
        }

        private void SendChat()
        {
            string text = _chatInput.Text.Trim();
            if (text.Length == 0) return;
            if (_chatSession == null || !_chatSession.Running)
            {
                Status("ingen aktiv session - chat kr\u00e6ver en forbindelse");
                return;
            }
            HostSession host = _chatSession as HostSession;
            ViewerSession viewer = _chatSession as ViewerSession;
            if (host != null)
            {
                host.SendText(text);
            }
            else if (viewer != null)
            {
                viewer.SendText(text);
            }
            _chatInput.Text = "";
            _chatList.Items.Add("mig: " + text);
            _chatList.TopIndex = _chatList.Items.Count - 1;
        }

        // ------------------------------------------------------------- indstillinger

        private void BuildSettingsPage()
        {
            _pageSettings = new Panel();
            _pageSettings.BackColor = Theme.Bg;
            _pageSettings.Dock = DockStyle.Fill;
            _pageSettings.AutoScroll = true;
            _pages.Controls.Add(_pageSettings);

            FlowCard card = new FlowCard("Indstillinger", "gemmes i %APPDATA%\\Fjern\\settings.ini");
            card.SetBounds(20, 16, 620, 400);
            _pageSettings.Controls.Add(card);

            int row = 0;
            SettingsFields settings = new SettingsFields();

            settings.Title("Overf\u00f8rsel", 20, ref row, card);

            settings.Field("FPS (billeder i sekundet)", _appSettings.GetInt("Fps", 20).ToString(), ref row, card);
            settings.Field("Kvalitet (JPEG 1-100)", _appSettings.GetInt("Quality", 65).ToString(), ref row, card);
            settings.Field("Maksimum bredde (px)", _appSettings.GetInt("MaxWidth", 1600).ToString(), ref row, card);

            settings.Title("Forbindelse", 20, ref row, card);
            settings.Field("Standard port", _appSettings.Get("Port", "6767"), ref row, card);
            settings.Field("Relay-server (venner kobler via denne)",
                _appSettings.Get("DefaultRelay", ""), ref row, card);

            SubLabel monLabel = new SubLabel("Sk\u00e6rm der deles (v\u00e6rts)", 7.5f, false, Theme.TextDim);
            monLabel.Location = new Point(20, 54 + row * 46);
            card.Controls.Add(monLabel);
            row++;

            ComboBox monitor = new ComboBox();
            monitor.DropDownStyle = ComboBoxStyle.DropDownList;
            monitor.BackColor = Theme.Input;
            monitor.ForeColor = Theme.Text;
            monitor.Font = new Font(Theme.FontName, 9.5f);
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                monitor.Items.Add((i + 1) + ". " + Screen.AllScreens[i].DeviceName
                    + " (" + Screen.AllScreens[i].Bounds.Width + "x" + Screen.AllScreens[i].Bounds.Height + ")");
            }
            monitor.SelectedIndex = Math.Max(0, Math.Min(Screen.AllScreens.Length - 1, _appSettings.GetInt("Monitor", 0)));
            monitor.SetBounds(20, 54 + row * 46, 380, 26);
            card.Controls.Add(monitor);
            row++;

            SubLabel folder = new SubLabel("Mappe til modtagne filer (host)", 7.5f, false, Theme.TextDim);
            folder.Location = new Point(20, 54 + row * 46);
            card.Controls.Add(folder);
            row++;

            TextBox txFolder = new TextBox();
            txFolder.BorderStyle = BorderStyle.FixedSingle;
            txFolder.BackColor = Theme.Input;
            txFolder.ForeColor = Theme.Text;
            txFolder.Font = new Font(Theme.FontName, 9.5f);
            txFolder.Text = _appSettings.Get("IncomingFolder",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Fjern");
            txFolder.SetBounds(20, 54 + row * 46, 380, 24);
            card.Controls.Add(txFolder);
            row++;

            CheckBox allow = new CheckBox();
            allow.Text = "Tillad fjernstyring som standard";
            allow.Checked = _appSettings.GetBool("AllowControl", true);
            allow.ForeColor = Theme.Text;
            allow.BackColor = Theme.Panel;
            allow.AutoSize = true;
            allow.SetBounds(20, 54 + row * 46, 300, 22);
            card.Controls.Add(allow);
            row++;

            CheckBox autoShare = new CheckBox();
            autoShare.Text = "AutoStart deling ved start (vennens klient)";
            autoShare.Checked = _appSettings.GetBool("AutoShare", false);
            autoShare.ForeColor = Theme.Text;
            autoShare.BackColor = Theme.Panel;
            autoShare.AutoSize = true;
            autoShare.SetBounds(20, 54 + row * 46, 300, 22);
            card.Controls.Add(autoShare);
            row++;

            settings.Title("Gem", 20, ref row, card);
            FlatButton save = new FlatButton("Gem indstillinger", delegate
            {
                SaveSettings(settings, monitor, txFolder, allow, autoShare);
            }, false);
            save.SetBounds(20, 54 + row * 46 + 4, 190, 32);
            card.Controls.Add(save);
        }

        private sealed class SettingsFields
        {
            private readonly List<LabeledText> items = new List<LabeledText>();

            public void Title(string text, int x, ref int row, Panel card)
            {
                SubLabel title = new SubLabel(text, 10, true, Theme.Text);
                title.Location = new Point(x, 54 + row * 46);
                title.Width = 400;
                card.Controls.Add(title);
                row++;
            }

            public void Field(string label, string value, ref int row, Panel card)
            {
                LabeledText item = new LabeledText(label, value, row);
                card.Controls.Add(item.Label);
                card.Controls.Add(item.Text);
                items.Add(item);
                row++;
            }

            public TextBox Get(string label)
            {
                foreach (LabeledText item in items)
                {
                    if (item.Label.Text == label) return item.Text;
                }
                return null;
            }

            public void Blink(string label, Color color)
            {
                foreach (LabeledText item in items)
                {
                    if (item.Label.Text == label)
                    {
                        item.Label.ForeColor = color;
                        return;
                    }
                }
            }
        }

        private sealed class LabeledText
        {
            public readonly SubLabel Label;
            public readonly TextBox Text;

            public LabeledText(string label, string value, int row)
            {
                Label = new SubLabel(label, 7.5f, false, Theme.TextDim);
                Label.Location = new Point(20, 54 + row * 46);
                Text = new TextBox();
                Text.BorderStyle = BorderStyle.FixedSingle;
                Text.BackColor = Theme.Input;
                Text.ForeColor = Theme.Text;
                Text.Font = new Font(Theme.FontName, 9.5f);
                Text.Text = value;
                Text.SetBounds(20, 54 + row * 46 + 16, 380, 24);
            }
        }

        private void SaveSettings(SettingsFields fields, ComboBox monitor, TextBox folder,
            CheckBox allow, CheckBox autoShare)
        {
            string fps = fields.Get("FPS (billeder i sekundet)").Text;
            string qual = fields.Get("Kvalitet (JPEG 1-100)").Text;
            string maxW = fields.Get("Maksimum bredde (px)").Text;
            string port = fields.Get("Standard port").Text;
            string relay = fields.Get("Relay-server (venner kobler via denne)").Text;
            relay = relay.Trim();

            bool bad = false;
            if (NotBetween(fps, 1, 60))    { fields.Blink("FPS (billeder i sekundet)", Theme.Error); bad = true; }
            if (NotBetween(qual, 10, 100)) { fields.Blink("Kvalitet (JPEG 1-100)", Theme.Error); bad = true; }
            if (NotBetween(maxW, 400, 4096)) { fields.Blink("Maksimum bredde (px)", Theme.Error); bad = true; }
            if (NotBetween(port, 1024, 65535)) { fields.Blink("Standard port", Theme.Error); bad = true; }
            if (bad) { Status("ugyldige tal - de r\u00f8de felter skal rettes", Theme.Error); return; }

            _appSettings.Set("Fps", fps);
            _appSettings.Set("Quality", qual);
            _appSettings.Set("MaxWidth", maxW);
            _appSettings.Set("Port", port);
            _appSettings.Set("Monitor", monitor.SelectedIndex.ToString());
            _appSettings.Set("IncomingFolder", folder.Text.Trim());
            _appSettings.Set("AllowControl", allow.Checked ? "True" : "False");
            _appSettings.Set("AutoShare", autoShare.Checked ? "True" : "False");
            _appSettings.Set("DefaultRelay", relay);
            _appSettings.Save();
            Status("indstillinger gemt" + (relay.Length > 0 ? " - vennens klient kobler " + relay : ""));
        }

        private static bool NotBetween(string text, int min, int max)
        {
            int value;
            if (!int.TryParse(text, out value)) return true;
            return value < min || value > max;
        }

        private static int fieldInt(TextBox box, int fallback)
        {
            if (box == null) return fallback;
            int value;
            return int.TryParse(box.Text, out value) ? value : fallback;
        }

        // -------------------------------------------------------------------- log

        private void BuildLogPage()
        {
            _logPage = new Panel();
            _logPage.BackColor = Theme.Bg;
            _logPage.Dock = DockStyle.Fill;
            _pages.Controls.Add(_logPage);

            _logList = new ListBox();
            _logList.BackColor = Theme.Panel;
            _logList.ForeColor = Theme.Text;
            _logList.BorderStyle = BorderStyle.FixedSingle;
            _logList.Font = new Font("Consolas", 8.5f);
            _logList.Dock = DockStyle.Fill;
            _logPage.Controls.Add(_logList);
            _logPage.Visible = false;
        }

        private void BuildHelpPage()
        {
            _helpPage = new Panel();
            _helpPage.BackColor = Theme.Bg;
            _helpPage.Dock = DockStyle.Fill;
            _helpPage.AutoScroll = true;
            _pages.Controls.Add(_helpPage);

            FlowCard help = new FlowCard("S\u00e5dan forbinder du korrekt - trin for trin",
                "F\u00f8lg tallene i r\u00e6kkef\u00f8lge, s\u00e5 virker det f\u00f8rste gang");
            help.SetBounds(20, 16, 620, 760);
            _helpPage.Controls.Add(help);

            string text = LoadHelpText();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int y = 62;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) { y += 10; continue; }
                bool heading = line.StartsWith("TRIN ") ||
                               line.StartsWith("HVAD ") ||
                               line.StartsWith("TIP ");
                SubLabel li = new SubLabel(line, heading ? 9 : 9,
                    heading, heading ? Theme.Accent : Theme.Text);
                li.Location = new Point(heading ? 20 : 30, y);
                help.Controls.Add(li);
                y += heading ? 24 : 20;
            }
        }

        private static string LoadHelpText()
        {
            System.Reflection.Assembly asm =
                System.Reflection.Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("Fjern.Hjaelp"))
            {
                if (s != null)
                {
                    using (StreamReader reader = new StreamReader(s, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "tekst\\hjaelp.txt");
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return "Se README.md";
            }
        }

        private void Copy(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                Status("kopieret " + text);
            }
        }

        private void Status(string text)
        {
            _statusText.Text = text;
        }

        private void Status(string text, Color color)
        {
            _statusText.Text = text;
            _statusText.ForeColor = color;
        }

        // --------------------------------------------------------------- sessioner

        private void StartShare()
        {
            string relay = _txHostRelay.Text.Trim();
            int port = ParseInt(_txHostPort.Text, 6767);
            string room = _txHostRoom.Text.Trim();
            string password = _txHostPass.Text.Trim();

            if (room.Length == 0 || password.Length == 0)
            {
                MessageBox.Show(this, "Udfyld rum og adgangskode f\u00f8rst.", "Fjern",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _appSettings.Set("Port", port.ToString());
            _appSettings.Set("HostRoom", room);
            _appSettings.Set("HostPass", password);
            _appSettings.Save();

            HostSession session = new HostSession(
                string.IsNullOrEmpty(relay) ? LinkMode.Direct : LinkMode.Relay,
                relay, port, room, password, port + 1);
            session.TargetFps = _appSettings.GetInt("Fps", 20);
            session.Quality = _appSettings.GetInt("Quality", 65);
            session.MaxWidth = _appSettings.GetInt("MaxWidth", 1600);
            session.MonitorIndex = _appSettings.GetInt("Monitor", 0);
            session.IncomingFolder = _appSettings.Get("IncomingFolder",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Fjern");
            session.AllowControl = _appSettings.GetBool("AllowControl", true);

            SessionBase previous = _activeSession;
            if (previous != null && previous.Running)
            {
                MessageBox.Show(this, "En session k\u00f8rer allerede - stop den f\u00f8rst.", "Fjern",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _activeSession = _chatSession = _filesSession = session;

            ShareForm share = new ShareForm(session, room, relay.Length > 0);
            share.Show();
            Status("deling startede - vent p\u00e5 din ven");
            session.Start();
        }

        /// <summary>Seer/admin: kobler til den delende pc og \u00e5bner kontrolvinduet.</summary>
        private void StartView()
        {
            string relay = _txViewRelay.Text.Trim();
            int port = ParseInt(_txViewPort.Text, 6767);
            string room = _txViewRoom.Text.Trim();
            string password = _txViewPass.Text.Trim();

            if (room.Length == 0)
            {
                MessageBox.Show(this, "Indtast dit vens rum-ID f\u00f8rst.", "Fjern",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _appSettings.Set("LastRoom", room);
            _appSettings.Set("LastPass", password);
            _appSettings.Set("LastDirect", _txViewDirectHost.Text.Trim());
            _appSettings.Set("LastDirectPort", ParseInt(_txViewDirectPort.Text, port + 1).ToString());
            _appSettings.Save();

            ViewerSession session = new ViewerSession(
                string.IsNullOrEmpty(relay) ? LinkMode.Direct : LinkMode.Relay,
                relay, port, room, password,
                _txViewDirectHost.Text.Trim(), ParseInt(_txViewDirectPort.Text, port + 1));

            SessionBase previous = _activeSession;
            if (previous != null && previous.Running)
            {
                MessageBox.Show(this, "En session k\u00f8rer allerede - stop den f\u00f8rst.", "Fjern",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _activeSession = _chatSession = _filesSession = session;

            ViewerForm viewer = new ViewerForm(session, room, relay.Length > 0);
            viewer.Show();
            Status("forbinder...");

            session.ScreenUpdated += delegate { };
            session.Start();
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse(text, out value) ? value : fallback;
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            SessionBase session = _activeSession;
            if (session != null)
            {
                session.Stop();
            }
        }
    }

    internal static class SettingsHelper
    {
        private static Random _random = new Random();

        // FILL: din Oracle VPS-adresse - fyldes ind i kildeteksten f\u00f8r build,
        // s\u00e5 vennens EXE automatisk ved, hvor relayet k\u00f8rer (tom = INI vinder).
        public const string DefaultRelay = "";
        public const int DefaultPort = 6767;

        public static string RandomRoom()
        {
            return _random.Next(100000, 999999).ToString();
        }

        public static string RandomPass()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(chars[_random.Next(chars.Length)]);
            return sb.ToString();
        }

        /// <summary>Relay-adresse vennens klient skal bruge: INI vinder, ellers konstanten.</summary>
        public static string RelayForFriends(Settings app)
        {
            string ini = app.Get("DefaultRelay", "");
            return string.IsNullOrEmpty(ini)
                ? DefaultRelay
                : ini;
        }
    }
}
