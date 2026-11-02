# Test checklist — Rifle Retention: blocking two-handed actions

While the rifle is retained (lowering, holding, and raising), actions whose animations need the left hand
are refused with an on-screen hint ("Can't … one-handed"). Once the rifle is back up, or
retention was cancelled, they must work normally again. Swap behavior itself has its own checklist:
[rifle-retention-swap.md](rifle-retention-swap.md).

**How to use:** tick what passes; for anything that fails, note the action, how it was triggered
(key / inventory / context menu), the phase, and the weapon. With **Debug Logging** on, each blocked action
also logs `<Action> blocked on <weapon>`.

## Setup

- [x] Rifle in a primary slot, pistol in the holster.
- [x] Spare pistol magazines in the rig, plus **one test with no spare magazines** (only loose ammo).
- [x] A revolver (for the cylinder reload tests).
- [x] A way to get a malfunction: a pistol with low durability and/or bad ammo, firing until it jams.
- [x] Your keybinds for check ammo, check chamber, unload chamber, inspect, fire mode, and check fire mode.

## 1. Blocked while holding the rifle

For each action: nothing happens (no animation starts, the left hand stays on the rifle), the hint appears,
and nothing changes about the ammo.

| ✓ | Action | Trigger | Expected hint |
|---|---|---|---|
| [x] | Reload | Reload key | "Can't reload …" |
| [x] | Quick reload | Quick reload (double tap) | "Can't reload …" |
| [x] | Reload with no spare magazine | Reload key (would chamber a loose round) | "Can't reload …" |
| [x] | Reload, empty magazine | Reload key with the magazine empty | "Can't reload …" |
| [x] | Reload by drag-and-drop | Inventory: drag a magazine onto the pistol in hands | Refused (hint may show) |
| [x] | Load ammo by drag-and-drop | Inventory: drag loose ammo onto the pistol | Refused (hint may show) |
| [x] | Check magazine | Check ammo key | "Can't check the magazine …" |
| [x] | Check chamber | Check chamber key | "Can't check the chamber …" |
| [ ] | Unload magazine | Unload magazine key (magazine goes into the rig) | "Can't unload the magazine …" |
| [x] | Unload chamber | Unload chamber key | "Can't unload the chamber …" |
| [x] | Inspect weapon | Inspect key | "Can't inspect the weapon …" |

- [x] Pressing the same blocked key repeatedly doesn't stack or flood notifications.

## 2. Revolver (as the holstered pistol)

- [x] Reload key → blocked.
- [x] Drag loose rounds onto the revolver → refused.
- [ ] Unload chamber key on a revolver: this is the game's "roll cylinder" action → blocked ("Can't unload the chamber …").
- [-] Roll cylinder / chamber-related key → note what happens (not explicitly blocked). - not tested, no idea how, need more clarifications

## 3. Malfunctions

Get the pistol to malfunction **while holding the rifle**.

- [x] Firing again with the malfunction: note what happens (the trigger isn't blocked).
- [x] Inspect key → blocked ("Can't inspect the weapon …").
- [x] Fire mode key while malfunctioned → blocked ("Can't deal with the malfunction …"), no examine
  animation.
- [x] Check fire mode / force auto key while malfunctioned → same as above.
- [x] Check chamber key with an **identified** malfunction → blocked ("Can't deal with the malfunction …"),
  no repair.
- [x] Check chamber key with an **unidentified** malfunction → blocked.
- [x] Reload key while malfunctioned → blocked.
- [x] Raise the rifle, switch to the pistol normally (not via retention): the malfunction can now be
  inspected and cleared as usual.

## 4. Every retention phase

Repeat a few of the actions above (reload, check magazine, inspect) during:

- [x] **Lowering** (right after the keypress, while the pistol is being drawn) → blocked.
- [x] **Holding** → blocked (already covered above).
- [x] **Raising** (while the pistol holsters and the rifle comes up) → blocked, including in the last
  moment before the real rifle appears.

## 5. Not blocked — must still work while holding the rifle

- [x] Fire the pistol (hip and aimed).
- [x] Aim down sights, change scope/sight mode.
- [x] Switch fire mode (no malfunction) — works normally.
- [x] Check fire mode (no malfunction) — note what happens (not blocked).
- [x] Toggle the pistol's light/laser, if it has one.
- [x] Stance keys.
- [ ] Pistol drawn with an empty chamber and a loaded magazine: the draw chambers a round (vanilla draw) → note how it looks.
- [x] Throw a grenade / use meds: these swap weapons, so retention should end cleanly (see the swap
  checklist, section 5). Note anything odd.

## 6. Unblocked again afterwards

For each, all actions from section 1 work normally again (no hint):

- [x] After raising the rifle back (with the rifle).
- [x] After raising the rifle, then switching to the pistol normally.
- [x] After cancelling retention with a manual weapon swap.
- [x] After cancelling retention by dropping the rifle.
- [x] After death / extraction, in the next raid.

## 7. Not affecting anyone else

- [x] Bots near you reload normally while you are holding the rifle.
- [x] Fika (if used): other players' reloads aren't affected.

## 8. Logs

- [x] No errors in `LogOutput.log` (search for `Exception`, `StanceOverhaul`, `Retention`).
- [x] With Debug Logging on: every blocked action has a matching `blocked on` line, and nothing is logged as
  blocked when retention isn't active.

Notes:
Pressing 6 with the unchambered but with loaded mag pistol chambers the pistol, which is, technically, a 2 hand action. Need to block it as well? Not sure