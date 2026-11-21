using System;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Ribbons;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.Ribbons;

// Drives LumenRibbonBackend through the node-feature seam and reads the strips Build produces.
[LumenTestSuite]
public static class RibbonBackendSuite
{
    private static readonly Matrix4x4 TopDown = new(
        1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    [LumenFact]
    public static void TrailEmitsByDistanceClipsItsTailAndAgesOut()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Trail(lifetime: 1f, minVertexDistance: 0.5f, maxPoints: 16));
        backend.UpdateInstance(id, Frame(new Vector3(0f, 0f, 0f), dt: 0.1f));
        backend.UpdateInstance(id, Frame(new Vector3(0.3f, 0f, 0f), dt: 0.1f));
        // 0.3 < minVertexDistance: no new point, but the live head still leads the strip.
        XS(backend, 0.3f, 0f);
        backend.UpdateInstance(id, Frame(new Vector3(0.6f, 0f, 0f), dt: 0.1f));
        XS(backend, 0.6f, 0f);
        // elapsed 1.2, cutoff 0.2: the point born at 0.1 is clipped halfway towards the one born at 0.3.
        backend.UpdateInstance(id, Frame(new Vector3(0.6f, 0f, 0f), dt: 0.9f));
        XS(backend, 0.6f, 0.3f);
        // elapsed 1.35: both points have expired; the head alone draws nothing.
        backend.UpdateInstance(id, Frame(new Vector3(0.6f, 0f, 0f), dt: 0.15f));
        Build(backend);
        Equal(0, backend.Vertices.Length);
    }

    [LumenFact]
    public static void TrailCapacityDropsTheOldestPoint()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Trail(lifetime: 10f, minVertexDistance: 0.5f, maxPoints: 2));
        backend.UpdateInstance(id, Frame(new Vector3(0f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(new Vector3(1f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(new Vector3(2f, 0f, 0f)));
        XS(backend, 2f, 1f);
    }

    [LumenFact]
    public static void RestartClearsTheTrail()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Trail(lifetime: 10f, minVertexDistance: 0.5f, maxPoints: 8));
        backend.UpdateInstance(id, Frame(new Vector3(0f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(new Vector3(1f, 0f, 0f)));
        backend.RestartInstance(id);
        backend.UpdateInstance(id, Frame(new Vector3(5f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(new Vector3(6f, 0f, 0f)));
        XS(backend, 6f, 5f);
    }

    [LumenFact]
    public static void DrainingTrailStopsGrowingAndDiesWhenItsPointsExpire()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Trail(lifetime: 1f, minVertexDistance: 0.5f, maxPoints: 8));
        backend.UpdateInstance(id, Frame(new Vector3(0f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(new Vector3(1f, 0f, 0f)));
        backend.DestroyInstance(id, immediate: false);
        Check(backend.IsInstanceAlive(id), "A draining trail with points stays alive.");
        backend.UpdateInstance(id, Frame(new Vector3(5f, 0f, 0f), active: false));
        XS(backend, 1f, 0f);
        backend.UpdateInstance(id, Frame(new Vector3(5f, 0f, 0f), active: false, dt: 1f));
        Check(!backend.IsInstanceAlive(id), "A drained trail is released.");
        Check(!backend.HasInstances, "Nothing remains after the drain.");

        int beam = Play(backend, Beam(new Vector3(1f, 0f, 0f)));
        backend.DestroyInstance(beam, immediate: false);
        Check(!backend.IsInstanceAlive(beam), "A beam has nothing to fade.");
        int other = Play(backend, Trail(lifetime: 1f, minVertexDistance: 0.5f, maxPoints: 8));
        Check(other != beam && other != id, "Ids are never reused.");
        backend.UpdateInstance(other, Frame(Vector3.Zero));
        backend.DestroyInstance(other, immediate: true);
        Check(!backend.IsInstanceAlive(other), "Immediate destroy releases a trail with points.");
    }

    [LumenFact]
    public static void WidthColourGainAndStretchUvFollowNormalizedLength()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, new RibbonSpec
        {
            Mode = LumenRibbonMode.Beam, EndLocal = new Vector3(4f, 0f, 0f), Segments = 4, Width = 2f, HdrIntensity = 3f,
            WidthOverLength = new ScalarCurve(new[] { new CurveKey(0f, 1f, -0.5f, -0.5f), new CurveKey(1f, 0.5f, -0.5f, -0.5f) }),
            ColorOverLength = new ColorGradient(new[] { new ColorStop(0f, new Vector4(1f, 0f, 0f, 1f)), new ColorStop(1f, new Vector4(0f, 0f, 1f, 0f)) }),
        });
        backend.UpdateInstance(id, Frame(Vector3.Zero, intensity: 2f));
        Build(backend);
        Equal(10, backend.Vertices.Length);
        Close(2f, Width(backend, 0));
        Close(1.5f, Width(backend, 2));
        Close(1f, Width(backend, 4));
        Vector(new Vector4(1f, 0f, 0f, 1f), backend.Vertices[0].Color);
        Vector(new Vector4(0.5f, 0f, 0.5f, 0.5f), backend.Vertices[4].Color);
        Close(6f, backend.Vertices[0].HdrGain);
        Close(0.5f, backend.Vertices[4].Uv.X);
        Close(0f, backend.Vertices[4].Uv.Y);
        Close(1f, backend.Vertices[5].Uv.Y);
        Vector(new Vector2(0.5f, 1f), backend.Vertices[5].RadialUv);
        Equal(24, backend.Indices.Length);
    }

    [LumenFact]
    public static void TileUvCountsCells()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, new RibbonSpec
        {
            Mode = LumenRibbonMode.Beam, EndLocal = new Vector3(4f, 0f, 0f), Segments = 4, TextureMode = LumenRibbonTextureMode.Tile,
        });
        backend.UpdateInstance(id, Frame(Vector3.Zero));
        Build(backend);
        Close(2f, backend.Vertices[4].Uv.X);
        Close(4f, backend.Vertices[8].Uv.X);
    }

    [LumenFact]
    public static void BeamNoiseIsSeededPerpendicularBoundedAndRerolledAtItsFrequency()
    {
        RibbonSpec spec = new()
        {
            Mode = LumenRibbonMode.Beam, EndLocal = new Vector3(4f, 0f, 0f), Segments = 4, NoiseAmplitude = 0.5f, NoiseFrequency = 2f,
        };
        Vector3[] Roll(float elapsed, uint seed)
        {
            var backend = new LumenRibbonBackend();
            int id = Play(backend, spec, seed);
            backend.UpdateInstance(id, Frame(Vector3.Zero, dt: elapsed));
            return Centres(backend);
        }
        Vector3[] first = Roll(0.1f, 7u);
        Check(first.SequenceEqual(Roll(0.4f, 7u)), "Same seed and roll give the same arc.");
        Check(!first.SequenceEqual(Roll(0.6f, 7u)), "Crossing 1/NoiseFrequency re-rolls the arc.");
        Check(!first.SequenceEqual(Roll(0.1f, 8u)), "Another seed gives another arc.");
        Vector(Vector3.Zero, first[0]);
        Vector(new Vector3(4f, 0f, 0f), first[4]);
        for (int i = 1; i < 4; i++)
        {
            Close(i, first[i].X);
            Check(new Vector2(first[i].Y, first[i].Z).Length() <= 0.5f + 1e-5f, $"Jitter {i} exceeds the amplitude.");
        }
        Check(first.Skip(1).Take(3).Any(c => c.Y != 0f || c.Z != 0f), "Interior vertices are jittered.");
    }

    [LumenFact]
    public static void LifecycleMidFrameBeamUsesOnlyItsOwnNoiseClock()
    {
        var asset = new EffectAsset { EffectId = "ribbon.mid-frame",
            Nodes = [new() { Name = "beam", Ribbon = new() { Mode = LumenRibbonMode.Beam,
                EndLocal = new Vector3(4, 0, 0), Segments = 3, NoiseAmplitude = 0.5f, NoiseFrequency = 4f } }],
            States = [new() { Name = "wait", Duration = 0.5f, Next = "show" }, new() { Name = "show", Nodes = ["beam"], Loop = true }] };
        var service = SceneAndLighting.VfxTestData.Service(asset);
        var backend = new LumenRibbonBackend();
        LumenHostRegistry.RegisterNodeFeatureBackend(backend);
        try
        {
            using var handle = service.Play(new LumenPlayRequest { MapIndex = 7, EffectId = asset.EffectId });
            SceneAndLighting.VfxTestData.Step(service, 0.625f);
            Vector3[] entered = Centres(backend);
            handle.Trigger("beam");
            SceneAndLighting.VfxTestData.Step(service, 0.125f);
            Build(backend);
            Equal(4, entered.Length);
            Check(entered.SequenceEqual(Centres(backend)), "After 0.5 s waiting, a 0.625 s frame must draw the 0.125 s beam, not the 0.625 s noise roll.");
        }
        finally { LumenHostRegistry.UnregisterNodeFeatureBackend(backend); }
    }

    [LumenFact]
    public static void RisingVertical3DTrailProjectsHalfItsRiseAboveFixedGround()
    {
        var backend = new LumenRibbonBackend();
        Vector3 launch = new(3f, 0f, 7f), apex = new(3f, 4f, 7f);
        int id = backend.CreateInstance(new EffectNodeSpec
        {
            Name = "trail", SpacePlane = LumenSpacePlane.Vertical3D, Ribbon = Trail(1f, 0.5f, 8),
        }, Frame(launch, active: false), false, 1u);
        backend.RestartInstance(id);
        backend.UpdateInstance(id, Frame(launch));
        backend.UpdateInstance(id, Frame(apex));
        Build(backend, TopDown);
        Vector3[] centres = Centres(backend);
        Equal(2, centres.Length);
        Console.WriteLine($"[GROUND] Trail rise=4, screen length={centres[0].Z - centres[1].Z} cells");
        Vector(new Vector3(0f, 0f, 2f), centres[0] - centres[1]);
        Close(7f, centres[1].Z);
    }

    [LumenFact]
    public static void Vertical3DBeamProjectsNodeAndEndpointHeights()
    {
        var backend = new LumenRibbonBackend();
        int id = backend.CreateInstance(new EffectNodeSpec
        {
            Name = "bolt", SpacePlane = LumenSpacePlane.Vertical3D, Ribbon = Beam(new Vector3(0f, 2f, 0f)),
        }, Frame(Vector3.Zero, active: false), false, 1u);
        backend.RestartInstance(id);
        // The node sits 1 cell above the ground; the beam rises 2 more cells.
        backend.UpdateInstance(id, Frame(new Vector3(3f, 1f, 0f)));
        Build(backend, TopDown);
        Vector3[] centres = Centres(backend);
        float drawnY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        // Straight-down 2D: each cell of world height draws cos(60) = 0.5 cells towards screen-up (+Z).
        Vector(new Vector3(3f, drawnY, 0.5f), centres[0]);
        Vector(new Vector3(3f, drawnY, 1.5f), centres[1]);
        // A beam along screen-up widens along screen-right.
        Vector(new Vector3(-0.5f, 0f, 0f), backend.Vertices[0].Position - centres[0]);
    }

    [LumenFact]
    public static void Vertical3DBeamEndsLandOnTheirOwnCells()
    {
        var backend = new LumenRibbonBackend();
        int id = backend.CreateInstance(new EffectNodeSpec
        {
            Name = "rail", SpacePlane = LumenSpacePlane.Vertical3D, Ribbon = Beam(new Vector3(0f, 0f, 25f)),
        }, Frame(Vector3.Zero, active: false), false, 1u);
        backend.RestartInstance(id);
        backend.UpdateInstance(id, Frame(new Vector3(3f, 0f, 7f)));
        Build(backend, TopDown);
        Vector3[] centres = Centres(backend);
        float drawnY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        Console.WriteLine($"[ANCHOR] beam end at Z=32 draws at Z={centres[1].Z:F4}, off by {centres[1].Z - 32f:F4}");
        Vector(new Vector3(3f, drawnY, 7f), centres[0]);
        Vector(new Vector3(3f, drawnY, 32f), centres[1]);
    }

    [LumenFact]
    public static void GroundXZRibbonsDrawOnTheMoteLayerUnderTopDownCamera()
    {
        // At Y = 0 a ground ribbon would sit at terrain depth: occluded, and its soft fade would zero it.
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Beam(new Vector3(2f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(new Vector3(3f, 0f, 7f)));
        Build(backend, TopDown);
        float drawnY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        Vector3[] centres = Centres(backend);
        Vector(new Vector3(3f, drawnY, 7f), centres[0]);
        Vector(new Vector3(5f, drawnY, 7f), centres[1]);
    }

    [LumenFact]
    public static void GroundRibbonsWidenInTheGroundPlane()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Beam(new Vector3(2f, 0f, 0f)));
        backend.UpdateInstance(id, Frame(Vector3.Zero));
        int ground = Play(backend, new RibbonSpec
        {
            Mode = LumenRibbonMode.Beam, EndLocal = new Vector3(2f, 0f, 0f), Segments = 1, Alignment = LumenRibbonAlignment.Ground,
        });
        backend.UpdateInstance(ground, Frame(Vector3.Zero));
        Build(backend);
        // Identity view faces -Z, so a View ribbon along X widens along Y; a Ground ribbon along Z.
        Equal(8, backend.Vertices.Length);
        Vector(new Vector3(0f, 0.5f, 0f), backend.Vertices[0].Position);
        Vector(new Vector3(0f, 0f, 0.5f), backend.Vertices[4].Position);
    }

    [LumenFact]
    public static void BuildDrawsOnlyTheCurrentMapAndOneSubBatchPerRibbon()
    {
        var backend = new LumenRibbonBackend();
        int a = Play(backend, Beam(new Vector3(1f, 0f, 0f)));
        backend.UpdateInstance(a, Frame(Vector3.Zero, map: 1));
        int b = Play(backend, new RibbonSpec
        {
            Mode = LumenRibbonMode.Beam, EndAtTarget = true, Segments = 2, TextureKey = "spark",
            BlendMode = LumenBlendMode.AlphaBlend, SoftParticleFadeDistance = 0.25f,
        });
        backend.UpdateInstance(b, Frame(Vector3.Zero, map: 2, target: new Vector3(0f, 0f, 3f)));
        Build(backend, map: 2);
        Equal(1, backend.SubBatches.Count);
        Equal(new L1SubBatch("spark", LumenBlendMode.AlphaBlend, 0, 12, 0.25f), backend.SubBatches[0]);
        Vector(new Vector3(0f, 0f, 3f), Centres(backend)[2]);
        Build(backend, map: -1);
        Equal(2, backend.SubBatches.Count);
        Equal(18, backend.Indices.Length);
    }

    [LumenFact]
    public static void SteadyStateFramesDoNotAllocate()
    {
        var backend = new LumenRibbonBackend();
        int trail = Play(backend, Trail(lifetime: 0.5f, minVertexDistance: 0.05f, maxPoints: 64));
        int beam = Play(backend, Beam(new Vector3(3f, 0f, 0f)));
        void Frame60(int i)
        {
            backend.UpdateInstance(trail, Frame(new Vector3(MathF.Sin(i * 0.1f), 0f, MathF.Cos(i * 0.1f)), dt: 1f / 60f));
            backend.UpdateInstance(beam, Frame(Vector3.Zero, dt: 1f / 60f));
            Build(backend, TopDown);
        }
        for (int i = 0; i < 120; i++) Frame60(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 120; i < 240; i++) Frame60(i);
        Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [LumenFact]
    public static void BuildSkipsGeometryWhenMapIsNotRenderedWhileUpdateStillAdvancesAndDrains()
    {
        var backend = new LumenRibbonBackend();
        int id = Play(backend, Trail(lifetime: 1f, minVertexDistance: 0.5f, maxPoints: 8));
        backend.UpdateInstance(id, Frame(new Vector3(0f, 0f, 0f), dt: 0.1f));
        backend.UpdateInstance(id, Frame(new Vector3(1f, 0f, 0f), dt: 0.1f));
        Build(backend);
        Equal(4, backend.Vertices.Length);

        backend.DestroyInstance(id, immediate: false);
        backend.UpdateInstance(id, Frame(new Vector3(1f, 0f, 0f), active: false, dt: 0.5f));
        backend.Build(new LumenCaptureContext
        {
            CurrentMapIndex = 1, RendersMap = false, FrameIndex = 2, DeltaTime = 0.5f, ViewportWidth = 100, ViewportHeight = 100,
            ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        });
        Check(backend.Vertices.IsEmpty && backend.Indices.IsEmpty && backend.SubBatches.Count == 0,
            $"RendersMap=false must build no ribbon geometry (got vertices={backend.Vertices.Length}, batches={backend.SubBatches.Count}).");
        Check(backend.IsInstanceAlive(id), "Draining trail remains alive before its 1.0 s lifetime elapses.");

        backend.UpdateInstance(id, Frame(new Vector3(1f, 0f, 0f), active: false, dt: 0.6f));
        Check(!backend.IsInstanceAlive(id), "Trail clock continues advancing and drains out while RendersMap=false.");
    }

    private static RibbonSpec Trail(float lifetime, float minVertexDistance, int maxPoints) => new()
    {
        Mode = LumenRibbonMode.Trail, Lifetime = lifetime, MinVertexDistance = minVertexDistance, MaxPoints = maxPoints,
    };

    private static RibbonSpec Beam(Vector3 end) => new() { Mode = LumenRibbonMode.Beam, EndLocal = end, Segments = 1 };

    private static int Play(LumenRibbonBackend backend, RibbonSpec spec, uint seed = 1u)
    {
        int id = backend.CreateInstance(new EffectNodeSpec { Name = "ribbon", Ribbon = spec }, Frame(Vector3.Zero, active: false), false, seed);
        backend.RestartInstance(id);
        return id;
    }

    private static LumenNodeFeatureFrame Frame(Vector3 position, bool active = true, float dt = 0.1f, float? depthReferenceZ = null,
        int map = 1, Vector3? target = null, float intensity = 1f)
        => new(new LumenEmitterTransform(Matrix4x4.CreateTranslation(position), Quaternion.Identity), target, map, intensity,
            active, dt, depthReferenceZ);

    private static void Build(LumenRibbonBackend backend, Matrix4x4? view = null, int map = -1)
        => backend.Build(new LumenCaptureContext
        {
            CurrentMapIndex = map, FrameIndex = 1, DeltaTime = 0f, ViewportWidth = 100, ViewportHeight = 100,
            ViewMatrix = view ?? Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        });

    // Each centre draws as a (minus, plus) vertex pair placed symmetrically about it.
    private static Vector3[] Centres(LumenRibbonBackend backend)
    {
        if (backend.Vertices.IsEmpty) Build(backend);
        var centres = new Vector3[backend.Vertices.Length / 2];
        for (int i = 0; i < centres.Length; i++)
            centres[i] = (backend.Vertices[2 * i].Position + backend.Vertices[2 * i + 1].Position) * 0.5f;
        return centres;
    }

    private static float Width(LumenRibbonBackend backend, int centre)
        => Vector3.Distance(backend.Vertices[2 * centre].Position, backend.Vertices[2 * centre + 1].Position);

    private static void XS(LumenRibbonBackend backend, params float[] expected)
    {
        Build(backend);
        Vector3[] centres = Centres(backend);
        Equal(expected.Length, centres.Length);
        for (int i = 0; i < expected.Length; i++) Close(expected[i], centres[i].X);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
        => Check(Equals(expected, actual), $"Expected {expected}, got {actual}.");

    private static void Close(float expected, float actual)
        => Check(MathF.Abs(expected - actual) < 1e-4f, $"Expected {expected}, got {actual}.");

    private static void Vector(Vector2 expected, Vector2 actual)
        => Check(Vector2.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}.");

    private static void Vector(Vector3 expected, Vector3 actual)
        => Check(Vector3.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}.");

    private static void Vector(Vector4 expected, Vector4 actual)
        => Check(Vector4.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}.");
}
