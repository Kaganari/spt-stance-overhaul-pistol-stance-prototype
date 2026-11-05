using RealismCommonLib.Utils;
using UnityEngine;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// How far along its way each axis of the rifle is, from the held stance (0) to the retained pose (1).
    /// Every axis follows its own curve, so the rifle doesn't move as one rigid line; values outside 0..1 are
    /// wind-up and overshoot. Lowering and raising are separate curves in curves.json, read live so they can
    /// be shaped in the curve editor. Each runs from 0 at the start of its move to 1 at the end.
    /// </summary>
    internal static class RetentionTravel
    {
        public static Vector3 Lowering(string curveName, float blend)
        {
            return Evaluate(curveName, blend);
        }

        public static Vector3 Raising(string curveName, float blend)
        {
            return Vector3.one - Evaluate(curveName, 1f - blend);
        }

        private static Vector3 Evaluate(string curveName, float time)
        {
            Vector3Curve? curve = CurveDrawer.GetCurve(curveName);
            if (curve == null)
                return Vector3.one * Mathf.SmoothStep(0f, 1f, time);

            return new Vector3(curve.XCurve.Evaluate(time), curve.YCurve.Evaluate(time), curve.ZCurve.Evaluate(time));
        }
    }
}
