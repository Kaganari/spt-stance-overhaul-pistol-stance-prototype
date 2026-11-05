using EFT;
using HarmonyLib;
using RootMotion.FinalIK;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>The player's left arm IK limb (Player._limbs[0]), which keeps the left hand on the copy.</summary>
    internal static class LeftArmIK
    {
        private static readonly AccessTools.FieldRef<Player, LimbIK[]> _limbs =
            AccessTools.FieldRefAccess<Player, LimbIK[]>("_limbs");

        public static LimbIK? Get(Player player)
        {
            LimbIK[] limbs = _limbs(player);
            return limbs != null && limbs.Length > 0 ? limbs[0] : null;
        }
    }
}
