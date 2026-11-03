# TuerAbschliessen (lock door)

Adds the entry **"Abschliessen"** (lock) to the door menu. A door that you
unlocked with the key and then closed again can be locked once more.

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.

## Installation

1. Download `TuerAbschliessen-1.0.0.zip` from the [Releases](https://github.com/Kaanori/spt-tuerabschliessen/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## Settings (F12 -> Tuer abschliessen)

| Setting | Default | Meaning |
|--------------------|-----------------|-----------|
| **Eingeschaltet** (enabled) | on | Show the entry |
| **Schluessel noetig** (key required) | on | Off: any door with a lock works, even without a key |
| **Beschriftung** (label) | `Abschliessen` | Text in the menu |
| **Jede Tuer eintretbar** (every door kickable) | on | "Eintreten" (kick in) on every closed door, from any angle |
| **Eintreten gelingt immer** (kicking in always succeeds) | on | No more failed kicks |
| **Protokoll** (log) | off | Write every action to the BepInEx console |

## How it works

The game can lock doors — it just does not show the button.
`EFT.Interactive.WorldInteractiveObject.Lock()` is fully built in, as is
`EInteractionType.Lock`. Only the menu that
`EFT.InteractionContextHelper.GetAvailableActions(GamePlayerOwner, Door)`
assembles knows just `OpenDoor`, `CloseDoor`, `Breach` and three greyed-out
tactical entries. A Harmony postfix appends our entry.

## Two pitfalls (for later)

**1. Do not go through `Door.Interact(..., EInteractionType.Lock)`.**
The base class handles `Lock`, but `Door` overrides `Interact` and its
switch only knows Open, Close, Unlock, Breach. The call would go nowhere.
Hence `door.Lock()` directly.

**2. `Sirenix.Serialization` must go into the csproj.** Doors inherit via
`SerializedMonoBehaviour` (Odin Inspector). Without the reference, the
build fails with `CS0012` at every Door access.

## Kicking in (src/Eintreten.cs)

Four hurdles were in the way: the two editor switches `CanBeBreached` and
`CanInteractWithBreach` per door, the angle check `IsBreachAngle`, the
success roll `BreachSuccessRoll`, and the block while prone.

Instead of defeating all four individually, two interventions are enough:

1. The "Breach" menu entry is **always** appended by the game as soon as
   the door is closed or locked — only its `Disabled` flag changes. We
   clear that flag.
2. Whether the kick succeeds is later decided solely by
   `MovementContext.NextBreachResult`. In `BreachDoorState` there is
   `if (NextBreachResult)` -> door opens, otherwise `FailBreach`. We set it
   to `true` and follow up the animation with
   `PlayerAnimatorSetKickSucceed(true)`.

**Deliberately NOT patched: `Door.BreachSuccessRoll`:** the same method is
also queried by `BotDoorOpener` — intervening there would open every door
for every Scav. `MovementContext` belongs solely to the player character.

## Known limitations

- **Keycard doors (KeycardDoor) are excluded.** They have their own menu
  method and a different lock.
- **No handle animation.** `door.Lock()` sets the state immediately.
  Bots still notice: `Lock()` writes via the `DoorState` setter, which
  fires `OnDoorStateChanged`.
- **Closed doors only.** Close an open door first.
- **Kicked-in doors are broken.** `KickOpen` sets `IsBroken = true`;
  afterwards they can be neither closed nor locked. If you want to stay
  quiet, use the key.

## Building from source

Needs the .NET 10 SDK and an SPT 4.1.6 installation to build against.

```bash
./build.sh             # Linux: build only
./build.sh --install   # build and copy into the game folder
```
```powershell
.\build.ps1            # Windows
.\build.ps1 -Install
```

The game folder is set in `Directory.Build.props` (Windows default `C:/SPT`)
or via the environment variable `SPT_DIR`.

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-tuerabschliessen/issues).

## License

MIT — see [LICENSE](LICENSE).
