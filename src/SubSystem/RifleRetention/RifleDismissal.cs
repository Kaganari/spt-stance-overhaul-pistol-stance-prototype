using EFT;
using UnityEngine;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// Putting the retained rifle away: the left hand takes the copy from where it hangs to
    /// <see cref="RifleAwayPose"/> (out of view), the copy is hidden there, then the hand lets go and returns
    /// to the pistol. One instance per dismissal.
    /// </summary>
    internal class RifleDismissal
    {
        private readonly RetainedRifleCopy _copy;
        private readonly RiflePoseSolver _pose;

        private float _elapsed;
        private float _releaseElapsed;
        private bool _copyHidden;
        private bool _fromCaptured;
        private Vector3 _fromPosition;
        private Quaternion _fromRotation;

        public bool HandReleased { get; private set; }

        private float Progress => Mathf.Clamp01(_elapsed * PluginConfig.RifleRetentionDismissSpeed.Value);

        public RifleDismissal(RetainedRifleCopy copy, RiflePoseSolver pose)
        {
            _copy = copy;
            _pose = pose;
            // The rifle's own put-away sound; its "hand off" plays once the hand lets go.
            _copy.PlaySound("Holster");
        }

        /// <summary>True once the hand has let go and the dismissal is over.</summary>
        public bool Update(float deltaTime)
        {
            _elapsed += deltaTime;
            if (Progress < 1f) return false;

            // Out of view by now: hiding it can't be seen. The hand still holds its grip point.
            if (!_copyHidden)
            {
                _copy.HideModel();
                _copy.PlaySound("HandOff");
                _copyHidden = true;
                RifleRetentionDebug.Log("dismiss: rifle out of view, hidden; left hand letting go");
            }

            _releaseElapsed += deltaTime;
            return HandReleased;
        }

        public void ApplyPose(Player player)
        {
            // Captured here, not on the key press: input runs mid-frame, the pose is set later in the frame.
            if (!_fromCaptured)
            {
                _fromPosition = _copy.Anchor.localPosition;
                _fromRotation = _copy.Anchor.localRotation;
                _fromCaptured = true;
            }

            // A fixed end pose, so it always ends out of view whatever the retained pose is tuned to.
            _pose.Evaluate(RifleAwayPose.Position, RifleAwayPose.Rotation, out Vector3 awayPos, out Quaternion awayRot);

            float eased = Mathf.SmoothStep(0f, 1f, Progress);
            _copy.SetLocalPose(Vector3.Lerp(_fromPosition, awayPos, eased), Quaternion.Slerp(_fromRotation, awayRot, eased));

            // The hand keeps holding the rifle on its way down, and only lets go once it is out of view.
            if (!_copyHidden)
            {
                _copy.HoldWithLeftHand(player);
                return;
            }

            float releaseTime = PluginConfig.RifleRetentionHandReleaseTime.Value;
            float handWeight = releaseTime > 0f ? 1f - Mathf.Clamp01(_releaseElapsed / releaseTime) : 0f;
            _copy.ReleaseLeftHand(player, handWeight);
            HandReleased = handWeight <= 0f;
        }
    }
}
