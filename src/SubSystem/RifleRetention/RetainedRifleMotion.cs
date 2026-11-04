using CW2.Animations;
using EFT.Animations;
using HarmonyLib;
using RealismCommonLib.Utils;
using UnityEngine;

namespace StanceOverhaul.SubSystem.RifleRetention
{
    /// <summary>
    /// The retained rifle's procedural motion. The game's hand springs only serve the weapon in hands, so a
    /// rifle held elsewhere has springs of its own: they continue from the rifle's spring state and settings
    /// at keypress and are driven by the game's own processors from what the body is doing (mouse turns,
    /// movement, steps, breath). What only concerns the weapon in hands stays out: recoil, aiming and
    /// holding breath. Hit reactions aren't modelled. One instance lives for one retention.
    /// </summary>
    internal class RetainedRifleMotion
    {
        // A slightly slower cycle, starting elsewhere in it: the two hands shouldn't rise and fall together.
        private const float BREATH_RATE = 0.9f;
        private const float BREATH_PHASE_OFFSET = 0.35f;

        // Steps follow the body's timing, reaching the low-held rifle a little late.
        private const float WALK_LAG = 0.05f;

        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> _displacementStr =
            AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_displacementStr");
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> _swayStrength =
            AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_swayStrength");

        private readonly Spring _position;
        private readonly Spring _rotation;
        private readonly BetterSpring _sway;

        // Some curves also push the camera. That is the game's business, so those pushes end here.
        private readonly Spring _cameraSink = new Spring();

        private readonly ValProcessor[] _movement;
        private readonly BetterValProcessor[] _mouse;
        private readonly AnimValProcessor[][] _walk;
        private readonly AnimValProcessor[][] _walkOverweight;
        private readonly AnimValProcessor _breath;
        private readonly SmoothRandom _breathX = new SmoothRandom();
        private readonly SmoothRandom _breathY = new SmoothRandom();

        // Hip values, read while the rifle is in hands: they carry its weight.
        private readonly Vector3 _swayFactors;
        private readonly float _displacement;

        /// <summary>Spring motion, in the units of the hands position spring.</summary>
        public Vector3 Position => _position.Current;

        /// <summary>Spring and mouse sway rotation, as the game adds them to a weapon.</summary>
        public Vector3 Rotation
        {
            get
            {
                Vector3 sway = _sway.Value;
                return _rotation.Current + sway + _displacement * new Vector3(sway.x, 0f, sway.z);
            }
        }

        /// <param name="pwa">Must still be set up for the rifle: call while it is in hands.</param>
        public RetainedRifleMotion(ProceduralWeaponAnimation pwa)
        {
            var hands = pwa.HandsContainer;
            _position = TakeOver(hands.HandsPosition);
            _rotation = TakeOver(hands.HandsRotation);
            _sway = Cloner.ShallowClone(hands.SwaySpring);

            _swayFactors = Vector3.one * _swayStrength(pwa);
            _displacement = _displacementStr(pwa);

            _movement = ValProcessor.CreateInstance(pwa.MotionReact.MovementParameters);
            foreach (ValProcessor processor in _movement)
                processor.Initialize(_cameraSink, _position, _rotation);

            _mouse = BetterValProcessor.CreateInstance(pwa.MotionReact.MouseParameters);
            foreach (BetterValProcessor processor in _mouse)
                processor.Initialize(_sway);

            _walk = CopyWalkPresets(pwa.Walk.Presets);
            _walkOverweight = CopyWalkPresets(pwa.Walk.OverweightPresets);
            _breath = CopyBreath(pwa.Breath);
        }

        /// <summary>Same steps and order as the game's ProcessEffectors, on the rifle's springs.</summary>
        public void Step(ProceduralWeaponAnimation pwa, float deltaTime, int nFixedFrames)
        {
            if (nFixedFrames <= 0 || Mathf.Approximately(deltaTime, 0f)) return;

            float dt = deltaTime / nFixedFrames;
            for (int i = 0; i < nFixedFrames; i++)
            {
                if ((pwa.Mask & EProceduralAnimationMask.MotionReaction) != 0)
                    PushFromBodyMotion(pwa.MotionReact, dt);
                if ((pwa.Mask & EProceduralAnimationMask.Breathing) != 0)
                    PushFromBreath(pwa, dt);
                if ((pwa.Mask & EProceduralAnimationMask.Walking) != 0)
                    PushFromSteps(pwa.Walk);

                _position.FixedUpdate(dt);
                _rotation.FixedUpdate(dt);
                _sway.Process(dt);
                _cameraSink.FixedUpdate(dt);
            }
        }

