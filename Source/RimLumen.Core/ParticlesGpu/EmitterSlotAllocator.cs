using System;
using System.Collections.Generic;

namespace RimLumen.Core.ParticlesGpu;

public readonly record struct EmitterSlotRange(int Offset, int Capacity);

public sealed class ParticlePoolExhaustedException : InvalidOperationException
{
    public ParticlePoolExhaustedException(string message) : base(message) { }
}

public sealed class EmitterSlotAllocator
{
    // 1M slots * 64 bytes/particle = 64 MB device-local SSBO (under half of Vulkan 1.2's 128 MB minimum
    // maxStorageBufferRange) and stays exact in IEEE-754 float slot fields (< 2^24).
    public const int DefaultCapacity = 1_048_576;

    private readonly List<EmitterSlotRange> free = new();
    private readonly HashSet<EmitterSlotRange> live = new();

    public EmitterSlotAllocator(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        free.Add(new EmitterSlotRange(0, capacity));
    }

    // Requires: count > 0.
    // Ensures: returns an exclusive contiguous range or throws ParticlePoolExhaustedException on exhaustion.
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
        throw new ParticlePoolExhaustedException("No contiguous particle slot range can satisfy the allocation.");
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
