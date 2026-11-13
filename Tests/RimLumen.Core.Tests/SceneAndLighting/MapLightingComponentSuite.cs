using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using Verse;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class MapLightingComponentSuite
{
    [LumenFact]
    public static void BareMapSinkFailsRegisterExplicitlyAndDeregisterIsANoOp()
    {
        var map = new Map();
        GlowAssert.Equal(true, map.glowGrid is null);
        var sink = new VerseGlowGridSink(map);
        GlowAssert.Throws<InvalidOperationException>(() =>
            sink.Register(new IntVec3(1, 0, 2), 5f, new ColorInt(255, 255, 255), 1f));
        sink.Deregister(0);
        sink.Deregister(-1);
        sink.Deregister(int.MaxValue);
    }

    [LumenFact]
    public static void DefaultComponentUsesTheMapsUninitializedGrid()
    {
        var component = new LumenMapLightingComponent(new Map());
        try
        {
            var driver = component.Driver;
            int id = driver.Add(new PointLightSpec(5f, 1f, Vector3.One));
            GlowAssert.Throws<InvalidOperationException>(() => driver.Update(id, new IntVec3(1, 0, 2), 0f, 1f));
            GlowAssert.Equal(1, driver.RegisterCallCount);
            GlowAssert.Equal(0, driver.DeregisterCallCount);
            GlowAssert.Equal(0, driver.ActiveLightCount);
        }
        finally
        {
            component.MapRemoved();
        }
        GlowAssert.Equal(true, component.Driver.IsDisposed);
        GlowAssert.Equal(0, component.Driver.DeregisterCallCount);
    }

    [LumenFact]
    public static void MapRemovedReleasesEveryLightAndUnregistersTheDriver()
    {
        var map = new Map { uniqueID = 901 };
        var sink = new RecordingGlowGridSink();
        var component = new LumenMapLightingComponent(map, sink);
        GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
        try
        {
            component.FinalizeInit();
            component.FinalizeInit();
            GlowAssert.Equal(1, LumenMapLightingRegistry.Count);
            GlowAssert.Equal(true, LumenMapLightingRegistry.TryResolve(901, out var resolved));
            GlowAssert.Equal(component.Driver, resolved);
            var driver = component.Driver;
            var spec = new PointLightSpec(5f, 1f, Vector3.One);
            int first = driver.Add(spec);
            int second = driver.Add(spec);
            driver.Add(spec);
            driver.Update(first, new IntVec3(1, 0, 2), 0f, 1f);
            driver.Update(second, new IntVec3(3, 0, 4), 0f, 1f);
            component.ExposeData();
            GlowAssert.Equal(false, driver.IsDisposed);
            GlowAssert.Equal(3, driver.TrackedLightCount);
            GlowAssert.Equal(2, driver.ActiveLightCount);
            GlowAssert.Equal(2, sink.LiveHandles.Count);
            GlowAssert.Equal(0, driver.DeregisterCallCount);
            component.MapRemoved();
            component.MapRemoved();
            GlowAssert.Equal(true, driver.IsDisposed);
            GlowAssert.Equal(0, driver.TrackedLightCount);
            GlowAssert.Equal(0, driver.ActiveLightCount);
            GlowAssert.Equal(0, sink.LiveHandles.Count);
            GlowAssert.Equal(2, driver.RegisterCallCount);
            GlowAssert.Equal(2, driver.DeregisterCallCount);
            GlowAssert.Equal(2, sink.DeregisterCount);
            GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
            GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(901, out resolved));
            GlowAssert.Equal(true, resolved is null);
        }
        finally
        {
            component.MapRemoved();
        }
    }

    [LumenFact]
    public static void DisposeReleasesEveryLightWithoutMapRemovedAndMixedTeardownIsIdempotent()
    {
        var sink = new RecordingGlowGridSink();
        using var component = new LumenMapLightingComponent(new Map { uniqueID = 903 }, sink);
        component.FinalizeInit();
        var driver = component.Driver;
        var spec = new PointLightSpec(5f, 1f, Vector3.One);
        int first = driver.Add(spec);
        int second = driver.Add(spec);
        driver.Add(spec);
        driver.Update(first, new IntVec3(1, 0, 2), 0f, 1f);
        driver.Update(second, new IntVec3(3, 0, 4), 0f, 1f);
        GlowAssert.Equal(2, sink.LiveHandles.Count);

        ((IDisposable)component).Dispose();
        GlowAssert.Equal(true, driver.IsDisposed);
        GlowAssert.Equal(0, driver.TrackedLightCount);
        GlowAssert.Equal(0, driver.ActiveLightCount);
        GlowAssert.Equal(0, sink.LiveHandles.Count);
        GlowAssert.Equal(2, sink.Registrations.Count);
        GlowAssert.Equal(2, sink.DeregisterCount);
        GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
        GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(903, out var resolved));
        GlowAssert.Equal(true, resolved is null);

        component.MapRemoved();
        component.Dispose();
        GlowAssert.Equal(2, sink.DeregisterCount);
        GlowAssert.Equal(2, driver.DeregisterCallCount);
        GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
    }

    [LumenFact]
    public static void TwoMapComponentsUnregisterIndependentlyWithoutPositiveKeyFallback()
    {
        var firstSink = new RecordingGlowGridSink();
        var secondSink = new RecordingGlowGridSink();
        using var first = new LumenMapLightingComponent(new Map { uniqueID = 111 }, firstSink);
        using var second = new LumenMapLightingComponent(new Map { uniqueID = 222 }, secondSink);
        first.FinalizeInit();
        second.FinalizeInit();
        AssertResolves(111, first.Driver);
        AssertResolves(222, second.Driver);
        AssertResolves(-1, second.Driver);
        GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(999, out var missing));
        GlowAssert.Equal(true, missing is null);
        var spec = new PointLightSpec(5f, 1f, Vector3.One);
        first.Driver.Update(first.Driver.Add(spec), new IntVec3(1, 0, 2), 0f, 1f);
        second.Driver.Update(second.Driver.Add(spec), new IntVec3(3, 0, 4), 0f, 1f);

        first.Dispose();
        GlowAssert.Equal(1, LumenMapLightingRegistry.Count);
        GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(111, out missing));
        GlowAssert.Equal(true, missing is null);
        AssertResolves(222, second.Driver);
        AssertResolves(-1, second.Driver);
        GlowAssert.Equal(0, firstSink.LiveHandles.Count);
        GlowAssert.Equal(1, firstSink.DeregisterCount);
        GlowAssert.Equal(false, second.Driver.IsDisposed);
        GlowAssert.Equal(1, secondSink.LiveHandles.Count);
        GlowAssert.Equal(0, secondSink.DeregisterCount);

        second.MapRemoved();
        second.Dispose();
        GlowAssert.Equal(true, second.Driver.IsDisposed);
        GlowAssert.Equal(0, secondSink.LiveHandles.Count);
        GlowAssert.Equal(1, secondSink.DeregisterCount);
        GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
        GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(222, out missing));
        GlowAssert.Equal(true, missing is null);
    }

    [LumenFact]
    public static void RemovingOldComponentDoesNotUnregisterItsReplacement()
    {
        var oldSink = new RecordingGlowGridSink();
        var replacementSink = new RecordingGlowGridSink();
        var old = new LumenMapLightingComponent(new Map { uniqueID = 902 }, oldSink);
        var replacement = new LumenMapLightingComponent(new Map { uniqueID = 902 }, replacementSink);
        try
        {
            old.FinalizeInit();
            replacement.FinalizeInit();
            var spec = new PointLightSpec(5f, 1f, Vector3.One);
            int oldId = old.Driver.Add(spec);
            int newId = replacement.Driver.Add(spec);
            old.Driver.Update(oldId, new IntVec3(1, 0, 2), 0f, 1f);
            replacement.Driver.Update(newId, new IntVec3(1, 0, 2), 0f, 1f);
            old.MapRemoved();
            GlowAssert.Equal(0, oldSink.LiveHandles.Count);
            GlowAssert.Equal(1, old.Driver.RegisterCallCount);
            GlowAssert.Equal(1, old.Driver.DeregisterCallCount);
            GlowAssert.Equal(1, replacementSink.LiveHandles.Count);
            GlowAssert.Equal(1, LumenMapLightingRegistry.Count);
            GlowAssert.Equal(true, LumenMapLightingRegistry.TryResolve(902, out var resolved));
            GlowAssert.Equal(replacement.Driver, resolved);
            replacement.MapRemoved();
            GlowAssert.Equal(0, replacementSink.LiveHandles.Count);
            GlowAssert.Equal(1, replacement.Driver.RegisterCallCount);
            GlowAssert.Equal(1, replacement.Driver.DeregisterCallCount);
            GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
        }
        finally
        {
            old.MapRemoved();
            replacement.MapRemoved();
        }
    }

    [LumenFact]
    public static void RegistryPrefersExactKeysAndTracksRecencyAndReplacementByInstance()
    {
        using var unsaved = new MapGlowerDriver(new RecordingGlowGridSink());
        using var first = new MapGlowerDriver(new RecordingGlowGridSink());
        using var second = new MapGlowerDriver(new RecordingGlowGridSink());
        using var replacement = new MapGlowerDriver(new RecordingGlowGridSink());
        GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
        GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(-1, out var resolved));
        GlowAssert.Equal(true, resolved is null);
        try
        {
            LumenMapLightingRegistry.Register(-1, unsaved);
            LumenMapLightingRegistry.Register(101, first);
            LumenMapLightingRegistry.Register(202, second);
            GlowAssert.Equal(3, LumenMapLightingRegistry.Count);
            AssertResolves(101, first);
            AssertResolves(-1, second);
            AssertResolves(-2, second);
            GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(999, out resolved));
            GlowAssert.Equal(true, resolved is null);
            LumenMapLightingRegistry.Register(101, first);
            GlowAssert.Equal(3, LumenMapLightingRegistry.Count);
            AssertResolves(-2, first);
            LumenMapLightingRegistry.Register(101, replacement);
            GlowAssert.Equal(3, LumenMapLightingRegistry.Count);
            AssertResolves(101, replacement);
            AssertResolves(-2, replacement);
            LumenMapLightingRegistry.Unregister(101, first);
            LumenMapLightingRegistry.Unregister(101, first);
            LumenMapLightingRegistry.Unregister(999, replacement);
            GlowAssert.Equal(3, LumenMapLightingRegistry.Count);
            AssertResolves(101, replacement);
            LumenMapLightingRegistry.Unregister(101, replacement);
            LumenMapLightingRegistry.Unregister(101, replacement);
            GlowAssert.Equal(2, LumenMapLightingRegistry.Count);
            GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(101, out resolved));
            GlowAssert.Equal(true, resolved is null);
            AssertResolves(-1, second);
            LumenMapLightingRegistry.Unregister(202, second);
            GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(999, out resolved));
            GlowAssert.Equal(true, resolved is null);
            AssertResolves(-1, unsaved);
            LumenMapLightingRegistry.Unregister(-1, unsaved);
            GlowAssert.Equal(0, LumenMapLightingRegistry.Count);
            GlowAssert.Equal(false, LumenMapLightingRegistry.TryResolve(101, out resolved));
            GlowAssert.Equal(true, resolved is null);
        }
        finally
        {
            LumenMapLightingRegistry.Unregister(-1, unsaved);
            LumenMapLightingRegistry.Unregister(101, first);
            LumenMapLightingRegistry.Unregister(101, replacement);
            LumenMapLightingRegistry.Unregister(202, second);
        }
    }

    private static void AssertResolves(int mapIndex, MapGlowerDriver expected)
    {
        GlowAssert.Equal(true, LumenMapLightingRegistry.TryResolve(mapIndex, out var actual));
        GlowAssert.Equal(expected, actual);
    }
}
