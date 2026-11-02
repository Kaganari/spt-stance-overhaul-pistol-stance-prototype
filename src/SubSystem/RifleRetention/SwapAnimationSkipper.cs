using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using static RealismCommonLib.Plugin;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Skips the game's own animations for the rifle being retained: "put the rifle away" when lowering, so
    /// the pistol draw starts immediately, and the rifle draw when raising, since the copy already showed the
    /// rifle coming up. Each skip is armed for one weapon right before the slot request and only fires
    /// within its window, so any other swap plays normally.
    /// </summary>
    internal class SwapAnimationSkipper
    {
        // Spawn is longer: the pistol holster animation plays before the rifle is spawned.
        private const float REMOVE_WINDOW = 1.5f;
        private const float SPAWN_WINDOW = 4f;

        private Weapon? _removeFor;
        private float _removeUntil;
        private Weapon? _spawnFor;
        private float _spawnUntil;
        private Player.FirearmController.SpawnOperation? _pendingSpawn;

        public void ArmRemove(Weapon rifle)
        {
            _removeFor = rifle;
            _removeUntil = Time.time + REMOVE_WINDOW;
        }

        public void ArmSpawn(Weapon rifle)
        {
            _spawnFor = rifle;
            _spawnUntil = Time.time + SPAWN_WINDOW;
        }

        public void Disarm()
        {
            _removeFor = null;
            _spawnFor = null;
            _pendingSpawn = null;
        }

        /// <summary>Remove.Start postfix. True once for the armed rifle: fast-forward its "put away" animation.</summary>
        public bool ConsumeRemove(Weapon weapon)
        {
            RifleRetentionDebug.Log($"Remove.Start weapon={RifleRetentionDebug.Name(weapon)} " +
                $"armedFor={RifleRetentionDebug.Name(_removeFor)} windowLeft={_removeUntil - Time.time:F2}s");

            if (_removeFor == null || !ReferenceEquals(weapon, _removeFor) || Time.time > _removeUntil) return false;
            _removeFor = null;
            RifleRetentionDebug.Log("  -> remove skipped (FastForward)");
            return true;
        }

        /// <summary>
        /// SpawnOperation.Start postfix, for the armed rifle: hides its model right away, before it can
        /// render a single frame, and defers the fast-forward to the next Update so the game's swap code has
        /// fully returned first.
        /// </summary>
        public void ConsumeSpawn(Player.FirearmController.SpawnOperation operation, HeldWeaponHider hider)
        {
            RifleRetentionDebug.Log($"SpawnOperation.Start weapon={RifleRetentionDebug.Name(operation.Weapon)} " +
                $"armedFor={RifleRetentionDebug.Name(_spawnFor)} windowLeft={_spawnUntil - Time.time:F2}s");

            if (_spawnFor == null || !ReferenceEquals(operation.Weapon, _spawnFor) || Time.time > _spawnUntil) return;
            _spawnFor = null;
            hider.Hide(operation.Controller.ControllerGameObject);
            _pendingSpawn = operation;
            RifleRetentionDebug.Log($"  -> spawn skip armed (fast-forward next frame), hidden renderers={hider.Count}");
        }

        /// <summary>Runs the deferred draw skip, if one is pending. Call from Update.</summary>
        public void RunPendingSpawn(Weapon? rifle)
        {
            var operation = _pendingSpawn;
            _pendingSpawn = null;
            if (operation == null || !ReferenceEquals(operation.Weapon, rifle)) return;

            // The draw animation is what normally fires this (chambering a round from the magazine on
            // draw); fire it ourselves so the ammo state matches. No-op when nothing was chambered.
            operation.OnAddAmmoInChamber();
            // Jumps the animator to idle and completes the draw (WeaponAppeared). No-op if already finished.
            operation.FastForward();

            RifleRetentionDebug.Log($"spawn skip run: op state={operation.State} animIdle={operation.FirearmsAnimator?.IsIdling()} " +
                $"currentOp={operation.Controller.CurrentOperation?.GetType().Name} weaponIsReady={PlayerStateInstance.WeaponIsReady}");
        }
    }
}
