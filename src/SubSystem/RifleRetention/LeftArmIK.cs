using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using System.Reflection;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>The player's left arm IK limb (Player._limbs[0]), which keeps the left hand on the copy.</summary>
    internal static class LeftArmIK
    {
        private static readonly FieldInfo _limbsField = AccessTools.Field(typeof(Player), "_limbs");

        public static LimbIK? Get(Player player)
        {
            return _limbsField.GetValue(player) is LimbIK[] limbs && limbs.Length > 0 ? limbs[0] : null;
        }
    }
}
