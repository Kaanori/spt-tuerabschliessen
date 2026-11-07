# TuerAbschliessen (lock door)

> **Now part of [Kaan's QoL](https://github.com/Kaanori/spt-kaansqol)** – this module is included there (with your settings migrated automatically). This stand-alone version is no longer updated.

Adds the entry **"Abschliessen"** (lock) to the door menu. A door that you
unlocked with the key and then closed again can be locked once more.

Since 1.1.0, an optional setting (off by default) also lets you lock doors
that have no keyhole, and bots can no longer get through a door you locked.
Since 1.1.1, locking sounds like unlocking and uses the same hand animation.

## How this mod was made (AI disclosure)

This mod was **programmed by Claude**, an AI assistant made by Anthropic.
**Kaan was the tester**: Kaan has no programming background, played with the mod
on SPT 4.1.6, reported what went wrong, and Claude fixed it. No human programmer
has reviewed the code. It is open source so anyone can check it — if you find a
bug, please open an [issue](https://github.com/Kaanori/spt-tuerabschliessen/issues).

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.

## Installation

1. Download `TuerAbschliessen-1.1.1.zip` from the [Releases](https://github.com/Kaanori/spt-tuerabschliessen/releases) page.
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
| **Bots duerfen eintreten** (bots may kick in) | off | Bots only open doors normally |
| **Auch Tueren ohne Schloss abschliessen** (also lock doors without a lock) | **off** | On: doors without a keyhole can be locked too (1.1.0) |
| **Bots respektieren abgeschlossene Tueren** (bots respect locked doors) | on | No bot can unlock, open or kick in a door you locked (1.1.0) |
| **Beschriftung Aufschliessen** (unlock label) | `Aufschliessen` | Menu text for unlocking a lockless door you locked |
| **Protokoll** (log) | off | Write every action, and every refused bot attempt with its caller, to the BepInEx console |

## How it works

The game can lock doors — it just does not show the button.
`EFT.Interactive.WorldInteractiveObject.Lock()` is fully built in, as is
`EInteractionType.Lock`. Only the menu that
`EFT.InteractionContextHelper.GetAvailableActions(GamePlayerOwner, Door)`
assembles knows just `OpenDoor`, `CloseDoor`, `Breach` and three greyed-out
tactical entries. A Harmony postfix appends our entry.

## Two pitfalls (for later)

**1. `Door.Interact(..., EInteractionType.Lock)` goes nowhere out of the box.**
The base class handles `Lock`, but `Door` overrides `Interact` and its
switch only knows Open, Close, Unlock, Breach. Up to 1.1.0 the mod hence
called `door.Lock()` directly (silent, no hand). Since 1.1.1 a prefix adds
exactly this case, for our own interaction only (see below).

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

## Doors without a lock (1.1.0, src/Abgeschlossen.cs)

Requested by a user on sp-mod.com. With **"Auch Tueren ohne Schloss
abschliessen"** (off by default) the lock entry also appears on doors without
a `KeyId`. Such a door opens again via the entry **"Aufschliessen"** (unlock)
or by kicking it in.

Why an entry of its own: the game offers nothing to unlock a locked door
without a `KeyId` — `InteractionContextHelper.TryAddUnlock` returns at once
when `KeyId` is empty, only "Breach" is left. "Aufschliessen" calls the
game's own `WorldInteractiveObject.Unlock()` (state back to Shut through the
`DoorState` setter). It only shows up on doors you locked yourself
(remembered per raid).

Never lockable without a lock: keycard doors (`KeycardDoor`), `DoorSwitch`,
doors the game itself locks without a key (`InitialDoorState` Locked: power,
quest and breach-only doors), doors with a `TriggersMap`, and every door a
script drives or watches (`Switch.Door`, `HandlerInteractable`,
`InteractiveProxy`, cutscenes, the Lighthouse keeper, quest events). Exit
doors (`ExfiltrationDoor`) and car trunks (`Trunk`) are not a `Door` at all.

If you turned **"Schluessel noetig"** off and locked a door with a lock but
without its key, you get "Aufschliessen" there as well.

## Sound and hand animation (1.1.1, src/SchlossBedienung.cs)

Where the unlock sound comes from (`InteractionContextHelper.TryAddUnlock`):
`Player.StartInteraction(door, UnlockResult, ...)` -> `DoorInteractState`
(hand to the lock, key into the hand after 1 s) -> `Door.Interact(Unlock)`
-> `Unlock()` -> `UnlockCoroutine` -> **`LockHandle.OpenCoroutine()`**. The
door's lock cylinder (`DoorHandle LockHandle`) turns and plays `DownSound`
(at once, group Environment) and `UpSound` (halfway through `OpenAnimation`,
group InteractiveObjects), both rolloff 14, volume 0.8. So the sound belongs
to the **door**, not to the key. The key only adds its jingle when it is
taken into the hand (`Player.SpawnInHands`, `GUISounds` "pickup").

Locking and unlocking now take the same path through
`Player.StartInteraction`:

- **Lock** with a result of our own (`EInteractionType.Lock`).
  `Door.GetInteractionParameters` returns the values for Locked (key
  animation `DoorKeyOpenInteraction`, lock grip), and the key goes into the
  hand after 1 s. When the hand reaches the lock, the cylinder turns as on
  unlocking (`LockHandle.OpenCoroutine`, same sound), then `Lock()` runs
  through the `DoorState` setter.
- **Aufschliessen** (unlock; your door without a lock or without its key)
  with an `UnlockResult` without a key — exactly what the game builds itself
  in `WorldInteractiveObject.Interact(Unlock)`. From there on everything
  runs like a normal unlock, just without a key in the hand.
- **Doors without a sounding cylinder** (nearly all doors without a lock):
  the game is silent there too when unlocking. We play the sounds of the
  map's most common cylinder instead — same groups, rolloff, volume and
  timing as `DoorHandle.OpenCoroutine`, at the door handle.

## Bots stay out (1.1.0)

No bot can unlock, open or kick in a door you locked (with or without a
lock) until the raid ends or you open it yourself.

- **Vanilla** bots never touch locked doors: `BotDoorOpener.InteractionWithDoor`
  and `WantPassTheDoor` only handle Shut and Open. **SAIN** likewise
  (`DoorOpener.InteractWithDoor`).
- **ORBIT** breaks locked doors open for PMCs, bypassing the game's rules:
  `Unlock()` directly, `UnlockCoroutine()` directly (card readers),
  `DoorState = Open` plus `CurrentAngle` directly (SnapDoorOpen, DoorWatch,
  stuck handling), `LockForInteraction` + `Interact(Open)`.
- So the guard sits on the door itself: the `Door.DoorState` setter,
  `SetDoorState`, `Unlock`, `KickOpen` and the `CurrentAngle` setter (also
  `SlidingDoor`). Only a short permit lets a change through, and only YOUR
  player gets one (`MovementContext.StartInteraction` / `StartDoorBreak`
  with `IsYourPlayer`), or choosing "Aufschliessen".
- ORBIT additionally gets a "no" from its own check
  `MultiStepAccess.CanForceUnlock` and treats the door like a power-locked
  one: no route is planned through it.
- **AI navigation:** `Lock()` fires `OnDoorStateChanged`; DrakiaXYZ-Waypoints
  then switches on the door's `Carver_Closed`, so the navmesh is closed at
  the door and bots route around it.

## Known limitations

- **Keycard doors (KeycardDoor) are excluded.** They have their own menu
  method and a different lock.
- **Locking now takes as long as unlocking** (a good second with the hand
  at the lock). If you are interrupted, the door stays unlocked, as in the
  game.
- **Experience for unlocking:** the game grants "door unlocked" experience
  when you unlock a locked door — also one you locked yourself.
- **Closed doors only.** Close an open door first.
- **Kicked-in doors are broken.** `KickOpen` sets `IsBroken = true`;
  afterwards they can be neither closed nor locked. If you want to stay
  quiet, use the key.
- **While you kick or unlock it yourself** (a second or two) the door holds
  a permit; a bot acting in exactly that moment would get through.
- **Without DrakiaXYZ-Waypoints** the navmesh is not closed at a locked door.
  Bots may walk up to it and have to re-route — they still cannot open it.

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
