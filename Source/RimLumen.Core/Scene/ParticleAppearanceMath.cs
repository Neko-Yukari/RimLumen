using System;
using System.Numerics;

namespace RimLumen.Core.Scene;

internal static class ParticleAppearanceMath
{
    internal static float SizeScale(in Matrix4x4 world)
    {
        float determinant = world.M11 * (world.M22 * world.M33 - world.M23 * world.M32)
            - world.M12 * (world.M21 * world.M33 - world.M23 * world.M31)
            + world.M13 * (world.M21 * world.M32 - world.M22 * world.M31);
        return MathF.Cbrt(MathF.Abs(determinant));
    }
}
