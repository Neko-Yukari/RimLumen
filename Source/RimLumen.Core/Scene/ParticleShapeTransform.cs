using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

internal static class ParticleShapeTransform
{
    // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468,
    // Source/RimUnity/Particles/ParticleSystem.Shapes.cs:84-86 (shape-only TRS order).
    // Requires: spec passed asset validation; position and direction are sampled in Core's shape frame.
    // Ensures: position = R(scale * position) + offset; direction = R(direction), before the node transform.
    internal static void Apply(EmitterSpec spec, ref Vector3 position, ref Vector3 direction)
    {
        position = Vector3.Transform(position * spec.ShapeScale, spec.ShapeRotation) + spec.ShapePosition;
        direction = Vector3.Transform(direction, spec.ShapeRotation);
    }
}
