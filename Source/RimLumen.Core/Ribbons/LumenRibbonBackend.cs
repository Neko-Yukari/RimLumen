using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Ribbons;

// Native trail and beam ribbons (EffectNodeSpec.Ribbon). Semantics port RimUnity's TrailRenderer and LineRenderer,
// https://github.com/chezhou9413/RimUnity at 45ca90987f7d3721715b45c021cab986ee14c468 (MIT):
//   trail points:   Source/RimUnity/Rendering/TrailRenderer.cs:48-80 (emission, age-out, visible head and tail)
//   strip geometry: Source/RimUnity/Rendering/TrailRenderer.Geometry.cs:38-86 (miter, width, colour, texture U)
//   line input:     Source/RimUnity/Rendering/TrailRenderer.LineGeometry.cs:14-19 (node-local points, dedupe)
// UpdateInstance advances simulation; Build turns every instance on the current map into one strip sub-batch of
// quads for the L1 particle quad shader (Particles/L1ParticleQuad.*): uv = (U, across), radialUv = (0.5, across),
// so an untextured ribbon gets the particle disc falloff across its width.
internal sealed class LumenRibbonBackend : ILumenNodeFeatureBackend
{
    private readonly Dictionary<int, Ribbon> ribbons = new();
    private readonly List<L1SubBatch> subBatches = new();
    private ParticleVertex[] vertices = Array.Empty<ParticleVertex>();
    private uint[] indices = Array.Empty<uint>();
    // Per-ribbon scratch, newest (head, length 0) first: world centre, drawn centre and XZ ground anchor.
    private Vector3[] path = Array.Empty<Vector3>(), drawn = Array.Empty<Vector3>();
    private Vector2[] anchors = Array.Empty<Vector2>();
    private int vertexCount, indexCount, nextId;

    public LumenNodeFeature Feature => LumenNodeFeature.Ribbon;

    internal bool HasInstances => ribbons.Count != 0;

    // Ensures: the strips of the last Build: two vertices per centre, one sub-batch per drawn ribbon.
    internal ReadOnlySpan<ParticleVertex> Vertices => vertices.AsSpan(0, vertexCount);
    internal ReadOnlySpan<uint> Indices => indices.AsSpan(0, indexCount);
    internal IReadOnlyList<L1SubBatch> SubBatches => subBatches;

