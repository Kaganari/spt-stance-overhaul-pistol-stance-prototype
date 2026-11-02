# Test checklist — Rifle Retention: swapping to the pistol and back

Covers lowering the rifle into the left hand while the pistol is drawn, holding it there, and raising it
back. Action blocking (reload etc.) has its own checklist: [rifle-retention-action-blocking.md](rifle-retention-action-blocking.md).

**How to use:** tick what passes; for anything that fails, note the weapon, the step and what you saw. If
something looks off, turn on **Debug Logging** (advanced setting in "14. Rifle Retention (Prototype)"),
repeat it, and keep `BepInEx/LogOutput.log`.

## Setup

- [x] Config: **Enable Rifle Retention** on, **Rifle Retention Keybind** set.
- [x] Loadout: a rifle in primary slot 1, a different one in primary slot 2, a pistol in the holster.
- [x] Have these weapons on hand for the weapon variety section:
  - [x] AR-style rifle with a foregrip
  - [x] Rifle without a foregrip (bare handguard)
  - [x] Rifle with an underbarrel grenade launcher (test with the launcher selected as active, too)
  - [x] Bullpup
  - [x] Pump shotgun
  - [x] Long rifle (SVD / Mosin / M1A)
  - [x] SMG (MP5 / Vector)
  - [x] Rifle with a light and/or laser
  - [x] Rifle with a folding stock (test folded and unfolded)
  - [x] Revolver as the holstered pistol (in addition to a normal pistol)

## 1. When the key should do nothing

- [x] Pistol already in hands → nothing happens.
- [x] No pistol in the holster → nothing happens, rifle stays in hands.
- [x] Melee / grenade / empty hands → nothing happens.
- [x] Feature disabled in config → nothing happens.
- [x] Key pressed during lowering → ignored (no double swap, no stuck state).
- [x] Key pressed during raising → ignored.
- [-] Rifle busy (mid-reload, mid-draw, inspecting, checking chamber) → note what happens; nothing should break or get stuck. - Nothing gets stuck, but the pistol is drawn mid animation and the gun is moving to the side in the last position it was during inspect or reload. Just looks a bit weird, nothing game breaking. Still worth fixing.
- [ ] Key pressed while the rifle is still coming up after a normal weapon switch → note what happens.
- [ ] Key pressed while throwing a grenade, and while the rifle comes back after the throw → note what happens.
- [ ] Key pressed mid-reload → afterwards, check the rifle's magazine and chamber are where they should be.
- [ ] Key pressed with the underbarrel launcher selected → note how long the swap takes.
- [x] While aiming down sights with the rifle → note what happens.

## 2. Lowering (rifle → pistol)

- [x] No "put the rifle away" animation: the pistol draw starts right away.
- [x] The rifle doesn't pop or jump at the start: the copy appears exactly where the rifle was.
- [x] Only one rifle visible at any time (no doubled or ghost rifle).
- [x] The rifle lowers smoothly into the patrol pose, without overshoot or jitter.
- [-] The left hand stays on the handguard the whole way down, with fingers wrapped around it (not open or flat, not floating short of it). - MP7 without foregrip, when switching to stance, looks like the thumb is going inside of the gun. Possibly trying to grip the nonexsitent foregrip
- [x] The pistol draw animation itself plays normally.
- [x] Arm stamina isn't drained (this is a normal swap, not the quick draw). - passing, but put in todo list a low prio point to think about making it a faster and drawing the stamina
- [x] Repeat from each stance: idle, high ready, low ready, short stock, active aim, patrol. Note anything
  odd. Stances other than idle are a known gap.
- [-] Repeat standing, crouched, prone, while walking, and while sprinting. - pressing 6 during sprint briefly changes it to normal running, but nothing is drawn. Not sure what to do here, since sprinting with the pistol drawn is allowed as long as you start sprinting after pressing 6. I'm thinking to allow switching mid-spring IF it doesn't introduce any fragility

## 3. Holding the rifle (pistol in hands)

- [x] Left hand stays on the handguard while standing still.
- [x] Hip-fire the pistol: the rifle copy doesn't move with recoil.
- [x] Aim down the pistol's sights: the rifle stays put, the hand stays on it.
- [x] Fire while aiming: same as above.
- [x] **Freelook** (also while moving): the rifle stays with the body and doesn't follow the camera.
- [-] Turn quickly left and right, and look all the way up and all the way down. - the gun looks to be glued to the screen. Possibly change it to be glued to the body, but keep in mind previous issue with it moving with freelook camera and distorting
- [x] Walk, sprint, crouch, go prone, lean left/right, jump, climb stairs, vault.
- [x] Test at a low FOV (50) and a high FOV (75): the hand is on the handguard at both, not short of it.
- [x] **Patrol Pose Tweak** sliders: changes apply live, and the hand follows the rifle.
- [x] With **Keep Rifle Within Arm Reach** off and an extreme tweak: the hand may stop short (expected).
  Turn it on: the rifle is pulled back into reach.
