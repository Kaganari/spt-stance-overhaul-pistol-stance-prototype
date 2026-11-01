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

- [ ] **Reload while retaining**
  - Currently allowed and done by an "invisible" left hand (it stays on the rifle copy while the reload
    animation plays).
  - For now: **block** reloading while the rifle is retained.
  - Do it through a single, replaceable decision point (e.g. a reload policy the retention system
    consults), so another behavior can be added later without touching the rest: drop the rifle and
    reload, temporarily release the hand, a one-handed reload animation, etc.

- [ ] **Tune the raise timing, then remove the temporary sliders**
  - Rifle Raising Speed / Rifle Raising Final Speed / Rifle Raising Hold Point.
  - Bake the chosen values in as defaults/constants.

## Low priority

- [ ] **Clean up weapon lights / lasers on the retained copy** — they work now, but are a bit janky.
- [ ] **Other players / Fika** see the stock rifle-on-back (retention is first-person only).
- [ ] **Update `handoff.txt`** for the new `RifleRetention/` folder layout.
