using System;
using System.Collections.Generic;
using System.Numerics;
using Verse;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;

namespace RimLumen.Core.Lighting.Casters;

// Host side of one map's object-shadow casters (design object-shadows (d)): each caster lives in the chunk of
// ChunkSize x ChunkSize cells holding its occupied rect's minimum cell, and every chunk bakes its casters into one static
// mesh (CasterMesh), rebuilt on the first capture after one of them changed. Lights do not own geometry: every light
// draws the chunks whose silhouettes reach its pool box.
internal sealed class CasterChunks : IDisposable
{
    internal const int ChunkSize = 8;
    private static readonly VertexAttribute[] VertexLayout =
    {
        new(VertexAttributeSemantic.Position, VertexAttributeFormat.Float32, 3, 0, 0),
        new(VertexAttributeSemantic.TexCoord0, VertexAttributeFormat.Float32, 4, 0, 12),
        new(VertexAttributeSemantic.TexCoord1, VertexAttributeFormat.Float32, 4, 0, 28),
        new(VertexAttributeSemantic.TexCoord2, VertexAttributeFormat.Float32, 4, 0, 44)
    };

    internal sealed class Chunk(int index)
    {
        internal readonly Dictionary<Thing, Caster> Members = new();
        internal readonly Mesh Mesh = new() { DebugName = $"RimLumen.ObjectShadows.{index}" };
        internal bool Dirty;
        // The xz box of every member loop and the members' greatest height; valid while Members is non-empty.
        internal float MinX, MinZ, MaxX, MaxZ, MaxHeight;

        internal bool Overlaps(float minX, float minZ, float maxX, float maxZ) =>
            MinX <= maxX && MaxX >= minX && MinZ <= maxZ && MaxZ >= minZ;
    }

    private readonly int chunksX;
    private readonly Chunk?[] chunks;
    private readonly Dictionary<Thing, Chunk> home = new();
    private readonly List<Chunk> dirty = new();
    private readonly List<CasterVertex> vertices = new();
    private readonly List<uint> indices = new();
    private readonly List<Thing> gone = new();
    // Chunks with members, in no particular order; Rebuild keeps it current.
    internal readonly List<Chunk> Filled = new();

    // Requires: map size in cells.
    internal CasterChunks(int width, int height)
    {
        chunksX = (width + ChunkSize - 1) / ChunkSize;
        chunks = new Chunk?[chunksX * ((height + ChunkSize - 1) / ChunkSize)];
    }

    // Ensures: thing casts caster, replacing what it cast before.
    internal void Set(Thing thing, Caster caster)
    {
        Remove(thing);
        int index = caster.Rect.MinZ / ChunkSize * chunksX + caster.Rect.MinX / ChunkSize;
        Chunk chunk = chunks[index] ??= new Chunk(index);
        chunk.Members.Add(thing, caster);
        home.Add(thing, chunk);
        MarkDirty(chunk);
    }

    // Ensures: thing casts nothing.
    internal void Remove(Thing thing)
    {
        if (!home.Remove(thing, out Chunk? chunk)) return;
        chunk.Members.Remove(thing);
        MarkDirty(chunk);
    }

    // Ensures: every caster whose thing is dead is removed.
    internal void RemoveWhere(Func<Thing, bool> dead)
    {
        gone.Clear();
        foreach (Thing thing in home.Keys)
            if (dead(thing)) gone.Add(thing);
        foreach (Thing thing in gone) Remove(thing);
    }

    private void MarkDirty(Chunk chunk)
    {
        if (chunk.Dirty) return;
        chunk.Dirty = true;
        dirty.Add(chunk);
    }

    // Requires: main thread, before this frame's MeshDrawSubmission snapshot (the engine drains the upload there).
    // Ensures: every changed chunk with members holds its casters' mesh and bounds; Filled lists exactly the chunks with
    //          members.
    internal void Rebuild(float altitude)
    {
        if (dirty.Count == 0) return;
        foreach (Chunk chunk in dirty)
        {
            if (!chunk.Dirty) continue;
            chunk.Dirty = false;
            Filled.Remove(chunk);
            if (chunk.Members.Count == 0) continue;
            Filled.Add(chunk);
            vertices.Clear();
            indices.Clear();
            chunk.MinX = chunk.MinZ = float.PositiveInfinity;
            chunk.MaxX = chunk.MaxZ = float.NegativeInfinity;
            chunk.MaxHeight = 0f;
            foreach (Caster caster in chunk.Members.Values)
            {
                CasterMesh.Append(caster, altitude, vertices, indices);
                chunk.MaxHeight = MathF.Max(chunk.MaxHeight, caster.Height);
                foreach (Vector2[] loop in caster.Loops)
                    foreach (Vector2 p in loop)
                    {
                        chunk.MinX = MathF.Min(chunk.MinX, p.X); chunk.MaxX = MathF.Max(chunk.MaxX, p.X);
                        chunk.MinZ = MathF.Min(chunk.MinZ, p.Y); chunk.MaxZ = MathF.Max(chunk.MaxZ, p.Y);
                    }
            }
            MeshDataArray data = Mesh.AllocateWritableMeshData(1);
            bool applied = false;
            try
            {
                MeshData writable = data[0];
                writable.SetVertexBufferParams(vertices.Count, VertexLayout);
                writable.SetIndexBufferParams(indices.Count, MeshIndexFormat.UInt32);
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices).CopyTo(writable.GetVertexData<CasterVertex>().AsSpan());
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices).CopyTo(writable.GetIndexData<uint>().AsSpan());
                writable.subMeshCount = 1;
                writable.SetSubMesh(0, new SubMesh(0, indices.Count));
                Mesh.ApplyAndDisposeWritableMeshData(data, chunk.Mesh);
                applied = true;
            }
            finally
            {
                if (!applied) data.Dispose();
            }
        }
        dirty.Clear();
    }

    // Requires: main thread; the map is gone (OcclusionField.Forget).
    public void Dispose()
    {
        foreach (Chunk? chunk in chunks) chunk?.Mesh.Dispose();
    }
}
