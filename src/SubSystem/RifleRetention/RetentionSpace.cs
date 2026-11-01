using EFT;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Reference space for every retention pose: the camera's parent, not "Cam" itself. Cam's own local
    /// rotation/position also carries freelook (head rotation), camera recoil and aim alignment, none of
    /// which move the weapon; anchored under Cam the copy swung with freelook. It lives under the player's
    /// CameraContainer, so it is stable across weapon swaps (the hands rig is rebuilt for every weapon).
    /// </summary>
    internal static class RetentionSpace
    {
        public static Transform? Root
        {
            get
            {
                Transform? cam = PlayerStateInstance.PWA?.HandsContainer?.CameraTransform;
                return cam != null && cam.parent != null ? cam.parent : cam;
            }
        }

        /// <summary>The in-hands weapon bone the copy is aligned to: "weapon", or "Weapon_root" when there is none.</summary>
        public static Transform? HeldWeaponBone(ECharacterWeaponBones bone)
        {
            var hands = PlayerStateInstance.PWA?.HandsContainer;
            if (hands == null) return null;
            return bone == ECharacterWeaponBones.weapon && hands.Weapon != null ? hands.Weapon : hands.WeaponRoot;
        }

        public static bool TryGetLocalPose(Transform? target, out Vector3 localPosition, out Quaternion localRotation)
        {
            Transform? root = Root;
            if (root == null || target == null)
            {
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                return false;
            }

            localPosition = root.InverseTransformPoint(target.position);
            localRotation = Quaternion.Inverse(root.rotation) * target.rotation;
            return true;
        }
    }
}
