using System;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// Computes far-to-near transparent depth ranks for adapter draws matching RimWorldWaylith TransparentDepthOrder,
// so overlapping CoverageAlpha draws inside the pass preserve the host's exact back-to-front draw order.
internal sealed class DrawAdapterPass(byte passId, ILumenDrawAdapter adapter)
{
    private ushort[] ranks = Array.Empty<ushort>();
    private float[] sortKeys = Array.Empty<float>();
    private int[] sortIndices = Array.Empty<int>();

    internal ILumenDrawAdapter Adapter { get; } = adapter;
    private bool ranked;
    internal ushort Order(int index) => ranked ? ranks[index] : (ushort)0;

    internal void Prepare(MeshDrawCommand[] commands, int count, in System.Numerics.Matrix4x4 view) =>
        ranked = ComputeForPass(commands, count, passId, in view);

    private bool ComputeForPass(MeshDrawCommand[] commands, int count, byte targetPassId, in System.Numerics.Matrix4x4 view)
    {
        int matched = 0;
        for (int i = 0; i < count; i++)
            if (commands[i].PassId == targetPassId) matched++;
        if (matched <= 1) return false;

        if (ranks.Length < count)
        {
            int size = Math.Max(count, Math.Max(64, ranks.Length * 2));
            ranks = new ushort[size];
            sortKeys = new float[size];
            sortIndices = new int[size];
        }

        int sortedCount = 0;
        for (int i = 0; i < count; i++)
        {
            ranks[i] = 0;
            ref MeshDrawCommand command = ref commands[i];
            if (command.PassId != targetPassId) continue;

            System.Numerics.Vector3 worldPos;
            if (command.Instancing?.SortCentre is { } centre)
            {
                worldPos = centre;
            }
            else
            {
                Waylith.Graphics.Meshes.MeshBounds bounds = SortBounds(in command, out bool sectionSubmesh);
                float y = sectionSubmesh ? bounds.MinY : (bounds.MinY + bounds.MaxY) * 0.5f;
                worldPos = TransformPoint(command.Matrix,
                    new System.Numerics.Vector3((bounds.MinX + bounds.MaxX) * 0.5f, y, (bounds.MinZ + bounds.MaxZ) * 0.5f));
            }

            float depth = MathF.Abs(TransformPoint(in view, worldPos).Z);
            if (float.IsFinite(depth))
            {
                depth = MathF.Round(depth * 4096f) / 4096f;
                sortKeys[sortedCount] = -depth;
                sortIndices[sortedCount] = i;
                sortedCount++;
            }
        }

        if (sortedCount <= 1) return false;
        Array.Sort(sortKeys, sortIndices, 0, sortedCount);
        int distinct = 1;
        for (int j = 1; j < sortedCount; j++)
            if (sortKeys[j] != sortKeys[j - 1]) distinct++;
        float scale = distinct > 65535 ? 65535f / (distinct - 1) : 1f;
        int rank = 0;
        for (int k = 0; k < sortedCount; k++)
        {
            if (k > 0 && sortKeys[k] != sortKeys[k - 1]) rank++;
            ranks[sortIndices[k]] = (ushort)(rank * scale);
        }
        return true;
    }

    private static Waylith.Graphics.Meshes.MeshBounds SortBounds(in MeshDrawCommand command, out bool sectionSubmesh)
    {
        sectionSubmesh = false;
        Waylith.Graphics.Meshes.SubMesh[] subMeshes = command.Mesh.CpuStorage.SubMeshes;
        if ((uint)command.SubmeshIndex < (uint)subMeshes.Length)
        {
            Waylith.Graphics.Meshes.MeshBounds sub = subMeshes[command.SubmeshIndex].Bounds;
            if (!sub.IsEmpty && (sub.MinX != 0f || sub.MinY != 0f || sub.MinZ != 0f || sub.MaxX != 0f || sub.MaxY != 0f || sub.MaxZ != 0f))
            {
                Waylith.Graphics.Meshes.MeshBounds meshBounds = command.Mesh.Bounds;
                sectionSubmesh = meshBounds.MinY != sub.MinY || meshBounds.MaxY != sub.MaxY;
                return sub;
            }
        }
        return command.Mesh.Bounds;
    }

    private static System.Numerics.Vector3 TransformPoint(in System.Numerics.Matrix4x4 m, System.Numerics.Vector3 p) =>
        new(m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
            m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
            m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);
}
