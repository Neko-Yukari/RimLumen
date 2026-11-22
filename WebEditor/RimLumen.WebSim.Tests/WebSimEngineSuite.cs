using System;
using System.Collections.Generic;
using System.Text.Json;
using RimLumen.Core.Contracts;

namespace RimLumen.WebSim.Tests;

internal static class WebSimEngineSuite
{
    public static void Register(List<(string Name, Action Run)> cases)
    {
        cases.Add(("WebSimEngine.InitDemosAndRoundTrip", InitDemosAndRoundTrip));
        cases.Add(("WebSimEngine.InvalidDraftKeepsActivePlayback", InvalidDraftKeepsActivePlayback));
        cases.Add(("WebSimEngine.FireworkAndPylonSimulationAndBuffers", FireworkAndPylonSimulationAndBuffers));
        cases.Add(("WebSimEngine.ColumnMajorCameraProjectionProducesValidVertices", ColumnMajorCameraProjectionProducesValidVertices));
    }

    private static void InitDemosAndRoundTrip()
    {
        var engine = new WebSimEngine();
        using JsonDocument init = JsonDocument.Parse(engine.Initialize());
        TestAssert.True(init.RootElement.GetProperty("ok").GetBoolean(), "init ok");
        TestAssert.Equal(56, init.RootElement.GetProperty("particleVertexSize").GetInt32(), "ParticleVertex 56 bytes");

        using JsonDocument demos = JsonDocument.Parse(engine.ListDemosJson());
        TestAssert.Equal(6, demos.RootElement.GetArrayLength(), "6 built-in demos listed");

        string xmlDemoJson = engine.GetDemoSource("RimLumen.Demo.Firework.Gold", "xml", compact: true);
        using JsonDocument xmlDoc = JsonDocument.Parse(xmlDemoJson);
        TestAssert.True(xmlDoc.RootElement.GetProperty("ok").GetBoolean(), "Firework XML export ok");
        string xmlContent = xmlDoc.RootElement.GetProperty("content").GetString()!;

        using JsonDocument val = JsonDocument.Parse(engine.ValidateSource(xmlContent, "xml"));
        TestAssert.True(val.RootElement.GetProperty("ok").GetBoolean(), "Firework XML validate ok");
        TestAssert.Equal("RimLumen.Demo.Firework.Gold", val.RootElement.GetProperty("effectId").GetString(), "effectId preserved");

        using JsonDocument exported = JsonDocument.Parse(engine.ExportSource(xmlContent, "xml", "xml", compact: false));
        TestAssert.True(exported.RootElement.GetProperty("ok").GetBoolean(), "XML -> XML roundtrip ok");
    }

    private static void InvalidDraftKeepsActivePlayback()
    {
        var engine = new WebSimEngine();
        engine.Initialize();
        using JsonDocument playOk = JsonDocument.Parse(engine.PlayDemo("RimLumen.Demo.ParticleFountain"));
        TestAssert.True(playOk.RootElement.GetProperty("ok").GetBoolean(), "play fountain ok");

        using JsonDocument badLoad = JsonDocument.Parse(engine.LoadAndPlay("<Defs><ThingDef /></Defs>", "xml"));
        TestAssert.True(!badLoad.RootElement.GetProperty("ok").GetBoolean(), "bad XML load rejected");
        TestAssert.True(badLoad.RootElement.GetProperty("line").GetInt32() >= 1, "line number reported");

        // Active playback must remain alive after failed reload.
        using JsonDocument frame = JsonDocument.Parse(engine.StepFrame(1f / 60f, 1280, 720, ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty));
        TestAssert.True(frame.RootElement.GetProperty("alive").GetBoolean(), "existing handle stayed alive");
        TestAssert.True(frame.RootElement.GetProperty("liveParticles").GetInt32() > 0, "existing particles kept simulating");
    }

