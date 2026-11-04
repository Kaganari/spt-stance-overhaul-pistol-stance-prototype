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
    /// hidden real rifle (draw animation skipped too), and the copy is swapped for the real rifle once it
    /// has settled onto it. A weapon key while retained puts the rifle away instead: the left hand takes the
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
            Dismissing  // weapon key while retained: copy put away with the left hand, then the hand lets go
        }

        private const float SWAP_TIMEOUT = 5f;

        // Raising: the real rifle is trusted after being drawn and idle for a few frames, then the copy
        // settles onto its live pose and is swapped for it.
        private const int REAL_POSE_MIN_FRAMES = 2;
        private const float SETTLE_SHARPNESS = 25f;
        private const float SETTLE_POS_EPSILON = 0.004f;
        private const float SETTLE_ANGLE_EPSILON = 1f;
        private const float SETTLE_MAX_TIME = 0.3f;

        private const float POST_REVEAL_TRACE_TIME = 1f;

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
        private readonly LoweredPoseSolver _loweredPose = new LoweredPoseSolver();
        private readonly SwapAnimationSkipper _skipper = new SwapAnimationSkipper();

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
        private float _swapTimer;
        private float _blend; // 0 = held pose, 1 = lowered pose

        // Held rifle pose in retention space, captured on keypress.
        private Vector3 _heldPosition;
        private Quaternion _heldRotation;
        private Vector3 _cameraAtKeypress;

        private bool _realRifleReady;
        private int _realPosedFrames;
        private float _settleTimer;
        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation;

        private float _postRevealTraceUntil;

        // Dismissing: the left hand takes the copy from where it was to RifleAwayPose (out of view), the copy
        // is hidden there, then the hand lets go and returns to the pistol.
        private float _dismissElapsed;
        private float _releaseElapsed;
        private bool _copyHidden;
        private bool _dismissFromCaptured;
        private Vector3 _dismissFromPosition;
        private Quaternion _dismissFromRotation;
        private bool _leftHandReleased;

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
                if (RifleRetentionDebug.Enabled && Time.time < _postRevealTraceUntil)
                    RifleRetentionDebug.TraceFrame("post-reveal", "Idle", 0f, true, null, _anchorBone);
                return;
            }

            // Safety: the game resets its FOV squash at end of frame; never carry ours into a new frame.
            _copy?.UndoFovSquash();

            _skipper.RunPendingSpawn(_rifle);

            Player player = PlayerStateInstance.Player;
            if (player == null || _copy == null || !_copy.IsAlive || !RifleStillEquipped(player))
            {
                EndRetention(player == null ? "no player" : _copy == null || !_copy.IsAlive ? "copy missing" : "rifle no longer in its slot");
                return;
            }

            var handsController = player.HandsController;
            Item? inHands = handsController?.Item;
            bool rifleInHands = ReferenceEquals(inHands, _rifle);
            bool pistolInHands = ReferenceEquals(inHands, _pistol);
            // Between two hands controllers the hands can briefly hold neither weapon. That is part of
            // the swap, not a cancel: rely on the timeout and on OnManualSwap for real cancels.
            bool somethingElseInHands = inHands != null && !rifleInHands && !pistolInHands;

            bool stillActive;
            switch (_state)
            {
                case ERetentionState.Lowering:
                    stillActive = UpdateLowering(handsController, rifleInHands, pistolInHands, somethingElseInHands, inHands, deltaTime);
                    break;
                case ERetentionState.Retained:
                    stillActive = UpdateRetained(pistolInHands, inHands, deltaTime);
                    break;
                case ERetentionState.Dismissing:
                    stillActive = UpdateDismissing(deltaTime);
                    break;
                default:
                    stillActive = UpdateRaising(handsController, rifleInHands, somethingElseInHands, inHands, deltaTime);
                    break;
            }
            if (!stillActive) return;

            // Every frame while moving; every 15th while retained.
            if (RifleRetentionDebug.Enabled && (_state != ERetentionState.Retained || Time.frameCount % 15 == 0))
                RifleRetentionDebug.TraceFrame("frame", _state.ToString(), _blend, _realRifleReady, _copy.Anchor, _anchorBone, _copy.LastReachClampMm);

            if (_state == ERetentionState.Raising)
                TryReveal(deltaTime);
        }

        private bool UpdateLowering(Player.AbstractHandsController? handsController, bool rifleInHands, bool pistolInHands,
            bool somethingElseInHands, Item? inHands, float deltaTime)
        {
            _swapTimer += deltaTime;
            if (_swapTimer > SWAP_TIMEOUT || somethingElseInHands)
            {
                EndRetention(_swapTimer > SWAP_TIMEOUT ? "lowering timeout" : "lowering: something else in hands (" + RifleRetentionDebug.Name(inHands) + ")");
                return false;
            }

            if (rifleInHands)
            {
                _hider.Hide(handsController!.ControllerGameObject);
            }
            else if (pistolInHands)
            {
                // The rifle's first-person object is gone (pooled); give its renderers back.
                _hider.RestoreAll();
                // Not WeaponIsReady alone: it can still be true from the rifle (seen at f0 in logs).
                if (_pistolAppeared || IsDrawnAndIdle(handsController))
                    SetState(ERetentionState.Retained, _pistolAppeared ? "pistol draw completed" : "pistol idle");
            }

            _blend = Mathf.MoveTowards(_blend, 1f, deltaTime * PluginConfig.RifleRetentionBlendSpeed.Value);
            return true;
        }

        private bool UpdateRetained(bool pistolInHands, Item? inHands, float deltaTime)
        {
            if (!pistolInHands)
            {
                EndRetention("retained: pistol left hands (" + RifleRetentionDebug.Name(inHands) + ")");
                return false;
            }

            _blend = Mathf.MoveTowards(_blend, 1f, deltaTime * PluginConfig.RifleRetentionBlendSpeed.Value);
            return true;
        }

        private bool UpdateDismissing(float deltaTime)
        {
            _dismissElapsed += deltaTime;
            if (DismissProgress < 1f) return true;

            // Out of view by now: hiding it can't be seen. The hand still holds its grip point.
            if (!_copyHidden)
            {
                _copy!.HideModel();
                _copy.PlaySound("HandOff");
                _copyHidden = true;
                RifleRetentionDebug.Log("dismiss: rifle out of view, hidden; left hand letting go");
            }

            _releaseElapsed += deltaTime;
            if (!_leftHandReleased) return true;

            EndRetention("dismissed");
            return false;
        }

        private float DismissProgress => Mathf.Clamp01(_dismissElapsed * PluginConfig.RifleRetentionDismissSpeed.Value);

        private bool UpdateRaising(Player.AbstractHandsController? handsController, bool rifleInHands,
            bool somethingElseInHands, Item? inHands, float deltaTime)
        {
            _swapTimer += deltaTime;
            if (_swapTimer > SWAP_TIMEOUT || somethingElseInHands)
            {
                EndRetention(_swapTimer > SWAP_TIMEOUT ? "raising timeout" : "raising: something else in hands (" + RifleRetentionDebug.Name(inHands) + ")");
                return false;
            }

            // Don't park on the pose saved at keypress while the pistol holsters: the real rifle comes
            // back ~1cm / 1deg off it and then settles into its idle (logs). Ease toward just short of
            // the top instead, and finish the rise onto the real rifle's live pose once it is trusted,
            // so the correction happens mid-motion.
            if (_realRifleReady)
                _blend = Mathf.MoveTowards(_blend, 0f, deltaTime * PluginConfig.RifleRetentionRaiseFinalSpeed.Value);
            else
                _blend = Mathf.Lerp(_blend, PluginConfig.RifleRetentionRaiseHoldPoint.Value,
                    1f - Mathf.Exp(-PluginConfig.RifleRetentionRaiseSpeed.Value * deltaTime));

            if (!rifleInHands) return true;

            // The copy stands in for the real rifle until it has settled onto it.
            _hider.Hide(handsController!.ControllerGameObject);

            // Hold the hidden rifle still in the stance it was lowered from: no previous-weapon stance
            // (PistolCompress) easing out on it, no enter animation, no spring tails. Logs showed
            // ~4cm / 9deg of drift over ~0.45s without this.
            var stances = Plugin.StanceControllerInstance;
            if (stances != null && stances.AwakeRan)
                stances.SnapStanceForRetention(_stanceAtKeypress);

            // Right after the fast-forward the animator is not posed yet and the weapon bone reports a
            // junk pose for a frame: only trust the real rifle after it has been drawn and idle for a bit.
            bool posed = PlayerStateInstance.WeaponIsReady && IsDrawnAndIdle(handsController);
            _realPosedFrames = posed ? _realPosedFrames + 1 : 0;
            bool wasReady = _realRifleReady;
            _realRifleReady = _realPosedFrames >= REAL_POSE_MIN_FRAMES;
            if (_realRifleReady && !wasReady)
                RifleRetentionDebug.Log($"real rifle trusted after {_realPosedFrames} posed frames");
            return true;
        }

        /// <summary>Swaps the copy for the real rifle once the copy has settled onto it (or after a timeout).</summary>
        private void TryReveal(float deltaTime)
        {
            if (!_realRifleReady || _blend > 0f || !TrySampleRealRiflePose(out Vector3 realPos, out Quaternion realRot))
                return;

            _settleTimer += deltaTime;
            float distance = Vector3.Distance(_smoothedPosition, realPos);
            float angle = Quaternion.Angle(_smoothedRotation, realRot);
            bool settled = distance < SETTLE_POS_EPSILON && angle < SETTLE_ANGLE_EPSILON;
            RifleRetentionDebug.Log($"settle check: t={_settleTimer:F3} d={distance * 1000f:F1}mm/{angle:F1}deg settled={settled}");

            // EndRetention reveals the real rifle in the same frame the copy goes away.
            if (settled || _settleTimer > SETTLE_MAX_TIME)
            {
                // The rifle is already in that stance (held there while raising); make it stick.
                Plugin.StanceControllerInstance?.AdoptStanceAfterRetention(_stanceAtKeypress);
                EndRetention(settled ? "REVEAL settled" : "REVEAL settle timeout");
            }
        }

        private void UpdatePose(Player player, float deltaTime)
        {
            if (_state == ERetentionState.Dismissing)
            {
                UpdateDismissPose(player);
                return;
            }

            float eased = Mathf.SmoothStep(0f, 1f, _blend);

            if (!_loweredPose.TryEvaluateHeld(out Vector3 upPos, out Quaternion upRot))
            {
                upPos = _heldPosition;
                upRot = _heldRotation;
            }

            // Re-evaluated every frame: picks up tweak slider changes.
            // TEMP (tuning): the preview toggle holds the rifle at the away pose instead.
            bool preview = PluginConfig.RifleRetentionPreviewAwayPose.Value;
            Vector3 targetPos = preview ? RifleAwayPose.Position : RetainedRiflePose.Position;
            Vector3 targetRot = preview ? RifleAwayPose.Rotation : RetainedRiflePose.Rotation;
            if (!_loweredPose.TryEvaluate(targetPos, targetRot, out Vector3 loweredPos, out Quaternion loweredRot))
            {
                loweredPos = _heldPosition;
                loweredRot = _heldRotation;
            }

            // "Up" end of the travel: where the rifle was held or, when raising and the real rifle is
            // already in hands, exactly where it actually is (it comes back in idle, not the old stance).
            if (_state == ERetentionState.Raising && _realRifleReady && TrySampleRealRiflePose(out Vector3 realPos, out Quaternion realRot))
            {
                upPos = realPos;
                upRot = realRot;
            }

            // Fades out on the way up, so the rifle still ends exactly on the real one.
            Vector3 pos = Vector3.Lerp(upPos, loweredPos, eased) + CameraDriftCorrection(player) * eased;
            Quaternion rot = Quaternion.Slerp(upRot, loweredRot, eased);

            if (_state == ERetentionState.Raising)
            {
                // Smooth out the retarget from the saved to the real pose instead of snapping.
                float t = 1f - Mathf.Exp(-SETTLE_SHARPNESS * deltaTime);
                _smoothedPosition = Vector3.Lerp(_smoothedPosition, pos, t);
                _smoothedRotation = Quaternion.Slerp(_smoothedRotation, rot, t);
                pos = _smoothedPosition;
                rot = _smoothedRotation;
            }

            _copy!.SetLocalPose(pos, rot);
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

        private void UpdateDismissPose(Player player)
        {
            // Captured here, not on the key press: input runs mid-frame, the pose is set later in the frame.
            if (!_dismissFromCaptured)
            {
                _dismissFromPosition = _copy!.Anchor.localPosition;
                _dismissFromRotation = _copy.Anchor.localRotation;
                _dismissFromCaptured = true;
            }

            // A fixed end pose, so it always ends out of view whatever the retained pose is tuned to.
            if (!_loweredPose.TryEvaluate(RifleAwayPose.Position, RifleAwayPose.Rotation, out Vector3 awayPos, out Quaternion awayRot))
            {
                awayPos = _dismissFromPosition;
                awayRot = _dismissFromRotation;
            }

            float eased = Mathf.SmoothStep(0f, 1f, DismissProgress);
            _copy!.SetLocalPose(Vector3.Lerp(_dismissFromPosition, awayPos, eased),
                Quaternion.Slerp(_dismissFromRotation, awayRot, eased));

            // The hand keeps holding the rifle on its way down, and only lets go once it is out of view.
            if (!_copyHidden)
            {
                _copy.HoldWithLeftHand(player);
                return;
            }

            float releaseTime = PluginConfig.RifleRetentionHandReleaseTime.Value;
            float handWeight = releaseTime > 0f ? 1f - Mathf.Clamp01(_releaseElapsed / releaseTime) : 0f;
            _copy.ReleaseLeftHand(player, handWeight);
            _leftHandReleased = handWeight <= 0f;
        }

        private bool TrySampleRealRiflePose(out Vector3 position, out Quaternion rotation)
        {
            return RetentionSpace.TryGetLocalPose(RetentionSpace.HeldWeaponBone(_anchorBone), out position, out rotation);
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
            if (!PluginConfig.EnableRifleRetention.Value) return;

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
            if (_state == ERetentionState.Dismissing) return;
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
                StartDismissing();
                return;
            }
            // Mid lowering/raising (only when no swap is running): drop the illusion right away.
            EndRetention("manual weapon swap input");
        }

        private void StartDismissing()
        {
            _dismissElapsed = 0f;
            _releaseElapsed = 0f;
            _copyHidden = false;
            _dismissFromCaptured = false;
            _leftHandReleased = false;
            SetState(ERetentionState.Dismissing, "weapon key while retained");
            // The rifle's own put-away sound; its "hand off" plays once the hand lets go.
            _copy?.PlaySound("Holster");
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
            RetentionSpace.TryGetLocalPose(heldBone, out _heldPosition, out _heldRotation);
            RetentionSpace.TryGetCameraPosition(out _cameraAtKeypress);

            _copy = RetainedRifleCopy.Spawn(player, rifle, space, heldBone, _heldPosition, _heldRotation);
            if (_copy == null)
            {
                EndRetention("copy spawn failed");
                return;
            }
            _anchorBone = _copy.AnchorBone;
            _loweredPose.Capture(space, heldBone);

            _hider.Hide(player.HandsController?.ControllerGameObject);
            _blend = 0f;
            _swapTimer = 0f;
            SetState(ERetentionState.Lowering, "keypress");
            UpdatePose(player, 0f);

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

            _swapTimer = 0f;
            _settleTimer = 0f;
            _realRifleReady = false;
            _realPosedFrames = 0;
            _smoothedPosition = _copy!.Anchor.localPosition;
            _smoothedRotation = _copy.Anchor.localRotation;
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
                if (_state == ERetentionState.Raising && newState == ERetentionState.Idle)
                    _postRevealTraceUntil = Time.time + POST_REVEAL_TRACE_TIME;
            }
            _state = newState;
        }

        private void EndRetention(string reason)
        {
            _skipper.Disarm();
            _realRifleReady = false;
            _realPosedFrames = 0;
            _pistolAppeared = false;
            _settleTimer = 0f;
            _dismissElapsed = 0f;
            _releaseElapsed = 0f;
            _copyHidden = false;
            _dismissFromCaptured = false;
            _leftHandReleased = false;
            _loweredPose.Reset();
            _hider.RestoreAll();

            _copy?.Destroy(PlayerStateInstance.Player);
            _copy = null;

            _rifle = null;
            _pistol = null;
            _stanceAtKeypress = null;
            _blend = 0f;
            _swapTimer = 0f;
            SetState(ERetentionState.Idle, reason);
        }

        // ---- Entry points for RifleRetentionPatches ----

        /// <summary>ProcessEffectors postfix: the game moved its springs, the rifle's own move in step.</summary>
        internal static void OnProceduralStep(ProceduralWeaponAnimation pwa, float deltaTime, int nFixedFrames)
        {
            // Runs for every player: the active check comes first, and rules out almost all of them.
            if (_instance != null && _instance.IsActive && ReferenceEquals(pwa, PlayerStateInstance.PWA))
                _instance._loweredPose.StepMotion(pwa, deltaTime, nFixedFrames);
        }

        /// <summary>
        /// IkProcess prefix: places the copy here rather than in Update. The game applies this frame's
        /// procedural motion in LateUpdate, just before this; in Update the copy would run a frame behind
        /// the right hand. It is also the last moment the left hand can still follow the copy this frame.
        /// </summary>
        internal static void OnBeforeLeftArmIK(Player player)
        {
            RifleRetentionSystem? system = _instance;
            if (system == null || system._state == ERetentionState.Idle || system._copy == null || !system._copy.IsAlive)
                return;

            system.UpdatePose(player, Time.deltaTime);
        }

        /// <summary>
        /// IkProcess prefix: keep the left arm on the copy even when the swap animation frees the hand, until
        /// the hand has let go of it (dismissing).
        /// </summary>
        internal static bool ForceLeftHandIK => _instance != null && _instance.IsActive && _instance._copy?.LeftGrip != null
            && !_instance._leftHandReleased;

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
    }
}