- [x] Pistol stance keys (high ready etc.) while holding the rifle: note what happens. - the only stance is allowed for the pistols is a patrol one and it's applied fine
- [x] Open and close the inventory: the rifle and hand are fine afterwards.
- [-] Open doors, loot a container, pick up an item: note what the left hand does, and that it goes back
  onto the rifle afterwards. - The body does the same motion as if it was done by the left hand, but the hand never leaves the foregrip. Also not sure what to do, make a note of it in todo
- [x] Light/laser on the rifle copy: works (known to be a bit janky; just note how).
- [x] Hold the rifle for a minute or more: no drift, no flicker.

## 4. Raising (pistol → rifle)

- [x] The pistol holster animation plays (by design).
- [x] The rifle rises while the pistol is being holstered: one smooth motion, no stop and no "double raise".
- [x] No visible pop when the copy is swapped for the real rifle at the end. (A small camera/gun twitch is
  base-game behavior, already in the TODO.)
- [x] There's no rifle draw animation; the rifle is simply up.
- [x] Left hand stays on the handguard the whole way and is on the real rifle's handguard afterwards.
- [x] After the return the rifle sits in its normal idle position, with no slow drift over the next second.
- [x] **Ammo:** a round chambered before lowering is still chambered after raising (no extra or missing
  round in the chamber or magazine). Check with a chamber check.
- [x] **Ammo:** empty chamber with a loaded magazine before lowering → note the chamber state after raising.
- [x] Fire, reload and aim immediately after the return all work.
- [x] **Raise sliders** (Rifle Raising Speed / Final Speed / Hold Point): changes are noticeable and nothing
  breaks at the slider extremes.
- [x] Rifle Lowering Speed only affects lowering.
- [x] Repeat the raise while moving, crouched, and prone.

## 5. Cancelling and interruptions

For each, check: the copy disappears, the left hand is released, and later there's no invisible rifle and
no missing model on the back or in hands.

- [x] Press the slot key for the same rifle while holding it. - plays switching animation by removing everything and bringing up the selected weapon. Fine for now.
- [x] Press the slot key for the other primary while holding it. - plays switching animation by removing everything and bringing up the selected weapon. Fine for now.
- [-] Use the pistol quick-draw key while holding it. - removes the rifle in the left hand instantly. If it's not on todo list - add, also not sure what to do there.
- [-] Use the pistol normal draw key while holding it. - nothing is happening. Weird.
- [x] Take out a grenade / meds / food while holding it. - plays switching animation by removing everything and bringing up the selected
- [-] Each of the above during **lowering** and during **raising** too. - not working, for meds and grenade, which is good. Pressing any weapon button in the process of lowering swithes to the pistol. Doing the same while raising plays switch animation to the currently held rifle, even if I pressed a different weapon
- [x] Drop the held rifle from the inventory while holding it.
- [x] Move the held rifle to the backpack (if allowed).
- [x] Drop or move the pistol while holding the rifle.
- [x] Die while holding the rifle; then check the next raid.
- [x] Extract while holding the rifle; then check the next raid.
- [x] Spam the key quickly many times: no stuck state, and you end up holding exactly one weapon normally.
- [x] Disable the feature in config while holding the rifle: note what happens, then raise.

## 6. Weapon variety

For every weapon from the Setup list: lower, hold (with some pistol fire), and raise.

- [x] Left hand lands on the visible grip point (foregrip, handguard, or launcher grip), not on air or
  inside the model.
- [x] Copy matches the real rifle exactly (attachments, magazine, colors).
- [x] No errors in `LogOutput.log` (search for `Exception`, `StanceOverhaul`, `Retention`).

## 7. Many cycles and stability

- [x] 20+ lower/raise cycles in a row with the same rifle: nothing degrades.
- [x] Alternate between primary 1 and primary 2 across cycles.
- [x] After many cycles: the rifle model is visible when drawn normally, when on the back, and when dropped
  on the ground. (Weapon models are pooled, so hidden pieces would show up here.)
- [x] No FPS drops or hitches on lowering or raising.
- [x] Full raid with regular use: no errors in `LogOutput.log`.

## 8. Known limitations (for reference, not failures)

- Other players / Fika see the stock rifle on your back.
- Entering from a stance other than idle may give an off pose.
- Base-game camera/gun twitch at the end of the return.
- Light/laser on the copy is a bit janky.


Notes:
Switching from the rifle with underbarrel grenade launcher, that is currently selected as active, takes longer
Pressing button (I'm just going to call it 6 in the future since that's what I binded it to for now) while the grenade is being thrown shows weapon pointed to the ground and hand in a weird place. Only visual though, shooting instantly fixes evething.
When normally switching from one rifle to another and while the gun is raising, press 6 quickly - the gun will be visually stuck at whatever point in animation it was during pressing. Not breaking anything functionally, in fact, can even fire from the gun itself, so the pistol draw doesn't happen, but it is also blocked untill the gun visual is returned to normal. Fixes itself after couple of seconds or by switching to anything.