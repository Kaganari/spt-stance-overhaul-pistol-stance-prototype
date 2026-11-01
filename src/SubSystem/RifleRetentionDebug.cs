using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem
{
    /// <summary>
    /// Diagnostic logging for the rifle retention prototype. Everything is gated behind the
    /// "Debug Logging" config toggle and goes to BepInEx/LogOutput.log under "StanceOverhaul.Retention".
    /// Times are relative to the last retention keypress: "T+0.123 f12" = 0.123 s / 12 frames after it.
    /// </summary>
    internal static class RifleRetentionDebug
    {
        private static readonly ManualLogSource _log = BepInEx.Logging.Logger.CreateLogSource("StanceOverhaul.Retention");

        private static float _t0;
        private static int _f0;
        private static bool _hasLastRealPos;
        private static Vector3 _lastRealPos;

        public static bool Enabled => PluginConfig.RifleRetentionDebug != null && PluginConfig.RifleRetentionDebug.Value;

        /// <summary>Resets the time origin. Call on every retention keypress.</summary>
        public static void MarkKeypress()
        {
            _t0 = Time.time;
            _f0 = Time.frameCount;
            _hasLastRealPos = false;
        }

        public static void Log(string message)
        {
            if (!Enabled) return;
            _log.LogInfo($"T+{Time.time - _t0:F3} f{Time.frameCount - _f0} | {message}");
        }

        public static string Name(Item? item)
        {
            if (item == null) return "null";
            return item.ShortName + "#" + item.Id.Substring(0, Mathf.Min(6, item.Id.Length));
        }

        public static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";
        public static string E(Quaternion q) => V3Deg(q.eulerAngles);
        private static string V3Deg(Vector3 v) => $"({Wrap(v.x):F1},{Wrap(v.y):F1},{Wrap(v.z):F1})";
        private static float Wrap(float a) => a > 180f ? a - 360f : a;

        /// <summary>
        /// One line describing everything that could move the real rifle: hands controller and its
        /// current operation, animator idle state, real weapon bone pose (camera space) and its
        /// per-frame movement, the copy's pose, and every spring/stance input that feeds the weapon.
        /// </summary>
        public static void TraceFrame(string phase, string state, float blend, bool realReady,
            Transform? copyAnchor, ECharacterWeaponBones anchorBone)
        {
            if (!Enabled) return;

            Player player = PlayerStateInstance.Player;
            var pwa = PlayerStateInstance.PWA;
            if (player == null || pwa == null) return;

            var hands = pwa.HandsContainer;
            var hc = player.HandsController;
            string handsInfo = hc == null ? "hands=null" : $"hands={Name(hc.Item)} ctrl={hc.GetType().Name}";

            string opInfo = "op=-";
            if (hc is Player.FirearmController fc && fc.CurrentOperation != null)
            {
                var op = fc.CurrentOperation;
                string anim = op.FirearmsAnimator != null
                    ? $" animIdle={op.FirearmsAnimator.IsIdling()} animBusy={op.FirearmsAnimator.IsHandsProcessing()}"
                    : "";
                opInfo = $"op={op.GetType().Name}/{op.State}{anim}";
            }

            string realInfo = "real=?";
            Transform cam = hands.CameraTransform;
            Transform bone = anchorBone == ECharacterWeaponBones.weapon && hands.Weapon != null ? hands.Weapon : hands.WeaponRoot;
            Vector3 realPos = Vector3.zero;
            Quaternion realRot = Quaternion.identity;
            if (cam != null && bone != null)
            {
                realPos = cam.InverseTransformPoint(bone.position);
                realRot = Quaternion.Inverse(cam.rotation) * bone.rotation;
                float moved = _hasLastRealPos ? (realPos - _lastRealPos).magnitude * 1000f : 0f;
                _lastRealPos = realPos;
                _hasLastRealPos = true;
                realInfo = $"real pos={V(realPos)} rot={E(realRot)} moved={moved:F1}mm";
            }

            string copyInfo = "copy=none";
            if (copyAnchor != null)
            {
                Vector3 cp = copyAnchor.localPosition;
                Quaternion cr = copyAnchor.localRotation;
                copyInfo = $"copy pos={V(cp)} rot={E(cr)} d={(cp - realPos).magnitude * 1000f:F1}mm/{Quaternion.Angle(cr, realRot):F1}deg";
            }

            string springInfo = "";
            var sc = Plugin.StanceControllerInstance;
            if (sc != null && sc.AwakeRan)
            {
                springInfo =
                    $" stance={sc.CurrentStanceType} stPos={V(sc.StancePosition)} stRot={V(sc.StanceRotation)}" +
                    $" stSprP={V(sc.StancePositionSpring.Get())} stSprR={V(sc.StanceRotationSpring.Get())}" +
                    $" offSprP={V(sc.OffsetPositionSpring.Get())} offSprR={V(sc.OffsetRotationSpring.Get())}" +
                    $" baseOff={V(sc.BaseWeaponOffsetPosition)}";
            }

            Log($"[{phase}] state={state} blend={blend:F3} realReady={realReady} weaponIsReady={PlayerStateInstance.WeaponIsReady} " +
                $"{handsInfo} {opInfo} | {realInfo} | {copyInfo} |" +
                $" handsP={V(hands.HandsPosition.Get())} handsR={V(hands.HandsRotation.Get())}{springInfo}");
        }
    }
}
