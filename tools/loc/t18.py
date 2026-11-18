# Teil 18: PersistentCars-Optionen nach der Umbenennung
_disable_reservations = ["Disable all track reservations"] * len(LANGS)
_disable_reservations[9] = "Alle Gleisreservierungen deaktivieren"
k("disable_reservations", *_disable_reservations)

_disable_reservations_tip = [
    "Jobs no longer reserve track space during generation or acceptance. Existing reservations are released when this option is enabled."
] * len(LANGS)
_disable_reservations_tip[9] = (
    "Jobs reservieren weder bei der Erzeugung noch bei der Annahme Gleisplatz. "
    "Bereits vorhandene Reservierungen werden beim Einschalten freigegeben."
)
k("disable_reservations_tip", *_disable_reservations_tip)
