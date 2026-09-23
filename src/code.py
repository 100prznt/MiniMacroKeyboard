import time
import board
import digitalio
import pwmio
import usb_hid
import usb_cdc
import rtc

from adafruit_hid.keyboard import Keyboard
from keyboard_layout_win_de import KeyboardLayout
from adafruit_hid.keycode import Keycode
from adafruit_hid.mouse import Mouse

from macros import TEXT_M2, TEXT_M4, TODO_SIGNATURE

kbd = Keyboard(usb_hid.devices)
layout = KeyboardLayout(kbd)
mouse = Mouse(usb_hid.devices)

# --- Taster-PCB: alle drei Taster schalten gegen GND ---
SWITCH_M1 = board.GP17
SWITCH_M2 = board.GP26
SWITCH_M3 = board.GP28

def make_switch(pin):
    io = digitalio.DigitalInOut(pin)
    io.direction = digitalio.Direction.INPUT
    io.pull = digitalio.Pull.UP   # gegen GND -> Pull-Up, gedrueckt = LOW
    return io

m1 = make_switch(SWITCH_M1)
m2 = make_switch(SWITCH_M2)
m3 = make_switch(SWITCH_M3)

# --- Onboard-LED: Herzschlag-Blinken via PWM, solange der Jiggler aktiv ist ---
led = pwmio.PWMOut(board.LED, frequency=1000, duty_cycle=0)

HEARTBEAT_FADE_IN = 0.3      # Sekunden pro Aufhellen
HEARTBEAT_FADE_OUT = 0.3     # Sekunden pro Abdunkeln
HEARTBEAT_GAP = 0.15         # Pause zwischen den zwei Pulsen eines "Herzschlags"
HEARTBEAT_PAUSE = 1.0        # Pause nach den zwei Pulsen, bevor es von vorn beginnt
HEARTBEAT_PULSE = HEARTBEAT_FADE_IN + HEARTBEAT_FADE_OUT
HEARTBEAT_CYCLE = 2 * HEARTBEAT_PULSE + HEARTBEAT_GAP + HEARTBEAT_PAUSE

def heartbeat_duty(t):
    # t: Sekunden seit Zyklusbeginn (0 .. HEARTBEAT_CYCLE)
    if t < HEARTBEAT_PULSE:
        pt = t
    elif t < HEARTBEAT_PULSE + HEARTBEAT_GAP:
        return 0
    elif t < 2 * HEARTBEAT_PULSE + HEARTBEAT_GAP:
        pt = t - (HEARTBEAT_PULSE + HEARTBEAT_GAP)
    else:
        return 0

    if pt < HEARTBEAT_FADE_IN:
        frac = pt / HEARTBEAT_FADE_IN
    else:
        frac = 1.0 - (pt - HEARTBEAT_FADE_IN) / HEARTBEAT_FADE_OUT

    frac = max(0.0, min(1.0, frac))
    return int(frac * 65535)

# --- USB-Serial-Datenkanal zur C#-Companion-App ---
serial = usb_cdc.data
active_window = "DEFAULT"

# Sicherer Default: bis der PC sich aktiv als "entsperrt" meldet, gilt er als gesperrt.
# Deckt Boot, Neustart und noch nicht laufende Companion-App ab.
pc_locked_reported = True
last_serial_received = 0.0          # time.monotonic()-Zeitpunkt der letzten Nachricht
LOCK_HEARTBEAT_TIMEOUT = 45.0        # Sekunden ohne Nachricht -> Verbindung gilt als tot -> gesperrt

def check_serial():
    global active_window, pc_locked_reported, last_serial_received
    if serial is None or serial.in_waiting == 0:
        return
    try:
        line = serial.readline().decode("utf-8").strip()
    except Exception:
        return

    last_serial_received = time.monotonic()

    if line.startswith("TIME:"):
        try:
            date_part, time_part = line[5:].split(" ")
            y, mo, d = map(int, date_part.split("-"))
            h, mi, s = map(int, time_part.split(":"))
            rtc.RTC().datetime = time.struct_time((y, mo, d, h, mi, s, 0, -1, -1))
        except Exception:
            pass

    elif line.startswith("WIN:"):
        active_window = line[4:]

    elif line.startswith("LOCK:"):
        pc_locked_reported = (line[5:] == "1")

def pc_is_locked(now):
    # Gesperrt, wenn entweder aktiv gemeldet, oder die Verbindung zu lange still war
    # (kein Heartbeat -> App laeuft nicht -> PC ist am Anmeldebildschirm o.ae.)
    if pc_locked_reported:
        return True
    return (now - last_serial_received) > LOCK_HEARTBEAT_TIMEOUT

