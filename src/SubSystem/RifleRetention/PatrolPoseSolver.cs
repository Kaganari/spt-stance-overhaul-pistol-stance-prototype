using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Where the held rifle would sit in the Patrol stance (+ config tweaks), in <see cref="RetentionSpace"/>.
    /// Snapshots the hands-spring state at keypress with the current stance's contribution removed, then
    /// replays the game's spring math: position offset on WeaponRootAnim, then rotation around
    /// RotationCenter. Evaluated every frame, so the tweak sliders are live after the rifle left the hands.
    /// </summary>
    internal class PatrolPoseSolver
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
            if (wra == null || parent == null || stances == null || stances.PatrolStance == null)
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

        public bool TryEvaluate(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            var stances = Plugin.StanceControllerInstance;
            if (!_isValid || stances == null || stances.PatrolStance == null)
                return false;

            Vector3 patrolPos = stances.PatrolStance.EnterPositionCurve.Evaluate(1f) + new Vector3(
                PluginConfig.RifleRetentionPosX.Value, PluginConfig.RifleRetentionPosY.Value, PluginConfig.RifleRetentionPosZ.Value);
            Vector3 patrolRot = stances.PatrolStance.EnterRotationCurve.Evaluate(1f) + new Vector3(
                PluginConfig.RifleRetentionPitch.Value, PluginConfig.RifleRetentionYaw.Value, PluginConfig.RifleRetentionRoll.Value);

            Vector3 tgtPosTotal = _nonStancePos + patrolPos;
            Vector3 tgtRotTotal = _nonStanceRot + patrolRot;

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
