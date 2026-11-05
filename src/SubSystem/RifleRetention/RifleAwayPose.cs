using UnityEngine;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Where the retained rifle goes when it is put away (switching fully to the pistol, or to another
    /// weapon): lowered, twisted and moved a bit to the left side, out of view, the left hand still on it.
    /// A fixed pose, as a stance offset like <see cref="RetainedRiflePose"/>, not an offset from it: wherever
    /// the retained pose is tuned to, the rifle always ends out of view before the copy is removed. The away
    /// tweak sliders are added on top.
    /// </summary>
    internal static class RifleAwayPose
    {
        // Axes, from the stance curves and in-game tests: position X -left/+right, Y -forward/+back,
        // Z -down/+up. Rotation X pitch (+muzzle down, like low ready), Y roll/cant around the barrel (active
        // aim cants with Y), Z yaw (muzzle left/right).
        private static readonly Vector3 BasePosition = new Vector3(-0.043f, -0.035f, -0.298f);
        private static readonly Vector3 BaseRotation = new Vector3(33f, -50f, -50f);

        public static Vector3 Position => BasePosition + new Vector3(
            PluginConfig.RifleRetentionAwayPosX.Value, PluginConfig.RifleRetentionAwayPosY.Value, PluginConfig.RifleRetentionAwayPosZ.Value);

        public static Vector3 Rotation => BaseRotation + new Vector3(
            PluginConfig.RifleRetentionAwayRotX.Value, PluginConfig.RifleRetentionAwayRotY.Value, PluginConfig.RifleRetentionAwayRotZ.Value);
    }
}