    private static void FireworkAndPylonSimulationAndBuffers()
    {
        var engine = new WebSimEngine();
        engine.Initialize();
        TestAssert.True(JsonDocument.Parse(engine.PlayDemo("RimLumen.Demo.DistortionPylon")).RootElement.GetProperty("ok").GetBoolean(), "play pylon");
        engine.SetOrbitMotion(true, 1.5f, 3.0f);

        int maxRibbonVerts = 0;
        int maxDistortions = 0;
        int maxLights = 0;
        for (int i = 0; i < 30; i++)
        {
            using JsonDocument frame = JsonDocument.Parse(engine.StepFrame(1f / 60f, 1280, 720, ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty));
            int rv = frame.RootElement.GetProperty("ribVertexCount").GetInt32();
            int dist = frame.RootElement.GetProperty("distortions").GetArrayLength();
            int lights = frame.RootElement.GetProperty("pointLights").GetArrayLength();
            if (rv > maxRibbonVerts) maxRibbonVerts = rv;
            if (dist > maxDistortions) maxDistortions = dist;
            if (lights > maxLights) maxLights = lights;
        }
        TestAssert.True(maxRibbonVerts > 0, "DistortionPylon generated ribbon vertices");
        TestAssert.True(maxDistortions > 0, "DistortionPylon generated active distortion snapshots");
        TestAssert.True(maxLights > 0, "DistortionPylon generated pointLight");
        TestAssert.Equal(maxRibbonVerts * 56, engine.GetRibbonVerticesBytes().Length, "ribbon vertex bytes exact size");

        // Now test state machine events on JSON asset.
        const string stateJson = """
        {
          "effectId": "StateTest",
          "nodes": [{ "name": "burst", "emitter": { "rateOverTime": 20 } }],
          "states": [
            { "name": "a", "nodes": ["burst"], "duration": 0.1, "next": "b" },
            { "name": "b", "nodes": ["burst"], "duration": 0.1 }
          ],
          "initialState": "a",
          "timelineEvents": [
            { "time": 0.05, "eventName": "ping", "state": "a", "notify": true }
          ]
        }
        """;
        TestAssert.True(JsonDocument.Parse(engine.LoadAndPlay(stateJson, "json")).RootElement.GetProperty("ok").GetBoolean(), "load stateJson");
        var seenEvents = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            using JsonDocument f = JsonDocument.Parse(engine.StepFrame(1f / 60f, 1280, 720, ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty));
            foreach (JsonElement ev in f.RootElement.GetProperty("events").EnumerateArray())
            {
                seenEvents.Add($"{ev.GetProperty("kind").GetString()}:{ev.GetProperty("message").GetString()}");
            }
        }
        TestAssert.True(seenEvents.Contains("StateChanged:a"), "StateChanged:a recorded");
        TestAssert.True(seenEvents.Contains("Notify:ping"), "Notify:ping recorded");
        TestAssert.True(seenEvents.Contains("StateChanged:b"), "StateChanged:b recorded");
        bool stillAlive = true;
        JsonValueKind finalStateKind = JsonValueKind.String;
        for (int i = 0; i < 20; i++)
        {
            using JsonDocument drainedFrame = JsonDocument.Parse(engine.StepFrame(0.15f, 1280, 720, ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty));
            stillAlive = drainedFrame.RootElement.GetProperty("alive").GetBoolean();
            finalStateKind = drainedFrame.RootElement.GetProperty("currentState").ValueKind;
            if (!stillAlive) break;
        }
        TestAssert.True(!stillAlive, "StateTest finished after draining");
        TestAssert.Equal(JsonValueKind.Null, finalStateKind, "currentState cleared when finished");

        // Verify SetParam persists across Restart() and distortion scales by localScale.
        const string scaledDistortionJson = """
        {
          "effectId": "ScaledDistortionTest",
          "defaultDuration": 0.5,
          "nodes": [
            {
              "name": "wave",
              "localScale": [2, 2, 2],
              "emitter": { "rateOverTime": 100, "duration": 0.5 },
              "distortion": { "kind": "RingWave", "radius": 3.0, "strength": 0.2, "thickness": 0.5, "speed": 2.0, "duration": 0.5 }
            }
          ]
        }
        """;
        TestAssert.True(JsonDocument.Parse(engine.LoadAndPlay(scaledDistortionJson, "json")).RootElement.GetProperty("ok").GetBoolean(), "load scaledDistortionJson");
        TestAssert.True(JsonDocument.Parse(engine.SetParam("rate", 0.2f)).RootElement.GetProperty("ok").GetBoolean(), "set rate 0.2");
        TestAssert.True(JsonDocument.Parse(engine.Restart()).RootElement.GetProperty("ok").GetBoolean(), "restart with persisted rate");
        using JsonDocument scaledFrame = JsonDocument.Parse(engine.StepFrame(0.1f, 1280, 720, ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty));
        float distRadius = scaledFrame.RootElement.GetProperty("distortions")[0].GetProperty("radius").GetSingle();
        TestAssert.True(MathF.Abs(6.0f - distRadius) <= 1e-4f, $"distortion radius scaled by WorldScale.XZ (got {distRadius})");
    }

    private static void ColumnMajorCameraProjectionProducesValidVertices()
    {
        var engine = new WebSimEngine();
        engine.Initialize();
        engine.PlayDemo("RimLumen.Demo.Firework.Gold");

        // Standard OpenGL column-major lookAt from (0, 20, 12) toward (0, 2, 0) + perspective projection:
        double[] viewColMajor =
        {
            1, 0, 0, 0,
            0, 0.8574929257, -0.5144957554, 0,
            0, 0.5144957554,  0.8574929257, 0,
            0, -1.7149858514, -23.323807579, 1
        };
        double[] projColMajor =
        {
            1.357995, 0, 0, 0,
            0, 2.4142135, 0, 0,
            0, 0, -1.002, -1,
            0, 0, -0.2002, 0
        };

        int lastSimVerts = 0;
        int lastSimIndices = 0;
        for (int i = 0; i < 10; i++)
        {
            using JsonDocument f = JsonDocument.Parse(engine.StepFrame(1f / 60f, 1280, 720, viewColMajor, projColMajor));
            lastSimVerts = f.RootElement.GetProperty("simVertexCount").GetInt32();
            lastSimIndices = f.RootElement.GetProperty("simIndexCount").GetInt32();
        }
        TestAssert.True(lastSimVerts > 0, "Firework emitted vertices under 3D column-major matrix");
        TestAssert.Equal(lastSimVerts * 56, engine.GetSimVerticesBytes().Length, "simVertexBytes exact size");
        TestAssert.Equal(lastSimIndices * 4, engine.GetSimIndicesBytes().Length, "simIndexBytes exact size");
    }
}
