using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Particles;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;

namespace EmitterFollowClient
{
    /// <summary>
    /// Client-only LuaCs plugin entry point. All patches are installed before content
    /// loading so custom ParticleEmitter attributes can be captured by the properties
    /// constructor without changing Barotrauma's serializable types.
    /// </summary>
    public sealed class Plugin : IAssemblyPlugin
    {
        internal const string HarmonyId = "eternal.barotrauma.emitterfollowclient.v1";
        internal const string ItemLayerHarmonyId = "eternal.barotrauma.emitterfollowclient.itemlayer.v1";

        private Harmony harmony;
        private Harmony itemLayerHarmony;
        private bool installAttempted;
        private bool installed;

        public void PreInitPatching()
        {
            Install();
        }

        public void Initialize()
        {
            // Be defensive in case a loader invokes Initialize without PreInitPatching.
            Install();
        }

        public void OnLoadCompleted()
        {
            if (installed)
            {
                string itemLayerStatus = ItemLayerParticleRuntime.IsEnabled
                    ? " itemlayerdepth is also available."
                    : " itemlayerdepth is unavailable; vanilla particle drawing remains active.";
                LuaCsLogger.Log(
                    "[EmitterFollowClient] Loaded. followemitter is available on client ParticleEmitters." +
                    itemLayerStatus);
            }
        }

        public void Dispose()
        {
            try
            {
                ItemLayerParticleRuntime.Shutdown();
            }
            catch (Exception exception)
            {
                LuaCsLogger.LogError("[EmitterFollowClient] Item-layer cleanup failed: " + exception);
            }

            try
            {
                FollowRuntime.Shutdown();
            }
            catch (Exception exception)
            {
                LuaCsLogger.LogError("[EmitterFollowClient] Cleanup failed: " + exception);
            }

            try
            {
                if (itemLayerHarmony != null)
                {
                    itemLayerHarmony.UnpatchSelf();
                }
                if (harmony != null)
                {
                    harmony.UnpatchSelf();
                }
            }
            catch (Exception exception)
            {
                LuaCsLogger.LogError("[EmitterFollowClient] Failed to remove Harmony patches: " + exception);
            }

            installed = false;
        }

        private void Install()
        {
            if (installAttempted) { return; }
            installAttempted = true;

            try
            {
                string error;
                if (!FollowAccess.TryInitialize(out error))
                {
                    LuaCsLogger.LogError(
                        "[EmitterFollowClient] Disabled: Barotrauma 1.13.4.0 compatibility check failed. " + error);
                    return;
                }

                harmony = new Harmony(HarmonyId);
                FollowPatchInstaller.Install(harmony);
                FollowRuntime.Enable();
                installed = true;

                string itemLayerError;
                if (!ItemLayerAccess.TryInitialize(out itemLayerError))
                {
                    LuaCsLogger.LogError(
                        "[EmitterFollowClient] itemlayerdepth disabled: Barotrauma 1.13.4.0 " +
                        "draw compatibility check failed. " + itemLayerError);
                    return;
                }

                try
                {
                    itemLayerHarmony = new Harmony(ItemLayerHarmonyId);
                    ItemLayerPatchInstaller.Install(itemLayerHarmony);
                    ItemLayerParticleRuntime.Enable();
                }
                catch (Exception itemLayerException)
                {
                    ItemLayerParticleRuntime.Shutdown();
                    try
                    {
                        if (itemLayerHarmony != null) { itemLayerHarmony.UnpatchSelf(); }
                    }
                    catch
                    {
                        // Keep followemitter installed even if item-layer cleanup fails.
                    }
                    LuaCsLogger.LogError(
                        "[EmitterFollowClient] itemlayerdepth disabled: patch installation failed: " +
                        itemLayerException);
                }
            }
            catch (Exception exception)
            {
                FollowRuntime.Shutdown();
                try
                {
                    if (harmony != null) { harmony.UnpatchSelf(); }
                }
                catch
                {
                    // Preserve the original exception and leave the extension disabled.
                }
                LuaCsLogger.LogError("[EmitterFollowClient] Disabled: patch installation failed: " + exception);
            }
        }
    }

    internal static class FollowAccess
    {
        internal static ConstructorInfo PropertiesConstructor;
        internal static MethodInfo EmitterEmit;
        internal static MethodInfo CreateParticle;
        internal static MethodInfo ParticleInit;
        internal static MethodInfo GameScreenUpdate;
        internal static MethodInfo RemoveParticleAt;
        internal static MethodInfo ClearParticles;
        internal static MethodInfo RemoveByPrefab;
        internal static MethodInfo SetMaxParticles;

