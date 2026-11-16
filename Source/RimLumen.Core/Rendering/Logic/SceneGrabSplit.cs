using System;
using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Logic;

internal static class SceneGrabSplit
{
    // Inserts ahead of every equal-queue grab without changing their relative order.
    internal static List<T> Insert<T>(IReadOnlyList<T>? sorted, T sentinel, Func<T, int> queue)
    {
        List<T> grabs = sorted as List<T> ?? (sorted == null ? new List<T>() : new List<T>(sorted));
        int index = 0;
        int split = queue(sentinel);
        while (index < grabs.Count && queue(grabs[index]) < split) index++;
        grabs.Insert(index, sentinel);
        return grabs;
    }
}
