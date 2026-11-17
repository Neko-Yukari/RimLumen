using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;
using CurveKey = RimLumen.Core.Contracts.CurveKey;

namespace RimLumen.Core.Demo;

// Built-in optional demo EffectAssets for the Firework Launcher and Particle Fountain joy buildings.
//
// Coordinates follow docs/api.md section 2: world +X is east, +Y is altitude and +Z is north (up on
// screen). Every emitter that should shoot upwards uses Vertical3D: the simulation is true 3D (the Cone
// axis stays world +Y, GravityDrag runs along (0, -1, 0)) and only the drawn position adapts to the camera.
// LocalPosition.Y is the height above the effect's play origin; KillBelowFloor removes particles that fall
// back below that origin height.
internal static class RimLumenDemoEffects
{
    internal const string FireworkGoldId = "RimLumen.Demo.Firework.Gold";
    internal const string FireworkCyanId = "RimLumen.Demo.Firework.Cyan";
    internal const string FireworkMagentaId = "RimLumen.Demo.Firework.Magenta";
    internal const string ParticleFountainId = "RimLumen.Demo.ParticleFountain";

    internal static readonly string[] FireworkEffectIds =
    {
        FireworkGoldId,
        FireworkCyanId,
        FireworkMagentaId,
    };

    // Seconds from launch to apex; the trail stops here, the burst fires here.
    internal const float LaunchSeconds = 0.65f;

    // Seconds the launched firework stays visible (burst particles live at most 2.1 s past the apex).
    internal const float FireworkVisibleSeconds = 2.75f;

    // Burst hue and the ember colour it cools to, per variant (vanilla scene's stored RGB encoding).
    private static readonly Vector4[] FireworkHues =
    {
        new(1.00f, 0.62f, 0.16f, 1f),
        new(0.16f, 0.82f, 1.00f, 1f),
        new(1.00f, 0.28f, 0.86f, 1f),
    };

    private static readonly Vector4[] FireworkEmbers =
    {
        new(0.90f, 0.28f, 0.05f, 0f),
        new(0.05f, 0.35f, 0.60f, 0f),
        new(0.45f, 0.06f, 0.35f, 0f),
    };

    // Requires: LumenSceneBootstrap.Catalog is initialized.
    // Ensures: registers the 3 firework variants and the fluid fountain asset idempotently.
    internal static void EnsureRegistered()
    {
        InMemoryAssetCatalog catalog = LumenSceneBootstrap.Catalog;
        for (int i = 0; i < FireworkEffectIds.Length; i++)
        {
            if (catalog.TryResolveEffect(FireworkEffectIds[i], out _)) continue;
            catalog.Register(CreateFireworkAsset(FireworkEffectIds[i], i));
        }

        if (!catalog.TryResolveEffect(ParticleFountainId, out _))
        {
            catalog.Register(CreateParticleFountainAsset());
        }
    }

