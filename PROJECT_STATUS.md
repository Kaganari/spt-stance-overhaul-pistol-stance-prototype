# Rifle Retention — project status

The current state of the Rifle Retention work: what it is and how the code is laid out. What is done and
what comes next is tracked in `TODO.md`.

Related files:
- `TODO.md`: the to-do list (cleanup, high and low priority), with done items marked.
- `docs/testing/*.md`: in-game test checklists.

---

## 1. Project and repo

- **Mod:** SPT Stance Overhaul, a BepInEx client mod for SPT 4.1.x (single-player Tarkov).
  - It reworks weapon stances procedurally by injecting offsets into the game's hands springs.
  - It depends on **RealismCommonLib** (separate plugin; provides `PlayerStateInstance`,
    `WeaponStateInstance` and the input/event buses).
- **Architecture doc:** `docs/STANCE.md`.
  - `StanceController` owns the `ISubSystem` instances.
  - `StanceState` is a dual-slot, curve-driven state machine.
  - Stance curves live in `src/Resources/curves.json`.
- **Remotes:**
  - `origin` is the user's fork: https://github.com/Kaganari/spt-stance-overhaul-pistol-stance-prototype
  - `upstream` is space-commits/spt-stance-overhaul. **Never push to upstream.**
- **Branch:** `rifle-retention-prototype`, pushed and up to date with `origin` as of `2091cc1`.
- **Commit identity:** `Kaganari` / `31786738+Kaganari@users.noreply.github.com`, the noreply address only.

### Working rules

- Build a plug-and-play release into `release/`. Never deploy into a working SPT install.
- Don't commit or push unless explicitly told to.
- When asked for analysis or options, don't change code until told to go ahead.
- The user tests in game and sends BepInEx logs; the assistant can't run the game.
- Work one fix at a time. Group fixes only when they share a cause.
- Commit messages say what changed or what was fixed. Never mention local work-plan numbering.
  End them with the Co-Authored-By line.

### Build

```
dotnet build spt-stance-overhaul.csproj -c Release
```

- Two local, untracked files set this machine up: `Directory.Build.props` sets `SptPath`,
  `RealismCommonLibDll` and `SkipDeploy=true`; `Directory.Build.targets` uses them to retarget the
  csproj's hard-coded `F:\SP EFT\SPT-41X` reference paths. MSBuild picks both up automatically, for builds
  and the IDE (VS Code), and a plain build never deploys.
- Then copy `bin/Release/netstandard2.1/spt-stance-overhaul.dll` and `curves.json` to
  `release/BepInEx/plugins/Realism/`.
- Never build with `SkipDeploy=false`: the build would copy itself into the SPT install.
- The game log is at `G:\Games\EscapeFromTarkov_4.1.16\BepInEx\LogOutput.log`. With Debug Logging on,
  retention lines go to the `StanceOverhaul.Retention` source.
- Both `Directory.Build.*` files are excluded via `.git/info/exclude`. A fresh clone or worktree needs
  them recreated.

---

## 2. The feature

Rifle Retention is opt-in. It has its own config section, "14. Rifle Retention (Prototype)", and its own
keybind (default None; the user binds it to 6).

