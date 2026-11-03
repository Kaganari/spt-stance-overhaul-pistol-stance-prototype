# Rifle Retention — TODO

✅ done, 🔴 open, 🟡 waiting on something outside this mod. Fixes are done one at a time and tested in
game before the next one. They are only grouped when they share a cause. Items marked *needs a log* need
one swap captured with Debug Logging on before any code changes. Many items come from test runs of the
checklists in `docs/testing/`.

## Code cleanup

- ✅ Split the prototype into the `RifleRetention/` subsystem folder (no behavior change).
- ✅ Tidy the wiring: the IK patch asks the system directly, one shared left-arm IK accessor, the
  missing-grip warning always logs, `Cleanup` renamed to `EndRetention`.
- ✅ Move the swap animation skipping into `SwapAnimationSkipper`, with per-instance state instead of
  static fields.
- 🔴 Restructure into a self-contained feature module, on a separate branch.

## High priority

- ✅ **Give the retained rifle its own pose, independent of the Patrol stance**
  - It used to read the Patrol stance's curves, so retuning Patrol would have moved the retained rifle.
  - Now `RetainedRiflePose` holds retention's own base values (Patrol's end values at the time) plus the
    tweak sliders, and `LoweredPoseSolver` takes the target as input instead of naming a stance.

- ✅ **Same retained pose from any stance**
  - Tested: lowering from the default stance and from other stances gives the same retained pose.
    `Capture` subtracts the current stance's spring contribution, so nothing needed fixing.

- ✅ **Return to the previous stance when raising the rifle**
  - The copy already rose back into the old stance, but the real rifle was drawn into the default one:
    while raising, the hidden rifle was snapped to rest every frame.
  - Now the stance at keypress is remembered. While raising, the hidden rifle is held in that stance
    (no enter animation), and at the reveal it becomes the stance input handler's chosen stance, so it
    sticks.
  - Normal weapon switching is unchanged: it still resets to the default stance. A cancelled retention
    doesn't restore the stance either.

- ✅ **Putting the retained rifle away: animate it instead of dropping it in one frame**
  - Was: a weapon key while retained (quick-draw, or a primary weapon key) removed the copy in one frame
    and the left hand teleported onto the pistol.
  - Now: the left hand takes the rifle down, twisted and a bit to the left, out of view
    (`RifleAwayPose`, a fixed pose so it always ends out of view whatever the retained pose is tuned to).
    The rifle is hidden there, then the hand lets go and comes back onto the pistol. The rifle's own
    Holster sound plays as it starts going away, its HandOff when the hand lets go.
  - Error exits are still instant. Tuning is part of the "Finish tuning" item below.
  - Raising plays only the pistol's holster sounds (the rifle's draw is skipped, so its Draw/HandOn
    don't fire). Tested: it sounds fine, kept as is.

- 🟡 **Normal pistol key should also go fully to the pistol** — *waiting on a RealismCommonLib change*
  - Now: the normal pistol key does nothing while the rifle is retained; quick-draw goes fully to the
    pistol. Both should do the same, through an intent per key, so they can be given different behaviors
    later with a one-line change.
  - Why (from the decompiled lib): RealismCommonLib raises `InputEvents.WeaponSwapInput` for
    `SelectFirstPrimaryWeapon`, `SelectSecondPrimaryWeapon` and `QuickSelectSecondaryWeapon`, but not
    for `SelectSecondaryWeapon` (the normal pistol key).
    - Quick-draw reaches retention's `OnManualSwap`, which cancels retention: that is the instant drop.
      The game itself does nothing, the pistol is already in hands.
    - The normal pistol key never reaches retention, and the game ignores it for the same reason.
  - **Lib changes needed first** (no mod changes for this until they are released):
    - A new weapon-swap input event that says which key was pressed (the `ECommand`, or the target
      slot). `WeaponSwapInput` has no parameter, so a consumer can't tell a pistol key from a primary
      weapon key. A new event, not a changed signature, so other consumers of the old one keep working.
    - That event also covers `SelectSecondaryWeapon`. Its absence from the current list looks like an
      omission (with a rifle in hands it is a weapon swap), but may be deliberate, so it's not added to
      the old event.
    - Why in the lib: it reports input for every consumer. Working around it in the mod needs a second
      patch on the same game method that must run before the lib's (Harmony priority) and a "pistol key
      handled this frame" flag, which is fragile.
  - Then in the mod: retention subscribes to the new event. Pistol keys go to the intent table, primary
    keys cancel retention as now; the extra patch and the frame flag aren't needed.
  - Do it after the animation above, so the pistol key doesn't spread the instant disappearance.

