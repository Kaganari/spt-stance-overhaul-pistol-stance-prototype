using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using RootMotion.FinalIK;
using SPT.Reflection.Patching;
using StanceOverhaul.SubSystem.RifleRetention;
using System;
using System.Reflection;
using static StanceOverhaul.Plugin;

namespace StanceOverhaul.Patches
{
    /// <summary>
    /// The swap animations free the left hand via the LEFT_HAND_WEIGHT curve, which zeroes the left limb
    /// IK weight and makes IkProcess skip it entirely. While the retained rifle copy is shown, keep the left
    /// arm fully on IK so it stays on the handguard.
    /// </summary>
    public class RetentionLeftHandIKPatch : ModulePatch
    {
        private static FieldInfo _limbsField = null!;

        protected override MethodBase GetTargetMethod()
        {
            _limbsField = AccessTools.Field(typeof(Player), "_limbs");
            return AccessTools.Method(typeof(Player), nameof(Player.IkProcess));
        }

        [PatchPrefix]
        private static void Prefix(Player __instance)
        {
            if (!__instance.IsYourPlayer || StanceControllerInstance == null || !StanceControllerInstance.ForceRetentionLeftHandIK)
                return;

            LimbIK[] limbs = (LimbIK[])_limbsField.GetValue(__instance);
            if (limbs == null || limbs.Length == 0 || limbs[0] == null)
                return;

            limbs[0].solver.IKPositionWeight = 1f;
            limbs[0].solver.IKRotationWeight = 1f;
            if (__instance.HandPosers != null && __instance.HandPosers.Length > 0 && __instance.HandPosers[0] != null)
                __instance.HandPosers[0].weight = 1f;
        }
    }

    /// <summary>
    /// Skips the "put the rifle away" animation. The game already has an instant path in Remove.Start
    /// (Start + FastForward, used for simplified skeletons); it is taken only for the one rifle the
    /// retention system just asked to swap away from.
    /// </summary>
    public class RetentionSkipRifleRemovePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player.FirearmController.Remove), nameof(Player.FirearmController.Remove.Start),
                new[] { typeof(Action), typeof(bool), typeof(Item) });
        }

        [PatchPostfix]
        private static void Postfix(Player.FirearmController.Remove __instance)
        {
            if (RifleRetentionSystem.ConsumeSkipRemove(__instance.Player, __instance.Weapon))
                __instance.FastForward();
        }
    }

    /// <summary>
    /// Switching back: skips the rifle's draw animation. The retained copy has already shown the rifle
    /// coming up, so the real rifle should just be there.
    /// </summary>
    public class RetentionSkipRifleSpawnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player.FirearmController.SpawnOperation), nameof(Player.FirearmController.SpawnOperation.Start),
                new[] { typeof(Action) });
        }

        [PatchPostfix]
        private static void Postfix(Player.FirearmController.SpawnOperation __instance)
        {
            RifleRetentionSystem.ConsumeSkipSpawn(__instance);
        }
    }

    /// <summary>Tells the retention system when a local firearm draw has really completed.</summary>
    public class RetentionWeaponAppearedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player.FirearmController.SpawnOperation), nameof(Player.FirearmController.SpawnOperation.WeaponAppeared));
        }

        [PatchPostfix]
        private static void Postfix(Player.FirearmController.SpawnOperation __instance)
        {
            RifleRetentionSystem.OnWeaponAppeared(__instance);
        }
    }

    /// <summary>
    /// EFT squashes the first-person arms/weapon toward the body for rendering (FOV compensation) and
    /// un-squashes at end of frame. Mirrors that squash onto the retained rifle copy, otherwise the hand
    /// is drawn short of the copy's handguard.
    /// </summary>
    public class RetentionFovApplyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(FirstPersonStrategy), nameof(FirstPersonStrategy.ApplyFovAdjustments));
        }

        [PatchPostfix]
        private static void Postfix(Player player)
        {
            RifleRetentionSystem.OnFovAdjustmentsApplied(player);
        }
    }

    public class RetentionFovResetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(FirstPersonStrategy), nameof(FirstPersonStrategy.ResetFovAdjustments));
        }

        [PatchPostfix]
        private static void Postfix()
        {
            RifleRetentionSystem.OnFovAdjustmentsReset();
        }
    }
}
