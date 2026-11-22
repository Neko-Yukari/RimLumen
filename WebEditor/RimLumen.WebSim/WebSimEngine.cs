using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Demo;
using RimLumen.Core.Lighting;
using RimLumen.Core.Rendering;
using RimLumen.Core.Ribbons;
using RimLumen.Core.Scene;

namespace RimLumen.WebSim;

public readonly record struct WebLogEvent(string Kind, string Message);

/// <summary>
/// Pure C# simulation/serialization host shared between browser WASM (<c>Program.cs</c>)
/// and offline tests (<c>RimLumen.WebSim.Tests</c>).
/// </summary>
public sealed class WebSimEngine
{
    private const string ActiveDraftProviderKey = "__rimlumen_web_active_draft__";

    private sealed class DraftAssetProvider : ILumenAssetProvider
    {
        public EffectAsset? Current { get; set; }

        public bool TryResolveEffect(string effectId, out EffectAsset? asset)
        {
            if (effectId == ActiveDraftProviderKey && Current is not null)
            {
                asset = Current;
                return true;
            }
            asset = null;
            return false;
        }
    }

    private static readonly Matrix4x4 DefaultTopDown2DView = new(
        1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, -1f, 0f, -30f,
        0f, 0f, 0f, 1f);

    private static readonly Matrix4x4 DefaultOrthoProj = Matrix4x4.CreateOrthographic(24f, 16f, 0.3f, 1000f);

    private static readonly string[] BuiltInDemoIds =
    {
        RimLumenDemoEffects.FireworkGoldId,
        RimLumenDemoEffects.FireworkCyanId,
        RimLumenDemoEffects.FireworkMagentaId,
        RimLumenDemoEffects.ParticleFountainId,
        RimLumenDemoEffects.StreetLampId,
        RimLumenDemoEffects.DistortionPylonId,
    };

    private bool initialized;
    private LumenRibbonBackend? ribbonBackend;
    private WebDistortionBackend? distortionBackend;
    private WebScreenDimBackend? screenDimBackend;
    private readonly DraftAssetProvider draftProvider = new();
    private readonly List<WebLogEvent> frameEvents = new();

    private ILumenEffectHandle? activeHandle;
    private EffectAsset? activeAsset;
    private string activeSourceFormat = "json";
    private string? currentState;
    private ulong frameIndex;

    private bool orbitEnabled;
    private float orbitRadius = 1.5f;
    private float orbitAngularSpeed = 2.4f;
    private float orbitAngle;
    private Vector3 targetWorld = new(3f, 1f, 0f);
    private float depthReferenceZ = 0f;
    private float particleDensity = 1f;
    private float intensityScale = 1f;
    private float rateScale = 1f;

    public bool IsInitialized => initialized;
    public EffectAsset? ActiveAsset => activeAsset;
    public ILumenEffectHandle? ActiveHandle => activeHandle;

    public string Initialize()
    {
        if (!initialized)
        {
            Verse.Log.WarningSink = msg => RecordHostMessage("Warn", msg);
            Verse.Log.ErrorSink = msg => RecordHostMessage("Error", msg);
            LumenSceneBootstrap.EnsureInitialized();
            if (LumenHostRegistry.GetNodeFeatureBackend(LumenNodeFeature.Ribbon) is LumenRibbonBackend existingRibbon)
            {
                ribbonBackend = existingRibbon;
            }
            else
            {
                ribbonBackend = new LumenRibbonBackend();
                LumenHostRegistry.RegisterNodeFeatureBackend(ribbonBackend);
            }
            if (LumenHostRegistry.GetNodeFeatureBackend(LumenNodeFeature.Distortion) is WebDistortionBackend existingDist)
            {
                distortionBackend = existingDist;
            }
            else
            {
                distortionBackend = new WebDistortionBackend();
                LumenHostRegistry.RegisterNodeFeatureBackend(distortionBackend);
            }
            if (LumenHostRegistry.GetNodeFeatureBackend(LumenNodeFeature.ScreenDim) is WebScreenDimBackend existingDim)
            {
                screenDimBackend = existingDim;
            }
            else
            {
                screenDimBackend = new WebScreenDimBackend();
                LumenHostRegistry.RegisterNodeFeatureBackend(screenDimBackend);
            }
            LumenHostRegistry.RegisterAssetProvider(draftProvider);
            RimLumenDemoEffects.EnsureRegistered();
            initialized = true;
        }

        return WriteJson(w =>
        {
            w.WriteBoolean("ok", true);
            w.WriteNumber("particleVertexSize", Marshal.SizeOf<ParticleVertex>());
            w.WriteNumber("catalogCount", LumenSceneBootstrap.Catalog.Count);
        });
    }

