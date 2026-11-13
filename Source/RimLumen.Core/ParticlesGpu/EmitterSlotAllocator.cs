using System;
using System.Collections.Generic;

namespace RimLumen.Core.ParticlesGpu;

public readonly record struct EmitterSlotRange(int Offset, int Capacity);

public sealed class EmitterSlotAllocator
{
    private readonly List<EmitterSlotRange> free = new();
    private readonly HashSet<EmitterSlotRange> live = new();

    public EmitterSlotAllocator(int capacity = 65536)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        free.Add(new EmitterSlotRange(0, capacity));
    }

    // Requires: count > 0.
    // Ensures: returns an exclusive contiguous range or throws on exhaustion.
    public EmitterSlotRange Allocate(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        for (int i = 0; i < free.Count; i++)
        {
            var block = free[i];
            if (block.Capacity < count) continue;
            var result = new EmitterSlotRange(block.Offset, count);
            if (block.Capacity == count) free.RemoveAt(i);
            else free[i] = new EmitterSlotRange(block.Offset + count, block.Capacity - count);
            live.Add(result);
            return result;
        }
        throw new InvalidOperationException("No contiguous particle slot range can satisfy the allocation.");
    }

    // Requires: range is a live allocation from this allocator.
    // Ensures: releases range and coalesces adjacent free ranges.
    public void Free(EmitterSlotRange range)
    {
        if (!live.Remove(range))
            throw new ArgumentException("Range is not a live allocation.", nameof(range));
        int index = 0;
        while (index < free.Count && free[index].Offset < range.Offset) index++;
        if (index > 0 && free[index - 1].Offset + free[index - 1].Capacity == range.Offset)
        {
            var left = free[--index];
            range = new EmitterSlotRange(left.Offset, left.Capacity + range.Capacity);
            free.RemoveAt(index);
        }
        if (index < free.Count && range.Offset + range.Capacity == free[index].Offset)
        {
            range = new EmitterSlotRange(range.Offset, range.Capacity + free[index].Capacity);
            free.RemoveAt(index);
        }
        free.Insert(index, range);
    }
}
