using System;

namespace RimLumen.Core.Rendering.Logic;

public readonly record struct MipSize(int Width, int Height);

public static class BloomMipChain
{
    // Requires: width, height, maxLevels, minDimension >= 1 (throws ArgumentOutOfRangeException otherwise).
    // Ensures: level 0 = (ceil(width/2), ceil(height/2)); each next level halves the previous with ceil;
    //   a level is appended only while both dimensions >= minDimension and count < maxLevels;
    //   level 0 is always present (clamped to >= 1 per dimension).
    public static MipSize[] Compute(int width, int height, int maxLevels, int minDimension)
    {
        throw new NotImplementedException();
    }
}
