using EFT.Animations;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Where the held rifle would sit with a given stance offset applied, in <see cref="RetentionSpace"/>.
    /// Snapshots the hands-spring state at keypress with the current stance's contribution removed, then
    /// replays the game's spring math: position offset on WeaponRootAnim, then rotation around
    /// RotationCenter. The target offset is passed in on every evaluation, so it can change live (tweak
    /// sliders) after the rifle left the hands. It knows nothing about which pose is asked for; see
    /// <see cref="RetainedRiflePose"/>.
    ///
    /// Every evaluation also adds the rifle's own procedural motion (<see cref="RetainedRifleMotion"/>), so
    /// it moves by itself rather than hanging still.
    /// </summary>
    internal class LoweredPoseSolver
    {
        // Set while a capture is valid.
        private RetainedRifleMotion? _motion;

        private Vector3 _curPosTotal;
        private Vector3 _nonStancePos;
        private Vector3 _nonStanceRot;
        private Vector3 _heldStancePos;
        private Vector3 _heldStanceRot;
        private Vector3 _rotCenter;
        private Quaternion _baseRot;
        private Vector3 _prePivotPos;

        // Held bone relative to WeaponRootAnim, and WeaponRootAnim's parent in retention space.
        private Vector3 _boneRelPos;
        private Quaternion _boneRelRot;
        private Vector3 _parentPos;
        private Quaternion _parentRot;

        /// <summary>The stance offset the rifle was held in at keypress: the "up" end of its travel.</summary>
        public Vector3 HeldPosition => _heldStancePos;
        public Vector3 HeldRotation => _heldStanceRot;

        public bool Capture(Transform space, Transform heldBone)
        {
            ProceduralWeaponAnimation pwa = PlayerStateInstance.PWA;
            var hands = pwa.HandsContainer;
            var stances = Plugin.StanceControllerInstance;
            Transform wra = hands.WeaponRootAnim;
            Transform? parent = wra != null ? wra.parent : null;
            if (wra == null || parent == null || stances == null)
            {
                _motion = null;
                return false;
            }

            // Totals currently applied to the weapon (SpringGetPatch already includes the stance springs).
            _curPosTotal = hands.HandsPosition.Get();
            Vector3 curRotTotal = hands.HandsRotation.Get() + RetainedRifleMotion.GameSwayRotation(pwa);
            _heldStancePos = stances.StancePositionSpring.Get() - stances.BaseWeaponOffsetPosition;
            _heldStanceRot = stances.StanceRotationSpring.Get();

            // Without the motion of this moment: it is added back live, or a key press during a mouse
            // turn would stay baked into the pose.
            _motion = new RetainedRifleMotion(pwa);
            _nonStancePos = _curPosTotal - _heldStancePos - _motion.Position;
            _nonStanceRot = curRotTotal - _heldStanceRot - _motion.Rotation;

            // Undo the pivot rotation of the held pose (parent space).
            _rotCenter = hands.RotationCenter;
            Quaternion heldRot = wra.localRotation;
            _baseRot = Quaternion.Inverse(SpringEuler(curRotTotal)) * heldRot;
            _prePivotPos = wra.localPosition - _baseRot * _rotCenter + heldRot * _rotCenter;

            _boneRelPos = Quaternion.Inverse(wra.rotation) * (heldBone.position - wra.position);
            _boneRelRot = Quaternion.Inverse(wra.rotation) * heldBone.rotation;
            _parentPos = space.InverseTransformPoint(parent.position);
            _parentRot = Quaternion.Inverse(space.rotation) * parent.rotation;

            return true;
        }

        public void Reset()
        {
            _motion = null;
        }

        public void StepMotion(ProceduralWeaponAnimation pwa, float deltaTime, int nFixedFrames)
        {
            _motion?.Step(pwa, deltaTime, nFixedFrames);
        }

        /// <param name="targetPosition">Stance position offset, in the same units and axes as the stance curves.</param>
        /// <param name="targetRotation">Stance rotation offset (degrees), same axes as the stance curves.</param>
        public bool TryEvaluate(Vector3 targetPosition, Vector3 targetRotation, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (_motion == null)
                return false;

            Vector3 tgtPosTotal = _nonStancePos + targetPosition + _motion.Position;
            Vector3 tgtRotTotal = _nonStanceRot + targetRotation + _motion.Rotation;

            Quaternion tgtRot = SpringEuler(tgtRotTotal) * _baseRot;
            Vector3 tgtPos = _prePivotPos + (tgtPosTotal - _curPosTotal)
                + _baseRot * _rotCenter - tgtRot * _rotCenter;

            // Parent space -> retention space, then carry the bone rigidly with WeaponRootAnim.
            Vector3 wraPos = _parentPos + _parentRot * tgtPos;
            Quaternion wraRot = _parentRot * tgtRot;
            position = wraPos + wraRot * _boneRelPos;
            rotation = wraRot * _boneRelRot;
            return true;
        }

        // Same composition as ProceduralWeaponAnimation.DeferredRotateWithCustomOrder.
        private static Quaternion SpringEuler(Vector3 v)
        {
            return Quaternion.Euler(v.x, 0f, v.z) * Quaternion.Euler(0f, v.y, 0f);
        }
    }
}
