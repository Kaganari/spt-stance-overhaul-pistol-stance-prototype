using EFT;
using EFT.Animations;
using EFT.InputSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using RootMotion.FinalIK;
using SPT.Reflection.Patching;
using StanceOverhaul.SubSystem.RifleRetention;
using System;
using System.Reflection;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.Patches
{
    /// <summary>
    /// The swap animations free the left hand via the LEFT_HAND_WEIGHT curve, which zeroes the left limb
    /// IK weight and makes IkProcess skip it entirely. While the retained rifle copy is shown, keep the left
    /// arm fully on IK so it stays on the handguard. Also where the copy is placed each frame: after the
    /// game's procedural motion, before the left arm is solved onto it.
    /// </summary>
    public class RetentionLeftHandIKPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.IkProcess));
        }

        [PatchPrefix]
        private static void Prefix(Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return;

            RifleRetentionSystem.OnBeforeLeftArmIK(__instance);

            if (!RifleRetentionSystem.ForceLeftHandIK)
                return;

            LimbIK? leftArm = LeftArmIK.Get(__instance);
            if (leftArm == null)
                return;

            leftArm.solver.IKPositionWeight = 1f;
            leftArm.solver.IKRotationWeight = 1f;
            if (__instance.HandPosers != null && __instance.HandPosers.Length > 0 && __instance.HandPosers[0] != null)
                __instance.HandPosers[0].weight = 1f;
        }
    }

    /// <summary>
    /// Steps the retained rifle's own springs (<see cref="RetainedRifleMotion"/>) in the same fixed steps as
    /// the game's: their math is per step, not per second. Runs before IkProcess in the same frame.
    /// </summary>
    public class RetentionProcessEffectorsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ProcessEffectors));
        }

        [PatchPostfix]
        private static void Postfix(ProceduralWeaponAnimation __instance, float deltaTime, int nFixedFrames)
        {
            RifleRetentionSystem.OnProceduralStep(__instance, deltaTime, nFixedFrames);
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
    /// Two-handed weapon actions started from keybinds. The translator turns key presses into hands
    /// actions, so this catches them before any animation starts, including the paths that skip
    /// CanStartReload (reload with no spare magazine chambers a loose round instead). What to do while
    /// the rifle is retained is up to the retention system's per-action policy.
    /// </summary>
    public class RetentionWeaponActionInputPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(FirearmHandsInputTranslator), nameof(FirearmHandsInputTranslator.TranslateCommand));
        }

        [PatchPrefix]
        private static bool Prefix(FirearmHandsInputTranslator __instance, ECommand command, ref InputNode.ETranslateResult __result)
        {
            if (!(__instance._controller is Player.FirearmController controller)
                || !ReferenceEquals(controller, PlayerStateInstance.Player?.HandsController))
                return true;

            ERetentionAction? action = ToAction(command, controller);
            if (action == null || RifleRetentionSystem.AllowAction(action.Value, controller))
                return true;

            // TranslateCommand always reports Ignore for these commands, so skipping it changes nothing else.
            __result = InputNode.ETranslateResult.Ignore;
            return false;
        }

        private static ERetentionAction? ToAction(ECommand command, Player.FirearmController controller)
        {
            bool malfunctioned = controller.Item.MalfState.State != Weapon.EMalfunctionState.None;
            switch (command)
            {
                case ECommand.ReloadWeapon:
                case ECommand.QuickReloadWeapon:
                    return ERetentionAction.Reload;
                case ECommand.CheckAmmo:
                    return ERetentionAction.CheckAmmo;
                case ECommand.CheckChamber:
                    // With a known malfunction this starts the repair.
                    return malfunctioned ? ERetentionAction.ClearMalfunction : ERetentionAction.CheckChamber;
                case ECommand.UnloadMagazine:
                    // With a feed malfunction this examines the malfunction instead.
                    return controller.Item.MalfState.State == Weapon.EMalfunctionState.Feed
                        ? ERetentionAction.ClearMalfunction : ERetentionAction.UnloadMagazine;
                case ECommand.ChamberUnload:
                    return ERetentionAction.UnloadChamber;
                case ECommand.ExamineWeapon:
                    return ERetentionAction.InspectWeapon;
                case ECommand.ChangeWeaponMode:
                case ECommand.ForceAutoWeaponMode:
                    // Only while malfunctioned: then the fire mode keys examine the malfunction instead.
                    return malfunctioned ? ERetentionAction.ClearMalfunction : (ERetentionAction?)null;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Every reload path asks CanStartReload first, including the ones that don't come from a keybind
    /// (inventory drag-and-drop of a magazine or ammo onto the weapon in hands).
    /// </summary>
    public class RetentionReloadPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.CanStartReload));
        }

        [PatchPrefix]
        private static bool Prefix(Player.FirearmController __instance, ref bool __result)
        {
            // Bots and other players go through this too: only the local player's hands are affected.
            if (!ReferenceEquals(__instance, PlayerStateInstance.Player?.HandsController))
                return true;

            if (RifleRetentionSystem.AllowAction(ERetentionAction.Reload, __instance))
                return true;

            __result = false;
            return false;
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
