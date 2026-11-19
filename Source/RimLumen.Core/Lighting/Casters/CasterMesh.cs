using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RimLumen.Core.Lighting.Casters;

// One corner of a silhouette edge's quad (RimLumenObjectShadow.vert.glsl). Position: the corner's edge end at the Shadows
// altitude; Edge: the edge (a.x, a.z, b.x, b.z); Shape: (height, fade, end: 0 a / 1 b, far: 0 on the edge / 1 pushed
// away from the light); Rect: the caster's occupied cells [minX, maxX + 1) x [minZ, maxZ + 1).
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CasterVertex(Vector3 Position, Vector4 Edge, Vector4 Shape, Vector4 Rect);

internal static class CasterMesh
{
    // Ensures: appends one quad per edge of every loop of caster (4 vertices, 2 triangles), indexed from vertices.Count.
    internal static void Append(Caster caster, float altitude, List<CasterVertex> vertices, List<uint> indices)
    {
        var rect = new Vector4(caster.Rect.MinX, caster.Rect.MinZ, caster.Rect.MaxX + 1, caster.Rect.MaxZ + 1);
        float fade = caster.Fade ? 1f : 0f;
        foreach (Vector2[] loop in caster.Loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                Vector2 a = loop[j], b = loop[i];
                var edge = new Vector4(a.X, a.Y, b.X, b.Y);
                uint first = (uint)vertices.Count;
                vertices.Add(new CasterVertex(new Vector3(a.X, altitude, a.Y), edge, new Vector4(caster.Height, fade, 0f, 0f), rect));
                vertices.Add(new CasterVertex(new Vector3(b.X, altitude, b.Y), edge, new Vector4(caster.Height, fade, 1f, 0f), rect));
                vertices.Add(new CasterVertex(new Vector3(b.X, altitude, b.Y), edge, new Vector4(caster.Height, fade, 1f, 1f), rect));
                vertices.Add(new CasterVertex(new Vector3(a.X, altitude, a.Y), edge, new Vector4(caster.Height, fade, 0f, 1f), rect));
                indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
                indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
            }
    }
}
