using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using RealismCommonLib.Events;
using StanceOverhaul.Stances;
using StanceOverhaul.SubSystem.StanceInput;
using System.Collections.Generic;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// PROTOTYPE: rifle-retained pistol transition.
    ///
    /// On keypress a <see cref="RetainedRifleCopy"/> is spawned exactly where the held rifle is, the real
    /// rifle is hidden, its "put away" animation is skipped (<see cref="SwapAnimationSkipper"/>) so the
    /// pistol draw starts immediately, and the copy drops from the held pose to the retained pose
    /// (<see cref="RetainedRiflePose"/>). Returning reverses it: the copy rises while the game draws the
    /// hidden real rifle (draw animation skipped too), held at the copy's pose by a stance offset. Partway
    /// up the copy is swapped for it, while it is moving, and the real rifle finishes the rise in the
    /// hands. A weapon key while retained puts the rifle away instead: the left hand takes the
    /// copy down out of view (<see cref="RifleAwayPose"/>), then lets go and returns to the pistol.
    /// </summary>
    internal class RifleRetentionSystem : ISubSystem
    {
        private enum ERetentionState
        {
            Idle,
            Lowering,   // copy dropping, game swapping rifle -> pistol
            Retained,   // pistol ready, copy held low
            Raising,    // copy rising, game swapping pistol -> rifle
            Rising,     // copy gone, the real rifle finishing the rise in the hands
            Dismissing  // weapon key while retained: see RifleDismissal
        }

        private const float SWAP_TIMEOUT = 5f;

        // Raising: the real rifle is trusted after being drawn and idle for a few frames.
        private const int REAL_POSE_MIN_FRAMES = 2;

        private const float POST_RAISE_TRACE_TIME = 1f;

        private static RifleRetentionSystem? _instance;

        private ERetentionState _state = ERetentionState.Idle;
        private Weapon? _rifle;
        private Weapon? _pistol;
        private EquipmentSlot _rifleSlot;
        // The rifle's stance at keypress (null = none): the rifle comes back in it.
        private IStance? _stanceAtKeypress;
        private RetainedRifleCopy? _copy;
        private ECharacterWeaponBones _anchorBone = ECharacterWeaponBones.weapon;
        private readonly HeldWeaponHider _hider = new HeldWeaponHider();
        private readonly RiflePoseSolver _pose = new RiflePoseSolver();
        private readonly SwapAnimationSkipper _skipper = new SwapAnimationSkipper();
        private RifleDismissal? _dismissal;

        // What happens to two-handed weapon actions while retaining. Swap an entry to change one action.
        private readonly Dictionary<ERetentionAction, IRetentionActionPolicy> _actionPolicies = new Dictionary<ERetentionAction, IRetentionActionPolicy>
        {
            { ERetentionAction.Reload, BlockActionPolicy.Instance },
            { ERetentionAction.CheckAmmo, BlockActionPolicy.Instance },
            { ERetentionAction.CheckChamber, BlockActionPolicy.Instance },
            { ERetentionAction.UnloadMagazine, BlockActionPolicy.Instance },
            { ERetentionAction.UnloadChamber, BlockActionPolicy.Instance },
            { ERetentionAction.InspectWeapon, BlockActionPolicy.Instance },
            { ERetentionAction.ClearMalfunction, BlockActionPolicy.Instance },
        };

        private bool _ignoreSwapInput;
        private bool _pistolAppeared;
        private float _moveTimer; // since the key press that started this move
        private float _blend; // 0 = held pose, 1 = lowered pose

        private Vector3 _cameraAtKeypress;

        private bool _realRifleReady;
        private int _realPosedFrames;
        private float _realReadyTime;
        private bool _rifleBackInHands;

        private float _postRaiseTraceUntil;

        public bool IsActive => _state != ERetentionState.Idle;

        public void RunOnAwake()
        {
            _instance = this;
            StanceInputEvents.ToggleRifleRetention += OnToggle;
            InputEvents.WeaponSwapInput += OnManualSwap;
        }

        public void RunOnDestroy()
        {
            StanceInputEvents.ToggleRifleRetention -= OnToggle;
            InputEvents.WeaponSwapInput -= OnManualSwap;
            EndRetention("subsystem destroyed");
            if (_instance == this) _instance = null;
        }

        public void RunOnUpdate(float deltaTime)
        {
            if (_state == ERetentionState.Idle)
            {
                if (RifleRetentionDebug.Enabled && Time.time < _postRaiseTraceUntil)
                    RifleRetentionDebug.TraceFrame("post-raise", "Idle", 0f, true, null, _anchorBone);
                return;
            }

            // Safety: the game resets its FOV squash at end of frame; never carry ours into a new frame.
            _copy?.UndoFovSquash();

            _skipper.RunPendingSpawn(_rifle);

            Player player = PlayerStateInstance.Player;
            if (_state == ERetentionState.Rising && player != null)
            {
                UpdateRising(player, deltaTime);
                return;
            }

            if (player == null || _copy == null || !_copy.IsAlive || !RifleStillEquipped(player))
            {
                EndRetention(player == null ? "no player" : _copy == null || !_copy.IsAlive ? "copy missing" : "rifle no longer in its slot");
                return;
            }

            var hands = new HandsView(player.HandsController, _rifle, _pistol);

            bool stillActive;
            switch (_state)
            {
                case ERetentionState.Lowering:
                    stillActive = UpdateLowering(hands, deltaTime);
                    break;
                case ERetentionState.Retained:
                    stillActive = UpdateRetained(hands, deltaTime);
                    break;
                case ERetentionState.Dismissing:
                    stillActive = UpdateDismissing(deltaTime);
                    break;
                default:
                    stillActive = UpdateRaising(hands, deltaTime);
                    break;
            }
            if (!stillActive) return;

            // Every frame while moving; every 15th while retained.
            if (RifleRetentionDebug.Enabled && (_state != ERetentionState.Retained || Time.frameCount % 15 == 0))
                RifleRetentionDebug.TraceFrame("frame", _state.ToString(), _blend, _realRifleReady, _copy.Anchor, _anchorBone, _copy.LastReachClampMm);
        }

        private bool UpdateLowering(HandsView hands, float deltaTime)
        {
            if (SwapFailed("lowering", hands, deltaTime)) return false;

            if (hands.Rifle)
            {
                _hider.Hide(hands.Controller!.ControllerGameObject);
            }
            else if (hands.Pistol)
            {
                // The rifle's first-person object is gone (pooled); give its renderers back.
                _hider.RestoreAll();
                // Not WeaponIsReady alone: it can still be true from the rifle (seen at f0 in logs).
                if (_pistolAppeared || IsDrawnAndIdle(hands.Controller))
                    SetState(ERetentionState.Retained, _pistolAppeared ? "pistol draw completed" : "pistol idle");
            }

            Lower(deltaTime);
            return true;
        }

        private bool UpdateRetained(HandsView hands, float deltaTime)
        {
            if (!hands.Pistol)
            {
                EndRetention("retained: pistol left hands (" + RifleRetentionDebug.Name(hands.Item) + ")");
                return false;
            }

            Lower(deltaTime);
            return true;
        }

        private void Lower(float deltaTime)
        {
            _blend = Mathf.MoveTowards(_blend, 1f, deltaTime * PluginConfig.RifleRetentionBlendSpeed.Value);
        }

        /// <summary>Counts the swap's time; true if it has failed and retention was ended.</summary>
        private bool SwapFailed(string phase, HandsView hands, float deltaTime)
        {
            _moveTimer += deltaTime;
            if (_moveTimer > SWAP_TIMEOUT)
                EndRetention(phase + " timeout");
            else if (hands.SomethingElse)
                EndRetention($"{phase}: something else in hands ({RifleRetentionDebug.Name(hands.Item)})");
            return _state == ERetentionState.Idle;
        }

        private bool UpdateDismissing(float deltaTime)
        {
            if (!_dismissal!.Update(deltaTime)) return true;

            EndRetention("dismissed");
            return false;
        }

        private bool UpdateRaising(HandsView hands, float deltaTime)
        {
            if (SwapFailed("raising", hands, deltaTime)) return false;

            Rise(PluginConfig.RifleRetentionRaiseCopyLimit.Value, deltaTime);

            if (!hands.Rifle) return true;

            // The copy stands in for the real rifle until it can take over.
            _hider.Hide(hands.Controller!.ControllerGameObject);
            _rifleBackInHands = true;
            // No previous-weapon stance (PistolCompress) easing out on it, no enter animation. Logs showed
            // ~4cm / 9deg of drift over ~0.45s without this and OnStanceSpringsSet.
            SetRifleStance();

            // Right after the fast-forward the animator is not posed yet and the weapon bone reports a
            // junk pose for a frame: only trust the real rifle after it has been drawn and idle for a bit.
            bool posed = PlayerStateInstance.WeaponIsReady && IsDrawnAndIdle(hands.Controller);
            _realPosedFrames = posed ? _realPosedFrames + 1 : 0;
            bool wasReady = _realRifleReady;
            _realRifleReady = _realPosedFrames >= REAL_POSE_MIN_FRAMES;
            if (_realRifleReady && !wasReady)
                RifleRetentionDebug.Log($"real rifle trusted after {_realPosedFrames} posed frames");
            if (!_realRifleReady) return true;

            // Its animator is still kicking in here: a ~5mm step, then the barrel roll, in logs. Hidden
            // for now, so the swap doesn't show them.
            _realReadyTime += deltaTime;
            if (_realReadyTime < PluginConfig.RifleRetentionRaiseSwapDelay.Value) return true;

            SwapToRealRifle();
            return false;
        }

        private void SetRifleStance()
        {
            var stances = Plugin.StanceControllerInstance;
            if (stances != null && stances.AwakeRan)
                stances.SetStanceForRetention(_stanceAtKeypress);
        }

        /// <summary>
        /// Shows the real rifle in place of the copy, partway up and on the move, where what little still
        /// differs between them can't be seen.
        /// </summary>
        private void SwapToRealRifle()
        {
            RifleRetentionDebug.LogSwapGap(_copy!.Anchor, _anchorBone);
            RemoveCopy();
            SetState(ERetentionState.Rising, "real rifle ready");
        }

        private void RemoveCopy()
        {
            _hider.RestoreAll();
            _copy?.Destroy(PlayerStateInstance.Player);
            _copy = null;
        }

        private void UpdateRising(Player player, float deltaTime)
        {
            _moveTimer += deltaTime;
            if (!ReferenceEquals(player.HandsController?.Item, _rifle))
            {
                EndRetention("rising: rifle left hands");
                return;
            }

            SetRifleStance();
            Rise(0f, deltaTime);
            if (RifleRetentionDebug.Enabled)
                RifleRetentionDebug.TraceFrame("frame", _state.ToString(), _blend, true, null, _anchorBone);
            if (_blend > 0f) return;

            Plugin.StanceControllerInstance?.AdoptStanceAfterRetention(_stanceAtKeypress);
            EndRetention("raised");
        }

        // One speed for the whole rise, copy and real rifle alike, so the swap doesn't show as a change of pace.
        private void Rise(float limit, float deltaTime)
        {
            if (_moveTimer < PluginConfig.RifleRetentionRaiseStartDelay.Value) return;
            _blend = Mathf.MoveTowards(_blend, limit, deltaTime * PluginConfig.RifleRetentionRaiseSpeed.Value);
        }

        /// <summary>
        /// Where the rifle is along its travel, as a stance offset. Copy and real rifle both follow this,
        /// so the copy can be swapped for the real one anywhere along the way.
        /// </summary>
        private void GetTravelOffset(out Vector3 position, out Vector3 rotation)
        {
            // TEMP (tuning): the preview toggle holds the rifle at the away pose instead.
            bool preview = PluginConfig.RifleRetentionPreviewAwayPose.Value;
            Vector3 targetPos = preview ? RifleAwayPose.Position : RetainedRiflePose.Position;
            Vector3 targetRot = preview ? RifleAwayPose.Rotation : RetainedRiflePose.Rotation;

            bool raising = _state == ERetentionState.Raising || _state == ERetentionState.Rising;
            Vector3 heldPos = _pose.HeldPosition;
            Vector3 heldRot = _pose.HeldRotation;
            position = heldPos + Vector3.Scale(targetPos - heldPos, RetentionTravel.Position(raising, _blend));
            rotation = heldRot + Vector3.Scale(targetRot - heldRot, RetentionTravel.Rotation(raising, _blend));
        }

        private void UpdatePose(Player player)
        {
            if (_dismissal != null)
            {
                _dismissal.ApplyPose(player);
                return;
            }

            float eased = Mathf.SmoothStep(0f, 1f, _blend);

            // Re-evaluated every frame: picks up tweak slider and curve changes.
            GetTravelOffset(out Vector3 offsetPos, out Vector3 offsetRot);
            _pose.Evaluate(offsetPos, offsetRot, out Vector3 pos, out Quaternion rot);

            _copy!.SetLocalPose(pos + CameraDriftCorrection(player) * eased, rot);
            if (!PluginConfig.RifleRetentionPreviewAwayPose.Value) // the real put-away isn't clamped either
                _copy.ClampToArmReach(player, eased);
            _copy.HoldWithLeftHand(player);
        }

        /// <summary>
        /// How far to move the copy so it keeps its place on screen while aiming slides the camera. Position
        /// only: the camera's rotation carries freelook.
        /// </summary>
        private Vector3 CameraDriftCorrection(Player player)
        {
            return RetentionSpace.TryGetCameraPosition(out Vector3 camera)
                ? _copy!.ThroughFovSquash(player, camera - _cameraAtKeypress)
                : Vector3.zero;
        }

        /// <summary>
        /// The weapon in hands has finished its draw and its animator is in idle, i.e. its bones are
        /// actually posed. On the frame of a fast-forwarded draw the animator is not idle yet and the
        /// weapon bone reports a junk pose (241mm / ~90deg off in logs).
        /// </summary>
        private static bool IsDrawnAndIdle(Player.AbstractHandsController? handsController)
        {
            if (!(handsController is Player.FirearmController fc)) return false;
            var op = fc.CurrentOperation;
            if (op == null || op is Player.FirearmController.SpawnOperation) return false;
            return op.FirearmsAnimator != null && op.FirearmsAnimator.IsIdling();
        }

        private void OnToggle()
        {
            RifleRetentionDebug.MarkKeypress();
            Player player = PlayerStateInstance.Player;
            RifleRetentionDebug.Log($"KEYPRESS state={_state} hands={RifleRetentionDebug.Name(player?.HandsController?.Item)} " +
                $"treatAsPistol={WeaponStateInstance.TreatAsPistol} weaponIsReady={PlayerStateInstance.WeaponIsReady}");

            if (_state == ERetentionState.Idle)
                TryStartRetention();
            else if (_state == ERetentionState.Retained)
                StartRaising();
        }

        private void OnManualSwap()
        {
            if (_ignoreSwapInput || _state == ERetentionState.Idle) return;
            // Already letting go of the rifle: let that finish.
            if (_dismissal != null) return;
            // The game ignores weapon keys while a swap is still running (this fires before it handles the
            // key). Cancelling here would only drop our half of the swap, so let it finish, like vanilla.
            if (PlayerStateInstance.Player?.InventoryController?.IsChangingWeapon == true)
            {
                RifleRetentionDebug.Log("weapon key ignored: swap still running");
                return;
            }
            // A weapon key while retained (quick-draw: fully to the pistol; a primary key: another weapon):
            // let go of the rifle instead of dropping it in one frame.
            if (_state == ERetentionState.Retained)
            {
                SetState(ERetentionState.Dismissing, "weapon key while retained");
                _dismissal = new RifleDismissal(_copy!, _pose);
                return;
            }
            // Mid lowering/raising (only when no swap is running): drop the illusion right away.
            EndRetention("manual weapon swap input");
        }

        private void TryStartRetention()
        {
            Player player = PlayerStateInstance.Player;
            if (player == null || !player.IsYourPlayer) return;
            if (WeaponStateInstance.TreatAsPistol) return;
            if (!(player.HandsController?.Item is Weapon rifle)) return;
            // Mid-reload, mid-inspect, mid-draw etc. the rifle is in some animation pose, and the copy
            // would start from it. Only swap from a drawn, idle rifle.
            if (!IsDrawnAndIdle(player.HandsController))
            {
                RifleRetentionDebug.Log("ignored: rifle is busy");
                return;
            }
            if (!TryGetRifleSlot(player.Equipment, rifle, out EquipmentSlot rifleSlot)) return;
            if (!(player.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem is Weapon pistol)) return;

            Transform? space = RetentionSpace.Root;
            Transform? heldBone = RetentionSpace.HeldWeaponBone(ECharacterWeaponBones.weapon);
            if (space == null || heldBone == null) return;

            _rifle = rifle;
            _pistol = pistol;
            _rifleSlot = rifleSlot;
            _stanceAtKeypress = Plugin.StanceControllerInstance?.CurrentStance;
            RifleRetentionDebug.Log($"stance at keypress: {_stanceAtKeypress?.StanceType.ToString() ?? "none"}");
            RetentionSpace.TryGetLocalPose(heldBone, out Vector3 heldPosition, out Quaternion heldRotation);
            RetentionSpace.TryGetCameraPosition(out _cameraAtKeypress);

            _copy = RetainedRifleCopy.Spawn(player, rifle, space, heldBone, heldPosition, heldRotation);
            if (_copy == null || !_pose.Capture(space, heldBone))
            {
                EndRetention(_copy == null ? "copy spawn failed" : "pose capture failed");
                return;
            }
            _anchorBone = _copy.AnchorBone;

            _hider.Hide(player.HandsController?.ControllerGameObject);
            _blend = 0f;
            _moveTimer = 0f;
            SetState(ERetentionState.Lowering, "keypress");
            UpdatePose(player);

            _pistolAppeared = false;
            _skipper.ArmRemove(rifle);
            RequestSlot(player, EquipmentSlot.Holster);
        }

        private static bool TryGetRifleSlot(InventoryEquipment equipment, Weapon rifle, out EquipmentSlot slot)
        {
            var container = rifle.CurrentAddress?.Container;
            slot = EquipmentSlot.FirstPrimaryWeapon;
            if (ReferenceEquals(container, equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)))
                return true;

            slot = EquipmentSlot.SecondPrimaryWeapon;
            return ReferenceEquals(container, equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon));
        }

        private void StartRaising()
        {
            Player player = PlayerStateInstance.Player;
            if (player == null) return;

            _moveTimer = 0f;
            SetState(ERetentionState.Raising, "keypress");

            _skipper.ArmSpawn(_rifle!);
            RequestSlot(player, _rifleSlot);
        }

        private void RequestSlot(Player player, EquipmentSlot slot)
        {
            // Normal (non-quickdraw) swap: separate from the game's fast pistol draw and its stamina cost.
            player.FastSlotSelection = false;
            RifleRetentionDebug.Log($"RequestSlot {slot} (contains {RifleRetentionDebug.Name(player.Equipment.GetSlot(slot).ContainedItem)})");
            _ignoreSwapInput = true;
            try
            {
                player.SetSlotItem(slot, result => { });
            }
            finally
            {
                _ignoreSwapInput = false;
            }
        }

        private bool RifleStillEquipped(Player player)
        {
            return ReferenceEquals(player.Equipment.GetSlot(_rifleSlot).ContainedItem, _rifle);
        }

        private void SetState(ERetentionState newState, string reason)
        {
            if (newState != _state)
            {
                RifleRetentionDebug.Log($"STATE {_state} -> {newState} ({reason}) blend={_blend:F3} hidden={_hider.Count}");
                if (_state == ERetentionState.Rising && newState == ERetentionState.Idle)
                    _postRaiseTraceUntil = Time.time + POST_RAISE_TRACE_TIME;
            }
            _state = newState;
        }

        private void EndRetention(string reason)
        {
            _skipper.Disarm();
            _realRifleReady = false;
            _realPosedFrames = 0;
            _realReadyTime = 0f;
            _pistolAppeared = false;
            _rifleBackInHands = false;
            _dismissal = null;
            _pose.Reset();
            RemoveCopy();

            _rifle = null;
            _pistol = null;
            _stanceAtKeypress = null;
            _blend = 0f;
            _moveTimer = 0f;
            SetState(ERetentionState.Idle, reason);
        }

        // ---- Entry points for RifleRetentionPatches ----

        /// <summary>
        /// ZeroAdjustments postfix: the stance springs were just set for this frame. While the rifle is
        /// coming back, it is held where the copy is along the travel, hidden at first, then on show.
        /// </summary>
        internal static void OnStanceSpringsSet()
        {
            RifleRetentionSystem? system = _instance;
            if (system == null || !system._rifleBackInHands) return;

            system.GetTravelOffset(out Vector3 position, out Vector3 rotation);
            Plugin.StanceControllerInstance?.HoldAtStanceOffset(position, rotation, exact: system._state == ERetentionState.Raising);
        }

        /// <summary>ProcessEffectors postfix: the game moved its springs, the rifle's own move in step.</summary>
        internal static void OnProceduralStep(ProceduralWeaponAnimation pwa, float deltaTime, int nFixedFrames)
        {
            // Runs for every player: the active check comes first, and rules out almost all of them.
            if (_instance != null && _instance.IsActive && ReferenceEquals(pwa, PlayerStateInstance.PWA))
                _instance._pose.StepMotion(pwa, deltaTime, nFixedFrames);
        }

        /// <summary>
        /// IkProcess prefix: places the copy here rather than in Update. The game applies this frame's
        /// procedural motion in LateUpdate, just before this; in Update the copy would run a frame behind
        /// the right hand. It is also the last moment the left hand can still follow the copy this frame.
        /// </summary>
        internal static void OnBeforeLeftArmIK(Player player)
        {
            RifleRetentionSystem? system = _instance;
            if (system?._copy != null && system._copy.IsAlive)
                system.UpdatePose(player);
        }

        /// <summary>
        /// IkProcess prefix: keep the left arm on the copy even when the swap animation frees the hand, until
        /// the hand has let go of it (dismissing).
        /// </summary>
        internal static bool ForceLeftHandIK => _instance?._copy?.LeftGrip != null && _instance._dismissal?.HandReleased != true;

        /// <summary>Remove.Start postfix. True when the rifle's "put away" animation should be fast-forwarded.</summary>
        internal static bool ConsumeSkipRemove(Player player, Weapon weapon)
        {
            if (player == null || !player.IsYourPlayer || _instance == null) return false;
            return _instance._skipper.ConsumeRemove(weapon);
        }

        /// <summary>SpawnOperation.Start postfix: skips the draw of the rifle being raised back.</summary>
        internal static void ConsumeSkipSpawn(Player.FirearmController.SpawnOperation operation)
        {
            Player player = operation.Player;
            if (player == null || !player.IsYourPlayer || _instance == null) return;
            _instance._skipper.ConsumeSpawn(operation, _instance._hider);
        }

        /// <summary>
        /// SpawnOperation.WeaponAppeared postfix: a local firearm draw has really completed. Lowering waits
        /// for this for the pistol: RealismCommonLib's WeaponIsReady can still be true from the rifle.
        /// </summary>
        internal static void OnWeaponAppeared(Player.FirearmController.SpawnOperation operation)
        {
            if (operation.Player == null || !operation.Player.IsYourPlayer || _instance == null) return;

            RifleRetentionDebug.Log($"WeaponAppeared weapon={RifleRetentionDebug.Name(operation.Weapon)} state={_instance._state}");
            if (ReferenceEquals(operation.Weapon, _instance._pistol))
                _instance._pistolAppeared = true;
        }

        /// <summary>
        /// Two-handed action patches, for the local player's hands only. False refuses the action; while
        /// retaining (any phase) the decision is left to that action's policy.
        /// </summary>
        internal static bool AllowAction(ERetentionAction action, Player.FirearmController controller)
        {
            if (_instance == null || !_instance.IsActive) return true;
            if (!_instance._actionPolicies.TryGetValue(action, out IRetentionActionPolicy policy)) return true;
            return policy.CanStart(action, controller);
        }

        internal static void OnFovAdjustmentsApplied(Player player)
        {
            if (player != null && player.IsYourPlayer)
                _instance?._copy?.ApplyFovSquash(player);
        }

        internal static void OnFovAdjustmentsReset()
        {
            _instance?._copy?.UndoFovSquash();
        }

        /// <summary>What the hands hold right now, relative to the two weapons of this retention.</summary>
        private readonly struct HandsView
        {
            public readonly Player.AbstractHandsController? Controller;
            public readonly Item? Item;
            public readonly bool Rifle;
            public readonly bool Pistol;

            // Between two hands controllers the hands can briefly hold neither weapon. That is part of the
            // swap, not a cancel: rely on the timeout and on OnManualSwap for real cancels.
            public bool SomethingElse => Item != null && !Rifle && !Pistol;

            public HandsView(Player.AbstractHandsController? controller, Weapon? rifle, Weapon? pistol)
            {
                Controller = controller;
                Item = controller?.Item;
                Rifle = ReferenceEquals(Item, rifle);
                Pistol = ReferenceEquals(Item, pistol);
            }
        }
    }
}