1. **Lowering:** with a rifle drawn and idle, press the key.
   - The holstered pistol is drawn one-handed.
   - A visual copy of the rifle stays in the left hand and drops into the retained pose (originally the Patrol stance's end pose).
   - There is no "put the rifle away" animation.
2. **Raising:** press the key again.
   - The pistol is holstered and the copy rises.
   - The real rifle comes back hidden, with no draw animation. Once it has settled it replaces the copy
     during the rise and finishes the rise in the hands.

While the rifle is retained:
- Two-handed weapon actions on the pistol are blocked, with a "Can't … one-handed" hint.
- Pressing another weapon key cancels retention and lets the game switch weapons, unless a swap is still
  running. In that case the key is ignored, as in vanilla.

---

## 3. Code layout

### `src/SubSystem/RifleRetention/` — the feature (~1910 lines)

| File | Role |
|---|---|
| `RifleRetentionSystem.cs` (629) | The `ISubSystem`. It owns the state machine (Idle → Lowering → Retained → Raising → Rising → Idle), the key and weapon-key handlers, the slot swaps, the action-policy table, and the static entry points the patches call. Every exit path goes through `EndRetention(reason)`. |
| `RetainedRifleCopy.cs` (371) | The visual-only rifle model. Spawn (colliders and animator off), alignment to the held rifle's weapon bone, left-hand grip choice and IK/fingers, arm-reach clamp, FOV squash mirror, and Destroy, which returns the model to the pool cleanly. |
| `RiflePoseSolver.cs` (113) | Computes where the rifle sits for any stance offset. It snapshots the hands-spring state at keypress, minus the current stance, and replays the game's spring math with a target offset passed in. It is re-evaluated every frame, so the sliders apply live. It knows nothing about stances. |
| `RifleDismissal.cs` (83) | Putting the retained rifle away on a weapon key: to `RifleAwayPose`, hide, let go. One instance per dismissal. |
| `RetainedRiflePose.cs` (22) | The retained pose itself: retention's own base position/rotation (taken from Patrol's end values) plus the tweak sliders. Retuning a stance doesn't move it. |
| `SwapAnimationSkipper.cs` (91) | Skips the put-away animation (on lowering) and the draw animation (on raising) for the one armed rifle, each within a time window (1.5 s and 4 s). The draw skip is deferred to the next Update and fires `OnAddAmmoInChamber` + `FastForward`. |
| `RetentionActionPolicy.cs` (60) | `ERetentionAction`, `IRetentionActionPolicy` and `BlockActionPolicy` (shows the hint and blocks). Each action has its own policy entry, so its behavior can change later without touching the rest. |
| `RetentionSpace.cs` (56) | The reference space for every pose: the camera's parent, not `Cam`, so freelook and recoil don't move the copy. |
| `HeldWeaponHider.cs` (57) | Hides the real weapon's renderers and lasers while the copy stands in. Everything hidden **must** be restored, because weapon models are pooled. |
| `RetainedRifleMotion.cs` (206) | The rifle's own procedural motion while retained: its own hand and sway springs, continued from the rifle at keypress and driven by the game's processors from the body's movement. |
| `RetentionTravel.cs` (38) | How far along each axis is, from the four `retention_` curves in `curves.json`, read live so the curve editor (F6, set in Realism Common Lib) works in game. |
| `LeftArmIK.cs` (19) | Shared accessor for the left arm IK limb (`Player._limbs[0]`). |
| `RifleRetentionDebug.cs` (139) | Logging gated by the Debug Logging toggle, with `T+<sec> f<frame>` timestamps since the last keypress and a per-frame trace. `Warn` always logs. |

### `src/Patches/RifleRetentionPatches.cs` (241) — Harmony patches, registered in `src/Plugin.cs`

| Patch | Target | Purpose |
|---|---|---|
| `RetentionLeftHandIKPatch` | prefix `Player.IkProcess` | Keeps the left arm IK weight at 1 while the copy is shown. Otherwise the swap animations' LEFT_HAND_WEIGHT curve switches it off. |
| `RetentionProcessEffectorsPatch` | postfix `ProceduralWeaponAnimation.ProcessEffectors` | Steps the rifle's own springs in the same fixed steps as the game's. |
| `RetentionSkipRifleRemovePatch` | postfix `FirearmController.Remove.Start` | Runs `FastForward` on the armed rifle's put-away. |
| `RetentionSkipRifleSpawnPatch` | postfix `SpawnOperation.Start` | Hides the armed rifle right away and queues the draw skip. |
| `RetentionWeaponAppearedPatch` | postfix `SpawnOperation.WeaponAppeared` | Marks a real draw completion. Lowering → Retained waits on it, because RealismCommonLib's `WeaponIsReady` can be stale. |
| `RetentionWeaponActionInputPatch` | prefix `FirearmHandsInputTranslator.TranslateCommand` | Maps keybinds to `ERetentionAction`s and asks the policy table whether to allow them. |
| `RetentionReloadPatch` | prefix `FirearmController.CanStartReload` | Covers reloads that don't come from a keybind, such as inventory drag-and-drop. |
| `RetentionFovApplyPatch` / `RetentionFovResetPatch` | postfix `FirstPersonStrategy.Apply/ResetFovAdjustments` | Mirrors EFT's first-person FOV squash onto the copy, so the hand isn't drawn short of the handguard. |

### Shared files touched (~170 lines total)