        internal static AccessTools.FieldRef<ParticleEmitter, float> EmitTimer;
        internal static AccessTools.FieldRef<ParticleEmitter, float> BurstEmitTimer;
        internal static AccessTools.FieldRef<ParticleEmitter, float> InitialDelay;

        internal static AccessTools.FieldRef<Particle, Vector2> Position;
        internal static AccessTools.FieldRef<Particle, Vector2> PrevPosition;
        internal static AccessTools.FieldRef<Particle, Vector2> Velocity;
        internal static AccessTools.FieldRef<Particle, float> Rotation;
        internal static AccessTools.FieldRef<Particle, float> PrevRotation;

        internal static AccessTools.FieldRef<ParticleManager, Particle[]> ParticlePool;
        internal static AccessTools.FieldRef<ParticleManager, int> ParticleCount;

        internal static bool TryInitialize(out string error)
        {
            try
            {
                PropertiesConstructor = Require(
                    AccessTools.Constructor(typeof(ParticleEmitterProperties), new Type[] { typeof(XElement) }),
                    "ParticleEmitterProperties(XElement)");

                EmitterEmit = Require(
                    AccessTools.Method(typeof(ParticleEmitter), "Emit", new Type[]
                    {
                        typeof(float), typeof(Vector2), typeof(Hull), typeof(float), typeof(float),
                        typeof(float), typeof(float), typeof(float), typeof(Color?), typeof(ParticlePrefab),
                        typeof(bool), typeof(Tuple<Vector2, Vector2>)
                    }),
                    "ParticleEmitter.Emit(float, Vector2, ...)");

                CreateParticle = Require(
                    AccessTools.Method(typeof(ParticleManager), "CreateParticle", new Type[]
                    {
                        typeof(ParticlePrefab), typeof(Vector2), typeof(Vector2), typeof(float), typeof(Hull),
                        typeof(ParticleDrawOrder), typeof(float), typeof(float), typeof(Tuple<Vector2, Vector2>)
                    }),
                    "ParticleManager.CreateParticle(ParticlePrefab, ...)");

                ParticleInit = Require(
                    AccessTools.Method(typeof(Particle), "Init", new Type[]
                    {
                        typeof(ParticlePrefab), typeof(Vector2), typeof(Vector2), typeof(float), typeof(Hull),
                        typeof(ParticleDrawOrder), typeof(float), typeof(float), typeof(Tuple<Vector2, Vector2>)
                    }),
                    "Particle.Init(ParticlePrefab, ...)");

                GameScreenUpdate = Require(
                    AccessTools.Method(typeof(GameScreen), "Update", new Type[] { typeof(double) }),
                    "GameScreen.Update(double)");
                RemoveParticleAt = Require(
                    AccessTools.DeclaredMethod(typeof(ParticleManager), "RemoveParticle", new Type[] { typeof(int) }),
                    "ParticleManager.RemoveParticle(int)");
                ClearParticles = Require(
                    AccessTools.Method(typeof(ParticleManager), "ClearParticles", Type.EmptyTypes),
                    "ParticleManager.ClearParticles()");
                RemoveByPrefab = Require(
                    AccessTools.Method(typeof(ParticleManager), "RemoveByPrefab", new Type[] { typeof(ParticlePrefab) }),
                    "ParticleManager.RemoveByPrefab(ParticlePrefab)");
                SetMaxParticles = Require(
                    AccessTools.PropertySetter(typeof(ParticleManager), "MaxParticles"),
                    "ParticleManager.MaxParticles setter");

                RequireField(typeof(ParticleEmitter), "emitTimer");
                RequireField(typeof(ParticleEmitter), "burstEmitTimer");
                RequireField(typeof(ParticleEmitter), "initialDelay");
                RequireField(typeof(Particle), "position");
                RequireField(typeof(Particle), "prevPosition");
                RequireField(typeof(Particle), "velocity");
                RequireField(typeof(Particle), "rotation");
                RequireField(typeof(Particle), "prevRotation");
                RequireField(typeof(ParticleManager), "particles");
                RequireField(typeof(ParticleManager), "particleCount");

                EmitTimer = AccessTools.FieldRefAccess<ParticleEmitter, float>("emitTimer");
                BurstEmitTimer = AccessTools.FieldRefAccess<ParticleEmitter, float>("burstEmitTimer");
                InitialDelay = AccessTools.FieldRefAccess<ParticleEmitter, float>("initialDelay");
                Position = AccessTools.FieldRefAccess<Particle, Vector2>("position");
                PrevPosition = AccessTools.FieldRefAccess<Particle, Vector2>("prevPosition");
                Velocity = AccessTools.FieldRefAccess<Particle, Vector2>("velocity");
                Rotation = AccessTools.FieldRefAccess<Particle, float>("rotation");
                PrevRotation = AccessTools.FieldRefAccess<Particle, float>("prevRotation");
                ParticlePool = AccessTools.FieldRefAccess<ParticleManager, Particle[]>("particles");
                ParticleCount = AccessTools.FieldRefAccess<ParticleManager, int>("particleCount");

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static T Require<T>(T member, string name) where T : MemberInfo
        {
            if (member == null) { throw new MissingMemberException(name); }
            return member;
        }

        private static void RequireField(Type type, string name)
        {
            if (AccessTools.Field(type, name) == null)
            {
                throw new MissingFieldException(type.FullName, name);
            }
        }
    }

    internal static class FollowPatchInstaller
    {
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(
                FollowAccess.PropertiesConstructor,
                postfix: new HarmonyMethod(typeof(FollowPatches), "PropertiesConstructorPostfix"));
            harmony.Patch(
                FollowAccess.EmitterEmit,
                prefix: new HarmonyMethod(typeof(FollowPatches), "EmitterEmitPrefix"),
                finalizer: new HarmonyMethod(typeof(FollowPatches), "EmitterEmitFinalizer"));
            harmony.Patch(
                FollowAccess.CreateParticle,
                postfix: new HarmonyMethod(typeof(FollowPatches), "CreateParticlePostfix"));
            harmony.Patch(
                FollowAccess.ParticleInit,
                prefix: new HarmonyMethod(typeof(FollowPatches), "ParticleInitPrefix"));
            harmony.Patch(
                FollowAccess.GameScreenUpdate,
                prefix: new HarmonyMethod(typeof(FollowPatches), "GameScreenUpdatePrefix"),
                finalizer: new HarmonyMethod(typeof(FollowPatches), "GameScreenUpdateFinalizer"));
            harmony.Patch(
                FollowAccess.RemoveParticleAt,
                prefix: new HarmonyMethod(typeof(FollowPatches), "RemoveParticleAtPrefix"));
            harmony.Patch(
                FollowAccess.ClearParticles,
                prefix: new HarmonyMethod(typeof(FollowPatches), "ClearParticlesPrefix"));
            harmony.Patch(
                FollowAccess.RemoveByPrefab,
                prefix: new HarmonyMethod(typeof(FollowPatches), "RemoveByPrefabPrefix"));
            harmony.Patch(
                FollowAccess.SetMaxParticles,
                prefix: new HarmonyMethod(typeof(FollowPatches), "SetMaxParticlesPrefix"));
        }
    }

