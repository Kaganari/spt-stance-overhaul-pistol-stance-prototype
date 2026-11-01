using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using RealismCommonLib.Events;
using StanceOverhaul.SubSystem.StanceInput;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem
{
    /// <summary>
    /// PROTOTYPE: rifle-retained pistol transition.
    ///
    /// On keypress a visual-only copy of the rifle is spawned exactly where the held rifle is,
    /// the real rifle is hidden, its "put away" animation is skipped (RetentionSkipRifleRemovePatch)
    /// so the pistol draw starts immediately, and the copy drops from the held pose to the
    /// Patrol stance pose (+ tweaks). The left hand is kept on the copy's handguard
    /// GripPose the whole time (the hand follows the rifle, never the other way around).
    /// Returning reverses it: the copy glides back up while the game draws the (hidden) real
    /// rifle, and the two are swapped once the real rifle is ready.
    ///
    /// The copy is parented to the camera, which is stable across weapon swaps (the hands rig
    /// is rebuilt for every weapon). First-person, local-only.
    /// </summary>
    internal class RifleRetentionSystem : ISubSystem
    {
        private enum ERetentionState
        {
            Idle,
            Lowering,   // copy gliding down, game swapping rifle -> pistol
            Retained,   // pistol ready, copy held low
            Raising     // copy gliding up, game swapping pistol -> rifle
        }

        private const float SWAP_TIMEOUT = 5f;

        private ERetentionState _state = ERetentionState.Idle;
        private bool _ignoreSwapInput;

        private Weapon? _rifle;
        private Weapon? _pistol;
        private EquipmentSlot _rifleSlot;
        private float _swapTimer;
        private float _blend; // 0 = held pose, 1 = lowered pose

        private GameObject? _anchor;
        private GameObject? _rifleModel;
        private GameObject? _fallbackGripGO;
        private GripPose? _leftGrip;
        private bool _fingersMapped;
        private UnityEngine.Animator? _disabledAnimator;
        private readonly List<Collider> _disabledColliders = new List<Collider>();
        private readonly List<Renderer> _hiddenHeldRenderers = new List<Renderer>();

        // Held rifle pose and lowered (patrol-derived) pose, both in camera space, captured on keypress.
        private Vector3 _heldLocalPosition;
        private Quaternion _heldLocalRotation;
        private Vector3 _loweredLocalPosition;
        private Quaternion _loweredLocalRotation;

        // Hands-spring snapshot used to (re)compute the Patrol pose every frame.
        private bool _patrolPoseValid;
        private Vector3 _springCurPosTotal;
        private Vector3 _springNonStancePos;
        private Vector3 _springNonStanceRot;
        private Vector3 _springRotCenter;
        private Quaternion _springBaseRot;
        private Vector3 _springPrePivotPos;
        private Vector3 _springRelPos;
        private Quaternion _springRelRot;
        private Vector3 _springParentCamPos;
        private Quaternion _springParentCamRot;

        // Armed right before we request the pistol; consumed by RetentionSkipRifleRemovePatch.
        private const float SKIP_REMOVE_WINDOW = 1.5f;
        private static Weapon? _skipRemoveFor;
        private static float _skipRemoveUntil;

        // Armed right before we request the rifle back; consumed by RetentionSkipRifleSpawnPatch.
        // Longer window: the pistol holster animation plays before the rifle is spawned.
        private const float SKIP_SPAWN_WINDOW = 4f;
        private static Weapon? _skipSpawnFor;
        private static float _skipSpawnUntil;

        // Raising: once the real rifle is ready, the copy settles onto its actual pose before the swap.
        private const float SETTLE_SHARPNESS = 25f;
        private const float SETTLE_POS_EPSILON = 0.004f;
        private const float SETTLE_ANGLE_EPSILON = 1f;
        private const float SETTLE_MAX_TIME = 0.3f;
        private bool _realRifleReady;
        private float _settleTimer;
        private Vector3 _smoothedLocalPosition;
        private Quaternion _smoothedLocalRotation;

        private static RifleRetentionSystem? _instance;
        private ECharacterWeaponBones _anchorBone = ECharacterWeaponBones.weapon;

        public bool IsActive => _state != ERetentionState.Idle;

        /// <summary>
        /// Called from the SpawnOperation.Start postfix. True once for the rifle we are raising back:
        /// its draw animation is fast-forwarded (the copy already shows the rifle coming up) and its
        /// model is hidden right away, before it can render a single frame.
        /// </summary>
        internal static bool ConsumeSkipSpawn(Player.FirearmController.SpawnOperation operation)
        {
            Player player = operation.Player;
            if (_skipSpawnFor == null || _instance == null || player == null || !player.IsYourPlayer) return false;
            if (!ReferenceEquals(operation.Weapon, _skipSpawnFor) || Time.time > _skipSpawnUntil) return false;
            _skipSpawnFor = null;
            _instance.HideRenderersUnder(operation.Controller.ControllerGameObject);
            _instance._pendingSpawnSkip = operation;
            return true;
        }

        private Player.FirearmController.SpawnOperation? _pendingSpawnSkip;

        private void RunPendingSpawnSkip()
        {
            var operation = _pendingSpawnSkip;
            _pendingSpawnSkip = null;
            if (operation == null || !ReferenceEquals(operation.Weapon, _rifle)) return;

            // The draw animation is what normally fires this event (chambering a round from the
            // magazine on draw); fire it ourselves so ammo state matches. No-op when nothing was chambered.
            operation.OnAddAmmoInChamber();
            // Jumps the animator to idle and completes the draw (WeaponAppeared). No-op if already finished.
            operation.FastForward();
        }

        /// <summary>
        /// Called from the Remove.Start postfix. True once for the rifle we are retaining, so its
        /// "put the rifle away" animation is fast-forwarded and the pistol draw starts immediately.
        /// </summary>
        internal static bool ConsumeSkipRemove(Player player, Weapon weapon)
        {
            if (_skipRemoveFor == null || player == null || !player.IsYourPlayer) return false;
            if (!ReferenceEquals(weapon, _skipRemoveFor) || Time.time > _skipRemoveUntil) return false;
            _skipRemoveFor = null;
            return true;
        }

        /// <summary>Read by the IK patch: keep the left arm on the copy even when the swap animation frees the hand.</summary>
        public bool ForceLeftHandIK => _state != ERetentionState.Idle && _leftGrip != null;

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
            Cleanup();
            if (_instance == this) _instance = null;
        }

        public void RunOnUpdate(float deltaTime)
        {
            if (_state == ERetentionState.Idle) return;

            if (_pendingSpawnSkip != null)
                RunPendingSpawnSkip();

            Player player = PlayerStateInstance.Player;
            if (player == null || _rifleModel == null || _anchor == null || !RifleStillEquipped(player))
            {
                Cleanup();
                return;
            }

            var handsController = player.HandsController;
            Item? inHands = handsController?.Item;
            bool rifleInHands = ReferenceEquals(inHands, _rifle);
            bool pistolInHands = ReferenceEquals(inHands, _pistol);
            // Between two hands controllers the hands can briefly hold neither weapon. That is part of
            // the swap, not a cancel: rely on the timeout and on OnManualSwap for real cancels.
            bool somethingElseInHands = inHands != null && !rifleInHands && !pistolInHands;

            switch (_state)
            {
                case ERetentionState.Lowering:
                    _swapTimer += deltaTime;
                    if (_swapTimer > SWAP_TIMEOUT || somethingElseInHands)
                    {
                        Cleanup();
                        return;
                    }
                    if (rifleInHands)
                    {
                        HideRenderersUnder(handsController!.ControllerGameObject);
                    }
                    else if (pistolInHands)
                    {
                        // The rifle's first-person object is gone (pooled); give its renderers back.
                        RestoreHeldRenderers();
                        if (PlayerStateInstance.WeaponIsReady)
                            _state = ERetentionState.Retained;
                    }
                    _blend = Mathf.MoveTowards(_blend, 1f, deltaTime * PluginConfig.RifleRetentionBlendSpeed.Value);
                    break;

                case ERetentionState.Retained:
                    if (!pistolInHands)
                    {
                        Cleanup();
                        return;
                    }
                    _blend = Mathf.MoveTowards(_blend, 1f, deltaTime * PluginConfig.RifleRetentionBlendSpeed.Value);
                    break;

                case ERetentionState.Raising:
                    _swapTimer += deltaTime;
                    if (_swapTimer > SWAP_TIMEOUT || somethingElseInHands)
                    {
                        Cleanup();
                        return;
                    }
                    _blend = Mathf.MoveTowards(_blend, 0f, deltaTime * PluginConfig.RifleRetentionBlendSpeed.Value);
                    if (rifleInHands)
                    {
                        // The copy stands in for the real rifle until it has settled onto it.
                        HideRenderersUnder(handsController!.ControllerGameObject);
                        _realRifleReady = PlayerStateInstance.WeaponIsReady;
                    }
                    break;
            }

            UpdatePose(player, deltaTime);

            if (_state == ERetentionState.Raising && _realRifleReady && _blend <= 0f && TrySampleRealRiflePose(out Vector3 realPos, out Quaternion realRot))
            {
                _settleTimer += deltaTime;
                bool settled = Vector3.Distance(_smoothedLocalPosition, realPos) < SETTLE_POS_EPSILON
                    && Quaternion.Angle(_smoothedLocalRotation, realRot) < SETTLE_ANGLE_EPSILON;
                if (settled || _settleTimer > SETTLE_MAX_TIME)
                    Cleanup(); // reveals the real rifle in the same frame the copy goes away
            }
        }

        /// <summary>Camera-space pose of the real (hidden) rifle's weapon bone, once it is in hands.</summary>
        private bool TrySampleRealRiflePose(out Vector3 localPos, out Quaternion localRot)
        {
            localPos = Vector3.zero;
            localRot = Quaternion.identity;
            var hands = PlayerStateInstance.PWA.HandsContainer;
            Transform cam = hands.CameraTransform;
            Transform bone = _anchorBone == ECharacterWeaponBones.weapon && hands.Weapon != null ? hands.Weapon : hands.WeaponRoot;
            if (cam == null || bone == null) return false;
            localPos = cam.InverseTransformPoint(bone.position);
            localRot = Quaternion.Inverse(cam.rotation) * bone.rotation;
            return true;
        }

        private void OnToggle()
        {
            if (!PluginConfig.EnableRifleRetention.Value) return;

            if (_state == ERetentionState.Idle)
                TryStartRetention();
            else if (_state == ERetentionState.Retained)
                StartRaising();
        }

        private void OnManualSwap()
        {
            // The player picked another slot themselves: drop the illusion right away.
            if (_ignoreSwapInput || _state == ERetentionState.Idle) return;
            Cleanup();
        }

        private void TryStartRetention()
        {
            Player player = PlayerStateInstance.Player;
            if (player == null || !player.IsYourPlayer) return;
            if (WeaponStateInstance.TreatAsPistol) return;
            if (!(player.HandsController?.Item is Weapon rifle)) return;

            InventoryEquipment equipment = player.Equipment;
            var container = rifle.CurrentAddress?.Container;
            EquipmentSlot rifleSlot;
            if (ReferenceEquals(container, equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)))
                rifleSlot = EquipmentSlot.FirstPrimaryWeapon;
            else if (ReferenceEquals(container, equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon)))
                rifleSlot = EquipmentSlot.SecondPrimaryWeapon;
            else
                return;

            if (!(equipment.GetSlot(EquipmentSlot.Holster).ContainedItem is Weapon pistol)) return;

            var hands = PlayerStateInstance.PWA.HandsContainer;
            Transform cam = hands.CameraTransform;
            Transform heldBone = hands.Weapon != null ? hands.Weapon : hands.WeaponRoot;
            if (cam == null || heldBone == null) return;

            _rifle = rifle;
            _pistol = pistol;
            _rifleSlot = rifleSlot;

            if (!SpawnCopy(player, cam, heldBone))
            {
                Cleanup();
                return;
            }

            _patrolPoseValid = CaptureSpringState(cam, heldBone);
            EvaluateLoweredPose();

            HideRenderersUnder(player.HandsController?.ControllerGameObject);
            _blend = 0f;
            _swapTimer = 0f;
            _state = ERetentionState.Lowering;
            UpdatePose(player, 0f);

            _skipRemoveFor = rifle;
            _skipRemoveUntil = Time.time + SKIP_REMOVE_WINDOW;
            RequestSlot(player, EquipmentSlot.Holster);
        }

        private static Quaternion SpringEuler(Vector3 v)
        {
            // Same composition as ProceduralWeaponAnimation.DeferredRotateWithCustomOrder.
            return Quaternion.Euler(v.x, 0f, v.z) * Quaternion.Euler(0f, v.y, 0f);
        }

        /// <summary>
        /// Snapshots the hands-spring state of the held rifle at keypress, with the current stance's
        /// contribution removed, so the Patrol pose can be re-evaluated every frame (live tweaking)
        /// after the real rifle has left the hands.
        /// </summary>
        private bool CaptureSpringState(Transform cam, Transform heldBone)
        {
            var hands = PlayerStateInstance.PWA.HandsContainer;
            var stances = Plugin.StanceControllerInstance;
            Transform wra = hands.WeaponRootAnim;
            Transform? parent = wra != null ? wra.parent : null;
            if (wra == null || parent == null || stances == null || stances.PatrolStance == null) return false;

            // Totals currently applied to the weapon (the SpringGet patch already includes stance offsets).
            Vector3 curPosTotal = hands.HandsPosition.Get();
            Vector3 curRotTotal = hands.HandsRotation.Get();
            Vector3 curStancePos = stances.StancePositionSpring.Get() - stances.BaseWeaponOffsetPosition;

            _springCurPosTotal = curPosTotal;
            _springNonStancePos = curPosTotal - curStancePos;
            _springNonStanceRot = curRotTotal - stances.StanceRotationSpring.Get();

            // Undo the pivot rotation of the held pose (parent space).
            _springRotCenter = hands.RotationCenter;
            Quaternion heldRot = wra.localRotation;
            _springBaseRot = Quaternion.Inverse(SpringEuler(curRotTotal)) * heldRot;
            _springPrePivotPos = wra.localPosition - _springBaseRot * _springRotCenter + heldRot * _springRotCenter;

            // Held bone relative to WeaponRootAnim, and WeaponRootAnim's parent in camera space.
            _springRelPos = Quaternion.Inverse(wra.rotation) * (heldBone.position - wra.position);
            _springRelRot = Quaternion.Inverse(wra.rotation) * heldBone.rotation;
            _springParentCamPos = cam.InverseTransformPoint(parent.position);
            _springParentCamRot = Quaternion.Inverse(cam.rotation) * parent.rotation;
            return true;
        }

        /// <summary>
        /// Where the held rifle would be in the Patrol stance (+ tweaks), by replaying the game's
        /// hands-spring math: position offset on WeaponRootAnim, then rotation around RotationCenter.
        /// Writes the camera-space pose of the copy's anchor bone.
        /// </summary>
        private void EvaluateLoweredPose()
        {
            var stances = Plugin.StanceControllerInstance;
            if (!_patrolPoseValid || stances == null || stances.PatrolStance == null)
            {
                _loweredLocalPosition = _heldLocalPosition;
                _loweredLocalRotation = _heldLocalRotation;
                return;
            }

            Vector3 patrolPos = stances.PatrolStance.EnterPositionCurve.Evaluate(1f) + new Vector3(
                PluginConfig.RifleRetentionPosX.Value, PluginConfig.RifleRetentionPosY.Value, PluginConfig.RifleRetentionPosZ.Value);
            Vector3 patrolRot = stances.PatrolStance.EnterRotationCurve.Evaluate(1f) + new Vector3(
                PluginConfig.RifleRetentionPitch.Value, PluginConfig.RifleRetentionYaw.Value, PluginConfig.RifleRetentionRoll.Value);

            Vector3 tgtPosTotal = _springNonStancePos + patrolPos;
            Vector3 tgtRotTotal = _springNonStanceRot + patrolRot;

            Quaternion tgtRot = SpringEuler(tgtRotTotal) * _springBaseRot;
            Vector3 tgtPos = _springPrePivotPos + (tgtPosTotal - _springCurPosTotal)
                + _springBaseRot * _springRotCenter - tgtRot * _springRotCenter;

            // Parent space -> camera space, then carry the bone rigidly with WeaponRootAnim.
            Vector3 wraCamPos = _springParentCamPos + _springParentCamRot * tgtPos;
            Quaternion wraCamRot = _springParentCamRot * tgtRot;
            _loweredLocalPosition = wraCamPos + wraCamRot * _springRelPos;
            _loweredLocalRotation = wraCamRot * _springRelRot;
        }

        private void StartRaising()
        {
            Player player = PlayerStateInstance.Player;
            if (player == null) return;
            _swapTimer = 0f;
            _settleTimer = 0f;
            _realRifleReady = false;
            _smoothedLocalPosition = _anchor!.transform.localPosition;
            _smoothedLocalRotation = _anchor.transform.localRotation;
            _state = ERetentionState.Raising;

            _skipSpawnFor = _rifle;
            _skipSpawnUntil = Time.time + SKIP_SPAWN_WINDOW;
            RequestSlot(player, _rifleSlot);
        }

        private void RequestSlot(Player player, EquipmentSlot slot)
        {
            // Normal (non-quickdraw) swap: separate from the game's fast pistol draw and its stamina cost.
            player.FastSlotSelection = false;
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

        private bool SpawnCopy(Player player, Transform cam, Transform heldBone)
        {
            // Bundles are already loaded (the rifle is in hands), so the sync path is cheap.
            _rifleModel = Singleton<ObjectsFactory>.Instance.CreateItem(_rifle, false);
            if (_rifleModel == null) return false;
            PrepareModel(_rifleModel);

            _heldLocalPosition = cam.InverseTransformPoint(heldBone.position);
            _heldLocalRotation = Quaternion.Inverse(cam.rotation) * heldBone.rotation;

            _anchor = new GameObject("RetainedRifleAnchor");
            _anchor.transform.SetParent(cam, false);
            _anchor.transform.localPosition = _heldLocalPosition;
            _anchor.transform.localRotation = _heldLocalRotation;

            // Match the bone we sampled on the held rifle so the copy lands exactly on top of it.
            _anchorBone = heldBone == PlayerStateInstance.PWA.HandsContainer.Weapon ? ECharacterWeaponBones.weapon : ECharacterWeaponBones.Weapon_root;
            AlignModelToAnchor(_rifleModel, _anchor.transform, _anchorBone);

            _leftGrip = FindLeftGrip(_rifleModel);
            if (_leftGrip == null)
            {
                Debug.LogWarning("[StanceOverhaul] Rifle retention: no left-hand grip point found on " + _rifle!.ShortName);
                return false;
            }

            _fingersMapped = CanMapFingers(player, _leftGrip);
            if (_fingersMapped)
                player.HandPosers[0].MapIkPalm(_leftGrip);

            // The copy starts on top of the real rifle, where the left hand already is: no snap.
            player.LeftHandInteractionTarget = _leftGrip;
            player.ThirdIkWeight.Value = 1f;
            return true;
        }

        private void PrepareModel(GameObject model)
        {
            _disabledAnimator = model.GetComponentInChildren<UnityEngine.Animator>();
            if (_disabledAnimator != null && _disabledAnimator.enabled)
                _disabledAnimator.enabled = false;
            else
                _disabledAnimator = null;

            // Visual only: must not block shots, interactions or the player's own physics.
            _disabledColliders.Clear();
            foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled) continue;
                collider.enabled = false;
                _disabledColliders.Add(collider);
            }

            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;

            // Same layer setup the game uses for the first-person weapon.
            TransformTools.SetLayersRecursively<MeshRenderer>(model, LayerMask.NameToLayer("Player"), "Shells");
            model.SetActive(true);
        }

        /// <summary>Parents the model so the given bone coincides with the anchor (same math as PlayerBody.SlotView).</summary>
        private static void AlignModelToAnchor(GameObject model, Transform anchor, ECharacterWeaponBones bone)
        {
            model.transform.SetParent(anchor, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            TransformLinks links = model.GetComponentInChildren<TransformLinks>(true);
            Transform? target = links != null
                ? (links.GetTransformOutOfRangeSafe(bone) ?? links.GetTransformOutOfRangeSafe(ECharacterWeaponBones.Weapon_root))
                : null;
            if (target == null) return;

            model.transform.localRotation *= Quaternion.Inverse(target.rotation) * anchor.rotation;
            model.transform.position += anchor.position - target.position;
        }

        /// <summary>
        /// Hides the first-person model of the rifle (the hands controller's object; the arms are a
        /// separate body skin) while it is in hands, so only the copy is visible.
        /// Re-run every frame: attachments can be created after the model.
        /// </summary>
        private void HideRenderersUnder(GameObject? controllerObject)
        {
            if (controllerObject == null) return;

            foreach (Renderer renderer in controllerObject.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                _hiddenHeldRenderers.Add(renderer);
            }
        }

        /// <summary>
        /// Must run before the hidden object can be reused: the game pools first-person weapon models,
        /// so a model returned with renderers off would be invisible the next time it is drawn.
        /// </summary>
        private void RestoreHeldRenderers()
        {
            foreach (Renderer renderer in _hiddenHeldRenderers)
                if (renderer != null) renderer.enabled = true;
            _hiddenHeldRenderers.Clear();
        }

        /// <summary>Same selection the game uses for the first-person left grip (Player.UpdateFirstPersonGrip).</summary>
        private GripPose? FindLeftGrip(GameObject model)
        {
            GripPose? grip = model.GetComponentsInChildren<GripPose>(true)
                .Where(g => g.Hand == GripPose.EHand.Left &&
                    (g.GripType == GripPose.EGripType.Common || g.GripType == GripPose.EGripType.UnderbarrelWeapon))
                .OrderByDescending(g => g.GripType == GripPose.EGripType.UnderbarrelWeapon)
                .ThenByDescending(g => Depth(g.transform, model.transform))
                .FirstOrDefault();

            if (grip != null) return grip;

            // Fallback: the weapon's left-hand IK marker, without finger data.
            TransformLinks links = model.GetComponentInChildren<TransformLinks>(true);
            Transform? marker = links != null ? links.GetTransformOutOfRangeSafe(ECharacterWeaponBones.weapon_L_IK_marker) : null;
            if (marker == null) return null;

            _fallbackGripGO = new GameObject("RetainedRifleFallbackGrip");
            _fallbackGripGO.transform.SetParent(marker, false);
            GripPose fallback = _fallbackGripGO.AddComponent<GripPose>();
            fallback.Hand = GripPose.EHand.Left;
            fallback.GripType = GripPose.EGripType.Common;
            fallback.DontCache = true;
            return fallback;
        }

        private static int Depth(Transform child, Transform root)
        {
            int depth = 0;
            for (Transform t = child; t != null && t != root; t = t.parent)
                depth++;
            return depth;
        }

        /// <summary>
        /// HandPoser indexes finger data by its own bone count; mapping a grip with fewer
        /// entries would throw every frame inside the game's hand posing.
        /// </summary>
        private static bool CanMapFingers(Player player, GripPose grip)
        {
            if (player.HandPosers == null || player.HandPosers.Length == 0 || player.HandPosers[0] == null) return false;
            int needed = player.HandPosers[0].Children.Length;
            if (grip.IsCached)
                return grip.Fingers != null && grip.Fingers.Length >= needed;
            return grip.GetComponentsInChildren<Transform>().Length >= needed;
        }

        private void UpdatePose(Player player, float deltaTime)
        {
            Transform cam = PlayerStateInstance.PWA.HandsContainer.CameraTransform;
            if (cam != null && _anchor!.transform.parent != cam)
                _anchor.transform.SetParent(cam, false);

            float eased = Mathf.SmoothStep(0f, 1f, _blend);

            EvaluateLoweredPose(); // live: picks up tweak slider changes

            // "Up" end of the travel: where the rifle was held, or, when raising and the real rifle is
            // already in hands, exactly where it actually is (it comes back in idle, not the old stance).
            Vector3 upPos = _heldLocalPosition;
            Quaternion upRot = _heldLocalRotation;
            if (_state == ERetentionState.Raising && _realRifleReady && TrySampleRealRiflePose(out Vector3 realPos, out Quaternion realRot))
            {
                upPos = realPos;
                upRot = realRot;
            }

            Vector3 pos = Vector3.Lerp(upPos, _loweredLocalPosition, eased);
            Quaternion rot = Quaternion.Slerp(upRot, _loweredLocalRotation, eased);

            if (_state == ERetentionState.Raising)
            {
                // Smooth out the retarget from the estimated to the real pose instead of snapping.
                float t = 1f - Mathf.Exp(-SETTLE_SHARPNESS * deltaTime);
                _smoothedLocalPosition = Vector3.Lerp(_smoothedLocalPosition, pos, t);
                _smoothedLocalRotation = Quaternion.Slerp(_smoothedLocalRotation, rot, t);
                pos = _smoothedLocalPosition;
                rot = _smoothedLocalRotation;
            }

            _anchor!.transform.localPosition = pos;
            _anchor.transform.localRotation = rot;

            // Re-assert every frame: other systems (props, door interactions) also drive these.
            player.LeftHandInteractionTarget = _leftGrip;
            player.ThirdIkWeight.Value = 1f;
        }

        private void Cleanup()
        {
            _skipRemoveFor = null;
            _skipSpawnFor = null;
            _pendingSpawnSkip = null;
            _realRifleReady = false;
            _settleTimer = 0f;
            _patrolPoseValid = false;
            RestoreHeldRenderers();

            Player player = PlayerStateInstance.Player;
            if (player != null)
            {
                if (_leftGrip != null && player.LeftHandInteractionTarget == _leftGrip)
                {
                    player.LeftHandInteractionTarget = null;
                    player.ThirdIkWeight.Value = 0f;
                }
                if (_fingersMapped && player.HandPosers != null && player.HandPosers.Length > 0)
                    player.HandPosers[0].MapIkPalm(null);
            }

            if (_fallbackGripGO != null)
                UnityEngine.Object.Destroy(_fallbackGripGO);

            if (_rifleModel != null)
            {
                // Restore what we changed before handing the object back to the shared pool.
                foreach (Collider collider in _disabledColliders)
                    if (collider != null) collider.enabled = true;
                if (_disabledAnimator != null)
                    _disabledAnimator.enabled = true;
                AssetPoolObject.ReturnToPool(_rifleModel);
            }

            if (_anchor != null)
                UnityEngine.Object.Destroy(_anchor);

            _disabledColliders.Clear();
            _disabledAnimator = null;
            _fallbackGripGO = null;
            _rifleModel = null;
            _anchor = null;
            _leftGrip = null;
            _fingersMapped = false;
            _rifle = null;
            _pistol = null;
            _blend = 0f;
            _swapTimer = 0f;
            _state = ERetentionState.Idle;
        }
    }
}
