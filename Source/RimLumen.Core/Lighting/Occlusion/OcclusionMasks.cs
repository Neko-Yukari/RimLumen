using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;

namespace RimLumen.Core.Lighting.Occlusion;

// Host boundary: sprite alpha masks (design 1.3). Resolves a thing's material and UV sub-rectangle, decodes the
// texture's mip 0 once through Texture2D.TryGetCpuMip0 and caches the box-filtered mask by (texture, UV rect, flip).
internal static class OcclusionMasks
{
    private readonly record struct Key(Waylith.Rendering.Texture2D Texture, Vector2 Offset, Vector2 Scale, bool Flip, int Width, int Height);
    private static readonly Dictionary<Key, AlphaMask> Masks = new();
    private static Func<Graphic_Linked, Thing, IntVec3, Material> linkedMaterial = null!;

    internal static void Arm()
    {
        MethodInfo method = typeof(Graphic_Linked).GetMethod("LinkedDrawMatFrom", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(Thing), typeof(IntVec3)], null) ?? throw new MissingMethodException(typeof(Graphic_Linked).FullName, "LinkedDrawMatFrom");
        if (method.ReturnType != typeof(Material) || !method.IsVirtual) throw new InvalidOperationException("Graphic_Linked.LinkedDrawMatFrom signature changed.");
        linkedMaterial = method.CreateDelegate<Func<Graphic_Linked, Thing, IntVec3, Material>>();
    }

    // Requires: main thread; thing spawned on a map.
    // Ensures: the mask of the material the thing draws with right now (Graphic_Linked: LinkedDrawMatFrom for its
    //          cell; flips per Graphic.EastFlipped/WestFlipped), resampled to 4 texels per drawn cell, and its world
    //          placement (DrawPos.xz, rotated drawSize). Returns false when the texture has no readable CPU pixels;
    //          the caller then applies the Q3 rule (blockLight: solid def.size footprint, one warning per def; cover:
    //          skipped).
    internal static bool TryGet(Thing thing, out AlphaMask mask, out MaskPlacement placement)
    {
        Graphic graphic = thing.Graphic;
        Rot4 rotation = thing.Rotation;
        Material material = graphic is Graphic_Linked linked ? linkedMaterial(linked, thing, thing.Position) : graphic.MatAt(rotation, thing);
        Vector2 size = thing is RimWorld.Building_Door ? new Vector2(thing.def.size.x, thing.def.size.z) : graphic.drawSize;
        if (thing is not RimWorld.Building_Door && rotation.IsHorizontal && !graphic.ShouldDrawRotated) size = new Vector2(size.y, size.x);
        Vector3 center = thing.DrawPos;
        placement = new MaskPlacement(center.x, center.z, size.x, size.y);
        var texture = ((UnityEngine.Texture2D)material.mainTexture).CoreTexture;
        bool flip = (rotation == Rot4.West && graphic.WestFlipped || rotation == Rot4.East && graphic.EastFlipped) && !graphic.ShouldDrawRotated;
        int width = Math.Max(1, (int)MathF.Round(size.x * 4)), height = Math.Max(1, (int)MathF.Round(size.y * 4));
        var key = new Key(texture, material.mainTextureOffset, material.mainTextureScale, flip, width, height);
        if (Masks.TryGetValue(key, out mask!))
        {
            if (thing is RimWorld.Building_Door cachedDoor) TransformDoor(cachedDoor, ref mask, ref placement);
            else if (graphic.ShouldDrawRotated) TransformRotated(graphic, rotation, ref mask, ref placement);
            return true;
        }
        byte[]? pixels = texture.TryGetCpuMip0();
        if (pixels is null) { mask = null!; return false; }
        byte[] alpha = new byte[width * height];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                float u0 = flip ? 1f - (x + 1f) / width : (float)x / width;
                float u1 = flip ? 1f - (float)x / width : (x + 1f) / width;
                float x0 = (key.Offset.x + u0 * key.Scale.x) * texture.width;
                float x1 = (key.Offset.x + u1 * key.Scale.x) * texture.width;
                float y0 = (key.Offset.y + (float)z / height * key.Scale.y) * texture.height;
                float y1 = (key.Offset.y + (z + 1f) / height * key.Scale.y) * texture.height;
                float sum = 0f, area = 0f;
                for (int py = (int)MathF.Floor(y0); py < (int)MathF.Ceiling(y1); py++)
                    for (int px = (int)MathF.Floor(x0); px < (int)MathF.Ceiling(x1); px++)
                    {
                        float weight = (MathF.Min(x1, px + 1) - MathF.Max(x0, px)) * (MathF.Min(y1, py + 1) - MathF.Max(y0, py));
                        sum += pixels[(py * texture.width + px) * 4 + 3] * weight;
                        area += weight;
                    }
                alpha[z * width + x] = (byte)MathF.Round(sum / area);
            }
        mask = new AlphaMask(width, height, alpha);
        Masks.Add(key, mask);
        if (thing is RimWorld.Building_Door door) TransformDoor(door, ref mask, ref placement);
        else if (graphic.ShouldDrawRotated) TransformRotated(graphic, rotation, ref mask, ref placement);
        return true;
    }

    private static void TransformRotated(Graphic graphic, Rot4 rotation, ref AlphaMask mask, ref MaskPlacement placement)
    {
        float degrees = rotation.AsAngle + graphic.DrawRotatedExtraAngleOffset;
        if (rotation == Rot4.West && graphic.WestFlipped || rotation == Rot4.East && graphic.EastFlipped) degrees += 180f;
        float radians = degrees * (MathF.PI / 180f);
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        float sizeX = MathF.Abs(placement.SizeX * cos) + MathF.Abs(placement.SizeZ * sin);
        float sizeZ = MathF.Abs(placement.SizeX * sin) + MathF.Abs(placement.SizeZ * cos);
        int width = Math.Max(1, (int)MathF.Round(sizeX * 4f)), height = Math.Max(1, (int)MathF.Round(sizeZ * 4f));
        byte[] alpha = new byte[width * height];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                float wx = ((x + 0.5f) / width - 0.5f) * sizeX, wz = ((z + 0.5f) / height - 0.5f) * sizeZ;
                float u = (wx * cos - wz * sin) / placement.SizeX + 0.5f;
                float v = (wx * sin + wz * cos) / placement.SizeZ + 0.5f;
                if (u >= 0f && u < 1f && v >= 0f && v < 1f)
                    alpha[z * width + x] = mask.Alpha[(int)(v * mask.Height) * mask.Width + (int)(u * mask.Width)];
            }
        mask = new AlphaMask(width, height, alpha);
        placement = placement with { SizeX = sizeX, SizeZ = sizeZ };
    }

    private static void TransformDoor(RimWorld.Building_Door door, ref AlphaMask mask, ref MaskPlacement placement)
    {
        bool horizontal = door.Rotation.IsHorizontal;
        int width = horizontal ? mask.Height : mask.Width;
        int height = horizontal ? mask.Width : mask.Height;
        byte[] alpha = new byte[width * height];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width, v = (z + 0.5f) / height;
                (float lu, float lv) = door.Rotation.AsInt switch
                {
                    0 => (u, v), 1 => (1f - v, u), 2 => (1f - u, 1f - v), _ => (v, 1f - u)
                };
                int row = (int)(lv * mask.Height);
                int col = (int)(lu * mask.Width);
                int opposite = mask.Width - 1 - col;
                if (door is RimWorld.Building_MultiTileDoor)
                {
                    col = lu < 0.5f ? (int)(lu * 2f * mask.Width) : (int)((1f - lu) * 2f * mask.Width);
                    alpha[z * width + x] = mask.Alpha[row * mask.Width + col];
                }
                else alpha[z * width + x] = Math.Max(mask.Alpha[row * mask.Width + col], mask.Alpha[row * mask.Width + opposite]);
            }
        mask = new AlphaMask(width, height, alpha);
        if (horizontal) placement = placement with { SizeX = placement.SizeZ, SizeZ = placement.SizeX };
    }
}
