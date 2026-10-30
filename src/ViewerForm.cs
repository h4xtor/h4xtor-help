using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Fjern
{
    // Sm\u00e5 knapt Kanaler:機overs\u00e6ttelse af mus-eventer til protokollens koder.
    internal static class MouseWire
    {
        public const byte Left = 1;
        public const byte Right = 2;
        public const byte Middle = 4;
    }
    // Seervinduet: tegner den modtagne sk\u00e6rm, overs\u00e6tter mus/tastatur
    // til meddelelser, og har en v\u00e6rkt\u00f8jslinje med chat, filer,
    // kvalitet, fuldsk\u00e6rm (F11) og forbindelsesstatus.
    internal sealed class ViewerForm : Form
    {
        private readonly ViewerSession _session;
        private readonly string _room;
        private readonly CanvasPanel _canvas;
        private readonly SubLabel _status;
        private readonly SubLabel _stats;
        private Panel _chatPanel;
        private ListBox _chatList;
        private TextBox _chatInput;
        private Control _chatHost;
        private readonly Timer _timer;
        private bool _fullScreen;
        private FormBorderStyle _savedBorderStyle;
        private Rectangle _savedBounds;

        // taleopkald
        private FlatButton _btnTalk;
        private FlatButton _btnCtrlToggle;
        private VoicePlayer _voicePlayer;
        private VoiceRecorder _voiceRecorder;
        private bool _callActive;
        private readonly object _camLock = new object();
        private CamViewerForm _camViewer;

        internal sealed class CanvasPanel : Panel
        {
            public ViewerSession Session;
            public Color Background = Theme.Sidebar;

            public CanvasPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Sidebar;
                Cursor = Cursors.Hand;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                ViewerSession session = Session;
                if (session == null) return;

                Bitmap screen = session.Screen;
                if (screen == null)
                {
                    TextRenderer.DrawText(e.Graphics, "venter p\u00e5 billede...", Font,
                        ClientRectangle, Theme.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                Rectangle fit = FitTo(screen.Size, ClientRectangle);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                e.Graphics.DrawImage(screen, fit);
            }

            private static Rectangle FitTo(Size source, Rectangle target)
            {
                double scale = Math.Min(target.Width / (double)source.Width,
                                        target.Height / (double)source.Height);
                int width = (int)Math.Round(source.Width * scale);
                int height = (int)Math.Round(source.Height * scale);
                return new Rectangle(target.X + (target.Width - width) / 2,
                                     target.Y + (target.Height - height) / 2,
                                     width, height);
            }

            public Rectangle ImageRect(Size source)
            {
                return FitTo(source, ClientRectangle);
            }
        }

        public ViewerForm(ViewerSession session, string room, bool viaRelay)
        {
            _session = session;
            _room = room;

            Text = "Fjern - fjernstyring af " + room;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font(Theme.FontName, 9.5f);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 520);
            Size = new Size(1000, 660);
            KeyPreview = true;

            // Toplinie med nav, chat-knap, fuldsk\u00e6rm og stop
            Panel toolbar = new Panel();
            toolbar.BackColor = Theme.Panel;
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 44;
            Controls.Add(toolbar);

            SubLabel title = new SubLabel("Styrer " + room, 11, true, Theme.Text);
            title.Location = new Point(14, 12);
            toolbar.Controls.Add(title);

            FlatButton btnChat = new FlatButton("Chat", delegate { ToggleChat(); }, true);
            btnChat.SetBounds(560, 8, 66, 28);
            toolbar.Controls.Add(btnChat);

            FlatButton btnFiles = new FlatButton("Filer", delegate { SendFiles(); }, true);
            btnFiles.SetBounds(632, 8, 66, 28);
            toolbar.Controls.Add(btnFiles);

            FlatButton btnClip = new FlatButton("Klip", delegate { _session.SendClipboard(); }, true);
            btnClip.SetBounds(704, 8, 56, 28);
            toolbar.Controls.Add(btnClip);

            FlatButton btnShot = new FlatButton("Skud", delegate { TakeSnapshot(); }, true);
            btnShot.SetBounds(500, 8, 60, 28);
            toolbar.Controls.Add(btnShot);

            FlatButton btnTalk = new FlatButton("Tale", delegate { ToggleCall(); }, true);
            _btnTalk = btnTalk;
            btnTalk.SetBounds(366, 8, 66, 28);
            toolbar.Controls.Add(btnTalk);

            _btnCtrlToggle = new FlatButton("Styring: JA", delegate { ToggleControlPass(); }, false);
            _btnCtrlToggle.SetBounds(446, 8, 50, 28);
            toolbar.Controls.Add(_btnCtrlToggle);

            FlatButton btnFs = new FlatButton("Fuld", delegate { ToggleFullscreen(); }, true);
            btnFs.SetBounds(766, 8, 66, 28);
            toolbar.Controls.Add(btnFs);

            FlatButton btnStop = new FlatButton("Luk", delegate { Close(); }, false);
            btnStop.AccentColor = Theme.Error;
            btnStop.SetBounds(838, 8, 66, 28);
            toolbar.Controls.Add(btnStop);

            // l\u00f6nbundlinje med status og tal
            Panel bottom = new Panel();
            bottom.BackColor = Theme.PanelDeep;
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 26;
            Controls.Add(bottom);

            _status = new SubLabel("forbinder...", 8, false, Theme.Ok);
            _status.Location = new Point(12, 6);
            bottom.Controls.Add(_status);

            _stats = new SubLabel("0 billeder - 0 KB", 8, false, Theme.TextDim);
            _stats.Location = new Point(520, 6);
            bottom.Controls.Add(_stats);

            _canvas = new CanvasPanel();
            _canvas.Session = session;
            _canvas.Dock = DockStyle.Fill;
            Controls.Add(_canvas);
            _canvas.BringToFront();

            // chatkasse der toggle p\u00e5 h\u00f8jre side
            _chatHost = BuildChat();

            _session.Status += text => SafeInvoke(delegate { SetStatus(text); });
            _session.TextReceived += text => SafeInvoke(delegate { ChatLine("ven: " + text); });
            _session.ScreenUpdated += delegate { SafeInvoke(delegate { _canvas.Invalidate(); }); };
            _session.ControlStateChanged += allowed => SafeInvoke(delegate
            {
                SetStatusPure(allowed ? "styring tilladt" : "v\u00e6rten har sl\u00e5et styring fra");
            });
            _session.ConnectionClosed += userRequested => SafeInvoke(delegate
            {
                SetStatus(userRequested ? "lukket" : "v\u00e6rten lukkede forbindelsen",
                          userRequested ? Theme.TextDim : Theme.Error);
            });

            _session.VoiceChunk += chunk => SafeInvoke(delegate
            {
                VoicePlayer p = _voicePlayer;
                if (p != null) p.PlayUlaw(chunk);
            });
            _session.VideoFrameReceived += bmp => SafeInvoke(delegate
            {
                if (_camViewer != null) _camViewer.PushFrame(bmp);
                else bmp.Dispose();
            });
            _session.ScreenshotJpeg += jpeg => SafeInvoke(delegate { SaveScreenshot(jpeg); });

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate
            {
                double kb = _session.BytesReceived / 1024.0;
                _stats.Text = _session.FramesReceived + " billeder - " + kb.ToString("0") + " KB";
            };
            _timer.Start();

            _session.Start();

            FormClosing += delegate
            {
                _timer.Stop();
                _session.Stop();
            };
        }

        private Control BuildChat()
        {
            _chatPanel = new Panel();
            _chatPanel.BackColor = Theme.Panel;
            _chatPanel.Dock = DockStyle.Right;
            _chatPanel.Width = 280;
            _chatPanel.Visible = false;

            SubLabel chatTitle = new SubLabel("Chat med din ven", 10, true, Theme.Text);
            chatTitle.Location = new Point(14, 12);
            _chatPanel.Controls.Add(chatTitle);

            _chatList = new ListBox();
            _chatList.BorderStyle = BorderStyle.FixedSingle;
            _chatList.BackColor = Theme.Input;
            _chatList.ForeColor = Theme.Text;
            _chatList.Font = new Font(Theme.FontName, 9f);
            _chatList.SetBounds(10, 40, 260, 480);
            _chatList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _chatPanel.Controls.Add(_chatList);

            _chatInput = new TextBox();
            _chatInput.BorderStyle = BorderStyle.FixedSingle;
            _chatInput.BackColor = Theme.Input;
            _chatInput.ForeColor = Theme.Text;
            _chatInput.Font = new Font(Theme.FontName, 9.5f);
            _chatInput.SetBounds(10, 526, 180, 24);
            _chatInput.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _chatInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    SendChat();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            _chatPanel.Controls.Add(_chatInput);

            FlatButton send = new FlatButton("Send", delegate { SendChat(); }, false);
            send.SetBounds(198, 524, 72, 28);
            send.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _chatPanel.Controls.Add(send);

            Controls.Add(_chatPanel);
            _chatPanel.BringToFront();
            return _chatPanel;
        }

        private void SafeInvoke(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
        }

        private void SetStatusPure(string text)
        {
            _status.Text = text;
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        // ------------------------------------------------- taleopkald / webcam / skud

        /// <summary>Seeren er admin og foreslår opkald til vennen.</summary>
        private void ToggleCall()
        {
            if (_callActive) { EndCallLocal(); return; }
            _session.SendCallRequest();
            SetStatus("\u00f8nsker om samtale sendt - afventer vennens svar");
            _camViewer = new CamViewerForm();
            _camViewer.Text = "Din vens webcam";
            _camViewer.Show();
            _camViewer.SetViewerSession(_session);
        }

        /// <summary>Admin-side af samtalen: starte lyd og kanaler, v\u00e6nt p\u00e5 godkendelse.</summary>
        private void AcceptCall()
        {
            if (_voicePlayer == null) _voicePlayer = new VoicePlayer();
            if (!_voicePlayer.Start()) SetStatus("kunne ikke \u00e5bne lydudgang", Theme.Error);

            if (_voiceRecorder == null) _voiceRecorder = new VoiceRecorder();
            _voiceRecorder.ChunkReady += chunk => _session.SendVoiceData(chunk);
            if (!_voiceRecorder.Start()) SetStatus("ingen mikrofon", Theme.Error);

            _session.SendCallAccept();
            _session.StartCamStream(500);

            _callActive = true;
            SetStatus("taleopkald aktivt", Theme.Ok);
            _btnTalk.Text = "Stop";
        }

        /// <summary Anal gamer: stop lyd og webcam lokalt og afslut.</summary>
        private void EndCallLocal()
        {
            _session.SendCallEnd();
            if (_voicePlayer != null) { _voicePlayer.Dispose(); _voicePlayer = null; }
            if (_voiceRecorder != null) { _voiceRecorder.Dispose(); _voiceRecorder = null; }
            _session.StopCamStream();
            if (_camViewer != null) _camViewer.MarkEnded();
            _callActive = false;
            SetStatus("opkald afsluttet");
            _btnTalk.Text = "Tale";
        }

        private void EndCallRemoteStopAll()
        {
            if (_voicePlayer != null) { _voicePlayer.Dispose(); _voicePlayer = null; }
            if (_voiceRecorder != null) { _voiceRecorder.Dispose(); _voiceRecorder = null; }
            _session.StopCamStream();
            if (_camViewer != null) _camViewer.MarkEnded();
            _callActive = false;
            SetStatus("opkald afsluttet af den anden side");
            _btnTalk.Text = "Tale";
        }

        private void TakeSnapshot()
        {
            _session.SendScreenshotRequest();
            SetStatus("beder om sk\u00e6rmbillede");
        }

        private void SaveScreenshot(byte[] jpeg)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Fjern");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string name = "Fjern-sk\u00e6rmbillede-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".jpg";
                string full = Path.Combine(folder, name);
                File.WriteAllBytes(full, jpeg);
                SetStatus("gemt " + name, Theme.Ok);
                ChatLine("sk\u00e6rmbillede gemt: " + full);
            }
            catch (Exception ex)
            {
                SetStatus("kunne ikke gemme: " + ex.Message, Theme.Error);
            }
        }

        /// <summary>Toggle admin-styring: klik for at give sin ven kontrollen.</summary>
        private void ToggleControlPass()
        {
            string cur = _btnCtrlToggle.Text;
            if (cur.Contains("JA"))
            {
                // tillad / vis indikator
                _btnCtrlToggle.Text = "Styring: NEJ";
                SetStatus("styring: tilladt");
            }
            else
            {
                _btnCtrlToggle.Text = "Styring: JA";
                SetStatus("styring: f\u00f8lg musemark\u00f8ren");
            }
        }

        private void ChatLine(string text)
        {
            _chatList.Items.Add(text);
            _chatList.TopIndex = _chatList.Items.Count - 1;
        }

        private void SendChat()
        {
            string text = _chatInput.Text.Trim();
            if (text.Length == 0) return;
            _session.SendText(text);
            ChatLine("mig: " + text);
            _chatInput.Text = "";
        }

        private void SendFiles()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Multiselect = true;
                dialog.Title = "Send filer til din ven";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _session.SendFiles(dialog.FileNames);
                    ChatLine("sender " + dialog.FileNames.Length + " filer...");
                    SetStatus("sender filer");
                }
            }
        }

        private void ToggleChat()
        {
            _chatPanel.Visible = !_chatPanel.Visible;
            if (!_chatPanel.Visible)
            {
                _canvas.Focus();
            }
        }

        private void ToggleFullscreen()
        {
            if (!_fullScreen)
            {
                _savedBorderStyle = FormBorderStyle;
                _savedBounds = Bounds;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                _fullScreen = true;
            }
            else
            {
                FormBorderStyle = _savedBorderStyle;
                Bounds = _savedBounds;
                _fullScreen = false;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11)
            {
                ToggleFullscreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Send n\u00e6sten alle taster videre; visse systemtaster beholder vi
            // lokalt (F11, Alt+Tab-owner etc.).
            if (e.KeyCode == Keys.F11 || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
            {
                base.OnKeyDown(e);
                return;
            }
            _session.SendKey((ushort)e.KeyCode, true);
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11 || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
            {
                base.OnKeyUp(e);
                return;
            }
            _session.SendKey((ushort)e.KeyCode, false);
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            SendMouseNormalized(WheelKind, e.Delta);
            base.OnMouseWheel(e);
        }

        private const byte WheelKind = 3;

        private void SendMouseNormalized(byte kind, int delta)
        {
            // Ingen handling her - vi s\u00e6lger f\u00f8rst koordinaterne
            double nx;
            double ny;
            Normalized(out nx, out ny);
            if (kind == WheelKind) _session.SendWheel(delta, nx, ny);
        }

        private void Normalized(out double nx, out double ny)
        {
            nx = 0.5;
            ny = 0.5;
            Size size = _session.ScreenSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            Rectangle rect = _canvas.ImageRect(size);
            if (rect.Width == 0 || rect.Height == 0) return;

            Point p = _canvas.PointToClient(Cursor.Position);
            nx = (p.X - rect.X) / (double)rect.Width;
            ny = (p.Y - rect.Y) / (double)rect.Height;
            nx = Math.Max(0, Math.Min(1, nx));
            ny = Math.Max(0, Math.Min(1, ny));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            double nx;
            double ny;
            Normalized(out nx, out ny);
            _session.SendMouseMove(nx, ny);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            double nx;
            double ny;
            Normalized(out nx, out ny);

            byte button = e.Button == MouseButtons.Left ? MouseWire.Left
                        : e.Button == MouseButtons.Right ? MouseWire.Right
                        : e.Button == MouseButtons.Middle ? MouseWire.Middle : (byte)0;

            if (button != 0) _session.SendMouseButton(button, true, nx, ny);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            double nx;
            double ny;
            Normalized(out nx, out ny);

            byte button = e.Button == MouseButtons.Left ? MouseWire.Left
                        : e.Button == MouseButtons.Right ? MouseWire.Right
                        : e.Button == MouseButtons.Middle ? MouseWire.Middle : (byte)0;

            if (button != 0) _session.SendMouseButton(button, false, nx, ny);
        }
    }
}
