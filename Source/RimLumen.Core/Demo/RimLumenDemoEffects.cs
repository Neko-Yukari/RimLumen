using System;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using CurveKey = RimLumen.Core.Contracts.CurveKey;

namespace RimLumen.Core.Demo;

// Built-in optional demo EffectAssets for the Firework Launcher, Particle Fountain, Distortion Pylon and street lamp
// joy buildings.
//
// Coordinates follow docs/api.md section 2: world +X is east, +Y is altitude and +Z is north (up on
// screen). Every emitter that should shoot upwards uses Vertical3D: the simulation is true 3D (the Cone
// axis stays world +Y, GravityDrag runs along (0, -1, 0)) and only the drawn position adapts to the camera.
// Every Y is height above the ground (Y = 0) in cells: play origins sit on the ground, except the firework's,
// which rises with the rocket (PlayFirework). KillBelowFloor removes particles that fall back to the ground.
internal static class RimLumenDemoEffects
{
    internal const string FireworkGoldId = "RimLumen.Demo.Firework.Gold";
    internal const string FireworkCyanId = "RimLumen.Demo.Firework.Cyan";
    internal const string FireworkMagentaId = "RimLumen.Demo.Firework.Magenta";
    internal const string ParticleFountainId = "RimLumen.Demo.ParticleFountain";
    internal const string StreetLampId = "RimLumen.Demo.StreetLamp";
    internal const string DistortionPylonId = "RimLumen.Demo.DistortionPylon";

    // The pylon node that CompRimLumenDemoEmitter re-fires with ILumenEffectHandle.Trigger.
    internal const string PylonShockRingNode = "shock_ring";

    // Shared by the street lamp's visual pool and CompProperties_RimLumenLogicalLight in the lamp ThingDefs.
    internal const float StreetLampRadius = 5f;

    internal static readonly string[] FireworkEffectIds =
    {
        FireworkGoldId,
        FireworkCyanId,
        FireworkMagentaId,
    };

    // Seconds from launch to apex; the trail stops here, the burst fires here.
    internal const float LaunchSeconds = 0.65f;

    // Rocket height above the ground at launch (the launcher mouth) and at the apex.
    internal const float LaunchHeight = 0.35f;
    internal const float ApexHeight = 4.8f;

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
    // Ensures: registers the 3 firework variants, the fluid fountain and the street lamp asset idempotently.
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

        if (!catalog.TryResolveEffect(StreetLampId, out _))
        {
            catalog.Register(CreateStreetLampAsset());
        }

