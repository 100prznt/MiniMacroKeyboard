import usb_cdc

# Aktiviert zusaetzlich zur normalen REPL-Konsole eine zweite,
# reine Daten-Serial-Schnittstelle, ueber die die C#-Companion-App
# Zeit und aktive Anwendung an den Pico sendet.
usb_cdc.enable(console=True, data=True)
