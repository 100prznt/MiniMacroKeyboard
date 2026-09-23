using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace MacroKeyboardSync
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // Prozessname (ohne .exe) -> Kennung, die an den Pico gesendet wird.
        // Ergaenzen/anpassen, falls ein Prozessname bei dir anders lautet
        // (z.B. je nach VS-/Office-Version).
        private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
        {
            { "devenv", "VS" },              // Visual Studio
            { "Code", "VSCODE" },            // Visual Studio Code
            { "kicad", "KICAD" },
            { "kicad_pcbnew", "KICAD" },
            { "eeschema", "KICAD" },
            { "opera", "OPERA" },
            { "explorer", "EXPLORER" },
            { "OUTLOOK", "OUTLOOK" },
            { "GitHubDesktop", "GITHUB" },
            { "ms-teams", "TEAMS" },         // neues Microsoft Teams (WebView2-basiert)
            { "Teams", "TEAMS" },            // klassisches Microsoft Teams
            { "csc_ui", "ANYCONNECT" },      // Cisco Secure Client AnyConnect
        };

        private const string DefaultApp = "DEFAULT";
        private const int PollIntervalMs = 500;
        private const int TimeSyncIntervalMs = 5 * 60 * 1000; // alle 5 Minuten
        private const int LockHeartbeatIntervalMs = 15 * 1000; // Pico wertet >45 s Stille als "gesperrt"

        private static SerialPort? _port;
        private static string _lastSentApp = "";
        private static DateTime _lastTimeSync = DateTime.MinValue;

        // --- Sperrstatus ---
        private static volatile bool _isLocked = false;
        private static bool? _lastSentLocked = null;
        private static DateTime _lastLockSent = DateTime.MinValue;

        private static void Main()
        {
            // SystemEvents richtet intern selbststaendig einen Nachrichten-Thread ein,
            // dafuer ist keine WinForms-/WPF-Message-Loop im Main-Thread noetig.
            SystemEvents.SessionSwitch += OnSessionSwitch;

            while (true)
            {
                try
                {
                    EnsureConnected();
                    SendTimeIfDue();
                    SendActiveWindowIfChanged();
                    SendLockStatusIfChanged();
                }
                catch
                {
                    // Verbindung verloren o.ae. -> beim naechsten Durchlauf neu verbinden
                    try { _port?.Close(); } catch { /* ignorieren */ }
                    _port = null;
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        private static void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _isLocked = true;
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _isLocked = false;
            }
        }

        private static void EnsureConnected()
        {
            if (_port is { IsOpen: true }) return;

            string? portName = FindPicoPortName();
            if (portName == null) return;

            _port = new SerialPort(portName, 115200)
            {
                NewLine = "\n",
                WriteTimeout = 500
            };
            _port.Open();

            // Nach (Neu-)Verbindung einmalig alles frisch senden
            _lastSentApp = "";
            _lastTimeSync = DateTime.MinValue;
            _lastSentLocked = null;
            _lastLockSent = DateTime.MinValue;
        }

        // Sucht ueber die USB-Vendor-ID (0x239A = Adafruit/CircuitPython-Boards)
        // nach dem passenden COM-Port, unabhaengig davon, welche Portnummer
        // Windows gerade vergeben hat.
        private static string? FindPicoPortName()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");

            foreach (ManagementObject device in searcher.Get())
            {
                string? pnpId = device["PNPDeviceID"]?.ToString();
                string? caption = device["Caption"]?.ToString();

                if (pnpId != null && caption != null && pnpId.Contains("VID_239A"))
                {
                    int start = caption.LastIndexOf("(COM", StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        int end = caption.IndexOf(')', start);
                        if (end > start)
                        {
                            return caption.Substring(start + 1, end - start - 1); // "COMx"
                        }
                    }
                }
            }

            return null;
        }

        private static void SendTimeIfDue()
        {
            if (_port is not { IsOpen: true }) return;
            if ((DateTime.Now - _lastTimeSync).TotalMilliseconds < TimeSyncIntervalMs) return;

            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _port.WriteLine($"TIME:{ts}");
            _lastTimeSync = DateTime.Now;
        }

        private static void SendActiveWindowIfChanged()
        {
            if (_port is not { IsOpen: true }) return;

            string appTag = GetActiveAppTag();
            if (appTag == _lastSentApp) return;

            _port.WriteLine($"WIN:{appTag}");
            _lastSentApp = appTag;
        }

        private static void SendLockStatusIfChanged()
        {
            if (_port is not { IsOpen: true }) return;

            bool locked = _isLocked;
            bool heartbeatDue = (DateTime.Now - _lastLockSent).TotalMilliseconds >= LockHeartbeatIntervalMs;
            if (_lastSentLocked == locked && !heartbeatDue) return;

            _port.WriteLine($"LOCK:{(locked ? 1 : 0)}");
            _lastSentLocked = locked;
            _lastLockSent = DateTime.Now;
        }

        private static string GetActiveAppTag()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return DefaultApp;

            GetWindowThreadProcessId(hWnd, out uint pid);

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;
                return KnownApps.TryGetValue(name, out string? tag) ? tag : DefaultApp;
            }
            catch
            {
                // Prozess evtl. schon beendet, Zugriff verweigert, o.ae.
                return DefaultApp;
            }
        }
    }
}