        if (!catalog.TryResolveEffect(DistortionPylonId, out _))
        {
            catalog.Register(CreateDistortionPylonAsset());
        }
    }

    // Ensures: LaunchHeight at 0 s and ApexHeight from LaunchSeconds on, slowing to zero vertical speed at the apex:
    //          h = LaunchHeight + (ApexHeight - LaunchHeight) * (1 - (1 - u)^2), u = min(1, seconds / LaunchSeconds).
    internal static float RocketHeight(float seconds)
    {
        float u = MathF.Min(1f, seconds / LaunchSeconds);
        return LaunchHeight + (ApexHeight - LaunchHeight) * (1f - (1f - u) * (1f - u));
    }

    // Requires: play is RimLumenApi.Play or Core's LumenVfxService.Play, so an alive handle is an EffectInstanceHandle.
    // Ensures: plays the firework with a Follow that keeps the origin over launcherGround and lifts it to
    //          RocketHeight(the handle's own PlaybackTime; 0 while Play builds it). The rocket and the burst schedule
    //          share that clock, so every node rides the rocket and the bursts fire exactly at the apex.
    internal static ILumenEffectHandle PlayFirework(Func<LumenPlayRequest, ILumenEffectHandle> play, string effectId,
        int mapIndex, Vector3 launcherGround)
    {
        EffectInstanceHandle? clock = null;
        ILumenEffectHandle handle = play(new LumenPlayRequest
        {
            EffectId = effectId,
            MapIndex = mapIndex,
            OriginWorld = launcherGround,
            Follow = (out Vector3 position, out float rotationDegrees) =>
            {
                position = launcherGround + new Vector3(0f, RocketHeight(clock?.PlaybackTime ?? 0f), 0f);
                rotationDegrees = 0f;
                return true;
            },
            RealTime = false,
        });
        if (handle.IsAlive) clock = (EffectInstanceHandle)handle;
        return handle;
    }

    // Requires: effectId is non-empty; variantIndex selects hue/ember (wrapped into range); played with PlayFirework.
    // Ensures: returns a 5-node firework whose nodes all sit at the rising play origin: the rocket's native trail
    //          ribbon and L1 shed sparks (0..0.65 s), L2 apex burst fired at 0.65 s whose sparks bounce off walls and
    //          expire under roofs, L1 white-hot sparkle core at the same instant, a point light and a screen dim that
    //          stay dark until the apex, peak there and are gone by 2.2 s and 1.6 s, and the L2 crackle the sparks
    //          spawn when they hit a wall or die (in the air, on the floor or under a roof).
    internal static EffectAsset CreateFireworkAsset(string effectId, int variantIndex)
    {
        int variant = variantIndex % FireworkHues.Length;
        Vector4 hue = FireworkHues[variant];
        Vector4 ember = FireworkEmbers[variant];

        return new EffectAsset
        {
            EffectId = effectId,
            DefaultDuration = 2.8f,
            Nodes = new[]
            {
                // 0: the rocket: a native trail ribbon behind the rising origin, plus sparks it sheds until the shell
                //    bursts. At the apex the head stops, so the ribbon shrinks into it within its Lifetime.
                new EffectNodeSpec
                {
                    Name = "launch_trail",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    Ribbon = new RibbonSpec
                    {
                        Mode = LumenRibbonMode.Trail,
                        Lifetime = 0.4f,
                        MinVertexDistance = 0.06f,
                        MaxPoints = 64,
                        Width = 0.14f,
                        WidthOverLength = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1f),
                            new CurveKey(1f, 0f),
                        }),
                        ColorOverLength = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(1.0f, 0.96f, 0.82f, 1.0f)),
                            new ColorStop(0.35f, new Vector4(1.0f, 0.70f, 0.28f, 0.85f)),
                            new ColorStop(1f, new Vector4(0.70f, 0.16f, 0.03f, 0.0f)),
                        }),
                        HdrIntensity = 5f,
                    },
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
                        Shape = LumenShapeKind.Sphere,
                        ShapeExtents = new Vector3(0.04f, 0.04f, 0.04f),
                        StartSpeedMin = 0.3f,
                        StartSpeedMax = 1.5f,
                        StartLifetimeMin = 0.25f,
                        StartLifetimeMax = 0.5f,
                        StartSizeMin = 0.06f,
                        StartSizeMax = 0.14f,
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
                        Collision = new CollisionSpec
                        {
                            Mode = LumenCollisionMode.Bounce,
                            Bounce = 0.4f,
                            Dampen = 0.25f,
                            LifetimeLoss = 0.15f,
                            KillUnderRoof = true,
                        },
                        SubEmitters = new[]
                        {
                            new SubEmitterSpec(LumenSubEmitterTrigger.Collision, CrackleNodeIndex, 4),
                            new SubEmitterSpec(LumenSubEmitterTrigger.Death, CrackleNodeIndex, 3, 0.12f),
                        },
                    },
                },

                // 2: a short white-hot core so the burst ignites instead of popping into view.
                new EffectNodeSpec
                {
                    Name = "sparkle_core",
                    SpacePlane = LumenSpacePlane.Vertical3D,
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

                // 3: the apex flash and a short background dim that makes the burst pop. Both dark until the shell
                //    bursts (their curves start at 0).
                new EffectNodeSpec
                {
                    Name = "apex_flash",
                    ScreenDim = new ScreenDimSpec
                    {
                        Strength = 0.35f,
                        Duration = 1.6f,
                        StrengthOverTime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 0f),
                            new CurveKey(LaunchSeconds, 0f),
                            new CurveKey(LaunchSeconds + 0.08f, 1f),
                            new CurveKey(1.6f, 0f),
                        }),
                    },
                    PointLight = new PointLightSpec(
                        9f,
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

                // 4: crackle, emitted only by the apex sparks' Collision and Death events at the event position.
                new EffectNodeSpec
                {
                    Name = "spark_crackle",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    Emitter = new EmitterSpec
                    {
                        Name = "spark_crackle",
                        PreferredTier = LumenParticleTier.L2_GpuCompute,
                        KillBelowFloor = true,
                        MaxParticles = 1200,
                        Duration = LaunchSeconds + 0.30f,
                        Looping = false,
                        WorldSpaceSimulation = true,
                        RateOverTime = 0f,
                        Shape = LumenShapeKind.Sphere,
                        ShapeExtents = new Vector3(0.05f, 0.05f, 0.05f),
                        StartSpeedMin = 0.8f,
                        StartSpeedMax = 3.2f,
                        StartLifetimeMin = 0.12f,
                        StartLifetimeMax = 0.32f,
                        StartSizeMin = 0.05f,
                        StartSizeMax = 0.14f,
                        StartColor = Vector4.One,
                        HdrIntensity = 10.0f,
                        SoftParticleFadeDistance = 0.3f,
                        BillboardMode = LumenBillboardMode.StretchedVelocity,
                        VelocityStretchFactor = 0.05f,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1.0f),
                            new CurveKey(1f, 0.2f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(1f, 1f, 0.92f, 1f)),
                            new ColorStop(0.4f, hue),
                            new ColorStop(1f, new Vector4(hue.X, hue.Y, hue.Z, 0f)),
                        }),
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.GravityDrag,
                                new Vector3(0f, -1f, 0f), new Vector3(1.5f, 0f, 0f), 0f, 3.0f),
                        },
                    },
                },
            },
        };
    }

    private const int CrackleNodeIndex = 4;

    // Ensures: returns a 3-node looping fountain: an L2 cone jet that arcs over and fades to alpha 0,
    //          an L2 ground mist disc, and a steady cool-cyan point light at the jet's throat.
    internal static EffectAsset CreateParticleFountainAsset()
    {
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
                        // Droplets bounce off walls below 2 cells and vanish where they enter a roofed cell.
                        Collision = new CollisionSpec
                        {
                            Mode = LumenCollisionMode.Bounce,
                            Bounce = 0.35f,
                            Dampen = 0.2f,
                            LifetimeLoss = 0.1f,
                            KillUnderRoof = true,
                        },
                    },
                },

                // 1: the splash mist pooling on the ground around the basin; GroundXZ draws on the mote layer in 2D.
                new EffectNodeSpec
                {
                    Name = "base_mist",
                    SpacePlane = LumenSpacePlane.GroundXZ,
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
                    PointLight = new PointLightSpec(7f, new Vector3(0.35f, 0.78f, 1f)),
                },
            },
        };
    }

    // Ensures: returns a light-only lamp: one steady warm point light at the lamp head, held by a single loop
    //          state so the handle lives until the building releases it.
    internal static EffectAsset CreateStreetLampAsset()
    {
        return new EffectAsset
        {
            EffectId = StreetLampId,
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "lamp_light",
                    LocalPosition = new Vector3(0f, 1.2f, 0f),
                    PointLight = new PointLightSpec(StreetLampRadius, new Vector3(1f, 0.78f, 0.46f)),
                },
            },
            States = new[]
            {
                new EffectStateSpec { Name = "lit", Nodes = new[] { "lamp_light" }, Loop = true },
            },
        };
    }

    // Ensures: returns a looping pylon: a heat haze over the pylon, a ground shock ring that fires on every
    //          (re)start of node "shock_ring", a spark orbiting 1.5 cells out at 2 cells height that draws a native
    //          trail ribbon, a jittering beam arc from the pylon tip to that spark, and a steady violet light pool.
    //          The orbit comes from the play request's Follow yaw: the nodes sit at a fixed local offset.
    internal static EffectAsset CreateDistortionPylonAsset()
    {
        var orbit = new Vector3(1.5f, 2.0f, 0f);
        var tip = new Vector3(0f, 1.1f, 0f);

        return new EffectAsset
        {
            EffectId = DistortionPylonId,
            Nodes = new[]
            {
                // 0: continuous shimmer around the orbit; Duration 0 = while the effect plays. It bends the scene and,
                //    with the player's refract-layer setting on (default), the orbit trail and the arc passing through.
                new EffectNodeSpec
                {
                    Name = "heat_haze",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = new Vector3(0f, 1.6f, 0f),
                    Distortion = new DistortionSpec
                    {
                        Kind = LumenDistortionKind.HeatHaze,
                        Radius = 2.6f,
                        Strength = 0.18f,
                        Frequency = 1.5f,
                    },
                },

                // 1: one shock ring on the ground per restart, fading out as it reaches 5 cells.
                new EffectNodeSpec
                {
                    Name = PylonShockRingNode,
                    Distortion = new DistortionSpec
                    {
                        Kind = LumenDistortionKind.RingWave,
                        Radius = 5f,
                        Strength = 0.45f,
                        Thickness = 0.6f,
                        Speed = 6f,
                        Duration = 0.85f,
                        StrengthOverTime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1f),
                            new CurveKey(0.85f, 0f),
                        }),
                    },
                },

                // 2: the orbiting spark: a native trail ribbon plus a few sparks shed from its head.
                new EffectNodeSpec
                {
                    Name = "orbit_spark",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = orbit,
                    Ribbon = new RibbonSpec
                    {
                        Mode = LumenRibbonMode.Trail,
                        Lifetime = 0.45f,
                        MinVertexDistance = 0.08f,
                        MaxPoints = 96,
                        Width = 0.22f,
                        WidthOverLength = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1f),
                            new CurveKey(1f, 0f),
                        }),
                        ColorOverLength = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(0.85f, 0.92f, 1f, 1f)),
                            new ColorStop(0.4f, new Vector4(0.50f, 0.45f, 1f, 0.8f)),
                            new ColorStop(1f, new Vector4(0.35f, 0.15f, 0.90f, 0f)),
                        }),
                        HdrIntensity = 4f,
                    },
                    Emitter = new EmitterSpec
                    {
                        Name = "orbit_spark",
                        PreferredTier = LumenParticleTier.L1_NativeCpu,
                        KillBelowFloor = true,
                        MaxParticles = 120,
                        Duration = 1f,
                        Looping = true,
                        WorldSpaceSimulation = true,
                        RateOverTime = 90f,
                        Shape = LumenShapeKind.Sphere,
                        ShapeExtents = new Vector3(0.05f, 0.05f, 0.05f),
                        StartSpeedMin = 0.2f,
                        StartSpeedMax = 0.8f,
                        StartLifetimeMin = 0.2f,
                        StartLifetimeMax = 0.4f,
                        StartSizeMin = 0.12f,
                        StartSizeMax = 0.28f,
                        StartColor = Vector4.One,
                        HdrIntensity = 6f,
                        SoftParticleFadeDistance = 0.3f,
                        SizeOverLifetime = new ScalarCurve(new[]
                        {
                            new CurveKey(0f, 1f),
                            new CurveKey(1f, 0.2f),
                        }),
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(1f, 1f, 1f, 1f)),
                            new ColorStop(1f, new Vector4(0.55f, 0.35f, 1f, 0f)),
                        }),
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.GravityDrag,
                                new Vector3(0f, -1f, 0f), new Vector3(0.8f, 0f, 0f), 0f, 1.5f),
                        },
                    },
                },

                // 3: a jagged arc from the pylon tip to the orbiting spark; both ends turn with the Follow yaw.
                new EffectNodeSpec
                {
                    Name = "arc_beam",
                    SpacePlane = LumenSpacePlane.Vertical3D,
                    LocalPosition = tip,
                    Ribbon = new RibbonSpec
                    {
                        Mode = LumenRibbonMode.Beam,
                        EndLocal = orbit - tip,
                        Segments = 8,
                        NoiseAmplitude = 0.15f,
                        NoiseFrequency = 24f,
                        Width = 0.09f,
                        ColorOverLength = ColorGradient.Constant(new Vector4(0.75f, 0.70f, 1f, 1f)),
                        HdrIntensity = 4f,
                    },
                },

                // 4: the pylon's light pool; it lies in the scene behind the effect layer, so the haze bends it.
                new EffectNodeSpec
                {
                    Name = "pylon_light",
                    LocalPosition = new Vector3(0f, 1.6f, 0f),
                    PointLight = new PointLightSpec(4.5f, new Vector3(0.62f, 0.45f, 1f)),
                },
            },
        };
    }
}
