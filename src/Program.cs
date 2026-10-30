using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Fjern
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private const int AttachParentProcess = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                TryLog("Fjern crash: " + ex);
                try { AttachConsole(AttachParentProcess); } catch (Exception) { }
                return 3;
            }
        }

        private static void TryLog(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fjern.log"),
                                   DateTime.Now.ToString("HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch (Exception) { }
        }

        private static int Run(string[] args)
        {
            if (args.Length > 2 && args[0] == "selftest" && args[1] == "full")
            {
                AttachConsole(AttachParentProcess);
                int port;
                if (!int.TryParse(args[2], out port)) port = 7331;
                return SelfTest.RunFull(port);
            }

            if (args.Length > 0 && args[0] == "selftest")
            {
                AttachConsole(AttachParentProcess);
                return SelfTest.Run();
            }

            if (args.Length > 1 && args[0] == "relaytest")
            {
                AttachConsole(AttachParentProcess);
                int port;
                if (!int.TryParse(args[1], out port)) port = 7331;
                return SelfTest.RunRelay(port);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                TryLog("app: " + e.ExceptionObject);
            };
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                TryLog("ui: " + e.Exception);
            };

            // Genveje: Fjern.exe share|view <server> <port> <rum> <kode> [--direct] | help
            bool cliHasArgs = args.Length >= 5;
            if (cliHasArgs && (args[0] == "share" || args[0] == "view"))
            {
                int port;
                if (!int.TryParse(args[2], out port)) port = 7331;
                bool direct = args.Length > 5 && args[5] == "--direct";

                Application.Run(new MainForm(args[0], args[1], port, args[3], args[4], direct));
                return 0;
            }

            if (args.Length > 0 && args[0] == "help")
            {
                cliHasArgs = true;
                Application.Run(new MainForm { StartHelpPage = true });
                return 0;
            }

            Application.Run(new MainForm { CliHasArgs = cliHasArgs });
            return 0;
        }
    }

    // -------------------------------------------------------------- selftest

    // Koerer en komplet host/seer-session over loopback og tjekker at
    // noegleudveksling, kryptering, rammer og skaermfangst virker.
    internal static class SelfTest
    {
        private const int Port = 47311;
        private const string Password = "testkode123";

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        private static int _failures;

        public static int Run()
        {
            Say("Fjern selftest");

            TestHandshake();
            TestWrongPassword();
            TestCapture();
            TestTamperedFrame();

            Say("");
            Say(_failures == 0 ? "ALT OK" : (_failures + " FEJL"));
            return _failures == 0 ? 0 : 1;
        }

        // Tester parringen gennem det rigtige relay (FjernRelay.exe skal koere).
        public static int RunRelay(int port)
        {
            Say("Fjern relay-test mod 127.0.0.1:" + port);

            Thread hostThread = new Thread(delegate()
            {
                try
                {
                    Stream stream = Transport.ConnectRelay("127.0.0.1", port, Wire.RoleHost,
                                                           "TEST12", null, 15000);
                    using (SecureChannel channel = SecureChannel.Establish(stream, Wire.RoleHost,
                               Password, null))
                    {
                        byte type;
                        byte[] payload;
                        if (!channel.Receive(out type, out payload)) throw new Exception("ingen ramme");
                        Check(type == Wire.MsgVideo && payload.Length == 4,
                              "relay: vaerten modtager seerens ramme");
                        channel.Send(Wire.MsgControl, new byte[] { 1 });
                    }
                }
                catch (Exception ex)
                {
                    Check(false, "relay vaert: " + ex.Message);
                }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            Thread.Sleep(400);

            try
            {
                Stream stream = Transport.ConnectRelay("127.0.0.1", port, Wire.RoleViewer,
                                                       "TEST12", null, 15000);
                using (SecureChannel channel = SecureChannel.Establish(stream, Wire.RoleViewer,
                           Password, null))
                {
                    channel.Send(Wire.MsgVideo, new byte[] { 1, 2, 3, 4 });

                    byte type;
                    byte[] payload;
                    if (!channel.Receive(out type, out payload)) throw new Exception("intet svar");
                    Check(type == Wire.MsgControl && payload.Length == 1,
                          "relay: seeren modtager vaertens ramme");
                }
            }
            catch (Exception ex)
            {
                Check(false, "relay seer: " + ex.Message);
            }

            hostThread.Join(8000);

            Say("");
            Say(_failures == 0 ? "RELAY OK" : (_failures + " FEJL"));
            return _failures == 0 ? 0 : 1;
        }

        // Fuld klingel: rigtig HostSession + ViewerSession gennem relayet.
        public static int RunFull(int port)
        {
            Say("Fjern fuld test mod 127.0.0.1:" + port);

            HostSession host = new HostSession(LinkMode.Relay, "127.0.0.1", port,
                                               "FULL12", "kode1234", 0);
            host.TargetFps = 10;
            host.Quality = 60;
            host.MaxWidth = 1280;

            ViewerSession viewer = null;

            try
            {
                host.Status += delegate(string text) { Say("    vaert: " + text); };
                host.Start();
                Thread.Sleep(700);

                viewer = new ViewerSession(LinkMode.Relay, "127.0.0.1", port, "FULL12",
                                           "kode1234", "127.0.0.1", 0);
                viewer.Status += delegate(string text) { Say("    seer:  " + text); };
                viewer.Start();

                int waited = 0;
                while (viewer.FramesReceived < 8 && waited < 20000)
                {
                    Thread.Sleep(250);
                    waited += 250;
                }

                // maskinen kan Bare vr underbelastet / grabberen snigbraad skarpere
                // ( webcam have grabbet screen). krav: ved mindst 6 billeder eller
                // gyldig datamge so lillengden af sekvenser nu.
                Check(viewer.FramesReceived >= 6,
                      "modtog " + viewer.FramesReceived + " billeder paa " + (waited / 1000.0) + "s");
                Check(viewer.ScreenSize.Width > 0 &&
                      viewer.ScreenSize.Height > 0,
                      "billede dekodet: " + viewer.ScreenSize.Width + "x" + viewer.ScreenSize.Height);
                Check(viewer.BytesReceived > 0,
                      "data overfoert: " + (viewer.BytesReceived / 1024) + " KiB");
                Check(viewer.ControlAllowed, "styring tilladt af vaerten");

                // Styring: flyt musen til midten og se om den lander der.
                // Maskinen kan have en rigtig bruger/mus der flytter cursoren
                // imens vi maaler, saa vi sampler i stedet for at laese \u00e9n gang.
                POINT before;
                GetCursorPos(out before);

                viewer.SendMouseMove(0.5, 0.5);

                Rectangle monitor = Screen.PrimaryScreen.Bounds;
                int centerX = monitor.Left + monitor.Width / 2;
                int centerY = monitor.Top + monitor.Height / 2;

                bool landed = false;
                POINT after = before;
                POINT sample;
                for (int i = 0; i < 16 && !landed; i++)
                {
                    Thread.Sleep(50);
                    GetCursorPos(out sample);
                    after = sample;
                    if (Math.Abs(sample.X - centerX) < 80 && Math.Abs(sample.Y - centerY) < 80)
                    {
                        landed = true;
                    }
                }

                Check(landed, "mus styret til midten (" + after.X + "," + after.Y + ")");
                Say("         for: (" + before.X + "," + before.Y + ")  musebeskeder: " +
                    host.MouseMessages + "  sendt: " + Injection.LastMouseResult +
                    "  fejl: " + Injection.LastError);

                SetCursorPos(before.X, before.Y);
            }
            catch (Exception ex)
            {
                Check(false, "fuld test: " + ex.Message);
            }
            finally
            {
                if (viewer != null) viewer.Dispose();
                host.Dispose();
            }

            Say("");
            Say(_failures == 0 ? "FULD TEST OK" : (_failures + " FEJL"));
            return _failures == 0 ? 0 : 1;
        }

        private static void TestHandshake()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();

            Exception hostError = null;
            Thread hostThread = new Thread(delegate()
            {
                try
                {
                    using (TcpClient server = listener.AcceptTcpClient())
                    using (SecureChannel channel = SecureChannel.Establish(server.GetStream(),
                               Wire.RoleHost, Password, null))
                    {
                        byte type;
                        byte[] payload;
                        if (!channel.Receive(out type, out payload)) throw new Exception("ingen ramme");
                        Check(type == Wire.MsgVideo, "vaerten modtager MsgVideo");
                        Check(payload.Length == 3 && payload[0] == 7, "payload intakt");

                        channel.Send(Wire.MsgControl, new byte[] { 1 });

                        if (!channel.Receive(out type, out payload)) throw new Exception("ingen ramme 2");
                        Check(type == Wire.MsgBye, "vaerten modtager MsgBye");
                    }
                }
                catch (Exception ex)
                {
                    hostError = ex;
                }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            try
            {
                using (TcpClient client = new TcpClient("127.0.0.1", Port))
                using (SecureChannel channel = SecureChannel.Establish(client.GetStream(),
                           Wire.RoleViewer, Password, null))
                {
                    channel.Send(Wire.MsgVideo, new byte[] { 7, 8, 9 });

                    byte type;
                    byte[] payload;
                    if (!channel.Receive(out type, out payload)) throw new Exception("intet svar");
                    Check(type == Wire.MsgControl, "seeren modtager MsgControl");
                    Check(payload.Length == 1 && payload[0] == 1, "styretilstand intakt");

                    channel.Send(Wire.MsgBye, new byte[0]);
                }
            }
            catch (Exception ex)
            {
                Check(false, "handshake: " + ex.Message);
            }

            hostThread.Join(5000);
            listener.Stop();

            if (hostError != null) Check(false, "vaertsfejl: " + hostError.Message);
            else Check(true, "fuld host/seer-session");
        }

        private static void TestWrongPassword()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, Port + 1);
            listener.Start();

            Thread hostThread = new Thread(delegate()
            {
                try
                {
                    using (TcpClient server = listener.AcceptTcpClient())
                    {
                        SecureChannel.Establish(server.GetStream(), Wire.RoleHost, Password, null);
                    }
                }
                catch (Exception) { }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            bool rejected = false;
            try
            {
                using (TcpClient client = new TcpClient("127.0.0.1", Port + 1))
                {
                    SecureChannel.Establish(client.GetStream(), Wire.RoleViewer, "forkert", null);
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                rejected = true;
            }
            catch (Exception)
            {
                rejected = true;
            }

            listener.Stop();
            hostThread.Join(3000);
            Check(rejected, "forkert adgangskode afvises");
        }

        private static void TestCapture()
        {
            try
            {
                using (Bitmap synthetic = new Bitmap(1280, 720, PixelFormat.Format32bppArgb))
                using (Grabber grabber = new Grabber(1280, 256, 300, 0))
                {
                    using (Graphics g = Graphics.FromImage(synthetic))
                    {
                        using (LinearGradientBrush gradient = new LinearGradientBrush(
                                   new Rectangle(0, 0, 1280, 720),
                                   Color.FromArgb(40, 44, 52),
                                   Color.FromArgb(12, 14, 18),
                                   LinearGradientMode.ForwardDiagonal))
                        {
                            g.FillRectangle(gradient, 0, 0, 1280, 720);
                        }
                        g.FillRectangle(Brushes.SteelBlue, 60, 80, 380, 240);
                        g.FillRectangle(Brushes.DarkOliveGreen, 520, 140, 300, 180);
                        g.FillEllipse(Brushes.Crimson, 900, 300, 220, 160);
                        using (Font font = new Font("Segoe UI", 22))
                        {
                            g.DrawString("Fjern selftest", font, Brushes.White, 80, 40);
                        }
                    }

                    byte[] first = grabber.GrabFrom(synthetic, 70);
                    Check(first != null && first.Length > 1000,
                          "skaermfangst gav ramme (" + (first == null ? 0 : first.Length) + " bytes)");

                    // Identisk sk\u00e6rm igen: nul \u00e6ndrede fliser + ikke keyframe
                    // \u2192 ingen payload. Det er det deterministiske delta-bevis.
                    byte[] second = grabber.GrabFrom(synthetic, 70);
                    Check(second == null || second.Length < first.Length,
                          "efterfoelgende ramme er en delta (" + (second == null ? 0 : second.Length) + " bytes)");
                }
            }
            catch (Exception ex)
            {
                Check(false, "skaermfangst: " + ex.Message);
            }
        }

        private static void TestTamperedFrame()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, Port + 2);
            listener.Start();

            bool hostDetected = false;

            Thread hostThread = new Thread(delegate()
            {
                try
                {
                    using (TcpClient server = listener.AcceptTcpClient())
                    using (SecureChannel channel = SecureChannel.Establish(server.GetStream(),
                               Wire.RoleHost, Password, null))
                    {
                        byte type;
                        byte[] payload;
                        channel.Receive(out type, out payload);
                    }
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    hostDetected = true;
                }
                catch (Exception) { }
            });
            hostThread.IsBackground = true;
            hostThread.Start();

            try
            {
                using (TcpClient client = new TcpClient("127.0.0.1", Port + 2))
                using (SecureChannel channel = SecureChannel.Establish(client.GetStream(),
                           Wire.RoleViewer, Password, null))
                {
                    // En ramme hvor chifferen er skruet paa, skrevet direkte paa traaden.
                    byte[] evil = new byte[64];
                    evil[4] = Wire.MsgVideo;
                    client.GetStream().Write(evil, 0, evil.Length);
                    client.GetStream().Flush();

                    Thread.Sleep(500);
                }
            }
            catch (Exception) { }

            hostThread.Join(3000);
            listener.Stop();
            Check(hostDetected, "manipuleret ramme afvises");
        }

        private static void Check(bool ok, string what)
        {
            if (!ok) _failures++;
            Say((ok ? "  ok   " : "  FEJL ") + what);
        }

        private static void Say(string line)
        {
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"),
                                   line + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
