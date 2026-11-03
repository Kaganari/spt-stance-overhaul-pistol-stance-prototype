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
    /// </summary>
    internal class LoweredPoseSolver
    {
        private bool _isValid;

        private Vector3 _curPosTotal;
        private Vector3 _nonStancePos;
        private Vector3 _nonStanceRot;
        private Vector3 _rotCenter;
        private Quaternion _baseRot;
        private Vector3 _prePivotPos;

        // Held bone relative to WeaponRootAnim, and WeaponRootAnim's parent in retention space.
        private Vector3 _boneRelPos;
        private Quaternion _boneRelRot;
        private Vector3 _parentPos;
        private Quaternion _parentRot;

        public bool Capture(Transform space, Transform heldBone)
        {
            var hands = PlayerStateInstance.PWA.HandsContainer;
            var stances = Plugin.StanceControllerInstance;
            Transform wra = hands.WeaponRootAnim;
            Transform? parent = wra != null ? wra.parent : null;
            if (wra == null || parent == null || stances == null)
            {
                _isValid = false;
                return false;
            }

            // Totals currently applied to the weapon (SpringGetPatch already includes the stance springs).
            Vector3 curPosTotal = hands.HandsPosition.Get();
            Vector3 curRotTotal = hands.HandsRotation.Get();
            Vector3 curStancePos = stances.StancePositionSpring.Get() - stances.BaseWeaponOffsetPosition;

            _curPosTotal = curPosTotal;
            _nonStancePos = curPosTotal - curStancePos;
            _nonStanceRot = curRotTotal - stances.StanceRotationSpring.Get();

            // Undo the pivot rotation of the held pose (parent space).
            _rotCenter = hands.RotationCenter;
            Quaternion heldRot = wra.localRotation;
            _baseRot = Quaternion.Inverse(SpringEuler(curRotTotal)) * heldRot;
            _prePivotPos = wra.localPosition - _baseRot * _rotCenter + heldRot * _rotCenter;

            _boneRelPos = Quaternion.Inverse(wra.rotation) * (heldBone.position - wra.position);
            _boneRelRot = Quaternion.Inverse(wra.rotation) * heldBone.rotation;
            _parentPos = space.InverseTransformPoint(parent.position);
            _parentRot = Quaternion.Inverse(space.rotation) * parent.rotation;

            _isValid = true;
            return true;
        }

        public void Reset()
        {
            _isValid = false;
        }

        /// <param name="targetPosition">Stance position offset, in the same units and axes as the stance curves.</param>
        /// <param name="targetRotation">Stance rotation offset (degrees), same axes as the stance curves.</param>
        public bool TryEvaluate(Vector3 targetPosition, Vector3 targetRotation, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!_isValid)
                return false;

            Vector3 tgtPosTotal = _nonStancePos + targetPosition;
            Vector3 tgtRotTotal = _nonStanceRot + targetRotation;

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
