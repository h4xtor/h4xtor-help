// WebCam.cs — webcam-frames via avicap32 (WM_CAP). Ingen lib-afhængigheder.
// Grabber menné: capCreateCaptureWindow + CONNECT + GRAB_FRAME_NOSTOP + EDIT_COPY,
// derefter Clipboard.GetImage på en dedikeret STA-tråd. ~2-5 fps lille billede.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Fjern
{
    internal sealed class WebCam : IDisposable
    {
        private const uint WM_CAP_DRIVER_CONNECT = 0x040A;      // WM_USER + 10
        private const uint WM_CAP_DRIVER_DISCONNECT = 0x040B;   // WM_USER + 11
        private const uint WM_CAP_GRAB_FRAME_NOSTOP = 0x043D;   // WM_USER + 61
        private const uint WM_CAP_EDIT_COPY = 0x041E;           // WM_USER + 30
        private const uint WM_USER = 0x0400;

        private int _capWindow;    // HWND
        private int _parentWindow; // HWND til dead head window

        [DllImport("avicap32.dll")]
        private static extern int capCreateCaptureWindowW(string title, uint style,
            int x, int y, int w, int h, int parent, int id);

        [DllImport("user32.dll")]
        private static extern bool SendMessageW(int hwnd, uint msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(int hwnd);

        [DllImport("user32.dll")]
        private static extern int CreateWindowExW(int exStyle, string cls, string title,
            int style, int x, int y, int w, int h, int parent, int menu, int inst, int param);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(int hwnd, int show);

        public int FrameMs = 400;  //Dette er op til 2,5 FPS

        /// <summary>Prober, om der er webcam tilgængelig (så appen ikke bare stødder).</summary>
        public bool Available
        {
            get
            {
                if (_capWindow != 0) return true;
                try
                {
                    _parentWindow = CreateWindowExW(0, "STATIC", "fjern-cam-parent",
                        0, 0, 0, 4, 4, 0, 0, 0, 0);
                    _capWindow = capCreateCaptureWindowW("fjern-cam",
                        0x40000000 | 0x10000000, 0, 0, 320, 240, _parentWindow, 0);
                    if (_capWindow == 0) return false;
                    if (!SendMessageW(_capWindow, WM_USER + 10, 0, 0))  // WM_CAP_DRIVER_CONNECT
                    {
                        return false;
                    }
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Tager et billede eller returnerer null.</summary>
        public Bitmap Grab()
        {
            if (_capWindow == 0 && !Available) return null;

            Bitmap result = null;
            Thread worker = new Thread(delegate()
            {
                try
                {
                    SendMessageW(_capWindow, WM_USER + 61, 0, 0);  // WM_CAP_GRAB_FRAME_NOSTOP
                    if (SendMessageW(_capWindow, WM_USER + 30, 0, 0))
                    {
                        // EDIT_COPY: DIB ligger på klippebordet nu
                        if (ClipboardHelper.ContainsImage())
                        {
                            result = (Bitmap)ClipboardHelper.GetImage();
                        }
                    }
                }
                catch (Exception) { result = null; }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            if (worker.Join(FrameMs * 2)) { /* return under-available */ }
            return result;
        }

        public void Dispose()
        {
            if (_capWindow != 0)
            {
                SendMessageW(_capWindow, WM_USER + 11, 0, 0);  // WM_CAP_DRIVER_DISCONNECT
                DestroyWindow(_capWindow);
                _capWindow = 0;
            }
            if (_parentWindow != 0)
            {
                DestroyWindow(_parentWindow);
                _parentWindow = 0;
            }
        }
    }

    // klippebord-wrappere til brug fra baggrundstråde
    internal static class ClipboardHelper
    {
        public static bool ContainsImage() { return System.Windows.Forms.Clipboard.ContainsImage(); }
        public static Image GetImage() { return System.Windows.Forms.Clipboard.GetImage(); }
    }
}