        /// <summary>The sway rotation the game is adding to the weapon in hands right now.</summary>
        public static Vector3 GameSwayRotation(ProceduralWeaponAnimation pwa)
        {
            Vector3 sway = pwa.HandsContainer.SwaySpring.Value;
            float displacement = _displacementStr(pwa) * (pwa.IsAiming ? pwa.AimingDisplacementStr : 1f);
            return sway + displacement * new Vector3(sway.x, 0f, sway.z);
        }

        private void PushFromBodyMotion(MotionEffector motion, float dt)
        {
            foreach (ValProcessor processor in _movement)
                processor.Process(motion, dt * motion.Intensity);
            foreach (BetterValProcessor processor in _mouse)
                processor.Process(motion, dt, _swayFactors);
        }

        // BreathEffector.Process without what steadies the weapon being aimed: its aiming factor, holding
        // breath and the steady period after aiming in. Oxygen and stamina are the body's, so they stay.
        private void PushFromBreath(ProceduralWeaponAnimation pwa, float dt)
        {
            BreathEffector breath = pwa.Breath;
            float strength = breath.Intensity / Mathf.Max(pwa.IntensityByAiming, 0.01f);
            float stamina = breath.StaminaLevel.Value;
            float oxygen = breath._lackOfOxygenStrength.Evaluate(breath.OxygenLevel.Value);
            float breathing = Mathf.Lerp(4f, 1f, oxygen);

            CopySettings(breath.YRandom, _breathY);
            CopySettings(breath.XRandom, _breathX);
            float y = _breathY.GetValue(dt);
            float x = _breathX.GetValue(dt);
            _rotation.AddAcceleration(new Vector3(Mathf.Max(0f, -y) * (1f - stamina) * 2f, y, x) * strength);

            _breath.ProcessRaw(Mathf.Clamp(breathing, 1f, 2.5f) * dt * BREATH_RATE, Mathf.Clamp(breathing, 1f, 1.5f) * strength);
        }

        private void PushFromSteps(WalkEffector walk)
        {
            int preset = (int)walk.CurrentWalkPreset;
            Replay(_walk[preset], walk.PresetProcessors[preset], walk.Intensity);
            if (walk.Overweight > 0f)
                Replay(_walkOverweight[preset], walk.OverweightPresetProcessors[preset], walk.Overweight);
        }

        // Reads the time of the game's curves rather than keeping one: it restarts when the player stops.
        private static void Replay(AnimValProcessor[] own, AnimValProcessor[] game, float intensity)
        {
            for (int i = 0; i < own.Length; i++)
                own[i].ProcessAtTime(Mathf.Max(0f, game[i]._time - WALK_LAG), intensity);
        }

        private static Spring TakeOver(Spring gameSpring)
        {
            // Zero is the weapon's pose offset, not motion: rest at 0 so Current is the motion alone.
            Spring spring = Cloner.ShallowClone(gameSpring);
            spring.Current -= spring.Zero;
            spring.Zero = Vector3.zero;
            spring.InputIntensity = 1f;
            return spring;
        }

        private AnimValProcessor[][] CopyWalkPresets(WalkPreset[] presets)
        {
            var copies = new AnimValProcessor[presets.Length][];
            for (int i = 0; i < presets.Length; i++)
            {
                AnimVal[] curves = presets[i].Curves;
                copies[i] = new AnimValProcessor[curves.Length];
                for (int j = 0; j < curves.Length; j++)
                    copies[i][j] = CopyCurve(curves[j]);
            }
            return copies;
        }

        private AnimValProcessor CopyBreath(BreathEffector breath)
        {
            AnimVal values = breath.Curves[0];
            AnimationCurve curve = values.Curve;
            float cycle = curve[curve.length - 1].time - curve[0].time;

            AnimValProcessor copy = CopyCurve(values);
            copy._time = breath._processors[0]._time + cycle * BREATH_PHASE_OFFSET;
            return copy;
        }

        private AnimValProcessor CopyCurve(AnimVal values)
        {
            var processor = new AnimValProcessor(values);
            processor.Initialize(_cameraSink, _position, _rotation, 1f, 1f, isHeadbobbing: true);
            return processor;
        }

        private static void CopySettings(SmoothRandom from, SmoothRandom to)
        {
            to.Amplitude = from.Amplitude;
            to.MinMaxDelay = from.MinMaxDelay;
            to.Hardness = from.Hardness;
        }
    }
}
