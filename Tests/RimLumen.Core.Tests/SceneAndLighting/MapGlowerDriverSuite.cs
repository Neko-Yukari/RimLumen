using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using Verse;
using CurveKey = RimLumen.Core.Contracts.CurveKey;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class MapGlowerDriverSuite
{
    [LumenFact]
    public static void StationaryLightRegistersOnlyOnceAcross600Frames()
    {
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        int id = driver.Add(new PointLightSpec(5f, 1f, Vector3.One));
        GlowAssert.Equal(true, id >= 0);
        GlowAssert.Equal(1, driver.TrackedLightCount);
        GlowAssert.Equal(0, driver.ActiveLightCount);
        GlowAssert.Equal(0, sink.Registrations.Count);

        for (int frame = 0; frame < 600; frame++)
        {
            driver.Update(id, new IntVec3(3, 0, 7), frame / 60f, 1f);
        }

        GlowAssert.Equal(1, driver.ActiveLightCount);
        GlowAssert.Equal(1, driver.RegisterCallCount);
        GlowAssert.Equal(0, driver.DeregisterCallCount);
        GlowAssert.Equal(1, sink.Registrations.Count);
        GlowAssert.Equal(0, sink.DeregisterCount);
        GlowAssert.Equal(1, sink.LiveHandles.Count);
        GlowAssert.Equal("R", string.Concat(sink.Events));
    }

    [LumenFact]
    public static void IntensityJitterDeduplicatesUntilAQuantizedByteChanges()
    {
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        int id = driver.Add(new PointLightSpec(5f, 1f, new Vector3(0.5f)));
        var cell = new IntVec3(3, 0, 7);
        driver.Update(id, cell, 0f, 1f);
        driver.Update(id, cell, 1f, 0.995f);
        driver.Update(id, cell, 2f, 1.005f);
        driver.Update(id, cell, 3f, 0.995f);
        GlowAssert.Equal(new ColorInt(128, 128, 128, 255), sink.Registrations[0].Color);
        GlowAssert.Equal(1, driver.RegisterCallCount);
        GlowAssert.Equal(0, driver.DeregisterCallCount);
        GlowAssert.Equal("R", string.Concat(sink.Events));

        driver.Update(id, cell, 4f, 1.04f);
        GlowAssert.Equal(new ColorInt(132, 132, 132, 255), sink.Registrations[1].Color);
        GlowAssert.Equal(2, driver.RegisterCallCount);
        GlowAssert.Equal(1, driver.DeregisterCallCount);
        GlowAssert.Equal(1, sink.LiveHandles.Count);
        GlowAssert.Equal("RDR", string.Concat(sink.Events));
    }

    [LumenFact]
    public static void RadiiClampAndQuantizeAndColorUsesRoundedBytesThenStepFour()
    {
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        int clamped = driver.Add(new PointLightSpec(50f, 80f, new Vector3(-1f, 0.5f, 2f)));
        driver.Update(clamped, new IntVec3(0, 0, 0), 0f, 1f);
        GlowAssert.Equal(40f, sink.Registrations[0].Radius);
        GlowAssert.Equal(40f, sink.Registrations[0].OverlightRadius);
        GlowAssert.Equal(new ColorInt(0, 128, 255, 255), sink.Registrations[0].Color);
        GlowAssert.Equal(false, sink.Registrations[0].IsCavePlant);

        int rounded = driver.Add(new PointLightSpec(1.024f, 1.026f, new Vector3(1.5f / 255f, 5f / 255f, 0f)));
        driver.Update(rounded, new IntVec3(0, 0, 0), 0f, 1f);
        GlowAssert.Equal(1f, sink.Registrations[1].Radius);
        GlowAssert.Equal(1.05f, sink.Registrations[1].OverlightRadius);
        GlowAssert.Equal(new ColorInt(4, 4, 0, 255), sink.Registrations[1].Color);

        int negative = driver.Add(new PointLightSpec(-1f, -5f, Vector3.One));
        driver.Update(negative, new IntVec3(0, 0, 0), 0f, 1f);
        GlowAssert.Equal(0f, sink.Registrations[2].Radius);
        GlowAssert.Equal(0f, sink.Registrations[2].OverlightRadius);
        GlowAssert.Equal(3, driver.RegisterCallCount);
        GlowAssert.Equal(0, driver.DeregisterCallCount);
    }

    [LumenFact]
    public static void NonPositiveIntensityUnregistersButKeepsLightForReactivation()
    {
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        int id = driver.Add(new PointLightSpec(5f, 1f, Vector3.One));
        var cell = new IntVec3(3, 0, 7);
        driver.Update(id, cell, 0f, 0f);
        GlowAssert.Equal(0, driver.RegisterCallCount);
        driver.Update(id, cell, 1f, 1f);
        driver.Update(id, cell, 2f, -0.1f);
        driver.Update(id, cell, 3f, -1f);
        driver.Update(id, cell, 4f, 0f);
        GlowAssert.Equal(1, driver.TrackedLightCount);
        GlowAssert.Equal(0, driver.ActiveLightCount);
        GlowAssert.Equal(0, sink.LiveHandles.Count);
        GlowAssert.Equal("RD", string.Concat(sink.Events));

        driver.Update(id, cell, 5f, 1f);
        GlowAssert.Equal(1, driver.ActiveLightCount);
        GlowAssert.Equal(2, driver.RegisterCallCount);
        GlowAssert.Equal(1, driver.DeregisterCallCount);
        GlowAssert.Equal("RDR", string.Concat(sink.Events));
        driver.Dispose();
        GlowAssert.Equal(0, sink.LiveHandles.Count);
        GlowAssert.Equal(driver.RegisterCallCount, driver.DeregisterCallCount);
    }

    [LumenFact]
    public static void IntensityCurveAndUpdateMultiplierDriveRegisteredColor()
    {
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(0f, 0f, OutTangent: 1f),
            new CurveKey(2f, 2f, InTangent: 1f),
        });
        GlowAssert.Equal(0.5f, CurveEvaluator.Evaluate(curve, 0.5f));
        GlowAssert.Equal(1f, CurveEvaluator.Evaluate(curve, 1f));
        GlowAssert.Equal(2f, CurveEvaluator.Evaluate(curve, 2f));

        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        int id = driver.Add(new PointLightSpec(5f, 1f, Vector3.One, curve));
        var cell = new IntVec3(3, 0, 7);
        driver.Update(id, cell, 0.5f, 0.5f);
        GlowAssert.Equal(new ColorInt(64, 64, 64, 255), sink.Registrations[0].Color);
        driver.Update(id, cell, 1f, 0.5f);
        GlowAssert.Equal(new ColorInt(128, 128, 128, 255), sink.Registrations[1].Color);
        driver.Update(id, cell, 2f, 0.5f);
        GlowAssert.Equal(new ColorInt(255, 255, 255, 255), sink.Registrations[2].Color);
        driver.Update(id, cell, 0f, 0.5f);
        GlowAssert.Equal("RDRDRD", string.Concat(sink.Events));
        GlowAssert.Equal(3, driver.RegisterCallCount);
        GlowAssert.Equal(3, driver.DeregisterCallCount);
        GlowAssert.Equal(1, driver.TrackedLightCount);
        GlowAssert.Equal(0, sink.LiveHandles.Count);

        // Add's base multiplier composes with the per-frame multiplier: 0.5 * 1.0 * curve(1s)=1 -> 128.
        int scaled = driver.Add(new PointLightSpec(5f, 1f, Vector3.One, curve), 0.5f);
        driver.Update(scaled, new IntVec3(4, 0, 7), 1f, 1f);
        GlowAssert.Equal(new ColorInt(128, 128, 128, 255), sink.Registrations[3].Color);
    }

    [LumenFact]
    public static void SubCellMovementDeduplicatesAndCellCrossingReregisters()
    {
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        int id = driver.Add(new PointLightSpec(5f, 1f, Vector3.One));
        driver.Update(id, MapGlowerDriver.CellFromWorld(new Vector3(3.1f, 0f, 7.1f)), 0f, 1f);
        driver.Update(id, MapGlowerDriver.CellFromWorld(new Vector3(3.49f, 0.49f, 7.49f)), 1f, 1f);
        GlowAssert.Equal("R", string.Concat(sink.Events));
        GlowAssert.Equal(1, driver.RegisterCallCount);
        GlowAssert.Equal(0, driver.DeregisterCallCount);

        driver.Update(id, MapGlowerDriver.CellFromWorld(new Vector3(3.5f, 0f, 7.1f)), 2f, 1f);
        GlowAssert.Equal(new IntVec3(4, 0, 7), sink.Registrations[1].Cell);
        GlowAssert.Equal("RDR", string.Concat(sink.Events));
        GlowAssert.Equal(2, driver.RegisterCallCount);
        GlowAssert.Equal(1, driver.DeregisterCallCount);
    }

    [LumenFact]
    public static void CellFromWorldRoundsEveryAxisHalfAwayFromZero()
    {
        GlowAssert.Equal(new IntVec3(0, 0, 0), MapGlowerDriver.CellFromWorld(new Vector3(0.49f, -0.49f, 0f)));
        GlowAssert.Equal(new IntVec3(1, -1, 2), MapGlowerDriver.CellFromWorld(new Vector3(0.5f, -0.5f, 1.5f)));
        GlowAssert.Equal(new IntVec3(-2, 3, -3), MapGlowerDriver.CellFromWorld(new Vector3(-1.5f, 2.5f, -2.5f)));
        GlowAssert.Equal(new IntVec3(-2, 3, -4), MapGlowerDriver.CellFromWorld(new Vector3(-1.51f, 3.49f, -3.51f)));
    }

    [LumenFact]
    public static void RemoveDisposeAndUnknownIdsCannotLeakOrAliasLiveLights()
    {
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        var spec = new PointLightSpec(5f, 1f, Vector3.One);
        int removed = driver.Add(spec);
        int survivor = driver.Add(spec);
        var cell = new IntVec3(3, 0, 7);
        driver.Update(removed, cell, 0f, 1f);
        driver.Update(survivor, cell, 0f, 1f);
        driver.Remove(removed);
        driver.Remove(removed);
        driver.Update(removed, cell, 1f, 1f);
        driver.Update(-1, cell, 1f, 1f);
        driver.Remove(-1);
        GlowAssert.Equal(1, driver.TrackedLightCount);
        GlowAssert.Equal(1, driver.ActiveLightCount);
        GlowAssert.Equal("RRD", string.Concat(sink.Events));

        int added = driver.Add(spec);
        GlowAssert.Equal(true, added >= 0 && added != survivor);
        driver.Update(added, cell, 1f, 1f);
        int dormant = driver.Add(spec);
        GlowAssert.Equal(true, dormant >= 0 && dormant != added && dormant != survivor);
        driver.Update(dormant, cell, 1f, 0f);
        GlowAssert.Equal(3, driver.TrackedLightCount);
        GlowAssert.Equal(2, driver.ActiveLightCount);
        driver.Dispose();
        driver.Dispose();
        driver.Remove(survivor);
        driver.Update(survivor, cell, 2f, 1f);
        GlowAssert.Throws<ObjectDisposedException>(() => driver.Add(spec));
        GlowAssert.Equal(true, driver.IsDisposed);
        GlowAssert.Equal(0, driver.TrackedLightCount);
        GlowAssert.Equal(0, driver.ActiveLightCount);
        GlowAssert.Equal(0, sink.LiveHandles.Count);
        GlowAssert.Equal(3, driver.RegisterCallCount);
        GlowAssert.Equal(3, driver.DeregisterCallCount);
        GlowAssert.Equal(3, sink.Registrations.Count);
        GlowAssert.Equal(3, sink.DeregisterCount);
        GlowAssert.Equal("RRDRDD", string.Concat(sink.Events));
    }
}

internal sealed class RecordingGlowGridSink : IGlowGridSink
{
    internal readonly record struct Registration(
        int Handle, IntVec3 Cell, float Radius, ColorInt Color, float OverlightRadius, bool IsCavePlant);

    public List<Registration> Registrations { get; } = new();
    public HashSet<int> LiveHandles { get; } = new();
    public List<string> Events { get; } = new();
    public int DeregisterCount { get; private set; }

    public int Register(IntVec3 position, float glowRadius, ColorInt glowColor, float overlightRadius, bool isCavePlant = false)
    {
        int handle = Registrations.Count;
        Registrations.Add(new Registration(handle, position, glowRadius, glowColor, overlightRadius, isCavePlant));
        LiveHandles.Add(handle);
        Events.Add("R");
        return handle;
    }

    public void Deregister(int handle)
    {
        if (!LiveHandles.Remove(handle))
        {
            throw new InvalidOperationException($"Deregister called without a live handle: {handle}.");
        }
        DeregisterCount++;
        Events.Add("D");
    }
}

internal static class GlowAssert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
