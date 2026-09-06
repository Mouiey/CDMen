using Barotrauma;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace EmitterFollowClient
{
    internal sealed class FollowEmitterConfiguration
    {
        internal readonly float FadeOutSeconds;

        internal FollowEmitterConfiguration(float fadeOutSeconds)
        {
            FadeOutSeconds = fadeOutSeconds;
        }
    }

    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

        private ReferenceComparer() { }

        public bool Equals(T left, T right)
        {
            return ReferenceEquals(left, right);
        }

        public int GetHashCode(T value)
        {
            return RuntimeHelpers.GetHashCode(value);
        }
    }

    internal static class FollowRuntime
    {
        internal const float DefaultFadeOutSeconds = 0.2f;

        internal enum EmitterPhase
        {
            Dormant,
            Active,
            Closing
        }

        internal sealed class EmitterState
        {
            internal readonly ParticleEmitter Emitter;
            internal FollowEmitterConfiguration Configuration;
            internal readonly HashSet<Particle> Particles;

            internal EmitterPhase Phase;
            internal long LastSeenFrame;
            internal Vector2 AnchorWorld;
            internal float DirectionAngle;
            internal float ParticleRotation;

            internal EmitterState(ParticleEmitter emitter, FollowEmitterConfiguration configuration)
            {
                Emitter = emitter;
                Configuration = configuration;
                Particles = new HashSet<Particle>(ReferenceComparer<Particle>.Instance);
                Phase = EmitterPhase.Dormant;
                LastSeenFrame = -1;
            }
        }

        private sealed class ParticleBinding
        {
            internal readonly Particle Particle;
            internal readonly EmitterState EmitterState;

            internal Vector2 LastAnchorInParticleSpace;
            internal Submarine LastSubmarine;
            internal float LastDirectionAngle;
            internal float LastParticleRotation;

            internal bool IsClosing;
            internal float FadeElapsed;
            internal Vector4 FadeBaseColor;

            internal ParticleBinding(
                Particle particle,
                EmitterState emitterState,
                Vector2 anchorInParticleSpace,
                Submarine submarine)
            {
                Particle = particle;
                EmitterState = emitterState;
                LastAnchorInParticleSpace = anchorInParticleSpace;
                LastSubmarine = submarine;
                LastDirectionAngle = emitterState.DirectionAngle;
                LastParticleRotation = emitterState.ParticleRotation;
                FadeBaseColor = particle.ColorMultiplier;
            }
        }

        private static ConditionalWeakTable<ParticleEmitterProperties, FollowEmitterConfiguration> configurations =
            new ConditionalWeakTable<ParticleEmitterProperties, FollowEmitterConfiguration>();

        private static readonly Dictionary<ParticleEmitter, EmitterState> emitters =
            new Dictionary<ParticleEmitter, EmitterState>(ReferenceComparer<ParticleEmitter>.Instance);
        private static readonly List<EmitterState> emitterList = new List<EmitterState>();
        private static readonly Dictionary<Particle, ParticleBinding> bindings =
            new Dictionary<Particle, ParticleBinding>(ReferenceComparer<Particle>.Instance);

        // Reused buffers keep the hot path allocation-free after their capacities settle.
        private static readonly List<Particle> particleRemovalBuffer = new List<Particle>();
        private static readonly HashSet<string> loggedErrors = new HashSet<string>(StringComparer.Ordinal);

        [ThreadStatic]
        private static EmitterState currentContext;

        private static bool enabled;
        private static bool frameOpen;
        private static bool invalidFadeWarningLogged;
        private static long currentFrame;

        internal static EmitterState CurrentContext
        {
            get { return currentContext; }
        }

        internal static void Enable()
        {
            enabled = true;
            frameOpen = false;
            currentContext = null;
        }

        internal static void CaptureConfiguration(ParticleEmitterProperties properties, XElement element)
        {
            if (properties == null || element == null) { return; }

            string enabledValue = GetAttributeValue(element, "followemitter");
            bool shouldFollow;
            if (!TryParseBoolean(enabledValue, out shouldFollow) || !shouldFollow)
            {
                configurations.Remove(properties);
                return;
            }

            float fadeOut = DefaultFadeOutSeconds;
            string fadeValue = GetAttributeValue(element, "followemitterfadeout");
            if (!string.IsNullOrWhiteSpace(fadeValue))
            {
                float parsed;
                bool valid = float.TryParse(
                    fadeValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed) &&
                    !float.IsNaN(parsed) &&
                    !float.IsInfinity(parsed) &&
                    parsed >= 0.0f;

                if (valid)
                {
                    fadeOut = parsed;
                }
                else if (!invalidFadeWarningLogged)
                {
                    invalidFadeWarningLogged = true;
                    LuaCsLogger.Log(
                        "[EmitterFollowClient] Warning: invalid followemitterfadeout=\"" + fadeValue +
                        "\". Falling back to 0.2 seconds. Further warnings are suppressed.");
                }
            }

            configurations.Remove(properties);
            configurations.Add(properties, new FollowEmitterConfiguration(fadeOut));
        }

        internal static void BeginFrame()
        {
            if (!enabled) { return; }
            currentFrame++;
            frameOpen = true;
        }

        internal static void AbortFrame()
        {
            frameOpen = false;
            currentContext = null;
        }

        internal static void EndFrame(float deltaTime)
        {
            if (!enabled || !frameOpen) { return; }
            frameOpen = false;
            currentContext = null;

            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0.0f)
            {
                deltaTime = 0.0f;
            }

            particleRemovalBuffer.Clear();

            for (int i = 0; i < emitterList.Count; i++)
            {
                EmitterState state = emitterList[i];
                if (state.Phase == EmitterPhase.Active)
                {
                    if (state.LastSeenFrame == currentFrame)
                    {
                        ApplyActiveTransform(state);
                    }
                    else
                    {
                        StartClosing(state);
                    }
                }

                if (state.Phase == EmitterPhase.Closing)
                {
                    AdvanceFade(state, deltaTime);
                }
            }

            for (int i = 0; i < particleRemovalBuffer.Count; i++)
            {
                RemoveBoundParticle(particleRemovalBuffer[i]);
            }
            particleRemovalBuffer.Clear();

            for (int i = emitterList.Count - 1; i >= 0; i--)
            {
                EmitterState state = emitterList[i];
                if (state.Phase != EmitterPhase.Active && state.Particles.Count == 0)
                {
                    emitters.Remove(state.Emitter);
                    emitterList.RemoveAt(i);
                }
            }
        }

        internal static void BeginEmitterCall(
            ParticleEmitter emitter,
            Vector2 anchorWorld,
            float directionAngle,
            float particleRotation)
        {
            // Always clear the context first. A non-follow emitter invoked reentrantly
            // must never inherit the parent emitter's binding context.
            currentContext = null;
            if (!enabled || emitter == null || emitter.Prefab == null || emitter.Prefab.Properties == null)
            {
                return;
            }

            FollowEmitterConfiguration configuration;
            if (!configurations.TryGetValue(emitter.Prefab.Properties, out configuration))
            {
                return;
            }

            EmitterState state;
            if (!emitters.TryGetValue(emitter, out state))
            {
                state = new EmitterState(emitter, configuration);
                emitters.Add(emitter, state);
                emitterList.Add(state);
            }
            else
            {
                state.Configuration = configuration;
            }

            if (state.Phase != EmitterPhase.Active)
            {
                RemoveAllStateParticles(state);
                ResetEmitterTimers(emitter);
                state.Phase = EmitterPhase.Active;
            }

            state.LastSeenFrame = currentFrame;
            state.AnchorWorld = anchorWorld;
            state.DirectionAngle = directionAngle;
            state.ParticleRotation = particleRotation;
            currentContext = state;
        }

        internal static void RestoreEmitterContext(EmitterState previousContext)
        {
            currentContext = enabled ? previousContext : null;
        }

        internal static void BindCreatedParticle(Particle particle)
        {
            if (!enabled || particle == null || currentContext == null) { return; }

            UnbindParticle(particle);

            Submarine submarine = GetParticleSubmarine(particle);
            Vector2 localAnchor = ToParticleSpace(currentContext.AnchorWorld, submarine);
            ParticleBinding binding = new ParticleBinding(particle, currentContext, localAnchor, submarine);

            bindings.Add(particle, binding);
            currentContext.Particles.Add(particle);
        }

        internal static void UnbindParticle(Particle particle)
        {
            if (particle == null) { return; }

            ParticleBinding binding;
            if (!bindings.TryGetValue(particle, out binding)) { return; }

            if (binding.IsClosing)
            {
                particle.ColorMultiplier = binding.FadeBaseColor;
            }

            bindings.Remove(particle);
            binding.EmitterState.Particles.Remove(particle);
        }

        internal static void UnbindParticleAt(ParticleManager manager, int index)
        {
            if (!enabled || manager == null) { return; }

            Particle[] pool = FollowAccess.ParticlePool(manager);
            int count = FollowAccess.ParticleCount(manager);
            if (pool == null || index < 0 || index >= count || index >= pool.Length) { return; }
            UnbindParticle(pool[index]);
        }

        internal static void UnbindAllManagerParticles(ParticleManager manager)
        {
            if (!enabled || manager == null) { return; }

            Particle[] pool = FollowAccess.ParticlePool(manager);
            int count = FollowAccess.ParticleCount(manager);
            if (pool != null)
            {
                int upper = Math.Min(count, pool.Length);
                for (int i = 0; i < upper; i++)
                {
                    UnbindParticle(pool[i]);
                }
            }

            // ClearParticles is used at world/round boundaries. Forget emitter
            // phases as well, so a reused emitter starts as a fresh activation and
            // cannot retain a long burst timer after the particle pool was cleared.
            RestoreAllColors();
            bindings.Clear();
            emitters.Clear();
            emitterList.Clear();
            currentContext = null;
        }

        internal static void UnbindManagerParticlesByPrefab(ParticleManager manager, ParticlePrefab prefab)
        {
            if (!enabled || manager == null || prefab == null) { return; }

            Particle[] pool = FollowAccess.ParticlePool(manager);
            int count = FollowAccess.ParticleCount(manager);
            if (pool == null) { return; }

            int upper = Math.Min(count, pool.Length);
            for (int i = 0; i < upper; i++)
            {
                Particle particle = pool[i];
                if (particle != null && ReferenceEquals(particle.Prefab, prefab))
                {
                    UnbindParticle(particle);
                }
            }
        }

        internal static void UnbindParticlesDroppedByLimit(ParticleManager manager, int newLimit)
        {
            if (!enabled || manager == null || newLimit < 4) { return; }

            Particle[] pool = FollowAccess.ParticlePool(manager);
            int count = FollowAccess.ParticleCount(manager);
            if (pool == null || newLimit >= count) { return; }

            int upper = Math.Min(count, pool.Length);
            for (int i = Math.Max(newLimit, 0); i < upper; i++)
            {
                UnbindParticle(pool[i]);
            }
        }

        internal static void DisableFromPatch(string stage, Exception exception)
        {
            string key = stage ?? "unknown";
            if (loggedErrors.Add(key))
            {
                LuaCsLogger.LogError(
                    "[EmitterFollowClient] Runtime extension disabled after an error in " + key + ": " + exception);
            }

            try
            {
                RestoreAllColors();
            }
            catch
            {
                // The extension is already failing; never let cleanup affect vanilla behavior.
            }

            bindings.Clear();
            emitters.Clear();
            emitterList.Clear();
            particleRemovalBuffer.Clear();
            currentContext = null;
            frameOpen = false;
            enabled = false;
        }

        internal static void Shutdown()
        {
            RestoreAllColors();
            bindings.Clear();
            emitters.Clear();
            emitterList.Clear();
            particleRemovalBuffer.Clear();
            configurations = new ConditionalWeakTable<ParticleEmitterProperties, FollowEmitterConfiguration>();
            currentContext = null;
            frameOpen = false;
            enabled = false;
            currentFrame = 0;
            invalidFadeWarningLogged = false;
            loggedErrors.Clear();
        }

        private static void ApplyActiveTransform(EmitterState state)
        {
            foreach (Particle particle in state.Particles)
            {
                ParticleBinding binding;
                if (!bindings.TryGetValue(particle, out binding)) { continue; }

                Submarine currentSubmarine = GetParticleSubmarine(particle);
                Vector2 currentAnchor = ToParticleSpace(state.AnchorWorld, currentSubmarine);

                // The particle changes coordinate space when it enters or leaves a
                // submarine. Rebase the anchor instead of applying a false jump.
                if (!ReferenceEquals(currentSubmarine, binding.LastSubmarine))
                {
                    binding.LastSubmarine = currentSubmarine;
                    binding.LastAnchorInParticleSpace = currentAnchor;
                }

                float directionDelta = ShortestAngleDelta(
                    binding.LastDirectionAngle,
                    state.DirectionAngle);
                float particleRotationDelta = ShortestAngleDelta(
                    binding.LastParticleRotation,
                    state.ParticleRotation);

                Vector2 position = FollowAccess.Position(particle);
                Vector2 previousPosition = FollowAccess.PrevPosition(particle);
                Vector2 velocity = FollowAccess.Velocity(particle);

                position = currentAnchor + Rotate(
                    position - binding.LastAnchorInParticleSpace,
                    directionDelta);
                previousPosition = currentAnchor + Rotate(
                    previousPosition - binding.LastAnchorInParticleSpace,
                    directionDelta);
                velocity = Rotate(velocity, directionDelta);

                FollowAccess.Position(particle) = position;
                FollowAccess.PrevPosition(particle) = previousPosition;
                FollowAccess.Velocity(particle) = velocity;
                FollowAccess.Rotation(particle) += particleRotationDelta;
                FollowAccess.PrevRotation(particle) += particleRotationDelta;

                binding.LastAnchorInParticleSpace = currentAnchor;
                binding.LastDirectionAngle = state.DirectionAngle;
                binding.LastParticleRotation = state.ParticleRotation;
            }
        }

        private static void StartClosing(EmitterState state)
        {
            state.Phase = EmitterPhase.Closing;
            foreach (Particle particle in state.Particles)
            {
                ParticleBinding binding;
                if (!bindings.TryGetValue(particle, out binding)) { continue; }
                binding.IsClosing = true;
                binding.FadeElapsed = 0.0f;
                binding.FadeBaseColor = particle.ColorMultiplier;
            }
        }

        private static void AdvanceFade(EmitterState state, float deltaTime)
        {
            float duration = state.Configuration.FadeOutSeconds;
            foreach (Particle particle in state.Particles)
            {
                ParticleBinding binding;
                if (!bindings.TryGetValue(particle, out binding)) { continue; }

                if (duration <= 0.0f)
                {
                    particleRemovalBuffer.Add(particle);
                    continue;
                }

                binding.FadeElapsed += deltaTime;
                float t = binding.FadeElapsed / duration;
                if (t >= 1.0f)
                {
                    particleRemovalBuffer.Add(particle);
                    continue;
                }
                if (t < 0.0f) { t = 0.0f; }

                // 1 - smoothstep(0, 1, t): fast response without a hard alpha edge.
                float smooth = t * t * (3.0f - 2.0f * t);
                float remaining = 1.0f - smooth;
                particle.ColorMultiplier = binding.FadeBaseColor * remaining;
            }
        }

        private static void RemoveAllStateParticles(EmitterState state)
        {
            if (state.Particles.Count == 0) { return; }

            particleRemovalBuffer.Clear();
            foreach (Particle particle in state.Particles)
            {
                particleRemovalBuffer.Add(particle);
            }
            for (int i = 0; i < particleRemovalBuffer.Count; i++)
            {
                RemoveBoundParticle(particleRemovalBuffer[i]);
            }
            particleRemovalBuffer.Clear();
        }

        private static void RemoveBoundParticle(Particle particle)
        {
            if (particle == null) { return; }

            ParticleManager manager = GameMain.ParticleManager;
            if (manager != null)
            {
                manager.RemoveParticle(particle);
            }

            // RemoveParticle may not find a stale entry, and its Harmony prefix may
            // already have unbound it. This second call is deliberately idempotent.
            UnbindParticle(particle);
        }

        private static void ResetEmitterTimers(ParticleEmitter emitter)
        {
            FollowAccess.EmitTimer(emitter) = 0.0f;
            FollowAccess.BurstEmitTimer(emitter) = 0.0f;
            FollowAccess.InitialDelay(emitter) = 0.0f;
        }

        private static void RestoreAllColors()
        {
            foreach (KeyValuePair<Particle, ParticleBinding> pair in bindings)
            {
                ParticleBinding binding = pair.Value;
                if (binding.IsClosing && binding.Particle != null)
                {
                    binding.Particle.ColorMultiplier = binding.FadeBaseColor;
                }
            }
        }

        private static Submarine GetParticleSubmarine(Particle particle)
        {
            Hull hull = particle.CurrentHull;
            return hull == null ? null : hull.Submarine;
        }

        private static Vector2 ToParticleSpace(Vector2 worldPosition, Submarine submarine)
        {
            return submarine == null ? worldPosition : worldPosition - submarine.Position;
        }

        private static Vector2 Rotate(Vector2 value, float radians)
        {
            if (Math.Abs(radians) < 0.000001f) { return value; }
            float cosine = (float)Math.Cos(radians);
            float sine = (float)Math.Sin(radians);
            return new Vector2(
                value.X * cosine - value.Y * sine,
                value.X * sine + value.Y * cosine);
        }

        private static float ShortestAngleDelta(float from, float to)
        {
            float delta = (to - from) % MathHelper.TwoPi;
            if (delta > MathHelper.Pi) { delta -= MathHelper.TwoPi; }
            if (delta < -MathHelper.Pi) { delta += MathHelper.TwoPi; }
            return delta;
        }

        private static string GetAttributeValue(XElement element, string attributeName)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute.Value;
                }
            }
            return null;
        }

        private static bool TryParseBoolean(string value, out bool result)
        {
            if (bool.TryParse(value, out result)) { return true; }
            if (string.Equals(value, "1", StringComparison.Ordinal))
            {
                result = true;
                return true;
            }
            if (string.Equals(value, "0", StringComparison.Ordinal))
            {
                result = false;
                return true;
            }
            result = false;
            return false;
        }
    }
}
