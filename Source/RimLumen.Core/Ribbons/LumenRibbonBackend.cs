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
    // Per-ribbon scratch, newest (head, length 0) first: world centre and drawn centre.
    private Vector3[] path = Array.Empty<Vector3>(), drawn = Array.Empty<Vector3>();
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
    // Ensures: clears Vertices/Indices/SubBatches when !context.RendersMap; otherwise rebuilds them for the ribbons on
    //   context.CurrentMapIndex (-1: every map).
    //   View ribbons face the camera; every centre draws through CameraBillboardBasis.ProjectToDisplay anchored at its
    //   own XZ, so both ends of a beam land on their cells and only height projects. Arrays grow only; steady state
    //   allocates nothing.
    internal void Build(in LumenCaptureContext context)
    {
        vertexCount = indexCount = 0;
        subBatches.Clear();
        if (!context.RendersMap) return;
        CameraBillboardBasis.Extract(context.ViewMatrix, out Vector3 cameraRight, out Vector3 cameraUp, out float yScaleRatio);
        bool isTopDown2D = CameraBillboardBasis.IsStraightTopDown2D(cameraRight, cameraUp, yScaleRatio);
        Vector3 groundUp = CameraBillboardBasis.GroundUp(cameraUp);
        float vanillaMoteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        foreach (Ribbon ribbon in ribbons.Values)
        {
            if (context.CurrentMapIndex >= 0 && ribbon.MapIndex >= 0 && ribbon.MapIndex != context.CurrentMapIndex) continue;
            EnsureScratch(ribbon.MaxCentres);
            int count = ribbon.Collect(path);
            if (count < 2) continue;
            RibbonStripGeometry.ProjectCentres(path.AsSpan(0, count), drawn.AsSpan(0, count),
                ribbon.Vertical3D, isTopDown2D, yScaleRatio, groundUp, vanillaMoteY, sharedAnchorXZ: null);
            bool ground = ribbon.Spec.Alignment == LumenRibbonAlignment.Ground;
            RibbonSpec spec = ribbon.Spec;
            var sampler = new NodeRibbonSampler(spec);
            int indexStart = RibbonStripGeometry.AppendStrip(path, drawn, count,
                ground ? Vector3.UnitX : cameraRight, ground ? Vector3.UnitZ : cameraUp,
                ground ? 1f : yScaleRatio, CameraBillboardBasis.DepthMaskAttribute(isTopDown2D, ribbon.DepthReferenceZ, vanillaMoteY),
                spec.TextureMode, spec.HdrIntensity * ribbon.Intensity, in sampler,
                ref vertices, ref vertexCount, ref indices, ref indexCount);
            subBatches.Add(new L1SubBatch(spec.TextureKey, spec.BlendMode, indexStart, indexCount - indexStart, spec.SoftParticleFadeDistance));
        }
    }

    private void EnsureScratch(int centres)
    {
        if (path.Length >= centres) return;
        path = new Vector3[centres];
        drawn = new Vector3[centres];
    }

    private struct TrailPoint
    {
        public Vector3 Position;
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

        internal int MapIndex => frame.MapIndex;
        internal float Intensity => frame.Intensity;
        internal float? DepthReferenceZ => frame.DepthReferenceZ;
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
                points[(start + count++) % points.Length] = new TrailPoint { Position = head, Birth = elapsed };
            }
            float cutoff = elapsed - Spec.Lifetime;
            while (count > 1 && At(1).Birth <= cutoff)
            {
                start = (start + 1) % points.Length;
                count--;
            }
            if (count == 1 && At(0).Birth < cutoff) count = 0;
        }

        // Ensures: writes the visible centres head first into path (deduplicated) and returns their count.
        internal int Collect(Vector3[] path)
        {
            int n = 0;
            if (IsTrail)
            {
                // TrailRenderer.cs:63-80: the live head leads; the oldest point is clipped to the Lifetime cutoff.
                if (count == 0) return 0;
                if (frame.Active) RibbonStripGeometry.PushDeduplicated(path, ref n, frame.Transform.World.Translation);
                float cutoff = elapsed - Spec.Lifetime;
                for (int i = count - 1; i >= 0; i--)
                {
                    TrailPoint point = At(i);
                    if (i == 0 && count > 1 && point.Birth < cutoff)
                    {
                        TrailPoint next = At(1);
                        float fraction = (cutoff - point.Birth) / (next.Birth - point.Birth);
                        point.Position = Vector3.Lerp(point.Position, next.Position, fraction);
                    }
                    RibbonStripGeometry.PushDeduplicated(path, ref n, point.Position);
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
                RibbonStripGeometry.PushDeduplicated(path, ref n, position);
            }
            return n;
        }

        private TrailPoint At(int index) => points[(start + index) % points.Length];
    }
}
