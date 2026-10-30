using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Fjern
{
    internal enum LinkMode
    {
        Relay,
        Direct
    }

    // Individual derived classes for the host and viewer sessions.
    // HostSession has a capture loop directly, while ViewerSession opens on its side
    // and decodes incoming frames. Both sides talk via SecureChannel and share the
    // same framing, so text and clipboard messages flow the same way.

    internal abstract class SessionBase : IDisposable
    {
        protected SecureChannel _channel;

        public abstract bool Running { get; }

        public virtual void Dispose()
        {
            Stop();
        }

        public abstract void Stop();

        protected static byte[] PackString(string text)
        {
            if (text == null) text = "";
            return Encoding.UTF8.GetBytes(text);
        }

        protected static string UnpackString(byte[] payload)
        {
            return payload == null ? "" : Encoding.UTF8.GetString(payload);
        }
    }

    // ------------------------------------------------------------ værtssiden

    internal sealed class HostSession : SessionBase
    {
        private readonly LinkMode _mode;
        private readonly string _relayHost;
        private readonly int _relayPort;
        private readonly string _room;
        private readonly string _password;
        private readonly int _directPort;

        private Thread _workThread;
        private Thread _rxThread;
        private Thread _clipboardThread;
        private TcpListener _listener;
        private Grabber _grabber;
        private volatile bool _running;
        private volatile bool _allowControl = true;
        private volatile bool _controlSent = true;
        private volatile bool _stopRequested;

        public event Action<string> Status;
        public event Action<string> TextReceived;
        public event Action<bool> ControlStateChanged;
        public event Action<string> FileReceived;
        public event Action<bool> ConnectionClosed;
        public event Action<byte[]> VoiceChunk;              // u-law fra admin
        public event Action<Bitmap> VideoFrameReceived;      // admin webcam (klienten ejer bitmap)
        public event Action<string> CallRequested;           // "admin vil tale"

        public int TargetFps = 20;
        public int Quality = 65;
        public int MaxWidth = 1600;
        public int MonitorIndex;
        public string IncomingFolder;

        // diagnostik
        public int MouseMessages;
        public int KeyMessages;

        public HostSession(LinkMode mode, string relayHost, int relayPort,
                           string room, string password, int directPort)
        {
            _mode = mode;
            _relayHost = relayHost;
            _relayPort = relayPort;
            _room = room;
            _password = password;
            _directPort = directPort;
        }

        public override bool Running { get { return _running; } }

        public Grabber Grabber { get { return _grabber; } }

        public bool AllowControl
        {
            get { return _allowControl; }
            set
            {
                _allowControl = value;
                _controlSent = false;
                InvokeEvent(ControlStateChanged, value);
            }
        }

        private void InvokeEvent(Action<string> handler, string text)
        {
            Action<string> copy = handler;
            if (copy != null) copy(text);
        }

        private void InvokeEvent(Action<bool> handler, bool value)
        {
            Action<bool> copy = handler;
            if (copy != null) copy(value);
        }

        private void Report(string text)
        {
            InvokeEvent(Status, text);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _stopRequested = false;
            _workThread = new Thread(Work);
            _workThread.IsBackground = true;
            _workThread.Name = "fjern-host";
            _workThread.Start();
        }

        private void Work()
        {
            try
            {
                Stream stream;

                if (_mode == LinkMode.Relay)
                {
                    Report("forbinder til relay " + _relayHost + ":" + _relayPort);
                    stream = Transport.ConnectRelay(_relayHost, _relayPort, Wire.RoleHost, _room,
                                                    Report, 15000);
                    Report("parret - udveksler noegler");
                }
                else
                {
                    _listener = Transport.ListenDirect(_directPort);
                    Report("lytter paa port " + _directPort + " - venter paa vennen");
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    stream = client.GetStream();
                    Report("forbundet - udveksler noegler");
                }

                _channel = SecureChannel.Establish(stream, Wire.RoleHost, _password, Report);
                Report("krypteret - forbundet");

                _clipboardThread = new Thread(ClipboardLoop);
                _clipboardThread.IsBackground = true;
                _clipboardThread.Name = "fjern-host-clip";
                _clipboardThread.Start();

                _rxThread = new Thread(ReceiveLoop);
                _rxThread.IsBackground = true;
                _rxThread.Name = "fjern-host-rx";
                _rxThread.Start();

                Report("deler skaermen - " + TargetFps + " fps");

                _grabber = new Grabber(MaxWidth, 256, TargetFps * 15, MonitorIndex);
                SendLoop();
            }
            catch (Exception ex)
            {
                if (!_stopRequested) Report("fejl: " + ex.Message);
            }
            finally
            {
                Stop();
                bool closed = _stopRequested;
                if (ConnectionClosed != null) ConnectionClosed(!closed);
            }
        }

        private void SendLoop()
        {
            while (_running && _channel != null && !_channel.IsClosed)
            {
                long start = Environment.TickCount;

                if (!_controlSent)
                {
                    byte state = (byte)(_allowControl ? 1 : 0);
                    _controlSent = true;
                    try { _channel.Send(Wire.MsgControl, new byte[] { state }); }
                    catch (Exception) { break; }
                }

                byte[] payload = _grabber.Grab(Quality);
                if (payload != null)
                {
                    try { _channel.Send(Wire.MsgVideo, payload); }
                    catch (Exception) { break; }
                }

                int budget = 1000 / Math.Max(1, TargetFps);
                int spent = (int)(Environment.TickCount - start);
                int wait = budget - spent;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                byte type;
                byte[] payload;

                while (_running && _channel != null && _channel.Receive(out type, out payload))
                {
                    if (type == Wire.MsgMouse)
                    {
                        if (!_allowControl || payload == null || payload.Length < 8) continue;
                        HandleMouse(payload);
                    }
                    else if (type == Wire.MsgKey)
                    {
                        if (!_allowControl || payload == null || payload.Length < 4) continue;
                        ushort vk = BitConverter.ToUInt16(payload, 0);
                        bool down = payload[2] != 0;
                        if (vk != 0)
                        {
                            KeyMessages++;
                            Injection.Key(vk, down);
                        }
                    }
                    else if (type == Wire.MsgText)
                    {
                        if (TextReceived != null) TextReceived(UnpackString(payload));
                    }
                    else if (type == Wire.MsgClip)
                    {
                        Win32.ClipboardSetText(UnpackString(payload));
                    }
                    else if (type == Wire.MsgFileStart)
                    {
                        if (payload != null && payload.Length >= 10) HandleFileStart(payload);
                    }
                    else if (type == Wire.MsgFileData)
                    {
                        if (payload != null && payload.Length >= 8) HandleFileData(payload);
                    }
                    else if (type == Wire.MsgFileEnd)
                    {
                        HandleFileEnd(payload);
                    }
                    else if (type == Wire.MsgVoice && payload != null && payload.Length > 0)
                    {
                        Action<byte[]> v = VoiceChunk;
                        if (v != null) v(payload);
                    }
                    else if (type == Wire.MsgVideoFrame && payload != null && payload.Length > 0)
                    {
                        HandleNabWebcam(payload);
                    }
                    else if (type == Wire.MsgCallInvite)
                    {
                        Action<string> c = CallRequested;
                        if (c != null) c("admin");
                    }
                    else if (type == Wire.MsgCallEnd)
                    {
                        StopCamStream();
                    }
                    else if (type == Wire.MsgShotReq)
                    {
                        SendScreenshotNow();
                    }
                    else if (type == Wire.MsgBye) break;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                _running = false;
                Stop();
            }
        }

        // -------------------------------------------------------- filer modtaget

        private Stream _incomingFile;
        private long _incomingSize;
        private long _incomingSoFar;
        private long _incomingIndex;

        private void HandleFileStart(byte[] payload)
        {
            try
            {
                if (_incomingFile != null)
                {
                    _incomingFile.Dispose();
                    _incomingFile = null;
                }

                short nameLength = BitConverter.ToInt16(payload, 0);
                long total = BitConverter.ToInt64(payload, 2);
                if (nameLength < 0 || nameLength > payload.Length - 10) return;

                string fileName = Encoding.UTF8.GetString(payload, 10, nameLength);
                if (total < 0) total = 0;

                string folder = IncomingFolder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    folder = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "Fjern");
                }
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                string full = BuildSafePath(folder, fileName);
                _incomingFile = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
                _incomingSize = total;
                _incomingSoFar = 0;
                _incomingIndex = 0;
            }
            catch (Exception ex)
            {
                Report("kunne ikke skrive modtaget fil: " + ex.Message);
            }
        }

        private static string BuildSafePath(string folder, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) fileName = "fjern-fil";
            fileName = fileName.Trim();
            if (fileName.Length > 128) fileName = fileName.Substring(fileName.Length - 128);
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (fileName.IndexOf(c) >= 0) fileName = fileName.Replace(c, '_');
            }
            if (fileName.IndexOf("..", StringComparison.Ordinal) >= 0) fileName = fileName.Replace("..", "_");

            string full = Path.Combine(folder, fileName);
            for (int i = 1; File.Exists(full) && i < 99; i++)
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                full = Path.Combine(folder, stem + " (" + i + ")" + ext);
            }
            return full;
        }

        private void HandleFileData(byte[] payload)
        {
            long index = BitConverter.ToInt64(payload, 0);
            int len = payload.Length - 8;
            if (_incomingFile == null) return;

            try
            {
                if (index == _incomingIndex)
                {
                    _incomingFile.Write(payload, 8, len);
                    _incomingSoFar += len;
                    _incomingIndex++;
                }
                else if (index > _incomingIndex)
                {
                    // stepped hul - efterspurgt tilbage som!
                    for (long i = _incomingIndex; i < index; i++)
                    {
                        _incomingFile.Write(new byte[1], 0, 1);
                    }
                    _incomingIndex = index;
                    _incomingFile.Write(payload, 8, len);
                    _incomingSoFar += len;
                }
            }
            catch (Exception ex)
            {
                Report("kunne ikke skrive data: " + ex.Message);
            }
        }

        private void HandleFileEnd(byte[] payload)
        {
            if (_incomingFile == null) return;
            try
            {
                _incomingFile.Close();
                _incomingFile.Dispose();
                _incomingFile = null;

                string text = "modtog fil";
                if (FileReceived != null) FileReceived(text);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------- tekst beskeder

        public void SendText(string text)
        {
            if (_channel == null) return;
            try { _channel.Send(Wire.MsgText, PackString(text)); }
            catch (Exception) { }
        }

        public void SendClipboardToRemote()
        {
            string text = Win32.ClipboardGetText();
            if (string.IsNullOrEmpty(text)) return;
            if (_channel == null) return;
            try { _channel.Send(Wire.MsgClip, PackString(text)); }
            catch (Exception) { }
        }

        private void ClipboardLoop()
        {
            uint last = Win32.ClipboardSequence;
            DateTime lastCopy = DateTime.UtcNow;

            while (_running)
            {
                Thread.Sleep(250);
                if (_channel == null) break;

                uint now = Win32.ClipboardSequence;
                if (now != last && (DateTime.UtcNow - lastCopy).TotalMilliseconds > 300)
                {
                    last = now;
                    lastCopy = DateTime.UtcNow;
                    SendClipboardToRemote();
                }
                else
                {
                    last = now;
                }
            }
        }

        // ---------------------------------------------------------- input

        private void HandleMouse(byte[] payload)
        {
            byte kind = payload[0];
            double nx = BitConverter.ToUInt16(payload, 1) / 65535.0;
            double ny = BitConverter.ToUInt16(payload, 3) / 65535.0;

            Rectangle window = _grabber != null
                ? _grabber.MonitorBounds
                : Screen.PrimaryScreen.Bounds;

            if (kind == Wire.MouseMove)
            {
                MouseMessages++;
                Injection.MoveTo(window, nx, ny);
            }
            else if (kind == Wire.MouseButton)
            {
                byte button = payload[5];
                byte arg = payload[6];
                Injection.Button(window, button, arg != 0, nx, ny);
            }
            else if (kind == Wire.MouseWheel)
            {
                short delta = BitConverter.ToInt16(payload, 6);
                Injection.Wheel(window, delta, nx, ny);
            }
        }

        public void ForceKeyFrame()
        {
            Grabber grabber = _grabber;
            if (grabber != null) grabber.Invalidate();
        }

        /// <summary>Sender u-law-stykke til admin (mikrofon).</summary>
        public void SendVoiceData(byte[] ulaw)
        {
            SecureChannel channel = _channel;
            if (channel == null || ulaw == null || ulaw.Length == 0) return;
            try { channel.Send(Wire.MsgVoice, ulaw); } catch (Exception) { }
        }

        /// <summary>Sender webcam-frame (JPEG 320x240) til admin.</summary>
        public void SendVideoFrame(byte[] jpegBytes)
        {
            SecureChannel channel = _channel;
            if (channel == null || jpegBytes == null || jpegBytes.Length == 0) return;
            try { channel.Send(Wire.MsgVideoFrame, jpegBytes); } catch (Exception) { }
        }

        public void SendCallAccept()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallAccept, new byte[0]); } catch (Exception) { }
        }

        public void SendCallReject()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallReject, new byte[0]); } catch (Exception) { }
        }

        public void SendCallEnd()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallEnd, new byte[0]); } catch (Exception) { }
        }

        // ------------------------------------------------- opkald og webcam (klient)

        private WebCam _nabCam;
        private Thread _camThread;
        private volatile bool _camRunning;

        /// <summary>Henter et webcam-billede fra min side og sender det til admin. Kalderes periodisk, efter at jeg selv har godkendt igennem UI'en.</summary>
        public void StartCamStream(int frameMs)
        {
            if (_camRunning) return;
            _camRunning = true;
            _camThread = new Thread(delegate()
            {
                try
                {
                    if (_nabCam == null) _nabCam = new WebCam();
                    while (_camRunning && _channel != null && !_channel.IsClosed)
                    {
                        Bitmap bmp = null;
                        try { bmp = _nabCam.Grab(); }
                        catch (Exception) { bmp = null; }

                        if (bmp != null)
                        {
                            try
                            {
                                using (bmp)
                                using (Bitmap small = new Bitmap(bmp, 320, 240))
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    small.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                                    _channel.Send(Wire.MsgVideoFrame, ms.ToArray());
                                }
                            }
                            catch (Exception) { }
                        }
                        Thread.Sleep(frameMs);
                    }
                }
                catch (Exception) { }
            });
            _camThread.IsBackground = true;
            _camThread.Name = "fjern-cam-sender";
            _camThread.Start();
        }

        public void StopCamStream()
        {
            _camRunning = false;
            if (_nabCam != null) { try { _nabCam.Dispose(); } catch (Exception) { } _nabCam = null; }
        }

        private void HandleNabWebcam(byte[] payload)
        {
            Bitmap bmp = null;
            try
            {
                using (MemoryStream ms = new MemoryStream(payload, false))
                using (Image img = Image.FromStream(ms))
                {
                    bmp = new Bitmap(img);
                }
            }
            catch (Exception) { return; }

            Action<Bitmap> handler = VideoFrameReceived;
            if (handler != null) handler(bmp);   // eftersjælivs handleren derefererer den
            else if (bmp != null) bmp.Dispose();
        }

        /// <summary>Fanger skærmen fuldt op og sender som JPEG til admin (screenshot-anmodning).</summary>
        public void SendScreenshotNow()
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                using (Bitmap full = new Bitmap(bounds.Width, bounds.Height))
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        full.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        try { _channel.Send(Wire.MsgShotJpeg, ms.ToArray()); }
                        catch (Exception) { }
                    }
                }
                Report("skærmbillede sendt til admin");
            }
            catch (Exception)
            {
            }
        }

        // ---------------------------------------------------------- stop

        public override void Stop()
        {
            if (_stopRequested) return;

            _stopRequested = true;
            _running = false;
            Injection.ReleaseModifiers();
            StopCamStream();
            if (_channel != null) { _channel.Dispose(); _channel = null; }
            if (_listener != null) { try { _listener.Stop(); } catch (Exception) { } _listener = null; }
            if (_grabber != null) { _grabber.Dispose(); _grabber = null; }
        }
    }

    // ------------------------------------------------------------- seer-siden

    internal sealed class ViewerSession : SessionBase
    {
        private readonly LinkMode _mode;
        private readonly string _relayHost;
        private readonly int _relayPort;
        private readonly string _room;
        private readonly string _password;
        private readonly string _directHost;
        private readonly int _directPort;

        private Thread _workThread;
        private volatile bool _running;
        private volatile bool _allowControl;

        private Bitmap _screen;
        private readonly object _screenLock = new object();
        private int _framesReceived;
        private int _bytesReceived;

        public event Action<string> Status;
        public event Action ScreenUpdated;
        public event Action<string> TextReceived;
        public event Action<bool> ConnectionClosed;
        public event Action<bool> ControlStateChanged;
        public event Action<byte[]> VoiceChunk;              // u-law fra klient (visses)
        public event Action<Bitmap> VideoFrameReceived;      // klient webcam
        public event Action<byte[]> ScreenshotJpeg;          // skærmbillede fuldt
        public event Action<long> FileProgress;              // (bytes modtaget)
        public event Action<string> FileDone;

        public ViewerSession(LinkMode mode, string relayHost, int relayPort, string room,
                             string password, string directHost, int directPort)
        {
            _mode = mode;
            _relayHost = relayHost;
            _relayPort = relayPort;
            _room = room;
            _password = password;
            _directHost = directHost;
            _directPort = directPort;
        }

        public override bool Running { get { return _running; } }
        public bool ControlAllowed { get { return _allowControl; } }
        public int FramesReceived { get { return _framesReceived; } }
        public int BytesReceived { get { return _bytesReceived; } }

        public Bitmap Screen
        {
            get { lock (_screenLock) { return _screen; } }
        }

        public Size ScreenSize
        {
            get { lock (_screenLock) { return _screen == null ? Size.Empty : _screen.Size; } }
        }

        public bool DrawTo(Graphics g, Rectangle target)
        {
            lock (_screenLock)
            {
                if (_screen == null) return false;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(_screen, target);
                return true;
            }
        }

        private void Report(string text)
        {
            Action<string> handler = Status;
            if (handler != null) handler(text);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _workThread = new Thread(Work);
            _workThread.IsBackground = true;
            _workThread.Name = "fjern-viewer";
            _workThread.Start();
        }

        private void Work()
        {
            try
            {
                Stream stream;

                if (_mode == LinkMode.Relay)
                {
                    Report("forbinder til relay " + _relayHost + ":" + _relayPort);
                    stream = Transport.ConnectRelay(_relayHost, _relayPort, Wire.RoleViewer, _room,
                                                    Report, 15000);
                    Report("parret - udveksler noegler");
                }
                else
                {
                    Report("forbinder til " + _directHost + ":" + _directPort);
                    TcpClient client = new TcpClient();
                    client.NoDelay = true;
                    IAsyncResult ar = client.BeginConnect(_directHost, _directPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(10000))
                    {
                        client.Close();
                        throw new IOException("ingen forbindelse til " + _directHost);
                    }
                    client.EndConnect(ar);
                    stream = client.GetStream();
                }

                _channel = SecureChannel.Establish(stream, Wire.RoleViewer, _password, Report);
                _allowControl = true;
                Report("forbundet - venter paa billede");

                byte type;
                byte[] payload;
                while (_running && _channel.Receive(out type, out payload))
                {
                    if (type == Wire.MsgVideo) ApplyVideo(payload);
                    else if (type == Wire.MsgVoice && payload != null && payload.Length > 0)
                    {
                        Action<byte[]> v = VoiceChunk;
                        if (v != null) v(payload);
                    }
                    else if (type == Wire.MsgVideoFrame && payload != null && payload.Length > 0)
                    {
                        HandleNabWebcam(payload);
                    }
                    else if (type == Wire.MsgShotJpeg && payload != null && payload.Length > 0)
                    {
                        Action<byte[]> s = ScreenshotJpeg;
                        if (s != null) s(payload);
                    }
                    else if (type == Wire.MsgCallInvite)
                    {
                        // klienten inviterede (fremtidig funktion)
                    }
                    else if (type == Wire.MsgControl && payload != null && payload.Length >= 1)
                    {
                        _allowControl = payload[0] != 0;
                        Report(_allowControl ? "styring tilladt" : "vaerten har slaaet styring fra");
                        Action<bool> handler = ControlStateChanged;
                        if (handler != null) handler(_allowControl);
                    }
                    else if (type == Wire.MsgText)
                    {
                        Action<string> handler = TextReceived;
                        if (handler != null) handler(Encoding.UTF8.GetString(payload));
                    }
                    else if (type == Wire.MsgClip)
                    {
                        Win32.ClipboardSetText(Encoding.UTF8.GetString(payload));
                    }
                    else if (type == Wire.MsgBye) break;
                }
            }
            catch (Exception ex)
            {
                if (_running) Report("fejl: " + ex.Message);
            }
            finally
            {
                _running = false;
                if (_channel != null) { _channel.Dispose(); _channel = null; }
                Action<bool> closed = ConnectionClosed;
                if (closed != null) closed(_running);
            }
        }

        private void ApplyVideo(byte[] payload)
        {
            List<Image> tiles = new List<Image>(32);
            List<Rectangle> places = new List<Rectangle>(32);
            int width;
            int height;

            try
            {
                using (MemoryStream ms = new MemoryStream(payload, false))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    width = r.ReadUInt16();
                    height = r.ReadUInt16();
                    r.ReadUInt16();
                    r.ReadUInt32();
                    int count = r.ReadUInt16();

                    for (int i = 0; i < count; i++)
                    {
                        int x = r.ReadUInt16();
                        int y = r.ReadUInt16();
                        int w = r.ReadUInt16();
                        int h = r.ReadUInt16();
                        int len = (int)r.ReadUInt32();

                        if (len <= 0 || len > payload.Length) break;
                        byte[] jpeg = r.ReadBytes(len);

                        MemoryStream tileStream = new MemoryStream(jpeg, false);
                        tiles.Add(Image.FromStream(tileStream));
                        places.Add(new Rectangle(x, y, w, h));
                    }
                }

                bool resized = false;
                lock (_screenLock)
                {
                    if (_screen == null || _screen.Width != width || _screen.Height != height)
                    {
                        if (_screen != null) _screen.Dispose();
                        _screen = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                        resized = true;
                    }

                    using (Graphics g = Graphics.FromImage(_screen))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        for (int i = 0; i < tiles.Count; i++)
                        {
                            g.DrawImage(tiles[i], places[i]);
                        }
                    }
                }

                _framesReceived++;
                _bytesReceived += payload.Length;
                if (resized) Report("billede " + width + "x" + height);
            }
            finally
            {
                for (int i = 0; i < tiles.Count; i++) tiles[i].Dispose();
            }

            Action updated = ScreenUpdated;
            if (updated != null) updated();
        }

        // ------------------------------------------------------------ output

        public void SendMouseMove(double nx, double ny)
        {
            SendMouse(Wire.MouseMove, nx, ny, 0, 0);
        }

        public void SendMouseButton(byte button, bool down, double nx, double ny)
        {
            SendMouse(Wire.MouseButton, nx, ny, button, down ? (short)1 : (short)0);
        }

        public void SendWheel(int delta, double nx, double ny)
        {
            SendMouse(Wire.MouseWheel, nx, ny, 0, (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, delta)));
        }

        private void SendMouse(byte kind, double nx, double ny, byte button, short arg)
        {
            SecureChannel channel = _channel;
            if (channel == null || !_running) return;

            byte[] payload = new byte[8];
            payload[0] = kind;
            ushort ux = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(nx * 65535)));
            ushort uy = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(ny * 65535)));
            payload[1] = (byte)(ux & 0xFF);
            payload[2] = (byte)(ux >> 8);
            payload[3] = (byte)(uy & 0xFF);
            payload[4] = (byte)(uy >> 8);
            payload[5] = button;
            payload[6] = (byte)(arg & 0xFF);
            payload[7] = (byte)((arg >> 8) & 0xFF);

            try { channel.Send(Wire.MsgMouse, payload); }
            catch (Exception) { }
        }

        public void SendKey(ushort vk, bool down)
        {
            SecureChannel channel = _channel;
            if (channel == null || !_running) return;

            byte[] payload = new byte[4];
            payload[0] = (byte)(vk & 0xFF);
            payload[1] = (byte)(vk >> 8);
            payload[2] = (byte)(down ? 1 : 0);
            payload[3] = 0;

            try { channel.Send(Wire.MsgKey, payload); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------ filer

        // ------------------------------------------------------- Viewersiden modtager også filer
        // Standardmodtager: filer fra den anden side (clil: admin-klient).

        private Stream _incomingFile;
        private long _incomingSize;
        private long _incomingSoFar;
        private long _incomingIndex;
        private string _incomingName;

        public string IncomingFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Fjern");

        private void HandleFileStart(byte[] payload)
        {
            if (payload == null || payload.Length < 10) return;
            try
            {
                if (_incomingFile != null) { try { _incomingFile.Dispose(); } catch (Exception) { } _incomingFile = null; }

                short nameLength = BitConverter.ToInt16(payload, 0);
                long total = BitConverter.ToInt64(payload, 2);
                if (nameLength < 0 || nameLength > payload.Length - 10) return;

                string fileName = Encoding.UTF8.GetString(payload, 10, nameLength);
                if (total < 0) total = 0;

                string folder = string.IsNullOrWhiteSpace(IncomingFolder) ?
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Fjern") :
                    IncomingFolder;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string full = SafePath(folder, fileName);
                _incomingFile = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 8192);
                _incomingName = full;
                _incomingSize = total;
                _incomingSoFar = 0;
                _incomingIndex = 0;
            }
            catch (Exception) { Report("kunne ikke starte fil"); }
        }

        private static string SafePath(string folder, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) fileName = "fjern-fil";
            fileName = fileName.Trim();
            if (fileName.Length > 128) fileName = fileName.Substring(fileName.Length - 128);
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            if (fileName.IndexOf("..", StringComparison.Ordinal) >= 0)
                fileName = fileName.Replace("..", "_");

            string full = Path.Combine(folder, fileName);
            for (int i = 1; File.Exists(full) && i < 99; i++)
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                full = Path.Combine(folder, stem + " (" + i + ")" + ext);
            }
            return full;
        }

        private void HandleFileData(byte[] payload)
        {
            if (payload == null || payload.Length < 8 || _incomingFile == null) return;
            long index = BitConverter.ToInt64(payload, 0);
            int len = payload.Length - 8;

            try
            {
                if (index == _incomingIndex)
                {
                    _incomingFile.Write(payload, 8, len);
                    _incomingSoFar += len;
                    _incomingIndex++;
                }
                else if (index > _incomingIndex)
                {
                    for (long i = _incomingIndex; i < index; i++)
                        _incomingFile.Write(new byte[1], 0, 1);
                    _incomingIndex = index;
                    _incomingFile.Write(payload, 8, len);
                    _incomingSoFar += len;
                }
                Action<long> h = FileProgress;
                if (h != null) h(_incomingSoFar);
            }
            catch (Exception) { }
        }

        private void HandleFileEnd(byte[] payload)
        {
            if (_incomingFile == null) return;
            try
            {
                _incomingFile.Close();
                _incomingFile.Dispose();
                _incomingFile = null;
                Action<string> h = FileDone;
                if (h != null) h(_incomingName);
            }
            catch (Exception) { }
        }

        public void SendFiles(string[] fullPaths)
        {
            SecureChannel channel = _channel;
            if (channel == null || !_running) return;

            foreach (string path in fullPaths)
            {
                try
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(path));
                    byte[] header = new byte[2 + 8 + nameBytes.Length];
                    header[0] = (byte)(nameBytes.Length & 0xFF);
                    header[1] = (byte)((nameBytes.Length >> 8) & 0xFF);
                    long size = new FileInfo(path).Length;
                    byte[] sizeBytes = BitConverter.GetBytes(size);
                    Buffer.BlockCopy(sizeBytes, 0, header, 2, 8);
                    Buffer.BlockCopy(nameBytes, 0, header, 10, nameBytes.Length);
                    channel.Send(Wire.MsgFileStart, header);

                    long sequence = 0;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] chunk = new byte[65536];
                        int read;
                        while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            byte[] packet = new byte[8 + read];
                            BitConverter.GetBytes(sequence++).CopyTo(packet, 0);
                            Buffer.BlockCopy(chunk, 0, packet, 8, read);
                            channel.Send(Wire.MsgFileData, packet);
                        }
                    }

                    channel.Send(Wire.MsgFileEnd, BitConverter.GetBytes(size));
                }
                catch (Exception)
                {
                }
            }
        }

        public void SendText(string text)
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgText, Encoding.UTF8.GetBytes(text ?? "")); }
            catch (Exception) { }
        }

        public void SendClipboard()
        {
            string text = Win32.ClipboardGetText();
            if (string.IsNullOrEmpty(text)) return;
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgClip, Encoding.UTF8.GetBytes(text)); }
            catch (Exception) { }
        }

        // ------------------------------------------------- opkald, mus, kamera etc.

        private WebCam _nabCam;
        private Thread _camThread;
        private volatile bool _camRunning;

        /// <summary>Request: "jeg vil tale" — klienten skal acceptere.</summary>
        public void SendCallRequest()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallInvite, new byte[0]); } catch (Exception) { }
        }

        public void SendCallAccept()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallAccept, new byte[0]); } catch (Exception) { }
        }

        public void SendCallReject()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallReject, new byte[0]); } catch (Exception) { }
        }

        public void SendCallEnd()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgCallEnd, new byte[0]); } catch (Exception) { }
        }

        public void SendScreenshotRequest()
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try { channel.Send(Wire.MsgShotReq, new byte[0]); } catch (Exception) { }
        }

        /// <summary>Sender webcam-frames (ap til 320x240) - kalder periodisk, startes af UI.</summary>
        public void StartCamStream(int frameMs)
        {
            if (_camRunning) return;
            _camRunning = true;
            _camThread = new Thread(delegate()
            {
                try
                {
                    if (_nabCam == null) _nabCam = new WebCam();
                    while (_camRunning && _channel != null && !_channel.IsClosed)
                    {
                        Bitmap bmp = null;
                        try { bmp = _nabCam.Grab(); }
                        catch (Exception) { }

                        if (bmp != null)
                        {
                            try
                            {
                                using (bmp)
                                using (Bitmap small = new Bitmap(bmp, 320, 240))
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    small.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                                    _channel.Send(Wire.MsgVideoFrame, ms.ToArray());
                                }
                            }
                            catch (Exception) { }
                        }
                        Thread.Sleep(frameMs);
                    }
                }
                catch (Exception) { }
            });
            _camThread.IsBackground = true;
            _camThread.Name = "fjern-cam-sender-admin";
            _camThread.Start();
        }

        public void StopCamStream()
        {
            _camRunning = false;
            if (_nabCam != null) { try { _nabCam.Dispose(); } catch (Exception) { } _nabCam = null; }
        }

        private void HandleNabWebcam(byte[] payload)
        {
            Bitmap bmp = null;
            try
            {
                using (MemoryStream ms = new MemoryStream(payload, false))
                using (Image img = Image.FromStream(ms))
                {
                    bmp = new Bitmap(img);
                }
            }
            catch (Exception) { return; }

            Action<Bitmap> handler = VideoFrameReceived;
            if (handler != null) handler(bmp);
            else if (bmp != null) bmp.Dispose();
        }

        /// <summary>Sender u-law-stykke til den anden pc (mikrofon).</summary>
        public void SendVoiceData(byte[] ulaw)
        {
            SecureChannel channel = _channel;
            if (channel == null || ulaw == null || ulaw.Length == 0) return;
            try { channel.Send(Wire.MsgVoice, ulaw); } catch (Exception) { }
        }

        /// <summary>Sender webcam-frame (JPEG 320x240) til den anden pc.</summary>
        public void SendVideoFrame(byte[] jpegBytes)
        {
            SecureChannel channel = _channel;
            if (channel == null || jpegBytes == null || jpegBytes.Length == 0) return;
            try { channel.Send(Wire.MsgVideoFrame, jpegBytes); } catch (Exception) { }
        }

        /// <summary>Sender en screenshot-fil til remote (fra klient til admin).</summary>
        public void SendScreenshotFile(byte[] jpegBytes)
        {
            SecureChannel channel = _channel;
            if (channel == null) return;
            try
            {
                string name = "Fjern-skærmbillede-" + DateTime.Now.ToString("HH-mm-ss") + ".jpg";
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                byte[] header = new byte[2 + 8 + nameBytes.Length];
                header[0] = (byte)(nameBytes.Length & 0xFF);
                header[1] = (byte)((nameBytes.Length >> 8) & 0xFF);
                byte[] sizeBytes = BitConverter.GetBytes((long)jpegBytes.Length);
                Buffer.BlockCopy(sizeBytes, 0, header, 2, 8);
                Buffer.BlockCopy(nameBytes, 0, header, 10, nameBytes.Length);
                channel.Send(Wire.MsgFileStart, header);

                header = new byte[8 + jpegBytes.Length];
                BitConverter.GetBytes((long)0).CopyTo(header, 0);
                Buffer.BlockCopy(jpegBytes, 0, header, 8, jpegBytes.Length);
                channel.Send(Wire.MsgFileData, header);
                channel.Send(Wire.MsgFileEnd, sizeBytes);
            }
            catch (Exception) { }
        }

        public override void Stop()
        {
            _running = false;
            StopCamStream();
            SecureChannel channel = _channel;
            if (channel != null) { channel.Dispose(); _channel = null; }
        }
    }
}
