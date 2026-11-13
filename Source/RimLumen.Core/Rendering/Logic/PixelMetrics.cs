using System;

namespace RimLumen.Core.Rendering.Logic;

public readonly record struct PixelDiffResult(int MaxAbsDelta, int DifferingPixels, int TotalPixels);

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

// RGBA8 top-down byte buffers (4 bytes per pixel), as returned by TryReadScreen / TryReadPixels.
public static class PixelMetrics
{
    // Requires: a.Length == b.Length == width * height * 4.
    // Ensures: MaxAbsDelta = max over RGB channels (alpha ignored) of |a - b|; DifferingPixels counts
    //   pixels with any RGB channel delta > 0.
    public static PixelDiffResult Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        throw new NotImplementedException();
    }

    // Requires: buffers as in Compare; quad lies inside the image; ringWidth >= 1.
    // Ensures: counts pixels inside the ring [quad expanded by ringWidth] minus quad (clipped to the image)
    //   whose Rec.709 luma in `active` exceeds `baseline` by more than lumaThreshold (0-255 scale).
    public static int CountHaloPixels(ReadOnlySpan<byte> baseline, ReadOnlySpan<byte> active, int width, int height,
        PixelRect quad, int ringWidth, int lumaThreshold)
    {
        throw new NotImplementedException();
    }
}
