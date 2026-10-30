using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Fjern
{
    // Vinduet en v\u00e6rt ser, mens der deles: status, live-statuslinje,
    // chat og mulighed for at stoppe sessionen.
    internal sealed class ShareForm : Form
    {
        private readonly HostSession _session;
        private readonly string _room;
        private readonly SubLabel _status;
        private ListBox _chatList;
        private TextBox _chatInput;
        private readonly CheckBox _allowControlBox;
        private readonly Timer _timer;
        private readonly SubLabel _stats;
        private readonly string _peerInfo;

        // opkald
        private VoicePlayer _voicePlayer;
        private VoiceRecorder _voiceRecorder;
        private Bitmap _nabWebcam;
        private readonly object _camLock = new object();
        private CamViewerForm _callForm;

        public ShareForm(HostSession session, string room, bool viaRelay)
        {
            _session = session;
            _room = room;
            _peerInfo = RelayLabel(viaRelay) + " - rum " + room;

            // WinForm hint i m\u00f8rkt tema: vi tegner det hele selv.
            BackColor = Theme.Bg;
            Font = new Font(Theme.FontName, 9.5f);
            Text = "Fjern - deler sk\u00e6rm (" + room + ")";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(520, 560);
            MinimumSize = new Size(520, 560);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            SubLabel title = new SubLabel("Deler din sk\u00e6rm", 14, true, Theme.Text);
            title.Location = new Point(20, 14);
            Controls.Add(title);

            SubLabel sub = new SubLabel(_peerInfo, 8, false, Theme.TextDim);
            sub.Location = new Point(20, 40);
            Controls.Add(sub);

            _status = new SubLabel("venter p\u00e5 din ven...", 9, false, Theme.Ok);
            _status.Location = new Point(20, 64);
            Controls.Add(_status);

            _allowControlBox = new CheckBox();
            _allowControlBox.Text = "Tillad at din ven styrer mus og tastatur";
            _allowControlBox.ForeColor = Theme.Text;
            _allowControlBox.BackColor = Theme.Bg;
            _allowControlBox.Checked = session.AllowControl;
            _allowControlBox.AutoSize = true;
            _allowControlBox.Location = new Point(20, 92);
            _allowControlBox.CheckedChanged += delegate
            {
                session.AllowControl = _allowControlBox.Checked;
            };
            Controls.Add(_allowControlBox);

            SubLabel chatTitle = new SubLabel("Chat", 10, true, Theme.Text);
            chatTitle.Location = new Point(20, 122);
            Controls.Add(chatTitle);

            _chatList = new ListBox();
            _chatList.BorderStyle = BorderStyle.FixedSingle;
            _chatList.BackColor = Theme.Input;
            _chatList.ForeColor = Theme.Text;
            _chatList.Font = new Font(Theme.FontName, 9f);
            _chatList.SetBounds(20, 148, 460, 260);
            Controls.Add(_chatList);

            _chatInput = new TextBox();
            _chatInput.BorderStyle = BorderStyle.FixedSingle;
            _chatInput.BackColor = Theme.Input;
            _chatInput.ForeColor = Theme.Text;
            _chatInput.Font = new Font(Theme.FontName, 9.5f);
            _chatInput.SetBounds(20, 418, 350, 24);
            _chatInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    SendChat();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(_chatInput);

            FlatButton send = new FlatButton("Send", delegate { SendChat(); }, false);
            send.SetBounds(378, 416, 102, 28);
            Controls.Add(send);

            _stats = new SubLabel("0 mus  0 taster  0 fps optaget", 7.5f, false, Theme.TextDim);
            _stats.Location = new Point(20, 452);
            Controls.Add(_stats);

            FlatButton stop = new FlatButton("Stop deling", delegate { StopSession(); }, false);
            stop.AccentColor = Theme.Error;
            stop.SetBounds(20, 472, 460, 34);
            Controls.Add(stop);

            _session.Status += text => SafeInvoke(delegate { SetStatus(text); });
            _session.TextReceived += text => SafeInvoke(delegate { ChatLine("ven: " + text); });
            _session.FileReceived += text => SafeInvoke(delegate { ChatLine("fil: " + text); });

            // admin ringer - klienten (v\u00e6rten) kan acceptere eller afvise
            _session.CallRequested += delegate
            {
                SafeInvoke(delegate
                {
                    DialogResult dr = MessageBox.Show(this,
                        "Din ven (admin) vil tale og sende webcam.\r\nGodkend?",
                        "Fjern", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr == DialogResult.Yes) AcceptCall();
                    else _session.SendCallReject();
                });
            };
            _session.VideoFrameReceived += bmp => SafeInvoke(delegate
            {
                Bitmap old;
                lock (_camLock)
                {
                    old = _nabWebcam;
                    _nabWebcam = bmp;
                }
                if (old != null) old.Dispose();
            });

            _callForm = new CamViewerForm();
            _callForm.SetSession(session);
            _callForm.Show();
            _callForm.Location = new Point(Location.X - 360, Location.Y);
            _callForm.Size = new Size(360, 300);

            _session.ConnectionClosed += userRequested =>
            {
                SafeInvoke(delegate
                {
                    SetStatus(userRequested ? "deling stoppet" : "din ven afbr\u00f8d forbindelsen",
                        userRequested ? Theme.TextDim : Theme.Error);
                    Close();
                });
            };

            _timer = new Timer();
            _timer.Interval = 500;
            _timer.Tick += delegate
            {
                _stats.Text = _session.MouseMessages + " mus  " + _session.KeyMessages +
                              " taster  " + (_session.Grabber != null ? _session.Grabber.ActiveFps.ToString() : "0") + " fps";
            };
            _timer.Start();

            FormClosing += delegate
            {
                _timer.Stop();
                if (_session.Running) _session.Stop();
            };
        }

        private static string RelayLabel(bool viaRelay)
        {
            return viaRelay ? "via relay" : "direkte LAN";
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

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void ChatLine(string line)
        {
            _chatList.Items.Add(line);
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

        private void StopSession()
        {
            _session.Stop();
            Close();
        }

        /// <summary>Siger ja til samtalen: starter mikrofon + webcam + lydud.</summary>
        private void AcceptCall()
        {
            if (_voicePlayer == null) _voicePlayer = new VoicePlayer();
            if (!_voicePlayer.Start()) SetStatus("kunne ikke \u00e5bne lyd", Theme.Error);

            if (_voiceRecorder == null) _voiceRecorder = new VoiceRecorder();
            _voiceRecorder.ChunkReady += chunk => _session.SendVoiceData(chunk);
            if (!_voiceRecorder.Start()) SetStatus("ingen mikrofon", Theme.Error);

            _session.SendCallAccept();
            if (_callForm != null) _callForm.MarkCallAccepted();
        }
    }

    // Lille vindue der viser den anden sides webcam-frames.
    internal sealed class CamViewerForm : Form
    {
        private Bitmap _bmp;
        private readonly object _lock = new object();
        private readonly Timer _timer;
        private VoiceRecorder _myMic;
        private VoicePlayer _player;
        private HostSession _hostSession;
        private ViewerSession _viewerSession;

        public CamViewerForm()
        {
            BackColor = Color.FromArgb(12, 12, 16);
            StartPosition = FormStartPosition.Manual;
            Size = new Size(360, 300);
            KeyPreview = true;

            _timer = new Timer();
            _timer.Interval = 100;
            _timer.Tick += delegate { Invalidate(); };
            _timer.Start();

            Paint += delegate(object s, PaintEventArgs e)
            {
                Bitmap bmp;
                lock (_lock) { bmp = _bmp; }
                if (bmp == null)
                {
                    TextRenderer.DrawText(e.Graphics, "venter p\u00e5 video...", Font,
                        ClientRectangle, Color.FromArgb(160, 160, 170),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                e.Graphics.DrawImage(bmp, ClientRectangle);
            };

            FormClosing += delegate
            {
                _timer.Stop();
                if (_myMic != null) { _myMic.Dispose(); _myMic = null; }
                if (_player != null) { _player.Dispose(); _player = null; }
                lock (_lock)
                {
                    if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
                }
            };
        }

        /// <summary>Aktiveres p\u00e5 klientsiden: giver webcam-voice og lader \u00e5b \u00e6n lyst \u00e6ndre session.</summary>
        public void SetSession(HostSession host)        {
            _hostSession = host;
            _player = new VoicePlayer();
            if (_player.Start())
            {
                host.VoiceChunk += chunk => PlayNext(chunk);
            }
            _myMic = new VoiceRecorder();
            _myMic.ChunkReady += chunk => host.SendVoiceData(chunk);
            if (!_myMic.Start()) Text = "Opkald (mikrofon utilg\u00e4ngelig)";
            Text = "Opkald klar - venter p\u00e5 din ven";
        }

        /// <summary>Kald dette n\u00e5r vennen har godkendt — start det lokale forward og webcam.</summary>
        public void MarkCallAccepted()
        {
            if (_hostSession != null) _hostSession.StartCamStream(500);
            Text = "Opkald aktivt";
        }

        /// <summary>Admin-side variant: lyd ind via ViewerSession (v\u00e6rten til den anden).</summary>
        public void SetViewerSession(ViewerSession session)
        {
            _player = new VoicePlayer();
            if (_player.Start())
            {
                session.VoiceChunk += chunk => PlayNext(chunk);
            }
            _myMic = new VoiceRecorder();
            _myMic.ChunkReady += chunk => session.SendVoiceData(chunk);
            if (!_myMic.Start()) Text = "Opkald (mikrofon utilg\u00e6ngelig)";
            _viewerSession = session;
            Text = "Opkald klar - venter p\u00e5 din ven";
        }

        private void PlayNext(byte[] chunk)
        {
            try
            {
                VoicePlayer p = _player;
                if (p == null) return;
                if (InvokeRequired) BeginInvoke(new Action(delegate { p.PlayUlaw(chunk); }));
                else p.PlayUlaw(chunk);
            }
            catch (Exception) { }
        }

        public void MarkEnded()
        {
            if (_myMic != null) { _myMic.Dispose(); _myMic = null; }
            if (_player != null) { _player.Dispose(); _player = null; }
            Text = "Opkald afsluttet";
            Close();
        }

        // S\u00e5 den ikke bliver lukket automatisk: n\u00e5r den anden side afslyser.
        public void SetEnded()
        {
            MarkEnded();
        }

        /// <summary>Kamera-billeder sendt fra min webkamera-stream.</summary>
        public void PushFrame(Bitmap bmp)
        {
            Bitmap old;
            lock (_lock)
            {
                old = _bmp;
                _bmp = bmp;
            }
            if (old != null) old.Dispose();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