    // Ids are never reused, so a handle that has not yet observed a drained instance cannot reach a new one.
    public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed)
    {
        int id = nextId++;
        ribbons.Add(id, new Ribbon(node.Ribbon!, node.SpacePlane == LumenSpacePlane.Vertical3D, seed, initial));
        return id;
    }

    public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame)
    {
        Ribbon ribbon = ribbons[instanceId];
        ribbon.Update(frame);
        if (ribbon.Drained) ribbons.Remove(instanceId);
    }

    public void RestartInstance(int instanceId) => ribbons[instanceId].Restart();

    // A beam has nothing to fade; a trail keeps ageing out its points through UpdateInstance (Active = false).
    public void DestroyInstance(int instanceId, bool immediate)
    {
        if (immediate || ribbons[instanceId].BeginDrain()) ribbons.Remove(instanceId);
    }

    public bool IsInstanceAlive(int instanceId) => ribbons.ContainsKey(instanceId);

    // Requires: main thread, after this frame's UpdateInstance calls.
    // Ensures: rebuilds Vertices/Indices/SubBatches for the ribbons on context.CurrentMapIndex (-1: every map).
    //   View ribbons face the camera; Vertical3D ribbons draw through CameraBillboardBasis.ProjectVertical3D around
    //   each centre's XZ ground anchor, as particles do. Arrays grow only; steady state allocates nothing.
    internal void Build(in LumenCaptureContext context)
    {
        vertexCount = indexCount = 0;
        subBatches.Clear();
        CameraBillboardBasis.Extract(context.ViewMatrix, out Vector3 cameraRight, out Vector3 cameraUp, out float yScaleRatio);
        bool isTopDown2D = CameraBillboardBasis.IsStraightTopDown2D(cameraUp, yScaleRatio);
        Vector3 groundUp = CameraBillboardBasis.GroundUp(cameraUp);
        float vanillaMoteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        foreach (Ribbon ribbon in ribbons.Values)
        {
            if (context.CurrentMapIndex >= 0 && ribbon.MapIndex >= 0 && ribbon.MapIndex != context.CurrentMapIndex) continue;
            EnsureScratch(ribbon.MaxCentres);
            int count = ribbon.Collect(path, anchors);
            if (count < 2) continue;
            for (int i = 0; i < count; i++)
            {
                drawn[i] = path[i];
                if (!ribbon.Vertical3D) continue;
                Vector3 unusedVelocity = default;
                CameraBillboardBasis.ProjectVertical3D(isTopDown2D, yScaleRatio, groundUp, anchors[i],
                    vanillaMoteY, ref drawn[i], ref unusedVelocity);
            }
            bool ground = ribbon.Spec.Alignment == LumenRibbonAlignment.Ground;
            AppendStrip(ribbon, count, ground ? Vector3.UnitX : cameraRight, ground ? Vector3.UnitZ : cameraUp,
                ground ? 1f : yScaleRatio);
        }
    }

    // Strip of `count` centres in path/drawn. axisX/axisY span the facing plane (camera right/up for View, world X/Z
    // for Ground); a world direction maps to that plane as in CameraBillboardBasis.ComputeStretchedAxes, which also
    // removes IsoRim's Y squash. Side = (dy, -dx) is Unity's Cross(direction, facing) for the RimWorld cameras.
    private void AppendStrip(Ribbon ribbon, int count, Vector3 axisX, Vector3 axisY, float yScale)
    {
        RibbonSpec spec = ribbon.Spec;
        float total = 0f;
        for (int i = 1; i < count; i++) total += Vector3.Distance(path[i - 1], path[i]);
        EnsureOutput(vertexCount + 2 * count, indexCount + 6 * (count - 1));
        int first = vertexCount, indexStart = indexCount;
        float gain = spec.HdrIntensity * ribbon.Intensity, distance = 0f;
        var scaledX = new Vector3(axisX.X, axisX.Y * yScale, axisX.Z);
        var scaledY = new Vector3(axisY.X, axisY.Y * yScale, axisY.Z);
        Vector2 Plane(Vector3 direction)
        {
            var scaled = new Vector3(direction.X, direction.Y * yScale, direction.Z);
            return new Vector2(Vector3.Dot(scaled, scaledX), Vector3.Dot(scaled, scaledY));
        }
        Vector2 sideBefore = default;
        for (int i = 0; i < count; i++)
        {
            Vector2 before = Plane(i > 0 ? drawn[i] - drawn[i - 1] : drawn[1] - drawn[0]);
            Vector2 after = i + 1 < count ? Plane(drawn[i + 1] - drawn[i]) : before;
            if (i > 0) distance += Vector3.Distance(path[i - 1], path[i]);
            float t = total > 0f ? distance / total : 0f;
            // Geometry.cs:53-57: a direction along the facing axis falls back to the camera right.
            sideBefore = before.LengthSquared() < 1e-10f ? Vector2.UnitX : Vector2.Normalize(new Vector2(before.Y, -before.X));
            Vector2 sideAfter = after.LengthSquared() < 1e-10f ? sideBefore : Vector2.Normalize(new Vector2(after.Y, -after.X));
            Vector2 miter = sideBefore + sideAfter;
            miter = miter.LengthSquared() < 1e-10f ? sideAfter : Vector2.Normalize(miter);
            float half = MathF.Max(0f, spec.Width * CurveEvaluator.Evaluate(spec.WidthOverLength, t)) * 0.5f;
            // Geometry.cs:69: the miter keeps the strip width through a bend, capped at 4x for sharp turns.
            miter *= half / MathF.Max(0.25f, MathF.Abs(Vector2.Dot(miter, sideAfter)));
            Vector3 offset = axisX * miter.X + axisY * miter.Y;
            float u = spec.TextureMode == LumenRibbonTextureMode.Stretch ? t : distance;
            Vector4 color = GradientEvaluator.Evaluate(spec.ColorOverLength, t);
            vertices[vertexCount++] = new ParticleVertex(drawn[i] - offset, new Vector2(u, 0f), color, new Vector2(0.5f, 0f), gain);
            vertices[vertexCount++] = new ParticleVertex(drawn[i] + offset, new Vector2(u, 1f), color, new Vector2(0.5f, 1f), gain);
        }
        for (int i = 0; i + 1 < count; i++)
        {
            uint minus = (uint)(first + 2 * i), plus = minus + 1, nextMinus = minus + 2, nextPlus = minus + 3;
            indices[indexCount++] = minus;
            indices[indexCount++] = plus;
            indices[indexCount++] = nextPlus;
            indices[indexCount++] = minus;
            indices[indexCount++] = nextPlus;
            indices[indexCount++] = nextMinus;
        }
        subBatches.Add(new L1SubBatch(spec.TextureKey, spec.BlendMode, indexStart, indexCount - indexStart, spec.SoftParticleFadeDistance));
    }

    private void EnsureScratch(int centres)
    {
        if (path.Length >= centres) return;
        path = new Vector3[centres];
        anchors = new Vector2[centres];
        drawn = new Vector3[centres];
    }

    private void EnsureOutput(int vertexTotal, int indexTotal)
    {
        if (vertices.Length < vertexTotal) Array.Resize(ref vertices, Math.Max(vertexTotal, 2 * vertices.Length));
        if (indices.Length < indexTotal) Array.Resize(ref indices, Math.Max(indexTotal, 2 * indices.Length));
    }

    private struct TrailPoint
    {
        public Vector3 Position;
        public Vector2 AnchorXZ;
        public float Birth;
    }

    private sealed class Ribbon
    {
        internal readonly RibbonSpec Spec;
        internal readonly bool Vertical3D;
        private readonly uint seed;
        // Trail history, oldest at `start`; capacity Spec.MaxPoints.
        private readonly TrailPoint[] points;
        private int start, count;
        // Seconds since the last restart: trail birth clock, or the beam's noise clock (advances while active).
        private float elapsed;
        private LumenNodeFeatureFrame frame;
        private bool draining;

        internal Ribbon(RibbonSpec spec, bool vertical3D, uint seed, in LumenNodeFeatureFrame initial)
        {
            Spec = spec;
            Vertical3D = vertical3D;
            this.seed = seed;
            points = new TrailPoint[spec.MaxPoints];
            frame = initial;
        }

        private Vector2 CurrentAnchorXZ => new(frame.Origin.X, frame.Origin.Z);
        internal int MapIndex => frame.MapIndex;
        internal float Intensity => frame.Intensity;
        private bool IsTrail => Spec.Mode == LumenRibbonMode.Trail;

        // Trail: the stored points plus the head; beam: Segments + 1 vertices.
        internal int MaxCentres => IsTrail ? Spec.MaxPoints + 1 : Spec.Segments + 1;

        internal bool Drained => draining && (!IsTrail || count == 0);

        // Ensures: true when nothing is left to show.
        internal bool BeginDrain()
        {
            draining = true;
            return Drained;
        }

        internal void Restart()
        {
            count = 0;
            elapsed = 0f;
        }

        // TrailRenderer.cs:48-60: emit when the node moved MinVertexDistance, then drop points older than Lifetime,
        // keeping the newest expired point while a later one still lives (it is clipped in Collect).
        internal void Update(in LumenNodeFeatureFrame next)
        {
            frame = next;
            if (!IsTrail)
            {
                if (next.Active) elapsed += next.DeltaTime;
                return;
            }
            elapsed += next.DeltaTime;
            Vector3 head = next.Transform.World.Translation;
            if (next.Active && (count == 0 || Vector3.Distance(At(count - 1).Position, head) >= Spec.MinVertexDistance))
            {
                if (count == points.Length)
                {
                    start = (start + 1) % points.Length;
                    count--;
                }
                points[(start + count++) % points.Length] = new TrailPoint { Position = head, AnchorXZ = CurrentAnchorXZ, Birth = elapsed };
            }
            float cutoff = elapsed - Spec.Lifetime;
            while (count > 1 && At(1).Birth <= cutoff)
            {
                start = (start + 1) % points.Length;
                count--;
            }
            if (count == 1 && At(0).Birth < cutoff) count = 0;
        }

        // Ensures: writes the visible centres head first into path/anchors (deduplicated) and returns their count.
        internal int Collect(Vector3[] path, Vector2[] anchors)
        {
            int n = 0;
            if (IsTrail)
            {
                // TrailRenderer.cs:63-80: the live head leads; the oldest point is clipped to the Lifetime cutoff.
                if (count == 0) return 0;
                if (frame.Active) Push(path, anchors, ref n, frame.Transform.World.Translation, CurrentAnchorXZ);
                float cutoff = elapsed - Spec.Lifetime;
                for (int i = count - 1; i >= 0; i--)
                {
                    TrailPoint point = At(i);
                    if (i == 0 && count > 1 && point.Birth < cutoff)
                    {
                        TrailPoint next = At(1);
                        float fraction = (cutoff - point.Birth) / (next.Birth - point.Birth);
                        point.Position = Vector3.Lerp(point.Position, next.Position, fraction);
                        point.AnchorXZ = Vector2.Lerp(point.AnchorXZ, next.AnchorXZ, fraction);
                    }
                    Push(path, anchors, ref n, point.Position, point.AnchorXZ);
                }
                return n;
            }
            if (!frame.Active) return 0;
            Matrix4x4 world = frame.Transform.World;
            Vector3 head = world.Translation;
            Vector3 end = Spec.EndAtTarget ? frame.TargetWorld!.Value : Vector3.Transform(Spec.EndLocal!.Value, world);
            Vector3 axis = end - head;
            if (axis.LengthSquared() <= 1e-12f) return 0;
            // Jitter lies in the plane perpendicular to the beam; a vertical beam measures it from world X.
            Vector3 across = Vector3.Cross(axis, Vector3.UnitY);
            across = across.LengthSquared() > 1e-12f ? Vector3.Normalize(across) : Vector3.UnitX;
            Vector3 normal = Vector3.Normalize(Vector3.Cross(axis, across));
            uint roll = Spec.NoiseFrequency > 0f ? (uint)(elapsed * Spec.NoiseFrequency) : 0u;
            uint random = unchecked(seed + roll * 0x9E3779B9u);
            if (random == 0) random = 1;
            for (int i = 0; i <= Spec.Segments; i++)
            {
                Vector3 position = Vector3.Lerp(head, end, (float)i / Spec.Segments);
                if (i > 0 && i < Spec.Segments)
                {
                    float angle = ParticleRandom.Next01(ref random) * MathF.Tau;
                    float radius = ParticleRandom.Next01(ref random) * Spec.NoiseAmplitude;
                    position += (across * MathF.Cos(angle) + normal * MathF.Sin(angle)) * radius;
                }
                Push(path, anchors, ref n, position, CurrentAnchorXZ);
            }
            return n;
        }

        private TrailPoint At(int index) => points[(start + index) % points.Length];

        private static void Push(Vector3[] path, Vector2[] anchors, ref int n, Vector3 position, Vector2 anchorXZ)
        {
            if (n > 0 && Vector3.DistanceSquared(path[n - 1], position) <= 1e-12f) return;
            path[n] = position;
            anchors[n++] = anchorXZ;
        }
    }
}