- **`StanceController`:** registers the subsystem and exposes `IsRetainingRifle`.
  - `SetStanceForRetention(stance)` puts the returning rifle in the stance it was lowered from (or at
    rest) with no animation.
  - `HoldAtStanceOffset(...)` holds it on the copy's path, called from `ZeroAdjustmentsPatch` through
    `RifleRetentionSystem.OnStanceSpringsSet`. Pinned while hidden, free after the swap so it settles.
  - `AdoptStanceAfterRetention` hands that stance to the input handler at the end of the raise.
- **`StanceInputHandler`:** `AdoptStance()`, the only retention entry point into stance intent.
- **`StanceState`:** `ClearImmediate()`, `EnterAtPoseImmediate()`, `IsHoldingAtPose()`.
- **`PluginConfig`:** section 14, which has these entries:
  - Enable, Keybind
  - Retained Pose Tweak position/rotation X/Y/Z
  - Rifle Lowering Speed
  - four temporary raise-tuning sliders: Raise Speed, Raise Start Delay, Raise Swap Delay, Raise Copy Limit
  - Keep Rifle Within Arm Reach (advanced)
  - Debug Logging (advanced)
- **`StanceInputEvents` / `StanceInputListener`:** the `ToggleRifleRetention` event and key.
- **`LeftHandAnimator`:** yields the left hand while retaining.
- **`Plugin`:** patch registration.

---

## 4. How it works (key mechanisms)

- **The copy:** EFT holds one item in hands, so the real rifle can't stay in hands. Instead:
  - A model is created with `ObjectsFactory.CreateItem(rifle, false)`.
  - Its colliders are restored before `ReturnToPool`.
  - It is aligned so that its `weapon` bone matches the held rifle's `HandsContainer.Weapon` bone at
    keypress.
- **Left hand:**
  - `LeftHandInteractionTarget` is set to the copy's left `GripPose`, with `ThirdIkWeight = 1` every frame.
  - Fingers are posed via `HandPosers[0].MapIkPalm`.
  - The grip is chosen like `Player.UpdateFirstPersonGrip`: only active, left-hand grips; underbarrel
    grips first, then the deepest one.
- **Swaps:**
  - They go through `player.SetSlotItem(slot, cb)` with `FastSlotSelection = false`. This deliberately
    avoids quickdraw, which costs arm stamina.
  - The skips are armed right before each slot request.
- **Busy rifle:** the key is ignored unless the rifle is drawn and idle (`IsDrawnAndIdle`), which is the
  same check raising uses. Pressing the key while aiming does nothing, and that is accepted.
- **Raising handover:**
  - The rifle starts rising after a delay, at one speed for the whole rise, so the pistol is away first.
  - The hidden real rifle follows the copy's path through a stance offset.
  - It is marked ready after 2 frames drawn and idle (~0.62 s). A ~5 mm step and a barrel roll of ~4 then
    ~6 degrees follow for ~0.3 s, so the swap waits that long, hidden.
  - The swap happens on the move, where what still differs can't be seen. The springs are freed there.
- **Game frame order** (`Player.VisualPass`):
  `ProcessEffectors` (springs, camera) → `ShiftWeaponRoot` → `RotateHead` → `IkProcess` → `AdjustElbows`
  → `IkApply` → `LateTransformations`. The FOV coroutine runs later in LateUpdate.

### Findings worth remembering

- On the first frame after `FastForward`, the weapon bone reports a junk pose. That is why the swap is
  gated on drawn + idle. Drawn + idle does not mean at rest: `IsIdling` only reads one animator layer.
- The pistol's default stance (PistolCompress) exits with an animation that carries onto the rifle and
  causes drift. That is why the springs stay pinned until the swap.
- Lasers (`LaserBeam`) draw themselves with `Graphics.DrawMesh`, so hiding renderers does not hide them.
- The "hand holding air short of the handguard" bug was not an IK error. It was EFT's FOV compensation
  squashing the arms but not the copy.
- **Weapon keys:**
  - The game itself ignores weapon keys while `InventoryController.IsChangingWeapon`.
  - RealismCommonLib's `WeaponSwapInput` fires in a prefix, before the game's own handling, so retention
    has to check that flag itself.
- **Game behavior relied on:**
  - On a revolver, ChamberUnload rolls the cylinder.
  - With a feed malfunction, UnloadMagazine examines the malfunction.
  - A vanilla draw chambers a round if the chamber is empty.