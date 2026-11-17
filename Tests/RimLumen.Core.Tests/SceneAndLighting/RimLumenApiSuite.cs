using System;
using System.Numerics;
using RimLumen.Core.Api;
using RimLumen.Core.Contracts;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class RimLumenApiSuite
{
    [LumenFact]
    public static void RegisterResolvePlayRoundtrip()
    {
        var asset = new EffectAsset
        {
            EffectId = "api.roundtrip",
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "root",
                    Emitter = new EmitterSpec
                    {
                        Name = "spark", PreferredTier = LumenParticleTier.L1_NativeCpu,
                        RateOverTime = 4f, MaxParticles = 16,
                        StartLifetimeMin = 5f, StartLifetimeMax = 5f,
                    },
                },
            },
        };
        RimLumenApi.RegisterEffect(asset);
        try
        {
            NativeTestData.Check(RimLumenApi.IsReady, "Bootstrap and VFX service are ready");
            NativeTestData.Check(
                RimLumenApi.TryGetEffect(asset.EffectId, out EffectAsset? resolved) && ReferenceEquals(asset, resolved),
                "The registered instance resolves through the facade");

            ILumenEffectHandle handle = RimLumenApi.Play(new LumenPlayRequest
            {
                EffectId = asset.EffectId, MapIndex = 111, OriginWorld = new Vector3(1f, 0f, 2f),
                Scale = 2f, RotationDegrees = 90f,
            });
            try
            {
                var typed = (EffectInstanceHandle)handle;
                NativeTestData.Check(typed.IsAlive && typed.CpuEmitterCount == 1, "Facade play binds an L1 emitter");
                NativeTestData.Check(typed.EffectId == asset.EffectId, "Handle reports the effect id");
                NativeTestData.Close(1f, typed.RootWorld.Translation.X, "origin x");
                NativeTestData.Close(2f, typed.RootWorld.Translation.Z, "origin z");
                Vector3 scaledAxis = Vector3.TransformNormal(Vector3.UnitX, typed.RootWorld);
                NativeTestData.Close(2f, scaledAxis.Length(), "request scale reaches the root matrix");
                NativeTestData.Check(MathF.Abs(scaledAxis.Z + 2f) < 1e-3f, "request yaw reaches the root matrix");
            }
            finally
            {
                handle.Dispose();
            }

            NativeTestData.Check(
                !RimLumenApi.TryGetEffect("api.missing", out EffectAsset? missing) && missing is null,
                "A missing id resolves to false/null");
            NativeTestData.Check(!RimLumenApi.TryGetEffect(string.Empty, out _), "An empty id resolves false");

            LumenCapabilities capabilities = RimLumenApi.Capabilities;
            NativeTestData.Check(capabilities.MaxGpuParticlesPerFrame >= 0, "Capabilities are readable before install");

            ILumenEffectHandle inert = RimLumenApi.Play(new LumenPlayRequest { EffectId = "api.missing", MapIndex = 111 });
            NativeTestData.Check(!inert.IsAlive, "An unknown effect returns an inert handle");
            inert.Dispose();
        }
        finally
        {
            LumenSceneBootstrap.Catalog.Unregister(asset.EffectId);
        }

        NativeTestData.Check(!RimLumenApi.TryGetEffect(asset.EffectId, out _), "Unregister removes the effect");
    }

    [LumenFact]
    public static void D30DensityRejectsNaNWithoutReplacingValidSetting()
        => RejectNonfiniteDensity(float.NaN);

    [LumenFact]
    public static void D30DensityRejectsPositiveInfinityWithoutReplacingValidSetting()
        => RejectNonfiniteDensity(float.PositiveInfinity);

    [LumenFact]
    public static void D30DensityRejectsNegativeInfinityWithoutReplacingValidSetting()
        => RejectNonfiniteDensity(float.NegativeInfinity);

    private static void RejectNonfiniteDensity(float input)
    {
        float original = RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumenRenderHost.ParticleDensityScale = 0.6f;
            bool rejected = false;
            try
            {
                RimLumenRenderHost.ParticleDensityScale = input;
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            float stored = RimLumenRenderHost.ParticleDensityScale;
            Console.WriteLine($"D30 input={input.ToString(System.Globalization.CultureInfo.InvariantCulture)}; ArgumentOutOfRangeException={rejected}; prior=0.6; stored={stored}.");
            NativeTestData.Check(rejected && stored == 0.6f,
                $"Nonfinite density {input} must throw ArgumentOutOfRangeException and preserve 0.6; rejected={rejected}, stored={stored}.");
        }
        finally
        {
            RimLumenRenderHost.ParticleDensityScale = original;
        }
    }

    [LumenFact]
    public static void SettingsApplyToRuntimeAndClampDensity()
    {
        bool originalGlow = RimLumenRenderHost.EnableVanillaAdditiveBloom;
        float originalDensity = RimLumenRenderHost.ParticleDensityScale;
        try
        {
            var settings = new RimLumenSettings { EnhanceVanillaAdditiveGlow = false, ParticleDensityScale = 0.05f };
            settings.ApplyScalarsToRuntime();
            NativeTestData.Check(!RimLumenRenderHost.EnableVanillaAdditiveBloom, "Glow toggle reaches the render host");
            NativeTestData.Close(0.1f, RimLumenRenderHost.ParticleDensityScale, "Density clamps up to 0.1");

            settings.ParticleDensityScale = 4f;
            settings.ApplyScalarsToRuntime();
            NativeTestData.Close(1f, RimLumenRenderHost.ParticleDensityScale, "Density clamps down to 1.0");

            settings.EnhanceVanillaAdditiveGlow = true;
            settings.ParticleDensityScale = 0.6f;
            settings.ApplyScalarsToRuntime();
            NativeTestData.Check(RimLumenRenderHost.EnableVanillaAdditiveBloom, "Glow toggle turns back on");
            NativeTestData.Close(0.6f, RimLumenRenderHost.ParticleDensityScale, "Density passes through inside the range");
        }
        finally
        {
            RimLumenRenderHost.EnableVanillaAdditiveBloom = originalGlow;
            RimLumenRenderHost.ParticleDensityScale = originalDensity;
        }
    }
}