    internal static class FollowPatches
    {
        internal struct EmitterCallState
        {
            internal FollowRuntime.EmitterState FollowContext;
            internal bool ItemLayerContext;
        }

        internal static void PropertiesConstructorPostfix(ParticleEmitterProperties __instance, XElement element)
        {
            try
            {
                FollowRuntime.CaptureConfiguration(__instance, element);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleEmitterProperties constructor", exception);
            }

            try
            {
                ItemLayerParticleRuntime.CaptureConfiguration(__instance, element);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleEmitterProperties constructor", exception);
            }
        }

        internal static void EmitterEmitPrefix(
            ParticleEmitter __instance,
            Vector2 position,
            float angle,
            float particleRotation,
            out EmitterCallState __state)
        {
            __state = new EmitterCallState
            {
                FollowContext = FollowRuntime.CurrentContext,
                ItemLayerContext = ItemLayerParticleRuntime.CurrentEmitterContext
            };
            try
            {
                FollowRuntime.BeginEmitterCall(__instance, position, angle, particleRotation);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleEmitter.Emit prefix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.BeginEmitterCall(__instance);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleEmitter.Emit prefix", exception);
            }
        }

        internal static Exception EmitterEmitFinalizer(
            Exception __exception,
            EmitterCallState __state)
        {
            try
            {
                FollowRuntime.RestoreEmitterContext(__state.FollowContext);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleEmitter.Emit finalizer", exception);
            }

            try
            {
                ItemLayerParticleRuntime.RestoreEmitterContext(__state.ItemLayerContext);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleEmitter.Emit finalizer", exception);
            }
            return __exception;
        }