    public string ListDemosJson()
    {
        EnsureInit();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (string id in BuiltInDemoIds)
            {
                if (!LumenSceneBootstrap.Catalog.TryResolveEffect(id, out EffectAsset? demo) || demo is null)
                    continue;
                w.WriteStartObject();
                w.WriteString("effectId", demo.EffectId);
                w.WriteNumber("defaultDuration", demo.DefaultDuration);
                w.WriteNumber("nodeCount", demo.Nodes.Length);
                w.WriteNumber("stateCount", demo.States.Length);
                WriteStringArray(w, "textureKeys", CollectTextureKeys(demo));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string GetDemoSource(string effectId, string format, bool compact = true)
    {
        EnsureInit();
        if (!LumenSceneBootstrap.Catalog.TryResolveEffect(effectId, out EffectAsset? asset) || asset is null)
        {
            return WriteErrorJson($"Unknown built-in demo effect '{effectId}'.", 1, 1);
        }
        return SerializeAssetResponse(asset, format, compact);
    }

    public string ValidateSource(string source, string format)
    {
        EnsureInit();
        if (!TryParseAsset(source, format, out EffectAsset? asset, out string? error, out int line, out int col))
        {
            return WriteErrorJson(error!, line, col);
        }
        return WriteAssetMetadataJson(asset!, NormalizeFormat(format));
    }

    public string ExportSource(string source, string fromFormat, string toFormat, bool compact = true)
    {
        EnsureInit();
        if (!TryParseAsset(source, fromFormat, out EffectAsset? asset, out string? error, out int line, out int col))
        {
            return WriteErrorJson(error!, line, col);
        }
        return SerializeAssetResponse(asset!, toFormat, compact);
    }

    public string LoadAndPlay(string source, string format, string? initialState = null)
    {
        EnsureInit();
        if (!TryParseAsset(source, format, out EffectAsset? asset, out string? error, out int line, out int col))
        {
            return WriteErrorJson(error!, line, col);
        }

        string normFormat = NormalizeFormat(format);
        try
        {
            PlayAssetInternal(asset!, normFormat, initialState);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return WriteErrorJson(ex.Message, 1, 1);
        }

        return WriteAssetMetadataJson(asset!, normFormat);
    }

    public string PlayDemo(string effectId)
    {
        EnsureInit();
        if (!LumenSceneBootstrap.Catalog.TryResolveEffect(effectId, out EffectAsset? asset) || asset is null)
        {
            return WriteErrorJson($"Unknown built-in demo effect '{effectId}'.", 1, 1);
        }
        try
        {
            PlayAssetInternal(asset, activeSourceFormat, initialState: null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return WriteErrorJson(ex.Message, 1, 1);
        }
        return WriteAssetMetadataJson(asset, activeSourceFormat);
    }

    public string Restart()
    {
        EnsureInit();
        if (activeAsset is null)
        {
            return WriteErrorJson("No active effect loaded.", 1, 1);
        }
        PlayAssetInternal(activeAsset, activeSourceFormat, initialState: null);
        return WriteAssetMetadataJson(activeAsset, activeSourceFormat);
    }

    public void Stop(bool allowFadeOut)
    {
        EnsureInit();
        activeHandle?.Stop(allowFadeOut);
    }

    public string GoToState(string stateName)
    {
        EnsureInit();
        if (activeHandle is null || !activeHandle.IsAlive)
        {
            return WriteErrorJson("No live effect handle.", 1, 1);
        }
        try
        {
            activeHandle.GoTo(stateName);
            return WriteJson(w => w.WriteBoolean("ok", true));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return WriteErrorJson(ex.Message, 1, 1);
        }
    }

    public void TriggerEvent(string name)
    {
        EnsureInit();
        if (!string.IsNullOrEmpty(name))
        {
            activeHandle?.Trigger(name);
        }
    }

    public string SetParam(string name, float value)
    {
        EnsureInit();
        if (activeHandle is null || !activeHandle.IsAlive)
        {
            return WriteErrorJson("No live effect handle.", 1, 1);
        }
        try
        {
            activeHandle.SetParam(name, value);
            if (name == "intensity") intensityScale = value;
            else if (name == "rate") rateScale = value;
            return WriteJson(w => w.WriteBoolean("ok", true));
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return WriteErrorJson(ex.Message, 1, 1);
        }
    }

    public void SetParticleDensity(float density)
    {
        EnsureInit();
        if (!float.IsFinite(density)) throw new ArgumentOutOfRangeException(nameof(density));
        particleDensity = Math.Clamp(density, 0.1f, 1.0f);
        RimLumenRenderHost.ParticleDensityScale = particleDensity;
    }

    public void SetTargetWorld(float x, float y, float z)
    {
        EnsureInit();
        targetWorld = new Vector3(x, y, z);
    }

    public void SetOrbitMotion(bool enabled, float radius, float angularSpeed)
    {
        EnsureInit();
        orbitEnabled = enabled;
        orbitRadius = float.IsFinite(radius) && radius >= 0f ? radius : 1.5f;
        orbitAngularSpeed = float.IsFinite(angularSpeed) ? angularSpeed : 2.4f;
        if (!enabled) orbitAngle = 0f;
    }

    public string StepFrame(float dt, int viewportWidth, int viewportHeight, ReadOnlySpan<double> viewColMajor, ReadOnlySpan<double> projColMajor)
    {
        EnsureInit();
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        if (orbitEnabled && dt > 0f)
        {
            orbitAngle = (orbitAngle + orbitAngularSpeed * dt) % MathF.Tau;
        }

        Matrix4x4 view = viewColMajor.Length == 16 ? MatrixFromColumnMajor(viewColMajor) : DefaultTopDown2DView;
        Matrix4x4 proj = projColMajor.Length == 16 ? MatrixFromColumnMajor(projColMajor) : DefaultOrthoProj;

        long t0 = Stopwatch.GetTimestamp();
        frameIndex++;
        var context = new LumenCaptureContext
        {
            CurrentMapIndex = 0,
            FrameIndex = frameIndex,
            DeltaTime = dt,
            UnscaledDeltaTime = dt,
            ViewportWidth = Math.Max(1, viewportWidth),
            ViewportHeight = Math.Max(1, viewportHeight),
            ViewMatrix = view,
            ProjectionMatrix = proj,
            LumenRenderPassId = 0,
            RendersMap = true,
        };

        LumenSceneBootstrap.Service.CaptureMainThread(in context);
        LumenSceneBootstrap.Simulator.CaptureMainThread(in context);
        ribbonBackend!.Build(in context);
        IReadOnlyList<WebDistortionSnapshot> distortions = distortionBackend!.EvaluateActiveSnapshots(0);
        float screenDimCoverage = screenDimBackend!.EvaluateCoverage(0);
        long t1 = Stopwatch.GetTimestamp();
        double simMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;

        CpuParticleSimulator sim = LumenSceneBootstrap.Simulator;
        IReadOnlyList<VisualPointLight> lights = LumenSceneBootstrap.Service.Lights;
        bool alive = activeHandle?.IsAlive ?? false;
        if (!alive) currentState = null;

        string json = WriteJson(w =>
        {
            w.WriteBoolean("alive", alive);
            w.WriteNumber("liveParticles", sim.LiveParticleCount);
            if (currentState is not null) w.WriteString("currentState", currentState);
            else w.WriteNull("currentState");
            w.WriteNumber("simMs", Math.Round(simMs, 4));
            w.WriteNumber("simVertexCount", sim.Vertices.Length);
            w.WriteNumber("simIndexCount", sim.Indices.Length);
            w.WriteNumber("ribVertexCount", ribbonBackend.Vertices.Length);
            w.WriteNumber("ribIndexCount", ribbonBackend.Indices.Length);
            w.WriteNumber("screenDimCoverage", screenDimCoverage);

            WriteSubBatches(w, "simBatches", sim.SubBatches);
            WriteSubBatches(w, "ribBatches", ribbonBackend.SubBatches);

            w.WriteStartArray("pointLights");
            foreach (VisualPointLight l in lights)
            {
                w.WriteStartObject();
                w.WriteNumber("x", l.X);
                w.WriteNumber("y", l.Y);
                w.WriteNumber("z", l.Z);
                w.WriteNumber("radius", l.Radius);
                w.WriteNumber("r", l.Light.X);
                w.WriteNumber("g", l.Light.Y);
                w.WriteNumber("b", l.Light.Z);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("distortions");
            foreach (WebDistortionSnapshot d in distortions)
            {
                float planarScale = MathF.Max(d.WorldScale.X, d.WorldScale.Z);
                w.WriteStartObject();
                w.WriteString("kind", d.Kind.ToString());
                w.WriteNumber("x", d.WorldPosition.X);
                w.WriteNumber("y", d.WorldPosition.Y);
                w.WriteNumber("z", d.WorldPosition.Z);
                w.WriteNumber("radius", d.Radius * planarScale);
                w.WriteNumber("strength", d.Strength);
                w.WriteNumber("thickness", d.Thickness * planarScale);
                w.WriteNumber("frequency", d.Frequency);
                w.WriteNumber("elapsed", d.Elapsed);
                w.WriteStartArray("ringRadii");
                w.WriteNumberValue(d.Frame.RingRadii.X * planarScale);
                w.WriteNumberValue(d.Frame.RingRadii.Y * planarScale);
                w.WriteNumberValue(d.Frame.RingRadii.Z * planarScale);
                w.WriteNumberValue(d.Frame.RingRadii.W * planarScale);
                w.WriteEndArray();
                w.WriteStartArray("ringAmplitudes");
                w.WriteNumberValue(d.Frame.RingAmplitudes.X);
                w.WriteNumberValue(d.Frame.RingAmplitudes.Y);
                w.WriteNumberValue(d.Frame.RingAmplitudes.Z);
                w.WriteNumberValue(d.Frame.RingAmplitudes.W);
                w.WriteEndArray();
                w.WriteNumber("hazeAmplitude", d.Frame.HazeAmplitude);
                w.WriteNumber("hazePhase", d.Frame.HazePhase);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("events");
            foreach (WebLogEvent ev in frameEvents)
            {
                w.WriteStartObject();
                w.WriteString("kind", ev.Kind);
                w.WriteString("message", ev.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        frameEvents.Clear();
        return json;
    }

    public byte[] GetSimVerticesBytes()
    {
        EnsureInit();
        return MemoryMarshal.AsBytes(LumenSceneBootstrap.Simulator.Vertices).ToArray();
    }

    public byte[] GetSimIndicesBytes()
    {
        EnsureInit();
        return MemoryMarshal.AsBytes(LumenSceneBootstrap.Simulator.Indices).ToArray();
    }

    public byte[] GetRibbonVerticesBytes()
    {
        EnsureInit();
        return MemoryMarshal.AsBytes(ribbonBackend!.Vertices).ToArray();
    }

    public byte[] GetRibbonIndicesBytes()
    {
        EnsureInit();
        return MemoryMarshal.AsBytes(ribbonBackend!.Indices).ToArray();
    }

    private void PlayAssetInternal(EffectAsset asset, string format, string? initialState)
    {
        StopActiveHandleImmediate();
        orbitAngle = 0f;
        draftProvider.Current = asset;
        activeAsset = asset;
        activeSourceFormat = format;
        currentState = initialState ?? asset.InitialState ?? (asset.States.Length > 0 ? asset.States[0].Name : null);
        frameEvents.Clear();

        bool needsTarget = false;
        bool needsDepthRef = false;
        foreach (EffectNodeSpec node in asset.Nodes)
        {
            if (node.Ribbon is { EndAtTarget: true }) needsTarget = true;
            if (node.DepthMask != LumenDepthMask.None) needsDepthRef = true;
        }

        ILumenEffectHandle handle = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
        {
            EffectId = ActiveDraftProviderKey,
            MapIndex = 0,
            OriginWorld = Vector3.Zero,
            TargetWorld = needsTarget ? targetWorld : null,
            DepthReferenceZ = needsDepthRef ? depthReferenceZ : null,
            InitialState = initialState,
            Follow = FollowProvider,
        });

        handle.Notify += name => frameEvents.Add(new WebLogEvent("Notify", name));
        handle.InteractionNotify += n => frameEvents.Add(new WebLogEvent("Interaction", $"{n.EventName} ({n.Trigger})"));
        handle.StateChanged += name =>
        {
            currentState = name;
            frameEvents.Add(new WebLogEvent("StateChanged", name));
        };
        handle.Finished += () => frameEvents.Add(new WebLogEvent("Finished", asset.EffectId));
        if (intensityScale != 1f) handle.SetParam("intensity", intensityScale);
        if (rateScale != 1f) handle.SetParam("rate", rateScale);
        activeHandle = handle;
    }

    private bool FollowProvider(out Vector3 worldPosition, out float rotationDegrees)
    {
        if (!orbitEnabled)
        {
            worldPosition = Vector3.Zero;
            rotationDegrees = 0f;
            return false;
        }
        // DistortionPylon orbits nodes via Follow yaw around origin; standalone trail nodes also translate on a circle when radius > 0.
        bool isPylonOrbit = activeAsset?.EffectId == RimLumenDemoEffects.DistortionPylonId;
        if (isPylonOrbit)
        {
            worldPosition = Vector3.Zero;
            rotationDegrees = orbitAngle * (180f / MathF.PI);
        }
        else
        {
            worldPosition = new Vector3(
                MathF.Cos(orbitAngle) * orbitRadius,
                0f,
                MathF.Sin(orbitAngle) * orbitRadius);
            rotationDegrees = -orbitAngle * (180f / MathF.PI);
        }
        return true;
    }

    private void StopActiveHandleImmediate()
    {
        if (activeHandle is not null)
        {
            activeHandle.Stop(allowFadeOut: false);
            activeHandle.Dispose();
            activeHandle = null;
        }
        FlushCapture();
    }

    private void FlushCapture()
    {
        frameIndex++;
        var flush = new LumenCaptureContext
        {
            CurrentMapIndex = 0,
            FrameIndex = frameIndex,
            DeltaTime = 0f,
            UnscaledDeltaTime = 0f,
            ViewportWidth = 1280,
            ViewportHeight = 720,
            ViewMatrix = DefaultTopDown2DView,
            ProjectionMatrix = DefaultOrthoProj,
            LumenRenderPassId = 0,
            RendersMap = false,
        };
        LumenSceneBootstrap.Service.CaptureMainThread(in flush);
        LumenSceneBootstrap.Simulator.CaptureMainThread(in flush);
        ribbonBackend!.Build(in flush);
    }

    private string SerializeAssetResponse(EffectAsset asset, string format, bool compact)
    {
        string norm = NormalizeFormat(format);
        string content;
        var warnings = new List<string>();
        if (norm == "xml")
        {
            content = WebEffectSerializer.ToXml(asset, out warnings, compact);
        }
        else
        {
            content = WebEffectSerializer.ToJson(asset, compact);
        }

        // Round-trip sanity check: exported output must pass the real parser identically.
        if (!TryParseAsset(content, norm, out _, out string? roundTripErr, out int line, out int col))
        {
            return WriteErrorJson($"Serialized output failed round-trip check: {roundTripErr}", line, col);
        }

        return WriteJson(w =>
        {
            w.WriteBoolean("ok", true);
            w.WriteString("format", norm);
            w.WriteString("content", content);
            WriteStringArray(w, "warnings", warnings);
        });
    }

    private static bool TryParseAsset(string source, string format, out EffectAsset? asset, out string? error, out int line, out int column)
    {
        line = 1;
        column = 1;
        string norm = NormalizeFormat(format);
        try
        {
            if (norm == "xml")
            {
                asset = WebDirectXmlLoader.ParseSingle(source, "web-draft.xml");
            }
            else if (norm == "json")
            {
                asset = LumenJsonAssetLoader.Parse(source, "web-draft");
            }
            else
            {
                asset = null;
                error = $"Unsupported source format '{format}'. Expected 'json' or 'xml'.";
                return false;
            }
            error = null;
            return true;
        }
        catch (XmlAssetParseException ex)
        {
            asset = null;
            error = ex.Message;
            line = ex.LineNumber;
            column = ex.LinePosition;
            return false;
        }
        catch (InvalidDataException ex)
        {
            asset = null;
            error = ex.Message;
            if (ex.InnerException is JsonException jsonEx)
            {
                if (jsonEx.LineNumber is { } l) line = (int)l + 1;
                if (jsonEx.BytePositionInLine is { } c) column = (int)c + 1;
            }
            return false;
        }
    }

    private static string WriteAssetMetadataJson(EffectAsset asset, string format)
    {
        List<string> textures = CollectTextureKeys(asset);
        return WriteJson(w =>
        {
            w.WriteBoolean("ok", true);
            w.WriteString("format", format);
            w.WriteString("effectId", asset.EffectId);
            w.WriteNumber("defaultDuration", asset.DefaultDuration);
            if (asset.InitialState is not null) w.WriteString("initialState", asset.InitialState);
            else w.WriteNull("initialState");
            if (asset.StopState is not null) w.WriteString("stopState", asset.StopState);
            else w.WriteNull("stopState");

            w.WriteStartArray("states");
            foreach (EffectStateSpec s in asset.States)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteNumber("duration", s.Duration ?? asset.DefaultDuration);
                w.WriteBoolean("loop", s.Loop);
                if (s.Next is not null) w.WriteString("next", s.Next);
                else w.WriteNull("next");
                WriteStringArray(w, "nodes", s.Nodes);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            for (int i = 0; i < asset.Nodes.Length; i++)
            {
                EffectNodeSpec n = asset.Nodes[i];
                w.WriteStartObject();
                w.WriteNumber("index", i);
                w.WriteString("name", n.Name);
                w.WriteNumber("parentIndex", n.ParentIndex);
                w.WriteString("spacePlane", n.SpacePlane.ToString());
                w.WriteBoolean("hasEmitter", n.Emitter is not null);
                w.WriteBoolean("hasPointLight", n.PointLight is not null);
                w.WriteBoolean("hasDistortion", n.Distortion is not null);
                w.WriteBoolean("hasRibbon", n.Ribbon is not null);
                w.WriteBoolean("hasScreenDim", n.ScreenDim is not null);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("timelineEvents");
            foreach (TimelineEventSpec ev in asset.TimelineEvents)
            {
                w.WriteStartObject();
                w.WriteNumber("time", ev.TimeSeconds);
                w.WriteString("eventName", ev.EventName);
                w.WriteNumber("targetNodeIndex", ev.TargetNodeIndex);
                if (ev.State is not null) w.WriteString("state", ev.State);
                else w.WriteNull("state");
                w.WriteBoolean("notify", ev.Notify);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            WriteStringArray(w, "textureKeys", textures);
        });
    }

    private static List<string> CollectTextureKeys(EffectAsset asset)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (EffectNodeSpec n in asset.Nodes)
        {
            if (n.Emitter?.TextureKey is { Length: > 0 } ek && seen.Add(ek)) keys.Add(ek);
            if (n.Emitter?.Trails?.TextureKey is { Length: > 0 } tk && seen.Add(tk)) keys.Add(tk);
            if (n.Ribbon?.TextureKey is { Length: > 0 } rk && seen.Add(rk)) keys.Add(rk);
        }
        return keys;
    }

    private static void WriteSubBatches(Utf8JsonWriter w, string propertyName, IReadOnlyList<L1SubBatch> batches)
    {
        w.WriteStartArray(propertyName);
        foreach (L1SubBatch b in batches)
        {
            w.WriteStartObject();
            if (b.TextureKey is not null) w.WriteString("textureKey", b.TextureKey);
            else w.WriteNull("textureKey");
            w.WriteString("blendMode", b.BlendMode.ToString());
            w.WriteNumber("indexStart", b.IndexStart);
            w.WriteNumber("indexCount", b.IndexCount);
            w.WriteNumber("softParticleFadeDistance", b.SoftParticleFadeDistance);
            w.WriteStartArray("materialTint");
            w.WriteNumberValue(b.MaterialTint.X);
            w.WriteNumberValue(b.MaterialTint.Y);
            w.WriteNumberValue(b.MaterialTint.Z);
            w.WriteNumberValue(b.MaterialTint.W);
            w.WriteEndArray();
            w.WriteString("colorSpace", b.ColorSpace.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static Matrix4x4 MatrixFromColumnMajor(ReadOnlySpan<double> c) => new(
        (float)c[0], (float)c[4], (float)c[8],  (float)c[12],
        (float)c[1], (float)c[5], (float)c[9],  (float)c[13],
        (float)c[2], (float)c[6], (float)c[10], (float)c[14],
        (float)c[3], (float)c[7], (float)c[11], (float)c[15]);

    private static string NormalizeFormat(string? format)
        => string.Equals(format, "xml", StringComparison.OrdinalIgnoreCase) ? "xml"
         : string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json"
         : format ?? string.Empty;

    private static string WriteErrorJson(string error, int line, int column)
        => WriteJson(w =>
        {
            w.WriteBoolean("ok", false);
            w.WriteString("error", error);
            w.WriteNumber("line", line);
            w.WriteNumber("column", column);
        });

    private static void WriteStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (string item in items) w.WriteStringValue(item);
        w.WriteEndArray();
    }

    private static string WriteJson(Action<Utf8JsonWriter> writeFields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeFields(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void RecordHostMessage(string kind, string message)
    {
        frameEvents.Add(new WebLogEvent(kind, message));
    }

    private void EnsureInit()
    {
        if (!initialized) Initialize();
    }
}
