using System;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Ribbons;

internal interface IRibbonStripSampler
{
    void Evaluate(float t, out float width, out Vector4 color);
}

internal readonly struct NodeRibbonSampler(RibbonSpec spec) : IRibbonStripSampler
{
    public void Evaluate(float t, out float width, out Vector4 color)
    {
        width = spec.Width * CurveEvaluator.Evaluate(spec.WidthOverLength, t);
        color = GradientEvaluator.Evaluate(spec.ColorOverLength, t);
    }
}

internal readonly struct ParticleTrailSampler(GpuCurveSample[] curves, float baseWidth, Vector4 baseColor)
    : IRibbonStripSampler
{
    public void Evaluate(float t, out float width, out Vector4 color)
    {
        GpuCurveSample sample = RibbonStripGeometry.SampleLut(curves.AsSpan(0, GpuCurveAtlasPacker.SamplesPerRow), t);
        width = baseWidth * sample.Size.X;
        color = baseColor * sample.Color;
    }
}

// Single authoritative CPU ribbon strip geometry builder shared by node ribbons (LumenRibbonBackend) and
// L1 particle trails (CpuParticleSimulator). L2 GPU shader ParticleTrail.vert.glsl mirrors these exact methods.
internal static class RibbonStripGeometry
{
    internal static GpuCurveSample SampleLut(ReadOnlySpan<GpuCurveSample> row, float t)
    {
        float scaled = Math.Clamp(t, 0f, 1f) * (GpuCurveAtlasPacker.SamplesPerRow - 1);
        int lower = (int)scaled;
        int upper = Math.Min(lower + 1, GpuCurveAtlasPacker.SamplesPerRow - 1);
        float blend = scaled - lower;
        GpuCurveSample a = row[lower], b = row[upper];
        return new GpuCurveSample
        {
            Color = Vector4.Lerp(a.Color, b.Color, blend),
            Size = new Vector4(float.Lerp(a.Size.X, b.Size.X, blend), 0f, 0f, 0f),
        };
    }

    internal static void PushDeduplicated(Vector3[] path, ref int count, Vector3 position)
    {
        if (count > 0 && Vector3.DistanceSquared(path[count - 1], position) <= 1e-12f) return;
        path[count++] = position;
    }

    // Tail point (oldestRemaining < 0) is clipped along the segment to the next living point (nextRemaining > 0).
    internal static Vector3 ClipTailPoint(Vector3 oldestPos, float oldestRemaining, Vector3 nextPos, float nextRemaining)
    {
        float denominator = MathF.Max(1e-6f, nextRemaining - oldestRemaining);
        float fraction = Math.Clamp(-oldestRemaining / denominator, 0f, 1f);
        return Vector3.Lerp(oldestPos, nextPos, fraction);
    }

    // Projects every trail/beam centre to display space. When sharedAnchorXZ is provided (particle trails),
    // every sample shares the owning particle's anchorXZ so Vertical3D never splits head and tail along screen slope;
    // when null (node ribbons), each centre anchors at its own world XZ.
    internal static void ProjectCentres(ReadOnlySpan<Vector3> path, Span<Vector3> drawn, bool vertical3D,
        bool isTopDown2D, float yScaleRatio, Vector3 groundUp, float vanillaMoteY, Vector2? sharedAnchorXZ)
    {
        for (int i = 0; i < path.Length; i++)
        {
            drawn[i] = path[i];
            Vector3 unusedVelocity = default;
            Vector2 anchor = sharedAnchorXZ ?? new Vector2(path[i].X, path[i].Z);
            CameraBillboardBasis.ProjectToDisplay(vertical3D, isTopDown2D, yScaleRatio, groundUp, anchor,
                vanillaMoteY, ref drawn[i], ref unusedVelocity);
        }
    }

    // Maps a world-space centre delta to the ribbon's facing plane (camera right/up for View, world X/Z for Ground),
    // un-squashing IsoRim's Y compression so the strip keeps visual screen/plane width.
    internal static Vector2 ProjectDirectionToFacingPlane(Vector3 direction, Vector3 scaledX, Vector3 scaledY, float yScale)
    {
        var scaled = new Vector3(direction.X, direction.Y * yScale, direction.Z);
        return new Vector2(Vector3.Dot(scaled, scaledX), Vector3.Dot(scaled, scaledY));
    }

