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
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLevels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minDimension, 1);

        var levels = new System.Collections.Generic.List<MipSize>();
        int mipWidth = width / 2 + width % 2;
        int mipHeight = height / 2 + height % 2;
        levels.Add(new MipSize(mipWidth, mipHeight));
        while (levels.Count < maxLevels)
        {
            mipWidth = mipWidth / 2 + mipWidth % 2;
            mipHeight = mipHeight / 2 + mipHeight % 2;
            if (mipWidth < minDimension || mipHeight < minDimension)
            {
                break;
            }
            levels.Add(new MipSize(mipWidth, mipHeight));
        }
        return levels.ToArray();
    }
}
