# Rifle Retention — TODO

## High priority

- [ ] **Stance support (retained pose + entry stance)**
  - The lowered pose is hardcoded to the Patrol stance (`PatrolPoseSolver` reads
    `StanceController.PatrolStance` curves + the Patrol Pose Tweak sliders).
  - Make it work with the other stances that already exist in the mod (high/low ready, short stock,
    active aim, ...) and with stances added later, **without changing the retention code each time**:
    resolve the target pose generically from the stance's own definition (`IStance` / curves) instead
    of naming a specific stance.
  - Also handle entering retention from a non-idle stance (offsets are currently relative to whatever
    stance the rifle was in at keypress).

- [ ] **Going fully to the pistol (dropping the retained rifle)**
  - Already allowed, but there is no animation: the rifle copy disappears from the hand and the left
    hand teleports onto the pistol.
  - Needs a transition: the rifle goes away (slung / to the back) and the left hand moves to the pistol
    smoothly.

- [x] **Block two-handed weapon actions while retaining**
  - Blocked with an on-screen hint: reload (keys, no-spare-mag chambering, inventory drag-and-drop),
    check magazine, check chamber, unload chamber, inspect weapon, malfunction examine/repair.
  - Each action goes through its own policy (`RetentionActionPolicy.cs`, table in
    `RifleRetentionSystem`), so it can get a different behavior later without touching the rest.
  - Basic in-game test passed; in-depth test still pending.

- [ ] **Tune the raise timing, then remove the temporary sliders**
  - Rifle Raising Speed / Rifle Raising Final Speed / Rifle Raising Hold Point.
  - Bake the chosen values in as defaults/constants.

## Low priority

- [ ] **Clean up weapon lights / lasers on the retained copy** — they work now, but are a bit janky.
- [ ] **Camera and gun twitch at the end of the rifle return** (maybe not worth fixing)
  - Base game behavior: the camera and the gun twitches at the end of every weapon draw, strongest with shotguns.
  - In vanilla it happens while the gun is still moving, so only the screen twitch is noticeable. With
    retention the rifle's draw is skipped and the copy has already arrived, so it lands after the motion.
  - Options: keep the copy moving through the twitch (reveal after it, like vanilla), or damp the
    camera change during the return. Needs a trace of camera / Camera_animated motion around the reveal first.
- [ ] **Other players / Fika** see the stock rifle-on-back (retention is first-person only).
- [ ] **Update `handoff.txt`** for the new `RifleRetention/` folder layout.
