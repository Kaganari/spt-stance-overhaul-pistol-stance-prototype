using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using RootMotion.FinalIK;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Visual-only copy of the rifle, held in the left hand while the pistol is out. The real rifle can't
    /// stay in hands (EFT holds one item), so a model of it is spawned with colliders and animator off,
    /// anchored in <see cref="RetentionSpace"/>, and the left hand is kept on its handguard grip by IK
    /// (the hand follows the rifle, never the other way around). First-person, local-only.
    /// </summary>
    internal class RetainedRifleCopy
    {
        private const float REACH_MARGIN = 0.97f;

        private readonly GameObject _model;
        private readonly HeldWeaponHider _modelHider = new HeldWeaponHider();
        private readonly GameObject _anchor;
        private GameObject? _fallbackGripGO;
        private UnityEngine.Animator? _disabledAnimator;
        private readonly List<Collider> _disabledColliders = new List<Collider>();
        private bool _fingersMapped;

        private Transform? _fovSpace;
        private bool _fovSquashed;
        private bool _fovLogged;

        public Transform Anchor => _anchor.transform;
        public ECharacterWeaponBones AnchorBone { get; }
        public GripPose? LeftGrip { get; private set; }

        /// <summary>Last reach clamp applied (mm), for the debug trace.</summary>
        public float LastReachClampMm { get; private set; }

        /// <summary>False once something outside the system destroyed the model or anchor.</summary>
        public bool IsAlive => _model != null && _anchor != null;

        /// <summary>Spawns the copy exactly on top of the held rifle, with the left hand on it. Null on failure.</summary>
        public static RetainedRifleCopy? Spawn(Player player, Weapon rifle, Transform space, Transform heldBone,
            Vector3 heldPosition, Quaternion heldRotation)
        {
            // Bundles are already loaded (the rifle is in hands), so the sync path is cheap.
            GameObject model = Singleton<ObjectsFactory>.Instance.CreateItem(rifle, false);
            if (model == null) return null;

            var copy = new RetainedRifleCopy(model, space, heldBone, heldPosition, heldRotation);
            if (!copy.AttachLeftHand(player))
            {
                RifleRetentionDebug.Warn("no left-hand grip point found on " + rifle.ShortName);
                copy.Destroy(player);
                return null;
            }
            return copy;
        }

        private RetainedRifleCopy(GameObject model, Transform space, Transform heldBone, Vector3 heldPosition, Quaternion heldRotation)
        {
            _model = model;
            PrepareModel();

            _anchor = new GameObject("RetainedRifleAnchor");
            _anchor.transform.SetParent(space, false);
            _anchor.transform.localPosition = heldPosition;
            _anchor.transform.localRotation = heldRotation;

            // Align the same bone that was sampled on the held rifle, so the copy lands exactly on top of it.
            AnchorBone = heldBone == PlayerStateInstance.PWA.HandsContainer.Weapon ? ECharacterWeaponBones.weapon : ECharacterWeaponBones.Weapon_root;
            AlignModelToAnchor();
        }

        public void SetLocalPose(Vector3 position, Quaternion rotation)
        {
            Transform? space = RetentionSpace.Root;
            if (space != null && _anchor.transform.parent != space)
                _anchor.transform.SetParent(space, false);

            _anchor.transform.localPosition = position;
            _anchor.transform.localRotation = rotation;
        }

        /// <summary>Re-assert every frame: other systems (props, door interactions) also drive these.</summary>
        public void HoldWithLeftHand(Player player)
        {
            player.LeftHandInteractionTarget = LeftGrip;
            player.ThirdIkWeight.Value = 1f;
        }

        /// <summary>
        /// Plays one of the weapon's own handling sounds, by the animation event name the game uses for it
        /// (e.g. "Holster", "HandOff"; the model lists them as "SndHolster", ...). The copy has no animator
        /// driving its sound player, so the clip is looked up and played directly, first person, on the
        /// weaponry channel like the game's own weapon sounds.
        /// </summary>
        public void PlaySound(string eventName)
        {
            BaseSoundPlayer? soundPlayer = _model.GetComponentInChildren<BaseSoundPlayer>(true);
            BaseSoundPlayer.SoundElement? sound = soundPlayer?.AdditionalSounds
                .Find(s => s != null && (s.EventName == eventName || s.EventName == "Snd" + eventName));
            AudioClip? clip = sound?.RandomSoundClip;
            if (clip == null || !Singleton<BetterAudio>.Instantiated)
            {
                RifleRetentionDebug.Log($"no '{eventName}' sound on {_model.name}");
                return;
            }

            Singleton<BetterAudio>.Instance.PlayNonspatial(clip, BetterAudio.AudioSourceGroupType.Weaponry, 0f, sound!.Volume);
        }

        /// <summary>Hides the model (renderers only): the left hand can still hold its grip point.</summary>
        public void HideModel()
        {
            _modelHider.Hide(_model);
        }

        /// <summary>
        /// Letting go: the fingers release, and the left hand's pull toward the grip fades to
        /// <paramref name="weight"/> (1 = on the grip, 0 = back to whatever the hands animation does).
        /// </summary>
        public void ReleaseLeftHand(Player player, float weight)
        {
            if (_fingersMapped && player.HandPosers != null && player.HandPosers.Length > 0)
            {
                player.HandPosers[0].MapIkPalm(null);
                _fingersMapped = false;
            }

            if (LeftGrip == null) return;
            if (weight > 0f)
            {
                player.LeftHandInteractionTarget = LeftGrip;
                player.ThirdIkWeight.Value = weight;
            }
            else if (player.LeftHandInteractionTarget == LeftGrip)
            {
                player.LeftHandInteractionTarget = null;
                player.ThirdIkWeight.Value = 0f;
            }
        }

        private void PrepareModel()
        {
            _disabledAnimator = _model.GetComponentInChildren<UnityEngine.Animator>();
            if (_disabledAnimator != null && _disabledAnimator.enabled)
                _disabledAnimator.enabled = false;
            else
                _disabledAnimator = null;

            // Visual only: must not block shots, interactions or the player's own physics.
            foreach (Collider collider in _model.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled) continue;
                collider.enabled = false;
                _disabledColliders.Add(collider);
            }

            foreach (Renderer renderer in _model.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;

            // Same layer setup the game uses for the first-person weapon.
            TransformTools.SetLayersRecursively<MeshRenderer>(_model, LayerMask.NameToLayer("Player"), "Shells");
            _model.SetActive(true);
        }

        /// <summary>Parents the model so its anchor bone coincides with the anchor (same math as PlayerBody.SlotView).</summary>
        private void AlignModelToAnchor()
        {
            Transform anchor = _anchor.transform;
            _model.transform.SetParent(anchor, false);
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;

            TransformLinks links = _model.GetComponentInChildren<TransformLinks>(true);
            Transform? target = links != null
                ? (links.GetTransformOutOfRangeSafe(AnchorBone) ?? links.GetTransformOutOfRangeSafe(ECharacterWeaponBones.Weapon_root))
                : null;
            if (target == null) return;

            _model.transform.localRotation *= Quaternion.Inverse(target.rotation) * anchor.rotation;
            _model.transform.position += anchor.position - target.position;
        }

        private bool AttachLeftHand(Player player)
        {
            LeftGrip = FindLeftGrip();
            if (LeftGrip == null) return false;

            _fingersMapped = CanMapFingers(player, LeftGrip);
            if (_fingersMapped)
                player.HandPosers[0].MapIkPalm(LeftGrip);

            // The copy starts on top of the real rifle, where the left hand already is: no snap.
            HoldWithLeftHand(player);
            return true;
        }

        /// <summary>Same selection the game uses for the first-person left grip (Player.UpdateFirstPersonGrip).</summary>
        private GripPose? FindLeftGrip()
        {
            // Active grips only, like the game (GetComponentsInChildren<GripPose>() without includeInactive):
            // an inactive one can be a disabled alternative grip point with nothing visible at it.
            GripPose? grip = _model.GetComponentsInChildren<GripPose>()
                .Where(g => g.Hand == GripPose.EHand.Left &&
                    (g.GripType == GripPose.EGripType.Common || g.GripType == GripPose.EGripType.UnderbarrelWeapon))
                .OrderByDescending(g => g.GripType == GripPose.EGripType.UnderbarrelWeapon)
                .ThenByDescending(g => Depth(g.transform, _model.transform))
                .FirstOrDefault();

            if (grip != null)
            {
                RifleRetentionDebug.Log($"grip chosen: {grip.name} type={grip.GripType}");
                return grip;
            }

            // Fallback: the weapon's left-hand IK marker, without finger data.
            RifleRetentionDebug.Log("no active left grip, falling back to weapon_L_IK_marker");
            TransformLinks links = _model.GetComponentInChildren<TransformLinks>(true);
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
        /// HandPoser indexes finger data by its own bone count; mapping a grip with fewer entries
        /// would throw every frame inside the game's hand posing.
        /// </summary>
        private static bool CanMapFingers(Player player, GripPose grip)
        {
            if (player.HandPosers == null || player.HandPosers.Length == 0 || player.HandPosers[0] == null) return false;
            int needed = player.HandPosers[0].Children.Length;
            if (grip.IsCached)
                return grip.Fingers != null && grip.Fingers.Length >= needed;
            return grip.GetComponentsInChildren<Transform>().Length >= needed;
        }

        /// <summary>
        /// If the grip is further from the left shoulder than the arm can reach, the IK leaves the hand
        /// short of it, in the air. Pulls the copy toward the shoulder just enough to make it reachable.
        /// </summary>
        public void ClampToArmReach(Player player, float loweredAmount)
        {
            LastReachClampMm = 0f;
            if (!PluginConfig.RifleRetentionClampReach.Value || LeftGrip == null) return;

            LimbIK? leftArm = LeftArmIK.Get(player);
            if (leftArm == null) return;
            var solver = leftArm.solver;
            if (solver.bone1?.transform == null || solver.bone2?.transform == null || solver.bone3?.transform == null) return;

            Vector3 shoulder = solver.bone1.transform.position;
            float reach = (Vector3.Distance(shoulder, solver.bone2.transform.position)
                + Vector3.Distance(solver.bone2.transform.position, solver.bone3.transform.position)) * REACH_MARGIN;

            Vector3 toGrip = LeftGrip.transform.position - shoulder;
            float distance = toGrip.magnitude;
            if (distance <= reach || distance < 0.0001f) return;

            // Scaled by how lowered the rifle is: at the held pose the arm is legitimately near full
            // extension, and the copy must stay exactly on the real rifle for the hand-over.
            float excess = (distance - reach) * loweredAmount;
            if (excess <= 0f) return;
            _anchor.transform.position -= toGrip / distance * excess;
            LastReachClampMm = excess * 1000f;
        }

        // EFT squashes the first-person arms and in-hands weapon toward the body for rendering (ribcage
        // local z scaled to 1..0.65 depending on FOV), then un-squashes at end of frame. IK runs unsquashed,
        // so the hand is exactly on the copy's grip there, but on screen the hand was drawn squashed and the
        // copy was not: it looked several inches short of the handguard. The copy gets the same squash.
        public void ApplyFovSquash(Player player)
        {
            if (_fovSquashed) return;

            Transform ribcage = player.PlayerBones.Ribcage.Original;
            float scale = ribcage.localScale.z;
            if (Mathf.Approximately(scale, 1f)) return;

            if (_fovSpace == null)
                _fovSpace = new GameObject("RetainedRifleFovSpace").transform;

            // Same pivot and axes the game scales the arms around (it has just set ribcage.rotation).
            _fovSpace.localScale = Vector3.one;
            _fovSpace.SetPositionAndRotation(ribcage.position, ribcage.rotation);
            _anchor.transform.SetParent(_fovSpace, true);
            _fovSpace.localScale = new Vector3(1f, 1f, scale);
            _fovSquashed = true;

            if (!_fovLogged)
            {
                RifleRetentionDebug.Log($"FOV squash mirrored on copy: ribcage z-scale={scale:F3}");
                _fovLogged = true;
            }
        }

        public void UndoFovSquash()
        {
            if (!_fovSquashed) return;
            _fovSquashed = false;
            if (_fovSpace != null)
                _fovSpace.localScale = Vector3.one; // first, so the reparent below keeps an unscaled pose
            if (_anchor != null)
                _anchor.transform.SetParent(RetentionSpace.Root, true);
        }

        /// <summary>Releases the left hand and hands the model back to the pool with everything restored.</summary>
        public void Destroy(Player? player)
        {
            UndoFovSquash(); // the anchor must be back in retention space before it is torn down

            if (player != null)
            {
                if (LeftGrip != null && player.LeftHandInteractionTarget == LeftGrip)
                {
                    player.LeftHandInteractionTarget = null;
                    player.ThirdIkWeight.Value = 0f;
                }
                if (_fingersMapped && player.HandPosers != null && player.HandPosers.Length > 0)
                    player.HandPosers[0].MapIkPalm(null);
            }

            if (_fallbackGripGO != null)
                UnityEngine.Object.Destroy(_fallbackGripGO);

            if (_model != null)
            {
                foreach (Collider collider in _disabledColliders)
                {
                    if (collider != null)
                        collider.enabled = true;
                }
                if (_disabledAnimator != null)
                    _disabledAnimator.enabled = true;
                _modelHider.RestoreAll();
                AssetPoolObject.ReturnToPool(_model);
            }

            if (_fovSpace != null)
                UnityEngine.Object.Destroy(_fovSpace.gameObject);

            if (_anchor != null)
                UnityEngine.Object.Destroy(_anchor);

            _disabledColliders.Clear();
            _disabledAnimator = null;
            _fallbackGripGO = null;
            _fovSpace = null;
            LeftGrip = null;
            _fingersMapped = false;
        }
    }
}
