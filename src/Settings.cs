using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Fjern
{
    // Små Hjælpere der bruges over hele programmet: odpåstler der bruger List<String>
    // i stedet for et bibliotek, så vi kan var en med hvad alle tingene er.
    internal static class Helpers
    {
        public static string AppData
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(root, "Fjern");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        // Read #br piano hvor line = kun value. Hvis filen mangler returneres empty.
        public static List<string> ReadLines(string path)
        {
            List<string> list = new List<string>();
            if (!File.Exists(path)) return list;
            string text = File.ReadAllText(path);
            if (!string.IsNullOrEmpty(text))
            {
                string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    list.Add(line);
                }
            }
            return list;
        }

        public static void WriteBatch(string path, string contents)
        {
            File.WriteAllText(path, contents, new UTF8Encoding(false));
        }
    }

    // Simple key=value store i %APPDATA%\Fjern\settings.ini . System.Runtime.Serialization's
    // DataContractJsonSerializer er tungere end hvad vi behøver, og csc i Framework 4
    // kan ikke Støtte JSON, så en lille INI-parser er den sikre, eksplicitte vej.
    internal sealed class Settings
    {
        private static readonly object Sync = new object();
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        private static string FilePath
        {
            get { return Path.Combine(Helpers.AppData, "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            lock (Sync)
            {
                foreach (string line in Helpers.ReadLines(FilePath))
                {
                    int eq = line.IndexOf('='); if (eq > 0)
                    {
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        s._values[key] = val;
                    }
                }
            }
            return s;
        }

        public string Get(string key, string fallback)
        {
            lock (Sync)
            {
                string val;
                if (_values.TryGetValue(key, out val)) return val;
            }
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int value;
            if (int.TryParse(Get(key, fallback.ToString()), out value)) return value;
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            bool value;
            return bool.TryParse(Get(key, fallback.ToString()), out value) ? value : fallback;
        }

        public void Set(string key, string value)
        {
            lock (Sync) _values[key] = value;
        }

        public void Save()
        {
            lock (Sync)
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _values)
                {
                    sb.AppendLine(kv.Key + "=" + (kv.Value == null ? "" : kv.Value));
                }
                Helpers.WriteBatch(FilePath, sb.ToString());
            }
        }
    }

    // Lager-område der lader en background-side cs in beklobe.
    internal static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalFree(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        private const uint CFUnicodeText = 13;
        private const uint GMemMoveable = 0x0002;

        public static string ClipboardGetText()
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                IntPtr handle = GetClipboardData(CFUnicodeText);
                if (handle == IntPtr.Zero) return null;
                IntPtr locked = GlobalLock(handle);
                if (locked == IntPtr.Zero) return null;
                try
                {
                    int length = 0;
                    while (Marshal.ReadInt16(locked, length * 2) != 0) length++;
                    return length == 0
                        ? string.Empty
                        : Marshal.PtrToStringUni(locked, length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        public static bool ClipboardSetText(string text)
        {
            if (text == null) return false;

            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                int size = Encoding.Unicode.GetByteCount(text) + 2;
                IntPtr handle = GlobalAlloc(GMemMoveable, (UIntPtr)size);
                if (handle == IntPtr.Zero) return false;

                IntPtr locked = GlobalLock(handle);
                if (locked == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }
                try
                {
                    Marshal.Copy(Encoding.Unicode.GetBytes(text + "\0"), 0, locked, size);
                    if (SetClipboardData(CFUnicodeText, handle) == IntPtr.Zero)
                    {
                        GlobalFree(handle);
                        return false;
                    }
                }
                finally
                {
                    GlobalUnlock(handle);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }

        public static uint ClipboardSequence
        {
            get { return GetClipboardSequenceNumber(); }
        }
    }
}
