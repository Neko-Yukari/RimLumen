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
        int maxAbsDelta = 0;
        int differingPixels = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int pixelDelta = Math.Max(Math.Abs(a[i] - b[i]),
                Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            maxAbsDelta = Math.Max(maxAbsDelta, pixelDelta);
            if (pixelDelta > 0)
            {
                differingPixels++;
            }
        }
        return new PixelDiffResult(maxAbsDelta, differingPixels, width * height);
    }

    // Requires: buffers as in Compare; quad lies inside the image; ringWidth >= 1.
    // Ensures: counts pixels inside the ring [quad expanded by ringWidth] minus quad (clipped to the image)
    //   whose Rec.709 luma in `active` exceeds `baseline` by more than lumaThreshold (0-255 scale).
    public static int CountHaloPixels(ReadOnlySpan<byte> baseline, ReadOnlySpan<byte> active, int width, int height,
        PixelRect quad, int ringWidth, int lumaThreshold)
    {
        int left = Math.Max(0, quad.X - ringWidth);
        int top = Math.Max(0, quad.Y - ringWidth);
        int right = Math.Min(width, quad.X + quad.Width + ringWidth);
        int bottom = Math.Min(height, quad.Y + quad.Height + ringWidth);
        int count = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (x >= quad.X && x < quad.X + quad.Width && y >= quad.Y && y < quad.Y + quad.Height)
                {
                    continue;
                }
                int i = (y * width + x) * 4;
                int lumaDelta = 2126 * (active[i] - baseline[i])
                    + 7152 * (active[i + 1] - baseline[i + 1])
                    + 722 * (active[i + 2] - baseline[i + 2]);
                if (lumaDelta > lumaThreshold * 10000L)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