        internal static void CreateParticlePostfix(Particle __result)
        {
            try
            {
                FollowRuntime.BindCreatedParticle(__result);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleManager.CreateParticle postfix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.BindCreatedParticle(__result);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleManager.CreateParticle postfix", exception);
            }
        }

        internal static void ParticleInitPrefix(Particle __instance)
        {
            try
            {
                FollowRuntime.UnbindParticle(__instance);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("Particle.Init prefix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.UnbindParticle(__instance);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("Particle.Init prefix", exception);
            }
        }

        internal static void GameScreenUpdatePrefix()
        {
            try
            {
                FollowRuntime.BeginFrame();
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("GameScreen.Update prefix", exception);
            }
        }

        internal static Exception GameScreenUpdateFinalizer(Exception __exception, double deltaTime)
        {
            try
            {
                if (__exception == null)
                {
                    FollowRuntime.EndFrame((float)deltaTime);
                }
                else
                {
                    FollowRuntime.AbortFrame();
                }
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("GameScreen.Update finalizer", exception);
            }
            return __exception;
        }

        internal static void RemoveParticleAtPrefix(ParticleManager __instance, int index)
        {
            try
            {
                FollowRuntime.UnbindParticleAt(__instance, index);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleManager.RemoveParticle(int) prefix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.UnbindParticleAt(__instance, index);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleManager.RemoveParticle(int) prefix", exception);
            }
        }

        internal static void ClearParticlesPrefix(ParticleManager __instance)
        {
            try
            {
                FollowRuntime.UnbindAllManagerParticles(__instance);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleManager.ClearParticles prefix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.UnbindAllManagerParticles(__instance);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleManager.ClearParticles prefix", exception);
            }
        }

        internal static void RemoveByPrefabPrefix(ParticleManager __instance, ParticlePrefab prefab)
        {
            try
            {
                FollowRuntime.UnbindManagerParticlesByPrefab(__instance, prefab);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleManager.RemoveByPrefab prefix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.UnbindManagerParticlesByPrefab(__instance, prefab);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleManager.RemoveByPrefab prefix", exception);
            }
        }

        internal static void SetMaxParticlesPrefix(ParticleManager __instance, int value)
        {
            try
            {
                FollowRuntime.UnbindParticlesDroppedByLimit(__instance, value);
            }
            catch (Exception exception)
            {
                FollowRuntime.DisableFromPatch("ParticleManager.MaxParticles prefix", exception);
            }

            try
            {
                ItemLayerParticleRuntime.UnbindParticlesDroppedByLimit(__instance, value);
            }
            catch (Exception exception)
            {
                ItemLayerParticleRuntime.DisableFromPatch("ParticleManager.MaxParticles prefix", exception);
            }
        }
    }

    internal static class ItemLayerAccess
    {
        internal static MethodInfo GameScreenDrawMap;
        internal static MethodInfo SubmarineDrawBack;
        internal static MethodInfo SubmarineDrawFront;
        internal static MethodInfo ParticleManagerDraw;
        internal static MethodInfo ParticleDraw;

        internal static AccessTools.FieldRef<Particle, int> SpriteIndex;
        internal static AccessTools.FieldRef<ParticleManager, LinkedList<Particle>> ParticlesInCreationOrder;

