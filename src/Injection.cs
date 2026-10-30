using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Fjern
{
    // Sender mus- og tastaturbeskeder ind i den lokale session.
    //
    // Mus bruger SendInput med MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
    // saa den rammer den skaerm der deles, ogsaa ved flere skaerme.
    //
    // Taster sendes som scancodes, ikke virtuelle noegler: saa rammer man den
    // fysiske tast, ogsaa naar afsender og modtager har forskellige tastaturlayouts.
    internal static class Injection
    {
        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        private static Size _virtualDesk;

        static Injection()
        {
            _virtualDesk = new Size(Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
                                    Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));
        }

        private static void SendMouse(uint flags, int dx, int dy, uint data)
        {
            INPUT[] buffer = new INPUT[1];
            buffer[0].type = INPUT_MOUSE;
            buffer[0].u.mi.dx = dx;
            buffer[0].u.mi.dy = dy;
            buffer[0].u.mi.mouseData = data;
            buffer[0].u.mi.dwFlags = flags;
            buffer[0].u.mi.time = 0;
            buffer[0].u.mi.dwExtraInfo = IntPtr.Zero;

            LastMouseResult = SendInput(1, buffer, Marshal.SizeOf(typeof(INPUT)));
            if (LastMouseResult == 0) LastError = Marshal.GetLastWin32Error();
        }

        // Diagnostik: SendInput returnerer antal accepterede beskeder.
        // 0 betyder blokeret, typisk af UIPI naar et forhoejet vindue har fokus.
        public static uint LastMouseResult;
        public static int LastError;

        // nx, ny er 0..1 inden for den skaerm der deles.
        public static void MoveTo(Rectangle monitor, double nx, double ny)
        {
            double clampedX = nx < 0 ? 0 : (nx > 1 ? 1 : nx);
            double clampedY = ny < 0 ? 0 : (ny > 1 ? 1 : ny);

            int absX = monitor.Left + (int)Math.Round(clampedX * (monitor.Width - 1));
            int absY = monitor.Top + (int)Math.Round(clampedY * (monitor.Height - 1));

            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);

            int relX = absX - vx;
            int relY = absY - vy;

            int normX = (int)((long)relX * 65535 / Math.Max(1, _virtualDesk.Width - 1));
            int normY = (int)((long)relY * 65535 / Math.Max(1, _virtualDesk.Height - 1));

            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                      normX, normY, 0);
        }

        public static void Button(Rectangle monitor, byte button, bool down, double nx, double ny)
        {
            MoveTo(monitor, nx, ny);

            uint flag;
            if (button == Wire.ButtonLeft) flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            else if (button == Wire.ButtonRight) flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
            else flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;

            SendMouse(flag, 0, 0, 0);
        }

        public static void Wheel(Rectangle monitor, int delta, double nx, double ny)
        {
            MoveTo(monitor, nx, ny);
            SendMouse(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta));
        }

        public static void Key(ushort vk, bool down)
        {
            uint scan = MapVirtualKey(vk, 0);
            uint flags = 0;

            if (scan != 0)
            {
                flags |= KEYEVENTF_SCANCODE;
                if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            }
            if (!down) flags |= KEYEVENTF_KEYUP;

            INPUT[] buffer = new INPUT[1];
            buffer[0].type = INPUT_KEYBOARD;
            if (scan != 0)
            {
                buffer[0].u.ki.wVk = 0;
                buffer[0].u.ki.wScan = (ushort)scan;
            }
            else
            {
                buffer[0].u.ki.wVk = vk;
                buffer[0].u.ki.wScan = 0;
            }
            buffer[0].u.ki.dwFlags = flags;
            buffer[0].u.ki.time = 0;
            buffer[0].u.ki.dwExtraInfo = IntPtr.Zero;

            SendInput(1, buffer, Marshal.SizeOf(typeof(INPUT)));
        }

        // Holder en tast nede fra den anden side, saa vi ikke sender auto-repeat.
        public static bool IsDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool IsExtended(ushort vk)
        {
            switch (vk)
            {
                case 0x21: // page up
                case 0x22: // page down
                case 0x23: // end
                case 0x24: // home
                case 0x25: // left
                case 0x26: // up
                case 0x27: // right
                case 0x28: // down
                case 0x2D: // insert
                case 0x2E: // delete
                case 0x5B: // left windows
                case 0x5C: // right windows
                case 0x5D: // apps
                case 0x6F: // numpad divide
                case 0x90: // num lock
                case 0xA3: // right control
                case 0xA5: // right alt
                    return true;
                default:
                    return false;
            }
        }

        // Slip alt hvad der maatte vaere trykket ned, naar styringen stopper.
        public static void ReleaseModifiers()
        {
            ushort[] keys = new ushort[]
            {
                0x10, 0x11, 0x12,             // shift, ctrl, alt
                0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
                0x5B, 0x5C                    // windows-taster
            };
            for (int i = 0; i < keys.Length; i++)
            {
                if (IsDown(keys[i])) Key(keys[i], false);
            }
        }
    }
}
