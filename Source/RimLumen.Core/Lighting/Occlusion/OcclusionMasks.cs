using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLumen.Core.Lighting.Occlusion;

// Host boundary: sprite alpha masks (design 1.3). Resolves a thing's material and UV sub-rectangle, decodes the
// texture's mip 0 once through Texture2D.TryGetCpuMip0 and caches the box-filtered mask by (texture, UV rect, flip), and
// each rotated or door-transformed mask by (source mask, transform), so a mask's distance field is built once.
internal static class OcclusionMasks
{
    private readonly record struct Key(Waylith.Rendering.Texture2D Texture, Vector2 Offset, Vector2 Scale, bool Flip, int Width, int Height);
    // Transform: the rotation in degrees and the drawn size for drawRotated graphics; for doors the rotation index, plus 4
    // for a multi-tile door, and size 0.
    private readonly record struct TransformKey(AlphaMask Source, float Transform, float SizeX, float SizeZ);
    private static readonly Dictionary<Key, AlphaMask> Masks = new();
    private static readonly Dictionary<TransformKey, AlphaMask> Transformed = new();
    private static Func<Graphic_Linked, Thing, IntVec3, Material> linkedMaterial = null!;

    internal static void Arm()
    {
        MethodInfo method = typeof(Graphic_Linked).GetMethod("LinkedDrawMatFrom", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(Thing), typeof(IntVec3)], null) ?? throw new MissingMethodException(typeof(Graphic_Linked).FullName, "LinkedDrawMatFrom");
        if (method.ReturnType != typeof(Material) || !method.IsVirtual) throw new InvalidOperationException("Graphic_Linked.LinkedDrawMatFrom signature changed.");
        linkedMaterial = method.CreateDelegate<Func<Graphic_Linked, Thing, IntVec3, Material>>();
    }

    // Requires: main thread; thing spawned on a map.
    // Ensures: the pieces the thing is drawn with right now, each the mask of its material (Graphic_Linked:
    //          LinkedDrawMatFrom for its cell; flips per Graphic.EastFlipped/WestFlipped) resampled to 4 texels per drawn
    //          cell and placed as vanilla prints it: Graphic.Print at TrueCenter + DrawOffset with the rotated drawSize
    //          (ACS:151943-151971); a door over its def.size; a plant as Plant.Print (ACS:600415-600519). Returns false
    //          when a texture has no readable CPU pixels; the caller then applies the Q3 rule (solid def.size footprint
    //          for blockLight and height things, one warning per def; cover: skipped).
    internal static bool TryGet(Thing thing, out SpritePiece[] pieces)
    {
        if (thing is Plant plant) return TryGetPlant(plant, out pieces);
        pieces = null!;
        Graphic graphic = thing.Graphic;
        Rot4 rotation = thing.Rotation;
        Material material = graphic is Graphic_Linked linked ? linkedMaterial(linked, thing, thing.Position) : graphic.MatAt(rotation, thing);
        Vector2 size = thing is RimWorld.Building_Door ? new Vector2(thing.def.size.x, thing.def.size.z) : graphic.drawSize;
        if (thing is not RimWorld.Building_Door && rotation.IsHorizontal && !graphic.ShouldDrawRotated) size = new Vector2(size.y, size.x);
        bool flip = (rotation == Rot4.West && graphic.WestFlipped || rotation == Rot4.East && graphic.EastFlipped) && !graphic.ShouldDrawRotated;
        if (!TryMask(material, flip, size, out AlphaMask mask)) return false;
        Vector3 center = thing.TrueCenter() + graphic.DrawOffset(rotation);
        var placement = new MaskPlacement(center.x, center.z, size.x, size.y);
        if (thing is RimWorld.Building_Door door) TransformDoor(door, ref mask, ref placement);
        else if (graphic.ShouldDrawRotated) TransformRotated(graphic, rotation, ref mask, ref placement);
        pieces = [new SpritePiece(mask, placement)];
        return true;
    }

    // Plant.Print: one square mesh of drawSize.x * growth scale per printed position (ceil(growth * maxMeshCount), at
    // least 1), under the plant's position-seeded Rand: maxMeshCount 1 jitters the centre by up to 0.05 and clamps it
    // north when centre.z - scale / 2 is south of the plant's cell; larger counts sit on a sqrt(maxMeshCount) grid
    // with jitter up to 0.3 of a grid step. Each mesh then draws Rand.Bool for flipUv and, for Graphic_Random, a
    // Rand.Range sub-graphic, in that order.
    private static bool TryGetPlant(Plant plant, out SpritePiece[] pieces)
    {
        pieces = null!;
        PlantProperties properties = plant.def.plant;
        Graphic graphic = plant.Graphic;
        float scale = properties.visualSizeRange.LerpThroughRange(plant.Growth);
        float size = plant.def.graphicData.drawSize.x * scale;
        int count = Math.Max(1, (int)MathF.Ceiling(plant.Growth * properties.maxMeshCount));
        int side = properties.maxMeshCount switch { 4 => 2, 9 => 3, 16 => 4, 25 => 5, _ => 1 };
        var printed = new List<(Material Material, bool Flip, Vector3 Center)>(count);
        Rand.PushState();
        try
        {
            Rand.Seed = plant.Position.GetHashCode();
            foreach (int index in PlantPosIndices.GetPositionIndices(plant))
            {
                Vector3 center;
                if (properties.maxMeshCount == 1)
                {
                    center = plant.TrueCenter() + Gen.RandomHorizontalVector(0.05f);
                    if (center.z - scale / 2f < plant.Position.z) center.z = plant.Position.z + scale / 2f;
                }
                else
                {
                    float step = 1f / side;
                    center = plant.Position.ToVector3() + new Vector3((0.5f + index / side) * step, 0f, (0.5f + index % side) * step)
                        + Gen.RandomHorizontalVector(step * 0.3f);
                }
                bool flip = Rand.Bool;
                Material material = graphic.MatSingleFor(plant);
                if (graphic is Graphic_Random random) material = random.SubGraphicAtIndex(Rand.Range(0, random.SubGraphicsCount)).MatSingle;
                printed.Add((material, flip, center));
                if (printed.Count >= count) break;
            }
        }
        finally { Rand.PopState(); }
        var result = new SpritePiece[printed.Count];
        for (int i = 0; i < printed.Count; i++)
        {
            if (!TryMask(printed[i].Material, printed[i].Flip, new Vector2(size, size), out AlphaMask mask)) return false;
            result[i] = new SpritePiece(mask, new MaskPlacement(printed[i].Center.x, printed[i].Center.z, size, size));
        }
        pieces = result;
        return true;
    }

    // Ensures: the box-filtered alpha of material's UV rectangle (flipped horizontally when flip), at 4 texels per cell
    //          of size and MaskSamples fine samples per texel axis, cached by (texture, UV rect, flip, resolution). False
    //          when the texture has no readable CPU pixels.
    private static bool TryMask(Material material, bool flip, Vector2 size, out AlphaMask mask)
    {
        var texture = ((UnityEngine.Texture2D)material.mainTexture).CoreTexture;
        int width = Math.Max(1, (int)MathF.Round(size.x * 4)), height = Math.Max(1, (int)MathF.Round(size.y * 4));
        var key = new Key(texture, material.mainTextureOffset, material.mainTextureScale, flip, width, height);
        if (Masks.TryGetValue(key, out mask!)) return true;
        byte[]? pixels = texture.TryGetCpuMip0();
        if (pixels is null) return false;
        int fineWidth = width * OcclusionLayout.MaskSamples, fineHeight = height * OcclusionLayout.MaskSamples;
        byte[] alpha = new byte[fineWidth * fineHeight];
        for (int z = 0; z < fineHeight; z++)
            for (int x = 0; x < fineWidth; x++)
            {
                float u0 = flip ? 1f - (x + 1f) / fineWidth : (float)x / fineWidth;
                float u1 = flip ? 1f - (float)x / fineWidth : (x + 1f) / fineWidth;
                float x0 = (key.Offset.x + u0 * key.Scale.x) * texture.width;
                float x1 = (key.Offset.x + u1 * key.Scale.x) * texture.width;
                float y0 = (key.Offset.y + (float)z / fineHeight * key.Scale.y) * texture.height;
                float y1 = (key.Offset.y + (z + 1f) / fineHeight * key.Scale.y) * texture.height;
                float sum = 0f, area = 0f;
                for (int py = (int)MathF.Floor(y0); py < (int)MathF.Ceiling(y1); py++)
                    for (int px = (int)MathF.Floor(x0); px < (int)MathF.Ceiling(x1); px++)
                    {
                        float weight = (MathF.Min(x1, px + 1) - MathF.Max(x0, px)) * (MathF.Min(y1, py + 1) - MathF.Max(y0, py));
                        sum += pixels[(py * texture.width + px) * 4 + 3] * weight;
                        area += weight;
                    }
                alpha[z * fineWidth + x] = (byte)MathF.Round(sum / area);
            }
        mask = new AlphaMask(width, height, alpha);
        Masks.Add(key, mask);
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
        var key = new TransformKey(mask, degrees, placement.SizeX, placement.SizeZ);
        placement = placement with { SizeX = sizeX, SizeZ = sizeZ };
        if (Transformed.TryGetValue(key, out AlphaMask? cached)) { mask = cached; return; }
        int width = Math.Max(1, (int)MathF.Round(sizeX * 4f)), height = Math.Max(1, (int)MathF.Round(sizeZ * 4f));
        const int s = OcclusionLayout.MaskSamples;
        int fineWidth = width * s, fineHeight = height * s, sourceWidth = mask.Width * s, sourceHeight = mask.Height * s;
        byte[] alpha = new byte[fineWidth * fineHeight];
        for (int z = 0; z < fineHeight; z++)
            for (int x = 0; x < fineWidth; x++)
            {
                float wx = ((x + 0.5f) / fineWidth - 0.5f) * sizeX, wz = ((z + 0.5f) / fineHeight - 0.5f) * sizeZ;
                float u = (wx * cos - wz * sin) / key.SizeX + 0.5f;
                float v = (wx * sin + wz * cos) / key.SizeZ + 0.5f;
                if (u >= 0f && u < 1f && v >= 0f && v < 1f)
                    alpha[z * fineWidth + x] = mask.Fine[(int)(v * sourceHeight) * sourceWidth + (int)(u * sourceWidth)];
            }
        mask = new AlphaMask(width, height, alpha);
        Transformed.Add(key, mask);
    }

    private static void TransformDoor(RimWorld.Building_Door door, ref AlphaMask mask, ref MaskPlacement placement)
    {
        bool horizontal = door.Rotation.IsHorizontal;
        if (horizontal) placement = placement with { SizeX = placement.SizeZ, SizeZ = placement.SizeX };
        var key = new TransformKey(mask, door.Rotation.AsInt + (door is RimWorld.Building_MultiTileDoor ? 4 : 0), 0f, 0f);
        if (Transformed.TryGetValue(key, out AlphaMask? cached)) { mask = cached; return; }
        const int s = OcclusionLayout.MaskSamples;
        int sourceWidth = mask.Width * s, sourceHeight = mask.Height * s;
        int fineWidth = horizontal ? sourceHeight : sourceWidth;
        int fineHeight = horizontal ? sourceWidth : sourceHeight;
        byte[] alpha = new byte[fineWidth * fineHeight];
        for (int z = 0; z < fineHeight; z++)
            for (int x = 0; x < fineWidth; x++)
            {
                float u = (x + 0.5f) / fineWidth, v = (z + 0.5f) / fineHeight;
                (float lu, float lv) = door.Rotation.AsInt switch
                {
                    0 => (u, v), 1 => (1f - v, u), 2 => (1f - u, 1f - v), _ => (v, 1f - u)
                };
                int row = (int)(lv * sourceHeight);
                int col = (int)(lu * sourceWidth);
                int opposite = sourceWidth - 1 - col;
                if (door is RimWorld.Building_MultiTileDoor)
                {
                    col = lu < 0.5f ? (int)(lu * 2f * sourceWidth) : (int)((1f - lu) * 2f * sourceWidth);
                    alpha[z * fineWidth + x] = mask.Fine[row * sourceWidth + col];
                }
                else alpha[z * fineWidth + x] = Math.Max(mask.Fine[row * sourceWidth + col], mask.Fine[row * sourceWidth + opposite]);
            }
        mask = new AlphaMask(horizontal ? mask.Height : mask.Width, horizontal ? mask.Width : mask.Height, alpha);
        Transformed.Add(key, mask);
    }
}