        internal static bool TryInitialize(out string error)
        {
            try
            {
                GameScreenDrawMap = Require(
                    AccessTools.Method(typeof(GameScreen), "DrawMap", new Type[]
                    {
                        typeof(GraphicsDevice), typeof(SpriteBatch), typeof(double)
                    }),
                    "GameScreen.DrawMap(GraphicsDevice, SpriteBatch, double)");
                SubmarineDrawBack = Require(
                    AccessTools.Method(typeof(Submarine), "DrawBack", new Type[]
                    {
                        typeof(SpriteBatch), typeof(bool), typeof(Predicate<MapEntity>)
                    }),
                    "Submarine.DrawBack(SpriteBatch, bool, Predicate<MapEntity>)");
                SubmarineDrawFront = Require(
                    AccessTools.Method(typeof(Submarine), "DrawFront", new Type[]
                    {
                        typeof(SpriteBatch), typeof(bool), typeof(Predicate<MapEntity>)
                    }),
                    "Submarine.DrawFront(SpriteBatch, bool, Predicate<MapEntity>)");
                ParticleManagerDraw = Require(
                    AccessTools.Method(typeof(ParticleManager), "Draw", new Type[]
                    {
                        typeof(SpriteBatch), typeof(bool), typeof(bool?),
                        typeof(ParticleBlendState), typeof(bool?)
                    }),
                    "ParticleManager.Draw(SpriteBatch, bool, bool?, ParticleBlendState, bool?)");
                ParticleDraw = Require(
                    AccessTools.Method(typeof(Particle), "Draw", new Type[] { typeof(SpriteBatch) }),
                    "Particle.Draw(SpriteBatch)");

                RequireField(typeof(Particle), "spriteIndex");
                RequireField(typeof(ParticleManager), "particlesInCreationOrder");
                SpriteIndex = AccessTools.FieldRefAccess<Particle, int>("spriteIndex");
                ParticlesInCreationOrder =
                    AccessTools.FieldRefAccess<ParticleManager, LinkedList<Particle>>(
                        "particlesInCreationOrder");

                int drawBackCalls = 0;
                int drawFrontCalls = 0;
                foreach (CodeInstruction instruction in
                    PatchProcessor.GetOriginalInstructions(GameScreenDrawMap, null))
                {
                    if (Equals(instruction.operand, SubmarineDrawBack)) { drawBackCalls++; }
                    if (Equals(instruction.operand, SubmarineDrawFront)) { drawFrontCalls++; }
                }
                if (drawBackCalls != ItemLayerParticleRuntime.ExpectedDrawBackCalls || drawFrontCalls != 1)
                {
                    throw new InvalidOperationException(
                        "GameScreen.DrawMap call layout changed (DrawBack=" + drawBackCalls +
                        ", DrawFront=" + drawFrontCalls + "; expected 3 and 1).");
                }

                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static T Require<T>(T member, string name) where T : MemberInfo
        {
            if (member == null) { throw new MissingMemberException(name); }
            return member;
        }

        private static void RequireField(Type type, string name)
        {
            if (AccessTools.Field(type, name) == null)
            {
                throw new MissingFieldException(type.FullName, name);
            }
        }
    }

    internal static class ItemLayerPatchInstaller
    {
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(
                ItemLayerAccess.GameScreenDrawMap,
                prefix: new HarmonyMethod(typeof(ItemLayerPatches), "GameScreenDrawMapPrefix"),
                finalizer: new HarmonyMethod(typeof(ItemLayerPatches), "GameScreenDrawMapFinalizer"));
            harmony.Patch(
                ItemLayerAccess.SubmarineDrawBack,
                postfix: new HarmonyMethod(typeof(ItemLayerPatches), "SubmarineDrawBackPostfix"));
            harmony.Patch(
                ItemLayerAccess.SubmarineDrawFront,
                prefix: new HarmonyMethod(typeof(ItemLayerPatches), "SubmarineDrawFrontPrefix"));
            harmony.Patch(
                ItemLayerAccess.ParticleManagerDraw,
                prefix: new HarmonyMethod(typeof(ItemLayerPatches), "ParticleManagerDrawPrefix"),
                finalizer: new HarmonyMethod(typeof(ItemLayerPatches), "ParticleManagerDrawFinalizer"));
            harmony.Patch(
                ItemLayerAccess.ParticleDraw,
                prefix: new HarmonyMethod(typeof(ItemLayerPatches), "ParticleDrawPrefix"));
        }
    }

    internal static class ItemLayerPatches
    {
        internal static void GameScreenDrawMapPrefix(GameScreen __instance, SpriteBatch spriteBatch)
        {
            ItemLayerParticleRuntime.BeginDrawMap(__instance, spriteBatch);
        }

        internal static Exception GameScreenDrawMapFinalizer(Exception __exception)
        {
            ItemLayerParticleRuntime.EndDrawMap(__exception == null);
            return __exception;
        }

        internal static void SubmarineDrawBackPostfix(SpriteBatch spriteBatch)
        {
            ItemLayerParticleRuntime.AfterDrawBack(spriteBatch);
        }

        internal static void SubmarineDrawFrontPrefix(SpriteBatch spriteBatch)
        {
            ItemLayerParticleRuntime.BeforeDrawFront(spriteBatch);
        }

        internal static void ParticleManagerDrawPrefix(out bool __state)
        {
            __state = ItemLayerParticleRuntime.BeginVanillaParticleDraw();
        }

        internal static Exception ParticleManagerDrawFinalizer(Exception __exception, bool __state)
        {
            ItemLayerParticleRuntime.RestoreVanillaParticleDraw(__state);
            return __exception;
        }

        internal static bool ParticleDrawPrefix(Particle __instance)
        {
            return ItemLayerParticleRuntime.ShouldRunVanillaParticleDraw(__instance);
        }
    }
}
