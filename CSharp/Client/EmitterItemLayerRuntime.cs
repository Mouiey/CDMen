using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Particles;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace EmitterFollowClient
{
    internal sealed class ItemLayerEmitterConfiguration
    {
    }

    /// <summary>
    /// Moves explicitly marked particles out of ParticleManager's normal scene passes
    /// and into the two item/structure insertion points surrounding character drawing.
    /// This runtime intentionally owns no movement or lifetime behavior.
    /// </summary>
    internal static class ItemLayerParticleRuntime
    {
        internal const int ExpectedDrawBackCalls = 3;
        private const float CharacterLayerBoundary = 0.5f;

        private static ConditionalWeakTable<ParticleEmitterProperties, ItemLayerEmitterConfiguration>
            configurations =
                new ConditionalWeakTable<ParticleEmitterProperties, ItemLayerEmitterConfiguration>();

        private static readonly HashSet<Particle> particles =
            new HashSet<Particle>(ReferenceComparer<Particle>.Instance);
        private static readonly List<Particle> alphaBuffer = new List<Particle>();
        private static readonly List<Particle> additiveBuffer = new List<Particle>();
        private static readonly HashSet<string> warningKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> errorKeys =
            new HashSet<string>(StringComparer.Ordinal);

        [ThreadStatic]
        private static bool emitterContext;

        [ThreadStatic]
        private static bool drawMapActive;

        [ThreadStatic]
        private static bool customDrawActive;

        [ThreadStatic]
        private static bool vanillaParticleDrawActive;

        [ThreadStatic]
        private static int drawBackCallCount;

        [ThreadStatic]
        private static bool frontInsertionDone;

        [ThreadStatic]
        private static GameScreen activeGameScreen;

        [ThreadStatic]
        private static SpriteBatch activeSpriteBatch;

        private static bool enabled;

        internal static bool IsEnabled
        {
            get { return enabled; }
        }

        internal static bool CurrentEmitterContext
        {
            get { return emitterContext; }
        }

        internal static void Enable()
        {
            enabled = true;
            ResetThreadDrawContext();
            emitterContext = false;
        }

        internal static void CaptureConfiguration(
            ParticleEmitterProperties properties,
            XElement element)
        {
            if (properties == null || element == null) { return; }

            bool useItemLayer;
            string value = GetAttributeValue(element, "itemlayerdepth");
            configurations.Remove(properties);
            if (TryParseBoolean(value, out useItemLayer) && useItemLayer)
            {
                configurations.Add(properties, new ItemLayerEmitterConfiguration());
            }
        }

        internal static void BeginEmitterCall(ParticleEmitter emitter)
        {
            // Clear first so a nested normal emitter cannot inherit its caller's flag.
            emitterContext = false;
            if (!enabled || emitter == null || emitter.Prefab == null ||
                emitter.Prefab.Properties == null)
            {
                return;
            }

            ItemLayerEmitterConfiguration configuration;
            emitterContext = configurations.TryGetValue(
                emitter.Prefab.Properties,
                out configuration);
        }

        internal static void RestoreEmitterContext(bool previousContext)
        {
            emitterContext = enabled && previousContext;
        }

        internal static void BindCreatedParticle(Particle particle)
        {
            if (!enabled || !emitterContext || particle == null || particle.Prefab == null)
            {
                return;
            }

            UnbindParticle(particle);

            if (particle.DrawTarget != ParticlePrefab.DrawTargetType.Both)
            {
                LogWarningOnce(
                    "drawtarget:" + particle.Prefab.Name,
                    "Particle prefab \"" + particle.Prefab.Name + "\" uses DrawTarget=\"" +
                    particle.DrawTarget + "\" with itemlayerdepth. Only DrawTarget=\"Both\" " +
                    "is supported; this prefab will use vanilla drawing.");
                return;
            }

            if (particle.DrawOrder != ParticleDrawOrder.Default)
            {
                LogWarningOnce(
                    "draworder:" + particle.Prefab.Name,
                    "Particle prefab \"" + particle.Prefab.Name + "\" uses DrawOrder=\"" +
                    particle.DrawOrder + "\" with itemlayerdepth. itemlayerdepth takes precedence; " +
                    "use DrawOrder=\"Default\" for unambiguous content.");
            }

            particles.Add(particle);
        }

        internal static void UnbindParticle(Particle particle)
        {
            if (particle != null) { particles.Remove(particle); }
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
            particles.Clear();
            emitterContext = false;
        }

        internal static void UnbindManagerParticlesByPrefab(
            ParticleManager manager,
            ParticlePrefab prefab)
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
                    particles.Remove(particle);
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
                particles.Remove(pool[i]);
            }
        }

        internal static void BeginDrawMap(GameScreen gameScreen, SpriteBatch spriteBatch)
        {
            if (!enabled) { return; }
            try
            {
                drawMapActive = true;
                customDrawActive = false;
                vanillaParticleDrawActive = false;
                drawBackCallCount = 0;
                frontInsertionDone = false;
                activeGameScreen = gameScreen;
                activeSpriteBatch = spriteBatch;
            }
            catch (Exception exception)
            {
                DisableFromPatch("GameScreen.DrawMap prefix", exception);
            }
        }

        internal static void EndDrawMap(bool completed)
        {
            if (enabled && drawMapActive && completed &&
                (drawBackCallCount != ExpectedDrawBackCalls || !frontInsertionDone))
            {
                DisableFromPatch(
                    "GameScreen.DrawMap runtime layout",
                    new InvalidOperationException(
                        "Observed DrawBack=" + drawBackCallCount + ", DrawFront=" +
                        (frontInsertionDone ? 1 : 0) + "; expected 3 and 1."));
                return;
            }
            ResetThreadDrawContext();
        }

        internal static void AfterDrawBack(SpriteBatch spriteBatch)
        {
            if (!enabled || !drawMapActive) { return; }
            try
            {
                drawBackCallCount++;
                if (drawBackCallCount == ExpectedDrawBackCalls)
                {
                    DrawLayer(spriteBatch, true);
                }
            }
            catch (Exception exception)
            {
                DisableFromPatch("Submarine.DrawBack postfix", exception);
            }
        }

        internal static void BeforeDrawFront(SpriteBatch spriteBatch)
        {
            if (!enabled || !drawMapActive || frontInsertionDone) { return; }
            frontInsertionDone = true;
            try
            {
                DrawLayer(spriteBatch, false);
            }
            catch (Exception exception)
            {
                DisableFromPatch("Submarine.DrawFront prefix", exception);
            }
        }

        internal static bool ShouldRunVanillaParticleDraw(Particle particle)
        {
            return !enabled || !drawMapActive || !vanillaParticleDrawActive ||
                   customDrawActive || particle == null ||
                   !particles.Contains(particle);
        }

        internal static bool BeginVanillaParticleDraw()
        {
            bool previous = vanillaParticleDrawActive;
            vanillaParticleDrawActive = enabled && drawMapActive;
            return previous;
        }

        internal static void RestoreVanillaParticleDraw(bool previous)
        {
            vanillaParticleDrawActive = enabled && previous;
        }

        internal static void DisableFromPatch(string stage, Exception exception)
        {
            string key = stage ?? "unknown";
            if (errorKeys.Add(key))
            {
                LuaCsLogger.LogError(
                    "[EmitterFollowClient] itemlayerdepth disabled after an error in " + key +
                    ": " + exception);
            }

            particles.Clear();
            alphaBuffer.Clear();
            additiveBuffer.Clear();
            emitterContext = false;
            ResetThreadDrawContext();
            enabled = false;
        }

        internal static void Shutdown()
        {
            particles.Clear();
            alphaBuffer.Clear();
            additiveBuffer.Clear();
            warningKeys.Clear();
            errorKeys.Clear();
            configurations =
                new ConditionalWeakTable<ParticleEmitterProperties, ItemLayerEmitterConfiguration>();
            emitterContext = false;
            ResetThreadDrawContext();
            enabled = false;
        }

        private static void DrawLayer(SpriteBatch spriteBatch, bool behindCharacters)
        {
            if (spriteBatch == null || spriteBatch != activeSpriteBatch || activeGameScreen == null)
            {
                throw new InvalidOperationException(
                    "The active DrawMap SpriteBatch or GameScreen did not match the insertion call.");
            }

            GatherParticles(behindCharacters);
            if (alphaBuffer.Count == 0 && additiveBuffer.Count == 0) { return; }

            bool callerBatchEnded = false;
            bool customBatchBegun = false;
            Exception failure = null;
            try
            {
                spriteBatch.End();
                callerBatchEnded = true;

                DrawBuffer(spriteBatch, alphaBuffer, BlendState.NonPremultiplied, ref customBatchBegun);
                DrawBuffer(spriteBatch, additiveBuffer, BlendState.Additive, ref customBatchBegun);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                customDrawActive = false;
                if (customBatchBegun)
                {
                    try
                    {
                        spriteBatch.End();
                    }
                    catch (Exception endException)
                    {
                        if (failure == null) { failure = endException; }
                    }
                }

                if (callerBatchEnded)
                {
                    try
                    {
                        spriteBatch.Begin(
                            SpriteSortMode.BackToFront,
                            BlendState.NonPremultiplied,
                            null,
                            DepthStencilState.None,
                            null,
                            null,
                            activeGameScreen.Cam.Transform);
                    }
                    catch (Exception restoreException)
                    {
                        if (failure == null) { failure = restoreException; }
                    }
                }

                alphaBuffer.Clear();
                additiveBuffer.Clear();
            }

            if (failure != null)
            {
                throw new InvalidOperationException(
                    "Failed to draw or restore an item-layer particle SpriteBatch.",
                    failure);
            }
        }

        private static void GatherParticles(bool behindCharacters)
        {
            alphaBuffer.Clear();
            additiveBuffer.Clear();

            ParticleManager manager = GameMain.ParticleManager;
            if (manager == null || particles.Count == 0) { return; }

            LinkedList<Particle> creationOrder = ItemLayerAccess.ParticlesInCreationOrder(manager);
            if (creationOrder == null)
            {
                throw new InvalidOperationException(
                    "ParticleManager.particlesInCreationOrder was unexpectedly null.");
            }

            foreach (Particle particle in creationOrder)
            {
                if (particle == null || !particles.Contains(particle)) { continue; }

                float depth;
                if (!TryGetActiveSpriteDepth(particle, out depth))
                {
                    particles.Remove(particle);
                    LogWarningOnce(
                        "sprite:" + GetParticleName(particle),
                        "Particle prefab \"" + GetParticleName(particle) +
                        "\" had no valid active Sprite index. Its item-layer marker was removed " +
                        "and vanilla drawing will be used.");
                    continue;
                }

                if ((depth > CharacterLayerBoundary) != behindCharacters) { continue; }

                if (particle.BlendState == ParticleBlendState.Additive)
                {
                    additiveBuffer.Add(particle);
                }
                else
                {
                    alphaBuffer.Add(particle);
                }
            }
        }

        private static bool TryGetActiveSpriteDepth(Particle particle, out float depth)
        {
            depth = 0.0f;
            ParticlePrefab prefab = particle.Prefab;
            if (prefab == null || prefab.Sprites == null) { return false; }

            int spriteIndex = ItemLayerAccess.SpriteIndex(particle);
            if (spriteIndex < 0 || spriteIndex >= prefab.Sprites.Count ||
                prefab.Sprites[spriteIndex] == null)
            {
                return false;
            }

            depth = prefab.Sprites[spriteIndex].Depth;
            return !float.IsNaN(depth) && !float.IsInfinity(depth);
        }

        private static void DrawBuffer(
            SpriteBatch spriteBatch,
            List<Particle> buffer,
            BlendState blendState,
            ref bool customBatchBegun)
        {
            if (buffer.Count == 0) { return; }

            spriteBatch.Begin(
                SpriteSortMode.Deferred,
                blendState,
                null,
                DepthStencilState.None,
                null,
                null,
                activeGameScreen.Cam.Transform);
            customBatchBegun = true;
            customDrawActive = true;
            for (int i = 0; i < buffer.Count; i++)
            {
                buffer[i].Draw(spriteBatch);
            }
            customDrawActive = false;
            spriteBatch.End();
            customBatchBegun = false;
        }

        private static void ResetThreadDrawContext()
        {
            drawMapActive = false;
            customDrawActive = false;
            vanillaParticleDrawActive = false;
            drawBackCallCount = 0;
            frontInsertionDone = false;
            activeGameScreen = null;
            activeSpriteBatch = null;
        }

        private static string GetParticleName(Particle particle)
        {
            return particle == null || particle.Prefab == null
                ? "unknown"
                : particle.Prefab.Name;
        }

        private static void LogWarningOnce(string key, string message)
        {
            if (warningKeys.Add(key))
            {
                LuaCsLogger.Log(
                    "[EmitterFollowClient] Warning: " + message +
                    " Further warnings for this prefab are suppressed.");
            }
        }

        private static string GetAttributeValue(XElement element, string attributeName)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (string.Equals(
                    attribute.Name.LocalName,
                    attributeName,
                    StringComparison.OrdinalIgnoreCase))
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
