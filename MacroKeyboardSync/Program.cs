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

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        // Prozessname (ohne .exe) -> Kennung, die an den Pico gesendet wird.
        // Ergaenzen/anpassen, falls ein Prozessname bei dir anders lautet
        // (z.B. je nach VS-/Office-Version).
        private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
        {
            { "devenv", "VS" },              // Visual Studio
            { "Code", "VSCODE" },            // Visual Studio Code
            // Bei dieser KiCad-Version laufen Projektmanager, Eeschema und Pcbnew
            // alle unter demselben Prozess "kicad.exe" - Pcbnew wird deshalb in
            // GetActiveAppTag() zusaetzlich ueber den Fenstertitel erkannt.
            { "kicad", "KICAD" },
            { "opera", "OPERA" },
            { "explorer", "EXPLORER" },
            { "OUTLOOK", "OUTLOOK" },
            { "GitHubDesktop", "GITHUB" },
            { "ms-teams", "TEAMS" },         // neues Microsoft Teams (WebView2-basiert)
            { "Teams", "TEAMS" },            // klassisches Microsoft Teams
            { "csc_ui", "VPN" },             // Cisco Secure Client AnyConnect
        };

        private const string DefaultApp = "DEFAULT";
        private const int PollIntervalMs = 500;
        private const int TimeSyncIntervalMs = 5 * 60 * 1000;   // alle 5 Minuten
        private const int LockHeartbeatIntervalMs = 15 * 1000;  // Sperrstatus regelmaessig erneut senden,
                                                                  // damit der Pico eine tote Verbindung erkennt

        private static SerialPort? _port;
        private static string _lastSentApp = "";
        private static DateTime _lastTimeSync = DateTime.MinValue;

        // --- Sperrstatus ---
        private static volatile bool _isLocked = false;
        private static bool? _lastSentLocked = null;
        private static DateTime _lastLockHeartbeat = DateTime.MinValue;

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
            _lastLockHeartbeat = DateTime.MinValue;
        }

        // Sucht ueber die USB-Vendor-ID (0x239A = Adafruit/CircuitPython-Boards)
        // nach dem passenden COM-Port, unabhaengig davon, welche Portnummer
        // Windows gerade vergeben hat.
        //
        // Der Pico meldet zwei COM-Ports mit derselben Vendor-ID: die REPL-Konsole
        // (Interface MI_00) und den Datenkanal aus boot.py (usb_cdc.data, i.d.R. MI_02).
        // Deshalb wird der Port mit der hoechsten Interface-Nummer gewaehlt und
        // MI_00 (Konsole) nie verwendet.
        private static string? FindPicoPortName()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");

            string? bestPort = null;
            int bestInterface = 0;

            foreach (ManagementObject device in searcher.Get())
            {
                string? pnpId = device["PNPDeviceID"]?.ToString();
                string? caption = device["Caption"]?.ToString();

                if (pnpId == null || caption == null || !pnpId.Contains("VID_239A", StringComparison.OrdinalIgnoreCase))
                    continue;

                int iface = GetUsbInterfaceNumber(pnpId);
                if (iface <= bestInterface) continue;   // Konsole (MI_00) bzw. schlechterer Treffer

                string? portName = ExtractComPortName(caption);
                if (portName == null) continue;

                bestPort = portName;
                bestInterface = iface;
            }

            // null, wenn nur die Konsole gefunden wurde (z.B. boot.py fehlt -> kein Datenkanal)
            return bestPort;
        }

        // "USB\VID_239A&PID_80F4&MI_02\..." -> 2; ohne MI_-Angabe -> 0
        private static int GetUsbInterfaceNumber(string pnpId)
        {
            int idx = pnpId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || idx + 6 > pnpId.Length) return 0;

            return int.TryParse(pnpId.AsSpan(idx + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out int iface)
                ? iface
                : 0;
        }

        // "USB Serial Device (COM7)" -> "COM7"
        private static string? ExtractComPortName(string caption)
        {
            int start = caption.LastIndexOf("(COM", StringComparison.Ordinal);
            if (start < 0) return null;

            int end = caption.IndexOf(')', start);
            return end > start ? caption.Substring(start + 1, end - start - 1) : null;
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
            bool changed = _lastSentLocked != locked;
            bool heartbeatDue = (DateTime.Now - _lastLockHeartbeat).TotalMilliseconds >= LockHeartbeatIntervalMs;

            if (!changed && !heartbeatDue) return;

            _port.WriteLine($"LOCK:{(locked ? 1 : 0)}");
            _lastSentLocked = locked;
            _lastLockHeartbeat = DateTime.Now;
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

                if (!KnownApps.TryGetValue(name, out string? tag))
                    return DefaultApp;

                // Pcbnew laeuft bei dieser KiCad-Version im selben Prozess wie der
                // Rest von KiCad - deshalb hier zusaetzlich ueber den Fenstertitel
                // unterscheiden, damit Pcbnew seine eigene LED-Farbe bekommt.
                if (tag == "KICAD")
                {
                    var titleBuffer = new System.Text.StringBuilder(256);
                    GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
                    string windowTitle = titleBuffer.ToString();

                    if (windowTitle.IndexOf("Leiterplatteneditor", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "KICAD_PCB";
                    }
                }

                return tag;
            }
            catch
            {
                // Prozess evtl. schon beendet, Zugriff verweigert, o.ae.
                return DefaultApp;
            }
        }
    }
}
