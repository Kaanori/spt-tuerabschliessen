# Changelog

## 1.1.1 — 2026-11-03

- Locking now sounds like unlocking and uses the same hand animation (key in
  hand, if you carry it). "Aufschliessen" (unlock) on doors without a lock
  sounds like a normal unlock as well. Both go through the game's own
  interaction path (`Player.StartInteraction`) instead of calling
  `Lock()`/`Unlock()` directly.

## 1.1.0 — 2026-11-03

- New setting **"Auch Tueren ohne Schloss abschliessen"** (also lock doors
  without a lock), **off by default**: lock ordinary doors that have no
  keyhole. Such a door gets an **"Aufschliessen"** (unlock) entry; kicking it
  in still works. Keycard, power/switch, quest, trigger and game-locked doors
  stay excluded.
- Bots can no longer unlock, open or kick in a door you locked — also not
  through ORBIT. New setting "Bots respektieren abgeschlossene Tueren" (on).
- New setting "Beschriftung Aufschliessen" (label of the unlock entry).

## 1.0.0 — 2026-11-03

- First release as a standalone repository (SPT 4.1.6).
