using EFT;
using EFT.Communications;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>Weapon actions whose animations need the left hand, which is holding the rifle copy.</summary>
    internal enum ERetentionAction
    {
        Reload,
        CheckAmmo,
        CheckChamber,
        UnloadMagazine,
        UnloadChamber,
        InspectWeapon,
        ClearMalfunction
    }

    /// <summary>
    /// Decides what happens when the player starts one of the <see cref="ERetentionAction"/>s while the rifle
    /// is retained. RifleRetentionSystem holds one policy per action; to change an action's behavior, add a
    /// policy and swap its entry there. Nothing else needs to know.
    /// </summary>
    internal interface IRetentionActionPolicy
    {
        /// <summary>
        /// True lets the game run the action as normal, false refuses it. A policy that wants a different
        /// behavior (e.g. release the rifle first, then reload) refuses here and drives it itself through
        /// the retention system.
        /// </summary>
        bool CanStart(ERetentionAction action, Player.FirearmController controller);
    }

    /// <summary>Refuses the action, with an on-screen hint.</summary>
    internal class BlockActionPolicy : IRetentionActionPolicy
    {
        public static readonly BlockActionPolicy Instance = new BlockActionPolicy();

        public bool CanStart(ERetentionAction action, Player.FirearmController controller)
        {
            NotificationManager.DisplaySingletonWarningNotification($"Can't {Describe(action)} one-handed");
            RifleRetentionDebug.Log($"{action} blocked on {RifleRetentionDebug.Name(controller.Item)}");
            return false;
        }

        private static string Describe(ERetentionAction action)
        {
            switch (action)
            {
                case ERetentionAction.Reload: return "reload";
                case ERetentionAction.CheckAmmo: return "check the magazine";
                case ERetentionAction.CheckChamber: return "check the chamber";
                case ERetentionAction.UnloadMagazine: return "unload the magazine";
                case ERetentionAction.UnloadChamber: return "unload the chamber";
                case ERetentionAction.InspectWeapon: return "inspect the weapon";
                case ERetentionAction.ClearMalfunction: return "deal with the malfunction";
                default: return "do that";
            }
        }
    }
}