    // Mirrors RimUnity TrailRenderer.Geometry.cs:53-57,69: falls back to facing +X when parallel to camera view,
    // computes miter bisecting before/after segment normals, and caps sharp-bend extension at 4x half-width.
    internal static Vector2 ComputeMiterOffset(Vector2 before, Vector2 after, float halfWidth)
    {
        Vector2 sideBefore = before.LengthSquared() < 1e-10f
            ? Vector2.UnitX
            : Vector2.Normalize(new Vector2(before.Y, -before.X));
        Vector2 sideAfter = after.LengthSquared() < 1e-10f
            ? sideBefore
            : Vector2.Normalize(new Vector2(after.Y, -after.X));
        Vector2 miter = sideBefore + sideAfter;
        miter = miter.LengthSquared() < 1e-10f ? sideAfter : Vector2.Normalize(miter);
        return miter * (halfWidth / MathF.Max(0.25f, MathF.Abs(Vector2.Dot(miter, sideAfter))));
    }

    internal static void EnsureStripCapacity(int vertexTotal, int indexTotal,
        ref ParticleVertex[] vertices, ref uint[] indices)
    {
        if (vertices.Length < vertexTotal) Array.Resize(ref vertices, Math.Max(vertexTotal, 2 * vertices.Length));
        if (indices.Length < indexTotal) Array.Resize(ref indices, Math.Max(indexTotal, 2 * indices.Length));
    }

    // Appends one triangle-pair strip over path[0..count)/drawn[0..count) (head at index 0, tail at count - 1)
    // and returns the starting index offset of the newly written 6*(count - 1) indices.
    internal static int AppendStrip<TSampler>(ReadOnlySpan<Vector3> path, ReadOnlySpan<Vector3> drawn, int count,
        Vector3 axisX, Vector3 axisY, float yScale, Vector2 depthMask, LumenRibbonTextureMode textureMode,
        float gain, in TSampler sampler, ref ParticleVertex[] vertices, ref int vertexCount,
        ref uint[] indices, ref int indexCount) where TSampler : struct, IRibbonStripSampler
    {
        float total = 0f;
        for (int i = 1; i < count; i++) total += Vector3.Distance(path[i - 1], path[i]);
        EnsureStripCapacity(vertexCount + 2 * count, indexCount + 6 * (count - 1), ref vertices, ref indices);
        int first = vertexCount, indexStart = indexCount;
        float distance = 0f;
        var scaledX = new Vector3(axisX.X, axisX.Y * yScale, axisX.Z);
        var scaledY = new Vector3(axisY.X, axisY.Y * yScale, axisY.Z);
        for (int i = 0; i < count; i++)
        {
            Vector2 before = ProjectDirectionToFacingPlane(i > 0 ? drawn[i] - drawn[i - 1] : drawn[1] - drawn[0], scaledX, scaledY, yScale);
            Vector2 after = i + 1 < count ? ProjectDirectionToFacingPlane(drawn[i + 1] - drawn[i], scaledX, scaledY, yScale) : before;
            if (i > 0) distance += Vector3.Distance(path[i - 1], path[i]);
            float t = total > 0f ? distance / total : 0f;
            sampler.Evaluate(t, out float width, out Vector4 color);
            Vector2 miter = ComputeMiterOffset(before, after, MathF.Max(0f, width) * 0.5f);
            Vector3 offset = axisX * miter.X + axisY * miter.Y;
            float u = textureMode == LumenRibbonTextureMode.Stretch ? t : distance;
            vertices[vertexCount++] = new ParticleVertex(drawn[i] - offset, new Vector2(u, 0f), color, new Vector2(0.5f, 0f), gain, depthMask);
            vertices[vertexCount++] = new ParticleVertex(drawn[i] + offset, new Vector2(u, 1f), color, new Vector2(0.5f, 1f), gain, depthMask);
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
        return indexStart;
    }
}
