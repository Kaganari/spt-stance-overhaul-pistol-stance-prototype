using System.Collections.Generic;
using UnityEngine;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Hides a first-person weapon model: the real weapon (the hands controller's object; the arms are a
    /// separate body skin) while the copy stands in for it, or the copy itself once it is out of view.
    /// Everything hidden MUST be restored before the object can be reused: the game pools first-person
    /// weapon models, so one returned with its renderers off would be invisible the next time it is drawn.
    /// </summary>
    internal class HeldWeaponHider
    {
        private readonly List<Renderer> _hidden = new List<Renderer>();

        public int Count => _hidden.Count;

        /// <summary>Safe to call every frame: attachments can be created after the model.</summary>
        public void Hide(GameObject? controllerObject)
        {
            if (controllerObject == null) return;

            foreach (Renderer renderer in controllerObject.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                _hidden.Add(renderer);
            }
        }

        public void RestoreAll()
        {
            foreach (Renderer renderer in _hidden)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }
            _hidden.Clear();
        }
    }
}