def current_timestamp():
    now = time.localtime()
    return "{:02d}.{:02d}.{:04d} {:02d}:{:02d}:{:02d}".format(
        now.tm_mday, now.tm_mon, now.tm_year, now.tm_hour, now.tm_min, now.tm_sec
    )

def write_todo_comment():
    # Zeile 1: Kommentar mit Name/Kuerzel und Zeitstempel
    line1 = "// {} {}".format(TODO_SIGNATURE, current_timestamp())
    # Zeile 2: leere Kommentarzeile zum Weiterschreiben
    line2 = "// "

    layout.write(line1)
    # Zeilenumbruch als Shift+Enter (weicher Umbruch statt neuem Absatz)
    kbd.send(Keycode.SHIFT, Keycode.ENTER)
    layout.write(line2)

# --- Zustaende Taster (True = losgelassen, fuer Flankenerkennung) ---
last_m1 = True
last_m2 = True
last_m3 = True

# --- Combo-Erkennung M1+M3 -> "M4" + Enter, nur bei passender Anwendung oder gesperrtem PC ---
combo_active = False     # True, solange M1+M3 gemeinsam gehalten werden
m1_suppressed = False    # verhindert Text bei M1 beim Loslassen nach einer Combo
m3_suppressed = False    # verhindert M3-Kurz-/Lang-Logik nach einer Combo

# --- Kurz/Lang-Erkennung fuer M3 ---
LONG_PRESS_THRESHOLD = 0.5   # Sekunden
m3_press_start = None
m3_long_triggered = False

# --- Zustand Mausjiggler: startet bereits aktiv ---
jiggler_active = True
JIGGLE_INTERVAL = 25.0       # Sekunden zwischen den minimalen Mausbewegungen
last_jiggle = 0.0

while True:
    now = time.monotonic()

    check_serial()

    state_m1 = m1.value
    state_m2 = m2.value
    state_m3 = m3.value

    # --- Combo: M1 + M3 gleichzeitig gedrueckt -> "M4" + Enter,
    #     aber nur bei passender Anwendung oder gesperrtem PC ---
    if (not state_m1) and (not state_m3) and not combo_active:
        if active_window == "VPN" or pc_is_locked(now):
            layout.write(TEXT_M4)
            kbd.send(Keycode.ENTER)
        combo_active = True
        m1_suppressed = True
        m3_suppressed = True
        m3_press_start = None
        m3_long_triggered = True   # unterdrueckt die spaetere Lang-Druck-Auswertung

    if state_m1 and state_m3:
        # beide wieder losgelassen -> Combo-Sperre aufheben
        combo_active = False

    # --- M1: TODO-Kommentar mit Zeitstempel erst beim Loslassen, ausser bei Combo (Testbelegung) ---
    if (not last_m1) and state_m1:
        if not m1_suppressed:
            write_todo_comment()
        m1_suppressed = False
    last_m1 = state_m1

    # --- M2: Text erst beim Loslassen (unbeteiligt an der Combo) ---
    if (not last_m2) and state_m2:
        layout.write(TEXT_M2)
    last_m2 = state_m2

    # --- M3: kurz -> aktive Anwendung beim Loslassen (Testbelegung),
    #          lang -> Jiggler sofort beim Ueberschreiten des Thresholds umschalten ---
    if last_m3 and not state_m3:
        # Flanke: losgelassen -> gedrueckt
        if not combo_active:
            m3_press_start = now
            m3_long_triggered = False

    elif not state_m3 and m3_press_start is not None and not m3_long_triggered and not m3_suppressed:
        # Taste wird weiterhin gehalten -> Haltedauer pruefen, ohne auf Loslassen zu warten
        if now - m3_press_start >= LONG_PRESS_THRESHOLD:
            jiggler_active = not jiggler_active
            m3_long_triggered = True

    elif (not last_m3) and state_m3:
        # Flanke: gedrueckt -> losgelassen
        if not m3_long_triggered and not m3_suppressed:
            layout.write(active_window)
        m3_press_start = None
        m3_long_triggered = False
        m3_suppressed = False

    last_m3 = state_m3

    # --- Mausjiggler-Logik ---
    if jiggler_active:
        led.duty_cycle = heartbeat_duty(now % HEARTBEAT_CYCLE)
        if now - last_jiggle >= JIGGLE_INTERVAL:
            mouse.move(x=1)
            mouse.move(x=-1)
            last_jiggle = now
    else:
        led.duty_cycle = 0

    time.sleep(0.01)
