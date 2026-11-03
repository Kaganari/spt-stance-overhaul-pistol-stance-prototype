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

- A local, untracked `Directory.Build.props` sets `SptPath`, `RealismCommonLibDll`, `SkipDeploy=true` and
  imports `spt-paths.targets`. That retargets the csproj's hard-coded `F:\SP EFT\SPT-41X` reference paths
  to this machine, for both builds and the IDE (VS Code), and keeps a plain build from deploying.
- Then copy `bin/Release/netstandard2.1/spt-stance-overhaul.dll` and `curves.json` to
  `release/BepInEx/plugins/Realism/`.
- Never build with `SkipDeploy=false`: the build would copy itself into the SPT install.
- The game log is at `G:\Games\EscapeFromTarkov_4.1.16\BepInEx\LogOutput.log`. With Debug Logging on,
  retention lines go to the `StanceOverhaul.Retention` source.
- `Directory.Build.props`, `spt-paths.targets` and `mod-compile-command.txt` are local helpers. They stay
  untracked.

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
   - The real rifle comes back with no draw animation, and the copy hands over to it seamlessly.

While the rifle is retained:
- Two-handed weapon actions on the pistol are blocked, with a "Can't … one-handed" hint.
- Pressing another weapon key cancels retention and lets the game switch weapons, unless a swap is still
  running. In that case the key is ignored, as in vanilla.

---

## 3. Code layout

### `src/SubSystem/RifleRetention/` — the feature (~1330 lines)

| File | Role |
|---|---|
| `RifleRetentionSystem.cs` (519) | The `ISubSystem`. It owns the state machine (Idle → Lowering → Retained → Raising → Idle), the key and weapon-key handlers, the slot swaps, the action-policy table, and the static entry points the patches call. Every exit path goes through `EndRetention(reason)`. |
| `RetainedRifleCopy.cs` (318) | The visual-only rifle model. Spawn (colliders and animator off), alignment to the held rifle's weapon bone, left-hand grip choice and IK/fingers, arm-reach clamp, FOV squash mirror, and Destroy, which returns the model to the pool cleanly. |
| `LoweredPoseSolver.cs` (106) | Computes the lowered pose. It snapshots the hands-spring state at keypress, minus the current stance, and replays the game's spring math with a target offset passed in. It is re-evaluated every frame, so the sliders apply live. It knows nothing about stances. |
| `RetainedRiflePose.cs` (22) | The retained pose itself: retention's own base position/rotation (taken from Patrol's end values) plus the tweak sliders. Retuning a stance doesn't move it. |
| `SwapAnimationSkipper.cs` (91) | Skips the put-away animation (on lowering) and the draw animation (on raising) for the one armed rifle, each within a time window (1.5 s and 4 s). The draw skip is deferred to the next Update and fires `OnAddAmmoInChamber` + `FastForward`. |
| `RetentionActionPolicy.cs` (60) | `ERetentionAction`, `IRetentionActionPolicy` and `BlockActionPolicy` (shows the hint and blocks). Each action has its own policy entry, so its behavior can change later without touching the rest. |
| `RetentionSpace.cs` (47) | The reference space for every pose: the camera's parent, not `Cam`, so freelook and recoil don't move the copy. |
| `HeldWeaponHider.cs` (41) | Hides the real weapon's renderers while the copy stands in. Everything hidden **must** be restored, because weapon models are pooled. |
| `LeftArmIK.cs` (18) | Shared accessor for the left arm IK limb (`Player._limbs[0]`). |
| `RifleRetentionDebug.cs` (130) | Logging gated by the Debug Logging toggle, with `T+<sec> f<frame>` timestamps since the last keypress and a per-frame trace. `Warn` always logs. |

### `src/Patches/RifleRetentionPatches.cs` (216) — Harmony patches, registered in `src/Plugin.cs`

| Patch | Target | Purpose |
|---|---|---|
| `RetentionLeftHandIKPatch` | prefix `Player.IkProcess` | Keeps the left arm IK weight at 1 while the copy is shown. Otherwise the swap animations' LEFT_HAND_WEIGHT curve switches it off. |
| `RetentionSkipRifleRemovePatch` | postfix `FirearmController.Remove.Start` | Runs `FastForward` on the armed rifle's put-away. |
| `RetentionSkipRifleSpawnPatch` | postfix `SpawnOperation.Start` | Hides the armed rifle right away and queues the draw skip. |
| `RetentionWeaponAppearedPatch` | postfix `SpawnOperation.WeaponAppeared` | Marks a real draw completion. Lowering → Retained waits on it, because RealismCommonLib's `WeaponIsReady` can be stale. |
| `RetentionWeaponActionInputPatch` | prefix `FirearmHandsInputTranslator.TranslateCommand` | Maps keybinds to `ERetentionAction`s and asks the policy table whether to allow them. |
| `RetentionReloadPatch` | prefix `FirearmController.CanStartReload` | Covers reloads that don't come from a keybind, such as inventory drag-and-drop. |
| `RetentionFovApplyPatch` / `RetentionFovResetPatch` | postfix `FirstPersonStrategy.Apply/ResetFovAdjustments` | Mirrors EFT's first-person FOV squash onto the copy, so the hand isn't drawn short of the handguard. |

### Shared files touched (~170 lines total)

- **`StanceController`:** registers the subsystem and exposes `IsRetainingRifle`.
  - `SnapStanceForRetention(stance)` holds the hidden rifle in the stance it was lowered from (or at rest)
    while it rises, with the springs snapped. `AdoptStanceAfterRetention` hands that stance to the input
    handler at the reveal.
- **`StanceInputHandler`:** `AdoptStance()`, the only retention entry point into stance intent.
- **`StanceState`:** `ClearImmediate()`, `EnterAtPoseImmediate()`, `IsHoldingAtPose()`.
- **`PluginConfig`:** section 14, which has these entries:
  - Enable, Keybind
  - Patrol Pose Tweak position/rotation X/Y/Z
  - Rifle Lowering Speed
  - three temporary raise-tuning sliders: Raising Speed, Raising Final Speed, Raising Hold Point
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
  - The copy rises to a hold point while the pistol holsters.
  - Once the real rifle has been drawn and idle for 2 frames, the copy retargets smoothly onto the real
    rifle's live pose.
  - The copy is revealed when within 4 mm / 1°, or after a 0.3 s timeout.
- **Game frame order** (`Player.VisualPass`):
  `ProcessEffectors` (springs, camera) → `ShiftWeaponRoot` → `RotateHead` → `IkProcess` → `AdjustElbows`
  → `IkApply` → `LateTransformations`. The FOV coroutine runs later in LateUpdate.

### Findings worth remembering

- On the first frame after `FastForward`, the weapon bone reports a junk pose. That is why the reveal is
  gated on drawn + idle.
- The pistol's default stance (PistolCompress) exits with an animation that carries onto the rifle and
  causes drift. That is what `SnapStanceForRetention` exists for.
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