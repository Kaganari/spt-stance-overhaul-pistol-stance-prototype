using UnityEngine;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// The pose the retained rifle is lowered into, as a stance offset (same units and axes as the stance
    /// curves). It is retention's own pose, not read from any stance, so retuning a stance never moves the
    /// retained rifle. The base values started out as the Patrol stance's end pose; the tweak sliders are
    /// added on top.
    /// </summary>
    internal static class RetainedRiflePose
    {
        private static readonly Vector3 BasePosition = new Vector3(0.01f, -0.035f, 0.01f);
        private static readonly Vector3 BaseRotation = new Vector3(8f, -16f, -18f);

        public static Vector3 Position => BasePosition + new Vector3(
            PluginConfig.RifleRetentionPosX.Value, PluginConfig.RifleRetentionPosY.Value, PluginConfig.RifleRetentionPosZ.Value);

        public static Vector3 Rotation => BaseRotation + new Vector3(
            PluginConfig.RifleRetentionPitch.Value, PluginConfig.RifleRetentionYaw.Value, PluginConfig.RifleRetentionRoll.Value);
    }
}