    // Requires: effectId is non-empty; variantIndex selects hue/ember (wrapped into range).
    // Ensures: returns a 4-node firework: L1 launch trail (0..0.65 s), L2 apex burst fired at 0.65 s,
    //          L1 white-hot sparkle core at the same instant, and a point light that stays dark until
    //          the apex, peaks there and is gone by 2.2 s.
    internal static EffectAsset CreateFireworkAsset(string effectId, int variantIndex)
    {
        int variant = variantIndex % FireworkHues.Length;
        Vector4 hue = FireworkHues[variant];
        Vector4 ember = FireworkEmbers[variant];
        const float apexY = 4.8f;

        return new EffectAsset
        {
            EffectId = effectId,
            DefaultDuration = 2.8f,
            Nodes = new[]
            {
                // 0: the rising rocket trail; it stops emitting the moment the shell bursts.
                new EffectNodeSpec
                {
                    Name = "launch_trail",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = new Vector3(0f, 0.35f, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "launch_trail",
                        PreferredTier = LumenParticleTier.L1_NativeCpu,
                        KillBelowFloor = true,
                        MaxParticles = 400,
                        Duration = LaunchSeconds,
                        Looping = false,
                        WorldSpaceSimulation = true,
                        RateOverTime = 240f,
                        Shape = LumenShapeKind.Cone,
                        ConeAngleDegrees = 7f,
                        StartSpeedMin = 8.0f,
                        StartSpeedMax = 10.5f,
                        StartLifetimeMin = 0.45f,
                        StartLifetimeMax = 0.65f,
                        StartSizeMin = 0.10f,
                        StartSizeMax = 0.20f,
                        StartColor = new Vector4(1.0f, 0.90f, 0.65f, 1f),
                        HdrIntensity = 4.0f,
                        SoftParticleFadeDistance = 0.4f,
                        BillboardMode = LumenBillboardMode.StretchedVelocity,
                        VelocityStretchFactor = 0.12f,
                        LengthScale = 0.6f,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1.0f),
                            new CurveKey(1f, 0.25f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(1.0f, 0.96f, 0.82f, 1.0f)),
                            new ColorStop(0.35f, new Vector4(1.0f, 0.70f, 0.28f, 0.85f)),
                            new ColorStop(1f, new Vector4(0.70f, 0.16f, 0.03f, 0.0f)),
                        }),
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.GravityDrag,
                                new Vector3(0f, -1f, 0f), new Vector3(0.55f, 0f, 0f), 0f, 3.5f),
                        },
                    },
                },

                // 1: the shell burst, one L2 burst at the apex so 1800 sparks leave together.
                new EffectNodeSpec
                {
                    Name = "apex_burst",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = new Vector3(0f, apexY, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "apex_burst",
                        PreferredTier = LumenParticleTier.L2_GpuCompute,
                        KillBelowFloor = true,
                        MaxParticles = 1850,
                        Duration = LaunchSeconds + 0.30f,
                        Looping = false,
                        WorldSpaceSimulation = true,
                        RateOverTime = 0f,
                        Bursts = new[] { new ParticleBurstSpec(LaunchSeconds, 1800) },
                        Shape = LumenShapeKind.Sphere,
                        ShapeExtents = new Vector3(0.18f, 0.18f, 0.18f),
                        StartSpeedMin = 6.0f,
                        StartSpeedMax = 14.5f,
                        StartLifetimeMin = 1.1f,
                        StartLifetimeMax = 2.1f,
                        StartSizeMin = 0.09f,
                        StartSizeMax = 0.22f,
                        StartColor = Vector4.One,
                        HdrIntensity = 8.0f,
                        SoftParticleFadeDistance = 0.6f,
                        BillboardMode = LumenBillboardMode.StretchedVelocity,
                        VelocityStretchFactor = 0.12f,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1.0f),
                            new CurveKey(0.35f, 1.15f),
                            new CurveKey(1f, 0.35f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(1f, 1f, 1f, 1f)),
                            new ColorStop(0.10f, hue),
                            new ColorStop(0.55f, new Vector4(hue.X * 0.90f, hue.Y * 0.90f, hue.Z * 0.90f, 0.90f)),
                            new ColorStop(1f, ember),
                        }),
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.GravityDrag,
                                new Vector3(0f, -1f, 0f), new Vector3(0.55f, 0f, 0f), 0f, 5.4f),
                            new ForceFieldSpec(ForceFieldKind.CurlTurbulence,
                                Vector3.Zero, Vector3.Zero, 0f, 1.8f, 1.3f),
                        },
                    },
                },

                // 2: a short white-hot core so the burst ignites instead of popping into view.
                new EffectNodeSpec
                {
                    Name = "sparkle_core",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = new Vector3(0f, apexY, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "sparkle_core",
                        PreferredTier = LumenParticleTier.L1_NativeCpu,
                        MaxParticles = 700,
                        Duration = LaunchSeconds + 0.35f,
                        Looping = false,
                        WorldSpaceSimulation = true,
                        RateOverTime = 0f,
                        Bursts = new[] { new ParticleBurstSpec(LaunchSeconds + 0.01f, 360) },
                        Shape = LumenShapeKind.Sphere,
                        ShapeExtents = new Vector3(0.30f, 0.30f, 0.30f),
                        StartSpeedMin = 0.6f,
                        StartSpeedMax = 3.8f,
                        StartLifetimeMin = 0.50f,
                        StartLifetimeMax = 1.00f,
                        StartSizeMin = 0.14f,
                        StartSizeMax = 0.32f,
                        StartColor = Vector4.One,
                        HdrIntensity = 12.0f,
                        SoftParticleFadeDistance = 0.5f,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 0.8f),
                            new CurveKey(0.20f, 1.1f),
                            new CurveKey(1f, 0.2f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(1f, 1f, 1f, 1f)),
                            new ColorStop(0.25f, new Vector4(1f, 1f, 0.95f, 0.9f)),
                            new ColorStop(1f, new Vector4(hue.X, hue.Y, hue.Z, 0f)),
                        }),
                    },
                },

                // 3: the apex flash. Dark until the shell bursts (IntensityOverTime starts at 0).
                new EffectNodeSpec
                {
                    Name = "apex_flash",
                    LocalPosition = new Vector3(0f, apexY, 0f),
                    PointLight = new PointLightSpec(
                        9f,
                        3f,
                        new Vector3(hue.X, hue.Y, hue.Z),
                        new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 0f),
                            new CurveKey(LaunchSeconds - 0.03f, 0f),
                            new CurveKey(LaunchSeconds + 0.05f, 3.2f),
                            new CurveKey(1.15f, 1.1f),
                            new CurveKey(2.2f, 0f),
                        })),
                },
            },
        };
    }

    // Ensures: returns a 3-node looping fountain: an L2 cone jet that arcs over and fades to alpha 0,
    //          an L2 ground mist disc, and a steady cool-cyan point light at the jet's throat.
    internal static EffectAsset CreateParticleFountainAsset()
    {
        float yMote = AltitudeLayer.MoteOverhead.AltitudeFor();

        return new EffectAsset
        {
            EffectId = ParticleFountainId,
            DefaultDuration = 3f,
            Nodes = new[]
            {
                // 0: the water jet itself.
                new EffectNodeSpec
                {
                    Name = "fluid_jet",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = new Vector3(0f, 0.55f, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "fluid_jet",
                        PreferredTier = LumenParticleTier.L2_GpuCompute,
                        KillBelowFloor = true,
                        MaxParticles = 2600,
                        Duration = 2f,
                        Looping = true,
                        WorldSpaceSimulation = true,
                        RateOverTime = 1300f,
                        Shape = LumenShapeKind.Cone,
                        ConeAngleDegrees = 15f,
                        StartSpeedMin = 6.2f,
                        StartSpeedMax = 8.6f,
                        StartLifetimeMin = 1.05f,
                        StartLifetimeMax = 1.55f,
                        StartSizeMin = 0.07f,
                        StartSizeMax = 0.17f,
                        StartColor = new Vector4(0.72f, 0.92f, 1f, 1f),
                        HdrIntensity = 2.6f,
                        SoftParticleFadeDistance = 0.9f,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 0.7f),
                            new CurveKey(0.30f, 1.0f),
                            new CurveKey(1f, 0.8f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(0.78f, 0.96f, 1.0f, 0.95f)),
                            new ColorStop(0.35f, new Vector4(0.32f, 0.82f, 1.0f, 0.85f)),
                            new ColorStop(0.72f, new Vector4(0.10f, 0.45f, 0.92f, 0.45f)),
                            new ColorStop(1f, new Vector4(0.05f, 0.20f, 0.55f, 0.0f)),
                        }),
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.GravityDrag,
                                new Vector3(0f, -1f, 0f), new Vector3(0.35f, 0f, 0f), 0f, 9.0f),
                            new ForceFieldSpec(ForceFieldKind.CurlTurbulence,
                                Vector3.Zero, Vector3.Zero, 0f, 1.1f, 2.0f),
                        },
                    },
                },

                // 1: the splash mist pooling on the ground around the basin.
                new EffectNodeSpec
                {
                    Name = "base_mist",
                    SpacePlane = LumenSpacePlane.GroundXZ,
                    LocalPosition = new Vector3(0f, yMote, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "base_mist",
                        PreferredTier = LumenParticleTier.L2_GpuCompute,
                        MaxParticles = 800,
                        Duration = 3f,
                        Looping = true,
                        WorldSpaceSimulation = true,
                        RateOverTime = 260f,
                        Shape = LumenShapeKind.CircleXZ,
                        ShapeExtents = new Vector3(0.75f, 0f, 0.75f),
                        StartSpeedMin = 0.05f,
                        StartSpeedMax = 0.30f,
                        StartLifetimeMin = 0.9f,
                        StartLifetimeMax = 1.6f,
                        StartSizeMin = 0.35f,
                        StartSizeMax = 0.75f,
                        StartColor = new Vector4(0.60f, 0.85f, 1f, 1f),
                        HdrIntensity = 1.2f,
                        SoftParticleFadeDistance = 0.7f,
                        BlendMode = LumenBlendMode.AlphaBlend,
                        BillboardMode = LumenBillboardMode.Horizontal,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 0.6f),
                            new CurveKey(0.40f, 1.0f),
                            new CurveKey(1f, 0.7f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(0.60f, 0.86f, 1.0f, 0.0f)),
                            new ColorStop(0.18f, new Vector4(0.55f, 0.82f, 1.0f, 0.50f)),
                            new ColorStop(0.70f, new Vector4(0.32f, 0.60f, 0.95f, 0.28f)),
                            new ColorStop(1f, new Vector4(0.20f, 0.40f, 0.70f, 0.0f)),
                        }),
                    },
                },

                // 2: the fountain's own light, steady for as long as the handle lives.
                new EffectNodeSpec
                {
                    Name = "light_core",
                    LocalPosition = new Vector3(0f, 0.6f, 0f),
                    PointLight = new PointLightSpec(7f, 2f, new Vector3(0.35f, 0.78f, 1f)),
                },
            },
        };
    }
}