- ✅ **Block two-handed weapon actions while retaining**
  - Blocked with an on-screen hint ("Can't … one-handed"):
    - reload (keys, no-spare-mag chambering, inventory drag-and-drop)
    - check magazine, check chamber
    - unload magazine, unload chamber
    - inspect weapon
    - malfunction examine/repair
  - The unload magazine key was found missing in the decompiled code and added later. With a feed
    malfunction, the same key examines the malfunction, so it's mapped to Clear Malfunction then.
  - Each action goes through its own policy (`RetentionActionPolicy.cs`, table in
    `RifleRetentionSystem`), so it can get a different behavior later without touching the rest.
  - Tested in depth with the action-blocking checklist.

- ✅ **Retention key pressed while the rifle is busy**
  - Reports:
    - Mid-reload, mid-inspect or mid-chamber-check: the pistol drew mid-animation and the copy lowered
      from the half-animated pose.
    - During or right after a grenade throw: the gun pointed at the ground until fired.
    - While the rifle was still coming up after a normal switch: the rifle froze for a couple of seconds.
  - Fix: the key is ignored unless the rifle is drawn and idle (the same check raising uses).
  - Pressing the key while aiming does nothing. It never worked before, and that is accepted.

- ✅ **Weapon keys pressed during lowering / raising picked the wrong weapon**
  - The game itself ignores weapon keys while a swap is running (vanilla). Retention still cancelled
    itself on those keys:
    - During lowering: the copy vanished and only the pistol was left.
    - During raising: the rifle played its full draw animation.
  - Fix: weapon keys are ignored while the game is changing weapons, so the swap just finishes.
  - Not done: remembering the pressed slot and switching to it afterwards. It needs more code and more
    states.

- 🔴 **Finish tuning, then bake the values in and clean up the tuning sliders**
  - Three groups of sliders still stand in for final values:
    - Rifle Raising Speed, Rifle Raising Final Speed and Rifle Raising Hold Point (raise timing). They
      are marked TEMP and go away once their values are baked in as constants.
    - Patrol Pose Tweak position/rotation X/Y/Z (retained pose). Their values get added into
      `RetainedRiflePose`'s base. Decide whether they stay as fine-tuning offsets (default 0, renamed),
      or go away too.
    - Away Pose Tweak position/rotation X/Y/Z, Rifle Away Speed and Left Hand Release Time (putting the
      rifle away). Tuning for the end pose of rifle when ,from retained position, switching fully to pistol. 
      Same as above: bake into `RifleAwayPose` and constants. The Preview Away Pose toggle only exists 
      for this tuning and goes away with them.

## Low priority

- 🔴 **Underbarrel launcher selected: the swap away takes longer** — *needs a log*
  - Guess: the game switches out of launcher mode first, or the instant put-away doesn't apply there.

- 🔴 **Retention key pressed while sprinting** — *needs a log*
  - The sprint drops to a run for a moment, and nothing is drawn.
  - Wanted: allow it mid-sprint only if that doesn't add fragility. Otherwise the key should simply do
    nothing.

- 🔴 **MP7 without a foregrip: the thumb goes inside the gun** — *needs a log of lowering it*
  - Likely the grip point picked on that weapon, or the finger posing.

- 🔴 **Revolver hint wording**
  - On a revolver, the unload chamber key rolls the cylinder. It is already blocked, but the hint says
    "Can't unload the chamber". Maybe reword it for revolvers.

- 🔴 **Clean up weapon lights / lasers on the retained copy.** They work now, but are a bit janky.

- 🔴 **Camera and gun twitch at the end of the rifle return** (maybe not worth fixing)
  - Base game behavior: the camera and the gun twitch at the end of every weapon draw, strongest with
    shotguns.
  - In vanilla it happens while the gun is still moving, so only the screen twitch is noticeable. With
    retention the rifle's draw is skipped and the copy has already arrived, so the twitch lands after the
    motion.
  - Options:
    - Keep the copy moving through the twitch and reveal after it, like vanilla.
    - Damp the camera change during the return.
  - Either way, it needs a trace of the camera / Camera_animated motion around the reveal first.

- 🔴 **Faster swap that drains arm stamina.** Maybe make the retention swap quicker and have it cost
  stamina, like the quick draw does.

- 🔴 **Interactions while holding the rifle** (doors, looting, picking up items)
  - The body plays the left-hand interaction motion, but the hand stays on the rifle.
  - Decide what should happen: let the hand go and come back, use the right hand, or block/lower the
    rifle first.

- 🔴 **Pistol draw chambers a round while the rifle is held**
  - A vanilla draw with an empty chamber and a loaded magazine racks the slide, while the left hand
    stays on the rifle.
  - Blocking it would leave the pistol empty, because chambering later counts as a reload, which is
    blocked.
  - Decide whether to keep it.

- 🔴 **Other players / Fika** see the stock rifle-on-back (retention is first-person only).
