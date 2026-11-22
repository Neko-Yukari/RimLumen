using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RimLumen.Core.Contracts;

namespace RimLumen.WebSim;

/// <summary>
/// Zero-reflection serializer converting validated <see cref="EffectAsset"/> instances back to
/// <c>.lumen.json</c> and standalone <c>LumenEffectDef</c> XML, preserving exact round-trip semantics.
/// </summary>
public static class WebEffectSerializer
{
    public static string ToJson(EffectAsset asset, bool compact = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("effectId", asset.EffectId);
            if (!compact || asset.DefaultDuration != 2f)
                writer.WriteNumber("defaultDuration", asset.DefaultDuration);

            writer.WriteStartArray("nodes");
            foreach (EffectNodeSpec node in asset.Nodes)
            {
                WriteNodeJson(writer, node, compact);
            }
            writer.WriteEndArray();

            if (!compact || asset.States.Length > 0)
            {
                writer.WriteStartArray("states");
                foreach (EffectStateSpec state in asset.States)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", state.Name);
                    if (!compact || state.Nodes.Length > 0)
                    {
                        writer.WriteStartArray("nodes");
                        foreach (string nodeName in state.Nodes) writer.WriteStringValue(nodeName);
                        writer.WriteEndArray();
                    }
                    if (state.Duration is { } d) writer.WriteNumber("duration", d);
                    if (!compact || state.Loop) writer.WriteBoolean("loop", state.Loop);
                    if (!compact || state.Next is not null)
                    {
                        if (state.Next is not null) writer.WriteString("next", state.Next);
                        else writer.WriteNull("next");
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (!compact || asset.InitialState is not null)
            {
                if (asset.InitialState is not null) writer.WriteString("initialState", asset.InitialState);
                else writer.WriteNull("initialState");
            }

            if (!compact || asset.StopState is not null)
            {
                if (asset.StopState is not null) writer.WriteString("stopState", asset.StopState);
                else writer.WriteNull("stopState");
            }

            if (!compact || asset.TimelineEvents.Length > 0)
            {
                writer.WriteStartArray("timelineEvents");
                foreach (TimelineEventSpec ev in asset.TimelineEvents)
                {
                    writer.WriteStartObject();
                    if (!compact || ev.TimeSeconds != 0f) writer.WriteNumber("time", ev.TimeSeconds);
                    writer.WriteString("eventName", ev.EventName);
                    if (!compact || ev.TargetNodeIndex != -1) writer.WriteNumber("targetNodeIndex", ev.TargetNodeIndex);
                    if (!compact || ev.State is not null)
                    {
                        if (ev.State is not null) writer.WriteString("state", ev.State);
                        else writer.WriteNull("state");
                    }
                    if (!compact || ev.Notify) writer.WriteBoolean("notify", ev.Notify);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (!compact || asset.InteractionEvents.Length > 0)
            {
                writer.WriteStartArray("interactionEvents");
                foreach (InteractionEventSpec ev in asset.InteractionEvents)
                {
                    writer.WriteStartObject();
                    writer.WriteString("trigger", ev.Trigger.ToString());
                    writer.WriteString("eventName", ev.EventName);
                    if (!compact || ev.Radius != 0f) writer.WriteNumber("radius", ev.Radius);
                    if (!compact || ev.AnchorNodeIndex != -1) writer.WriteNumber("anchorNodeIndex", ev.AnchorNodeIndex);
                    if (!compact || ev.MinCursorSpeed != 0f) writer.WriteNumber("minCursorSpeed", ev.MinCursorSpeed);
                    if (ev.MaxCursorSpeed is { } maxSpeed) writer.WriteNumber("maxCursorSpeed", maxSpeed);
                    else if (!compact) writer.WriteNull("maxCursorSpeed");
                    if (!compact || ev.HoldSeconds != 0f) writer.WriteNumber("holdSeconds", ev.HoldSeconds);
                    if (!compact || ev.MinExplosionStrength != 0f) writer.WriteNumber("minExplosionStrength", ev.MinExplosionStrength);
                    if (!compact || ev.CooldownSeconds != 0f) writer.WriteNumber("cooldownSeconds", ev.CooldownSeconds);
                    if (!compact || ev.State is not null)
                    {
                        if (ev.State is not null) writer.WriteString("state", ev.State);
                        else writer.WriteNull("state");
                    }
                    if (!compact || ev.GoToState is not null)
                    {
                        if (ev.GoToState is not null) writer.WriteString("goToState", ev.GoToState);
                        else writer.WriteNull("goToState");
                    }
                    if (!compact || ev.TargetNodeIndex != -1) writer.WriteNumber("targetNodeIndex", ev.TargetNodeIndex);
                    if (!compact || ev.Notify) writer.WriteBoolean("notify", ev.Notify);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ToXml(EffectAsset asset, out List<string> warnings, bool compact = true)
    {
        warnings = new List<string>();
        var defEl = new XElement(WebDirectXmlLoader.FullDefTypeName);
        defEl.Add(new XElement("defName", asset.EffectId));
        if (!compact || asset.DefaultDuration != 2f)
            defEl.Add(new XElement("defaultDuration", FormatFloat(asset.DefaultDuration)));

        var nodesEl = new XElement("nodes");
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            nodesEl.Add(WriteNodeXml(asset.Nodes[i], i, compact, warnings));
        }
        defEl.Add(nodesEl);

        if (!compact || asset.States.Length > 0)
        {
            var statesEl = new XElement("states");
            foreach (EffectStateSpec state in asset.States)
            {
                var li = new XElement("li");
                li.Add(new XElement("name", EscapeStringXml(state.Name)));
                if (!compact || state.Nodes.Length > 0)
                {
                    var stateNodesEl = new XElement("nodes");
                    foreach (string n in state.Nodes) stateNodesEl.Add(new XElement("li", EscapeStringXml(n)));
                    li.Add(stateNodesEl);
                }
                if (state.Duration is { } d) li.Add(new XElement("duration", FormatFloat(d)));
                if (!compact || state.Loop) li.Add(new XElement("loop", state.Loop ? "true" : "false"));
                if (!compact || state.Next is not null)
                    li.Add(NullableStringEl("next", state.Next));
                statesEl.Add(li);
            }
            defEl.Add(statesEl);
        }

        if (!compact || asset.InitialState is not null)
            defEl.Add(NullableStringEl("initialState", asset.InitialState));
        if (!compact || asset.StopState is not null)
            defEl.Add(NullableStringEl("stopState", asset.StopState));

        if (!compact || asset.TimelineEvents.Length > 0)
        {
            var evsEl = new XElement("timelineEvents");
            foreach (TimelineEventSpec ev in asset.TimelineEvents)
            {
                var li = new XElement("li");
                if (!compact || ev.TimeSeconds != 0f) li.Add(new XElement("time", FormatFloat(ev.TimeSeconds)));
                li.Add(new XElement("eventName", EscapeStringXml(ev.EventName)));
                if (!compact || ev.TargetNodeIndex != -1) li.Add(new XElement("targetNodeIndex", ev.TargetNodeIndex.ToString(CultureInfo.InvariantCulture)));
                if (!compact || ev.State is not null) li.Add(NullableStringEl("state", ev.State));
                if (!compact || ev.Notify) li.Add(new XElement("notify", ev.Notify ? "true" : "false"));
                evsEl.Add(li);
            }
            defEl.Add(evsEl);
        }

        if (!compact || asset.InteractionEvents.Length > 0)
        {
            var evsEl = new XElement("interactionEvents");
            foreach (InteractionEventSpec ev in asset.InteractionEvents)
            {
                var li = new XElement("li");
                li.Add(new XElement("trigger", ev.Trigger.ToString()));
                li.Add(new XElement("eventName", EscapeStringXml(ev.EventName)));
                if (!compact || ev.Radius != 0f) li.Add(new XElement("radius", FormatFloat(ev.Radius)));
                if (!compact || ev.AnchorNodeIndex != -1) li.Add(new XElement("anchorNodeIndex", ev.AnchorNodeIndex.ToString(CultureInfo.InvariantCulture)));
                if (!compact || ev.MinCursorSpeed != 0f) li.Add(new XElement("minCursorSpeed", FormatFloat(ev.MinCursorSpeed)));
                if (ev.MaxCursorSpeed is { } maxSpeed) li.Add(new XElement("maxCursorSpeed", FormatFloat(maxSpeed)));
                else if (!compact) li.Add(new XElement("maxCursorSpeed", new XAttribute("IsNull", "true")));
                if (!compact || ev.HoldSeconds != 0f) li.Add(new XElement("holdSeconds", FormatFloat(ev.HoldSeconds)));
                if (!compact || ev.MinExplosionStrength != 0f) li.Add(new XElement("minExplosionStrength", FormatFloat(ev.MinExplosionStrength)));
                if (!compact || ev.CooldownSeconds != 0f) li.Add(new XElement("cooldownSeconds", FormatFloat(ev.CooldownSeconds)));
                if (!compact || ev.State is not null) li.Add(NullableStringEl("state", ev.State));
                if (!compact || ev.GoToState is not null) li.Add(NullableStringEl("goToState", ev.GoToState));
                if (!compact || ev.TargetNodeIndex != -1) li.Add(new XElement("targetNodeIndex", ev.TargetNodeIndex.ToString(CultureInfo.InvariantCulture)));
                if (!compact || ev.Notify) li.Add(new XElement("notify", ev.Notify ? "true" : "false"));
                evsEl.Add(li);
            }
            defEl.Add(evsEl);
        }

        var doc = new XDocument(new XElement("Defs", defEl));
        return doc.ToString(SaveOptions.None);
    }

    private static void WriteNodeJson(Utf8JsonWriter w, EffectNodeSpec node, bool compact)
    {
        w.WriteStartObject();
        w.WriteString("name", node.Name);
        if (!compact || node.ParentIndex != -1) w.WriteNumber("parentIndex", node.ParentIndex);
        if (!compact || node.SpacePlane != LumenSpacePlane.GroundXZ) w.WriteString("spacePlane", node.SpacePlane.ToString());
        if (!compact || node.DepthMask != LumenDepthMask.None) w.WriteString("depthMask", node.DepthMask.ToString());
        if (!compact || node.LocalPosition != Vector3.Zero) WriteVec3Json(w, "localPosition", node.LocalPosition);
        if (!compact || node.LocalRotation != Quaternion.Identity)
        {
            if (TryExtractPureYawDegrees(node.LocalRotation, out float yaw))
                w.WriteNumber("localYawDegrees", yaw);
            else
                WriteQuatJson(w, "localRotation", node.LocalRotation);
        }
        if (!compact || node.LocalScale != Vector3.One)
        {
            if (node.LocalScale.X == node.LocalScale.Y && node.LocalScale.Y == node.LocalScale.Z)
                w.WriteNumber("localScale", node.LocalScale.X);
            else
                WriteVec3Json(w, "localScale", node.LocalScale);
        }
        if (node.Emitter is { } emitter) WriteEmitterJson(w, node.Name, emitter, compact);
        if (node.PointLight is { } light) WritePointLightJson(w, light, compact);
        if (node.Distortion is { } distortion) WriteDistortionJson(w, distortion, compact);
        if (node.Ribbon is { } ribbon) WriteRibbonJson(w, ribbon, compact);
        if (node.ScreenDim is { } dim) WriteScreenDimJson(w, dim, compact);
        w.WriteEndObject();
    }

    private static void WriteEmitterJson(Utf8JsonWriter w, string nodeName, EmitterSpec e, bool compact)
    {
        w.WriteStartObject("emitter");
        if (!compact || e.Name != nodeName) w.WriteString("name", e.Name);
        if (!compact || e.PreferredTier != LumenParticleTier.L2_GpuCompute) w.WriteString("preferredTier", e.PreferredTier.ToString());
        if (!compact || e.MaxParticles != 1024) w.WriteNumber("maxParticles", e.MaxParticles);
        if (!compact || e.Duration != 1f) w.WriteNumber("duration", e.Duration);
        if (!compact || e.Looping) w.WriteBoolean("looping", e.Looping);
        if (!compact || !e.WorldSpaceSimulation) w.WriteBoolean("worldSpaceSimulation", e.WorldSpaceSimulation);
        if (!compact || e.RateOverTime != 0f) w.WriteNumber("rateOverTime", e.RateOverTime);
        if (!compact || e.Bursts.Length > 0)
        {
            w.WriteStartArray("bursts");
            foreach (ParticleBurstSpec b in e.Bursts)
            {
                w.WriteStartObject();
                w.WriteNumber("time", b.Time);
                w.WriteNumber("count", b.Count);
                if (!compact || b.Probability != 1f) w.WriteNumber("probability", b.Probability);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (!compact || e.Shape != LumenShapeKind.Point) w.WriteString("shape", e.Shape.ToString());
        if (!compact || e.ShapeExtents != Vector3.One) WriteVec3Json(w, "shapeExtents", e.ShapeExtents);
        if (!compact || e.ShapePosition != Vector3.Zero) WriteVec3Json(w, "shapePosition", e.ShapePosition);
        if (!compact || e.ShapeRotation != Quaternion.Identity) WriteQuatJson(w, "shapeRotation", e.ShapeRotation);
        if (!compact || e.ShapeScale != Vector3.One) WriteVec3Json(w, "shapeScale", e.ShapeScale);
        if (!compact || e.ConeAngleDegrees != 25f) w.WriteNumber("coneAngleDegrees", e.ConeAngleDegrees);
        if (!compact || e.ConeLength != 5f) w.WriteNumber("coneLength", e.ConeLength);
        if (!compact || e.DonutRadius != 0.2f) w.WriteNumber("donutRadius", e.DonutRadius);
        if (!compact || e.RadiusThickness != 1f) w.WriteNumber("radiusThickness", e.RadiusThickness);
        if (!compact || e.BoxThickness != Vector3.Zero) WriteVec3Json(w, "boxThickness", e.BoxThickness);
        if (!compact || e.ArcDegrees != 360f) w.WriteNumber("arcDegrees", e.ArcDegrees);
        if (!compact || e.ArcSpread != 0f) w.WriteNumber("arcSpread", e.ArcSpread);
        if (!compact || e.RadiusSpread != 0f) w.WriteNumber("radiusSpread", e.RadiusSpread);
        if (!compact || e.RandomDirectionAmount != 0f) w.WriteNumber("randomDirectionAmount", e.RandomDirectionAmount);
        if (!compact || e.SphericalDirectionAmount != 0f) w.WriteNumber("sphericalDirectionAmount", e.SphericalDirectionAmount);
        if (!compact || e.RandomPositionAmount != 0f) w.WriteNumber("randomPositionAmount", e.RandomPositionAmount);
        if (!compact || e.StartLifetimeMin != 1f) w.WriteNumber("startLifetimeMin", e.StartLifetimeMin);
        if (!compact || e.StartLifetimeMax != 1f) w.WriteNumber("startLifetimeMax", e.StartLifetimeMax);
        if (!compact || e.StartSpeedMin != 0f) w.WriteNumber("startSpeedMin", e.StartSpeedMin);
        if (!compact || e.StartSpeedMax != 0f) w.WriteNumber("startSpeedMax", e.StartSpeedMax);
        if (!compact || e.StartSizeMin != 1f) w.WriteNumber("startSizeMin", e.StartSizeMin);
        if (!compact || e.StartSizeMax != 1f) w.WriteNumber("startSizeMax", e.StartSizeMax);
        if (!compact || e.StartColor != Vector4.One) WriteColorJson(w, "startColor", e.StartColor);
        if (!compact || e.ColorSpace != LumenColorSpace.Stored) w.WriteString("colorSpace", e.ColorSpace.ToString());
        if (!compact || e.MaterialTint != Vector4.One) WriteColorJson(w, "materialTint", e.MaterialTint);
        if (!compact || !IsConstantOneCurve(e.SizeOverLifetime)) WriteCurveJson(w, "sizeOverLifetime", e.SizeOverLifetime);
        if (!compact || !IsConstantOneGradient(e.ColorOverLifetime)) WriteGradientJson(w, "colorOverLifetime", e.ColorOverLifetime);
        if (!compact || e.ForceFields.Length > 0)
        {
            w.WriteStartArray("forceFields");
            foreach (ForceFieldSpec ff in e.ForceFields)
            {
                w.WriteStartObject();
                w.WriteString("kind", ff.Kind.ToString());
                if (!compact || ff.CenterOrAxis != Vector3.Zero) WriteVec3Json(w, "centerOrAxis", ff.CenterOrAxis);
                if (!compact || ff.Parameters != Vector3.Zero) WriteVec3Json(w, "parameters", ff.Parameters);
                if (!compact || ff.Radius != 5f) w.WriteNumber("radius", ff.Radius);
                w.WriteNumber("strength", ff.Strength);
                if (!compact || ff.Frequency != 1f) w.WriteNumber("frequency", ff.Frequency);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (!compact || e.EnvironmentChannels != (LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor))
            w.WriteString("environmentChannels", e.EnvironmentChannels.ToString());
        if (!compact || e.EnvironmentForceScale != 1f) w.WriteNumber("environmentForceScale", e.EnvironmentForceScale);
        if (!compact || !IsDefaultCollision(e.Collision))
        {
            w.WriteStartObject("collision");
            if (!compact || e.Collision.Mode != LumenCollisionMode.None) w.WriteString("mode", e.Collision.Mode.ToString());
            if (!compact || e.Collision.Bounce != 1f) w.WriteNumber("bounce", e.Collision.Bounce);
            if (!compact || e.Collision.Dampen != 0f) w.WriteNumber("dampen", e.Collision.Dampen);
            if (!compact || e.Collision.LifetimeLoss != 0f) w.WriteNumber("lifetimeLoss", e.Collision.LifetimeLoss);
            if (!compact || e.Collision.KillUnderRoof) w.WriteBoolean("killUnderRoof", e.Collision.KillUnderRoof);
            w.WriteEndObject();
        }
        if (!compact || e.SubEmitters.Length > 0)
        {
            w.WriteStartArray("subEmitters");
            foreach (SubEmitterSpec sub in e.SubEmitters)
            {
                w.WriteStartObject();
                w.WriteString("trigger", sub.Trigger.ToString());
                w.WriteNumber("targetNodeIndex", sub.TargetNodeIndex);
                w.WriteNumber("count", sub.Count);
                if (!compact || sub.Probability != 1f) w.WriteNumber("probability", sub.Probability);
                if (!compact || sub.Inherit != LumenSubEmitterInherit.None) w.WriteString("inherit", sub.Inherit.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (e.Trails is { } trails) WriteTrailsJson(w, trails, compact);
        if (!compact || e.TextureKey is not null)
        {
            if (e.TextureKey is not null) w.WriteString("textureKey", e.TextureKey);
            else w.WriteNull("textureKey");
        }
        if (!compact || e.FlipbookTilesX != 1) w.WriteNumber("flipbookTilesX", e.FlipbookTilesX);
        if (!compact || e.FlipbookTilesY != 1) w.WriteNumber("flipbookTilesY", e.FlipbookTilesY);
        if (!compact || e.FlipbookCycles != 1f) w.WriteNumber("flipbookCycles", e.FlipbookCycles);
        if (!compact || e.BlendMode != LumenBlendMode.Additive) w.WriteString("blendMode", e.BlendMode.ToString());
        if (!compact || e.BillboardMode != LumenBillboardMode.Camera) w.WriteString("billboardMode", e.BillboardMode.ToString());
        if (!compact || e.VelocityStretchFactor != 0f) w.WriteNumber("velocityStretchFactor", e.VelocityStretchFactor);
        if (!compact || e.LengthScale != 1f) w.WriteNumber("lengthScale", e.LengthScale);
        if (!compact || e.SoftParticleFadeDistance != 0.5f) w.WriteNumber("softParticleFadeDistance", e.SoftParticleFadeDistance);
        if (!compact || e.HdrIntensity != 1f) w.WriteNumber("hdrIntensity", e.HdrIntensity);
        if (!compact || e.KillBelowFloor) w.WriteBoolean("killBelowFloor", e.KillBelowFloor);
        w.WriteEndObject();
    }

    private static void WritePointLightJson(Utf8JsonWriter w, PointLightSpec light, bool compact)
    {
        w.WriteStartObject("pointLight");
        if (!compact || light.GlowRadius != 5f) w.WriteNumber("glowRadius", light.GlowRadius);
        if (!compact || light.ColorRgb != Vector3.One) WriteColorJson(w, "color", new Vector4(light.ColorRgb, 1f));
        if (light.IntensityOverTime is { } curve)
        {
            if (IsConstantShape(curve))
            {
                if (!compact || curve.Multiplier != 1f) w.WriteNumber("intensity", curve.Multiplier);
            }
            else
            {
                if (!compact || curve.Multiplier != 1f) w.WriteNumber("intensity", curve.Multiplier);
                WriteCurveKeysArrayJson(w, "intensityOverTime", curve.Keys);
            }
        }
        w.WriteEndObject();
    }

    private static void WriteDistortionJson(Utf8JsonWriter w, DistortionSpec d, bool compact)
    {
        w.WriteStartObject("distortion");
        w.WriteString("kind", d.Kind.ToString());
        w.WriteNumber("radius", d.Radius);
        w.WriteNumber("strength", d.Strength);
        if (!compact || d.Thickness != 0f) w.WriteNumber("thickness", d.Thickness);
        if (!compact || d.Speed != 0f) w.WriteNumber("speed", d.Speed);
        if (!compact || d.Frequency != 0f) w.WriteNumber("frequency", d.Frequency);
        if (!compact || d.Duration != 0f) w.WriteNumber("duration", d.Duration);
        if (d.StrengthOverTime is { } curve) WriteCurveJson(w, "strengthOverTime", curve);
        w.WriteEndObject();
    }

    private static void WriteScreenDimJson(Utf8JsonWriter w, ScreenDimSpec d, bool compact)
    {
        w.WriteStartObject("screenDim");
        w.WriteNumber("strength", d.Strength);
        if (!compact || d.Duration != 0f) w.WriteNumber("duration", d.Duration);
        if (d.StrengthOverTime is { } curve) WriteCurveJson(w, "strengthOverTime", curve);
        w.WriteEndObject();
    }

    private static void WriteRibbonJson(Utf8JsonWriter w, RibbonSpec r, bool compact)
    {
        w.WriteStartObject("ribbon");
        w.WriteString("mode", r.Mode.ToString());
        if (!compact || r.Lifetime != 0f) w.WriteNumber("lifetime", r.Lifetime);
        if (!compact || r.MinVertexDistance != 0f) w.WriteNumber("minVertexDistance", r.MinVertexDistance);
        if (!compact || r.MaxPoints != 0) w.WriteNumber("maxPoints", r.MaxPoints);
        if (r.EndLocal is { } end) WriteVec3Json(w, "endLocal", end);
        else if (!compact) w.WriteNull("endLocal");
        if (!compact || r.EndAtTarget) w.WriteBoolean("endAtTarget", r.EndAtTarget);
        if (!compact || r.Segments != 0) w.WriteNumber("segments", r.Segments);
        if (!compact || r.NoiseAmplitude != 0f) w.WriteNumber("noiseAmplitude", r.NoiseAmplitude);
        if (!compact || r.NoiseFrequency != 0f) w.WriteNumber("noiseFrequency", r.NoiseFrequency);
        if (!compact || r.Width != 1f) w.WriteNumber("width", r.Width);
        if (!compact || !IsConstantOneCurve(r.WidthOverLength)) WriteCurveJson(w, "widthOverLength", r.WidthOverLength);
        if (!compact || !IsConstantOneGradient(r.ColorOverLength)) WriteGradientJson(w, "colorOverLength", r.ColorOverLength);
        if (!compact || r.HdrIntensity != 1f) w.WriteNumber("hdrIntensity", r.HdrIntensity);
        if (!compact || r.TextureKey is not null)
        {
            if (r.TextureKey is not null) w.WriteString("textureKey", r.TextureKey);
            else w.WriteNull("textureKey");
        }
        if (!compact || r.BlendMode != LumenBlendMode.Additive) w.WriteString("blendMode", r.BlendMode.ToString());
        if (!compact || r.TextureMode != LumenRibbonTextureMode.Stretch) w.WriteString("textureMode", r.TextureMode.ToString());
        if (!compact || r.Alignment != LumenRibbonAlignment.View) w.WriteString("alignment", r.Alignment.ToString());
        if (!compact || r.SoftParticleFadeDistance != 0.5f) w.WriteNumber("softParticleFadeDistance", r.SoftParticleFadeDistance);
        w.WriteEndObject();
    }

    private static void WriteTrailsJson(Utf8JsonWriter w, ParticleTrailSpec t, bool compact)
    {
        w.WriteStartObject("trails");
        if (!compact || t.Ratio != 1f) w.WriteNumber("ratio", t.Ratio);
        if (!compact || t.Lifetime != 1f) w.WriteNumber("lifetime", t.Lifetime);
        if (!compact || t.MinVertexDistance != 0.2f) w.WriteNumber("minVertexDistance", t.MinVertexDistance);
        if (!compact || t.MaxPoints != 32) w.WriteNumber("maxPoints", t.MaxPoints);
        if (!compact || !t.DieWithParticles) w.WriteBoolean("dieWithParticles", t.DieWithParticles);
        if (!compact || !t.WorldSpace) w.WriteBoolean("worldSpace", t.WorldSpace);
        if (!compact || !t.SizeAffectsWidth) w.WriteBoolean("sizeAffectsWidth", t.SizeAffectsWidth);
        if (!compact || t.SizeAffectsLifetime) w.WriteBoolean("sizeAffectsLifetime", t.SizeAffectsLifetime);
        if (!compact || !t.InheritParticleColor) w.WriteBoolean("inheritParticleColor", t.InheritParticleColor);
        if (!compact || t.Width != 1f) w.WriteNumber("width", t.Width);
        if (!compact || !IsConstantOneCurve(t.WidthOverTrail)) WriteCurveJson(w, "widthOverTrail", t.WidthOverTrail);
        if (!compact || !IsConstantOneGradient(t.ColorOverTrail)) WriteGradientJson(w, "colorOverTrail", t.ColorOverTrail);
        if (!compact || !IsConstantOneGradient(t.ColorOverLifetime)) WriteGradientJson(w, "colorOverLifetime", t.ColorOverLifetime);
        if (!compact || t.TextureKey is not null)
        {
            if (t.TextureKey is not null) w.WriteString("textureKey", t.TextureKey);
            else w.WriteNull("textureKey");
        }
        if (!compact || t.MaterialTint != Vector4.One) WriteColorJson(w, "materialTint", t.MaterialTint);
        if (!compact || t.ColorSpace != LumenColorSpace.Stored) w.WriteString("colorSpace", t.ColorSpace.ToString());
        if (!compact || t.BlendMode != LumenBlendMode.Additive) w.WriteString("blendMode", t.BlendMode.ToString());
        if (!compact || t.TextureMode != LumenRibbonTextureMode.Stretch) w.WriteString("textureMode", t.TextureMode.ToString());
        if (!compact || t.Alignment != LumenRibbonAlignment.View) w.WriteString("alignment", t.Alignment.ToString());
        if (!compact || t.HdrIntensity != 1f) w.WriteNumber("hdrIntensity", t.HdrIntensity);
        if (!compact || t.SoftParticleFadeDistance != 0.5f) w.WriteNumber("softParticleFadeDistance", t.SoftParticleFadeDistance);
        w.WriteEndObject();
    }

    private static XElement WriteNodeXml(EffectNodeSpec node, int nodeIndex, bool compact, List<string> warnings)
    {
        var li = new XElement("li");
        li.Add(new XElement("name", EscapeStringXml(node.Name)));
        if (!compact || node.ParentIndex != -1) li.Add(new XElement("parentIndex", node.ParentIndex.ToString(CultureInfo.InvariantCulture)));
        if (!compact || node.SpacePlane != LumenSpacePlane.GroundXZ) li.Add(new XElement("spacePlane", node.SpacePlane.ToString()));
        if (!compact || node.DepthMask != LumenDepthMask.None) li.Add(new XElement("depthMask", node.DepthMask.ToString()));
        if (!compact || node.LocalPosition != Vector3.Zero) li.Add(new XElement("localPosition", FormatVec3(node.LocalPosition)));

        if (!compact || node.LocalRotation != Quaternion.Identity)
        {
            if (!TryExtractPureYawDegrees(node.LocalRotation, out float yaw))
            {
                warnings.Add($"nodes[{nodeIndex}] ('{node.Name}'): XML <LumenNodeXml> only supports Y-axis <localYawDegrees> (got 3D quaternion {FormatQuat(node.LocalRotation)}); projected to Y yaw {FormatFloat(yaw)}°.");
            }
            if (!compact || yaw != 0f)
                li.Add(new XElement("localYawDegrees", FormatFloat(yaw)));
        }

        if (!compact || node.LocalScale != Vector3.One)
        {
            float scalar = node.LocalScale.X;
            if (node.LocalScale.X != node.LocalScale.Y || node.LocalScale.Y != node.LocalScale.Z)
            {
                warnings.Add($"nodes[{nodeIndex}] ('{node.Name}'): XML <localScale> is a scalar float (got non-uniform ({FormatFloat(node.LocalScale.X)}, {FormatFloat(node.LocalScale.Y)}, {FormatFloat(node.LocalScale.Z)})); wrote {FormatFloat(scalar)}.");
            }
            li.Add(new XElement("localScale", FormatFloat(scalar)));
        }

        string pathPrefix = $"nodes[{nodeIndex}] ('{node.Name}')";
        if (node.Emitter is { } emitter) li.Add(WriteEmitterXml(node.Name, emitter, pathPrefix + ".emitter", compact, warnings));
        if (node.PointLight is { } light) li.Add(WritePointLightXml(light, compact));
        if (node.Distortion is { } distortion) li.Add(WriteDistortionXml(distortion, pathPrefix + ".distortion", compact, warnings));
        if (node.Ribbon is { } ribbon) li.Add(WriteRibbonXml(ribbon, pathPrefix + ".ribbon", compact, warnings));
        if (node.ScreenDim is { } dim) li.Add(WriteScreenDimXml(dim, pathPrefix + ".screenDim", compact, warnings));
        return li;
    }

    private static XElement WriteEmitterXml(string nodeName, EmitterSpec e, string path, bool compact, List<string> warnings)
    {
        var el = new XElement("emitter");
        if (!compact || e.Name != nodeName) el.Add(new XElement("name", EscapeStringXml(e.Name)));
        if (!compact || e.PreferredTier != LumenParticleTier.L2_GpuCompute) el.Add(new XElement("preferredTier", e.PreferredTier.ToString()));
        if (!compact || e.MaxParticles != 1024) el.Add(new XElement("maxParticles", e.MaxParticles.ToString(CultureInfo.InvariantCulture)));
        if (!compact || e.Duration != 1f) el.Add(new XElement("duration", FormatFloat(e.Duration)));
        if (!compact || e.Looping) el.Add(new XElement("looping", e.Looping ? "true" : "false"));
        if (!compact || !e.WorldSpaceSimulation) el.Add(new XElement("worldSpaceSimulation", e.WorldSpaceSimulation ? "true" : "false"));
        if (!compact || e.RateOverTime != 0f) el.Add(new XElement("rateOverTime", FormatFloat(e.RateOverTime)));
        if (!compact || e.Bursts.Length > 0)
        {
            var burstsEl = new XElement("bursts");
            foreach (ParticleBurstSpec b in e.Bursts)
            {
                var bli = new XElement("li");
                bli.Add(new XElement("time", FormatFloat(b.Time)));
                bli.Add(new XElement("count", b.Count.ToString(CultureInfo.InvariantCulture)));
                if (!compact || b.Probability != 1f) bli.Add(new XElement("probability", FormatFloat(b.Probability)));
                burstsEl.Add(bli);
            }
            el.Add(burstsEl);
        }
        if (!compact || e.Shape != LumenShapeKind.Point) el.Add(new XElement("shape", e.Shape.ToString()));
        if (!compact || e.ShapeExtents != Vector3.One) el.Add(new XElement("shapeExtents", FormatVec3(e.ShapeExtents)));
        if (!compact || e.ShapePosition != Vector3.Zero) el.Add(new XElement("shapePosition", FormatVec3(e.ShapePosition)));
        if (!compact || e.ShapeRotation != Quaternion.Identity) el.Add(new XElement("shapeRotation", FormatQuat(e.ShapeRotation)));
        if (!compact || e.ShapeScale != Vector3.One) el.Add(new XElement("shapeScale", FormatVec3(e.ShapeScale)));
        if (!compact || e.ConeAngleDegrees != 25f) el.Add(new XElement("coneAngleDegrees", FormatFloat(e.ConeAngleDegrees)));
        if (!compact || e.ConeLength != 5f) el.Add(new XElement("coneLength", FormatFloat(e.ConeLength)));
        if (!compact || e.DonutRadius != 0.2f) el.Add(new XElement("donutRadius", FormatFloat(e.DonutRadius)));
        if (!compact || e.RadiusThickness != 1f) el.Add(new XElement("radiusThickness", FormatFloat(e.RadiusThickness)));
        if (!compact || e.BoxThickness != Vector3.Zero) el.Add(new XElement("boxThickness", FormatVec3(e.BoxThickness)));
        if (!compact || e.ArcDegrees != 360f) el.Add(new XElement("arcDegrees", FormatFloat(e.ArcDegrees)));
        if (!compact || e.ArcSpread != 0f) el.Add(new XElement("arcSpread", FormatFloat(e.ArcSpread)));
        if (!compact || e.RadiusSpread != 0f) el.Add(new XElement("radiusSpread", FormatFloat(e.RadiusSpread)));
        if (!compact || e.RandomDirectionAmount != 0f) el.Add(new XElement("randomDirectionAmount", FormatFloat(e.RandomDirectionAmount)));
        if (!compact || e.SphericalDirectionAmount != 0f) el.Add(new XElement("sphericalDirectionAmount", FormatFloat(e.SphericalDirectionAmount)));
        if (!compact || e.RandomPositionAmount != 0f) el.Add(new XElement("randomPositionAmount", FormatFloat(e.RandomPositionAmount)));
        if (!compact || e.StartLifetimeMin != 1f) el.Add(new XElement("startLifetimeMin", FormatFloat(e.StartLifetimeMin)));
        if (!compact || e.StartLifetimeMax != 1f) el.Add(new XElement("startLifetimeMax", FormatFloat(e.StartLifetimeMax)));
        if (!compact || e.StartSpeedMin != 0f) el.Add(new XElement("startSpeedMin", FormatFloat(e.StartSpeedMin)));
        if (!compact || e.StartSpeedMax != 0f) el.Add(new XElement("startSpeedMax", FormatFloat(e.StartSpeedMax)));
        if (!compact || e.StartSizeMin != 1f) el.Add(new XElement("startSizeMin", FormatFloat(e.StartSizeMin)));
        if (!compact || e.StartSizeMax != 1f) el.Add(new XElement("startSizeMax", FormatFloat(e.StartSizeMax)));
        if (!compact || e.StartColor != Vector4.One) el.Add(new XElement("startColor", FormatColor(e.StartColor)));
        if (!compact || e.ColorSpace != LumenColorSpace.Stored) el.Add(new XElement("colorSpace", e.ColorSpace.ToString()));
        if (!compact || e.MaterialTint != Vector4.One) el.Add(new XElement("materialTint", FormatColor(e.MaterialTint)));
        if (!compact || !IsConstantOneCurve(e.SizeOverLifetime)) el.Add(WriteCurveXml("sizeOverLifetime", e.SizeOverLifetime, path + ".sizeOverLifetime", compact, warnings));
        if (!compact || !IsConstantOneGradient(e.ColorOverLifetime)) el.Add(WriteGradientXml("colorOverLifetime", e.ColorOverLifetime));
        if (!compact || e.ForceFields.Length > 0)
        {
            var ffsEl = new XElement("forceFields");
            foreach (ForceFieldSpec ff in e.ForceFields)
            {
                var fli = new XElement("li");
                fli.Add(new XElement("kind", ff.Kind.ToString()));
                if (!compact || ff.CenterOrAxis != Vector3.Zero) fli.Add(new XElement("centerOrAxis", FormatVec3(ff.CenterOrAxis)));
                if (!compact || ff.Parameters != Vector3.Zero) fli.Add(new XElement("parameters", FormatVec3(ff.Parameters)));
                if (!compact || ff.Radius != 5f) fli.Add(new XElement("radius", FormatFloat(ff.Radius)));
                fli.Add(new XElement("strength", FormatFloat(ff.Strength)));
                if (!compact || ff.Frequency != 1f) fli.Add(new XElement("frequency", FormatFloat(ff.Frequency)));
                ffsEl.Add(fli);
            }
            el.Add(ffsEl);
        }
        if (!compact || e.EnvironmentChannels != (LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor))
            el.Add(new XElement("environmentChannels", e.EnvironmentChannels.ToString()));
        if (!compact || e.EnvironmentForceScale != 1f) el.Add(new XElement("environmentForceScale", FormatFloat(e.EnvironmentForceScale)));
        if (!compact || !IsDefaultCollision(e.Collision))
        {
            var colEl = new XElement("collision");
            if (!compact || e.Collision.Mode != LumenCollisionMode.None) colEl.Add(new XElement("mode", e.Collision.Mode.ToString()));
            if (!compact || e.Collision.Bounce != 1f) colEl.Add(new XElement("bounce", FormatFloat(e.Collision.Bounce)));
            if (!compact || e.Collision.Dampen != 0f) colEl.Add(new XElement("dampen", FormatFloat(e.Collision.Dampen)));
            if (!compact || e.Collision.LifetimeLoss != 0f) colEl.Add(new XElement("lifetimeLoss", FormatFloat(e.Collision.LifetimeLoss)));
            if (!compact || e.Collision.KillUnderRoof) colEl.Add(new XElement("killUnderRoof", e.Collision.KillUnderRoof ? "true" : "false"));
            el.Add(colEl);
        }
        if (!compact || e.SubEmitters.Length > 0)
        {
            var subsEl = new XElement("subEmitters");
            foreach (SubEmitterSpec sub in e.SubEmitters)
            {
                var sli = new XElement("li");
                sli.Add(new XElement("trigger", sub.Trigger.ToString()));
                sli.Add(new XElement("targetNodeIndex", sub.TargetNodeIndex.ToString(CultureInfo.InvariantCulture)));
                sli.Add(new XElement("count", sub.Count.ToString(CultureInfo.InvariantCulture)));
                if (!compact || sub.Probability != 1f) sli.Add(new XElement("probability", FormatFloat(sub.Probability)));
                if (!compact || sub.Inherit != LumenSubEmitterInherit.None) sli.Add(new XElement("inherit", sub.Inherit.ToString()));
                subsEl.Add(sli);
            }
            el.Add(subsEl);
        }
        if (e.Trails is { } trails) el.Add(WriteTrailsXml(trails, path + ".trails", compact, warnings));
        if (!compact || e.TextureKey is not null) el.Add(NullableStringEl("textureKey", e.TextureKey));
        if (!compact || e.FlipbookTilesX != 1) el.Add(new XElement("flipbookTilesX", e.FlipbookTilesX.ToString(CultureInfo.InvariantCulture)));
        if (!compact || e.FlipbookTilesY != 1) el.Add(new XElement("flipbookTilesY", e.FlipbookTilesY.ToString(CultureInfo.InvariantCulture)));
        if (!compact || e.FlipbookCycles != 1f) el.Add(new XElement("flipbookCycles", FormatFloat(e.FlipbookCycles)));
        if (!compact || e.BlendMode != LumenBlendMode.Additive) el.Add(new XElement("blendMode", e.BlendMode.ToString()));
        if (!compact || e.BillboardMode != LumenBillboardMode.Camera) el.Add(new XElement("billboardMode", e.BillboardMode.ToString()));
        if (!compact || e.VelocityStretchFactor != 0f) el.Add(new XElement("velocityStretchFactor", FormatFloat(e.VelocityStretchFactor)));
        if (!compact || e.LengthScale != 1f) el.Add(new XElement("lengthScale", FormatFloat(e.LengthScale)));
        if (!compact || e.SoftParticleFadeDistance != 0.5f) el.Add(new XElement("softParticleFadeDistance", FormatFloat(e.SoftParticleFadeDistance)));
        if (!compact || e.HdrIntensity != 1f) el.Add(new XElement("hdrIntensity", FormatFloat(e.HdrIntensity)));
        if (!compact || e.KillBelowFloor) el.Add(new XElement("killBelowFloor", e.KillBelowFloor ? "true" : "false"));
        return el;
    }

    private static XElement WritePointLightXml(PointLightSpec light, bool compact)
    {
        var el = new XElement("pointLight");
        if (!compact || light.ColorRgb != Vector3.One) el.Add(new XElement("color", FormatColor(new Vector4(light.ColorRgb, 1f))));
        if (!compact || light.GlowRadius != 5f) el.Add(new XElement("radiusCells", FormatFloat(light.GlowRadius)));
        if (light.IntensityOverTime is { } curve)
        {
            if (IsConstantShape(curve))
            {
                if (!compact || curve.Multiplier != 1f) el.Add(new XElement("intensity", FormatFloat(curve.Multiplier)));
            }
            else
            {
                if (!compact || curve.Multiplier != 1f) el.Add(new XElement("intensity", FormatFloat(curve.Multiplier)));
                el.Add(WriteCurveKeysXml("intensityOverTime", curve.Keys, 1f, compact));
            }
        }
        return el;
    }

    private static XElement WriteDistortionXml(DistortionSpec d, string path, bool compact, List<string> warnings)
    {
        var el = new XElement("distortion");
        el.Add(new XElement("kind", d.Kind.ToString()));
        el.Add(new XElement("radius", FormatFloat(d.Radius)));
        el.Add(new XElement("strength", FormatFloat(d.Strength)));
        if (!compact || d.Thickness != 0f) el.Add(new XElement("thickness", FormatFloat(d.Thickness)));
        if (!compact || d.Speed != 0f) el.Add(new XElement("speed", FormatFloat(d.Speed)));
        if (!compact || d.Frequency != 0f) el.Add(new XElement("frequency", FormatFloat(d.Frequency)));
        if (!compact || d.Duration != 0f) el.Add(new XElement("duration", FormatFloat(d.Duration)));
        if (d.StrengthOverTime is { } curve) el.Add(WriteCurveXml("strengthOverTime", curve, path + ".strengthOverTime", compact, warnings));
        return el;
    }

    private static XElement WriteScreenDimXml(ScreenDimSpec d, string path, bool compact, List<string> warnings)
    {
        var el = new XElement("screenDim");
        el.Add(new XElement("strength", FormatFloat(d.Strength)));
        if (!compact || d.Duration != 0f) el.Add(new XElement("duration", FormatFloat(d.Duration)));
        if (d.StrengthOverTime is { } curve) el.Add(WriteCurveXml("strengthOverTime", curve, path + ".strengthOverTime", compact, warnings));
        return el;
    }

    private static XElement WriteRibbonXml(RibbonSpec r, string path, bool compact, List<string> warnings)
    {
        var el = new XElement("ribbon");
        el.Add(new XElement("mode", r.Mode.ToString()));
        if (!compact || r.Lifetime != 0f) el.Add(new XElement("lifetime", FormatFloat(r.Lifetime)));
        if (!compact || r.MinVertexDistance != 0f) el.Add(new XElement("minVertexDistance", FormatFloat(r.MinVertexDistance)));
        if (!compact || r.MaxPoints != 0) el.Add(new XElement("maxPoints", r.MaxPoints.ToString(CultureInfo.InvariantCulture)));
        if (r.EndLocal is { } end) el.Add(new XElement("endLocal", FormatVec3(end)));
        else if (!compact) el.Add(new XElement("endLocal", new XAttribute("IsNull", "true")));
        if (!compact || r.EndAtTarget) el.Add(new XElement("endAtTarget", r.EndAtTarget ? "true" : "false"));
        if (!compact || r.Segments != 0) el.Add(new XElement("segments", r.Segments.ToString(CultureInfo.InvariantCulture)));
        if (!compact || r.NoiseAmplitude != 0f) el.Add(new XElement("noiseAmplitude", FormatFloat(r.NoiseAmplitude)));
        if (!compact || r.NoiseFrequency != 0f) el.Add(new XElement("noiseFrequency", FormatFloat(r.NoiseFrequency)));
        if (!compact || r.Width != 1f) el.Add(new XElement("width", FormatFloat(r.Width)));
        if (!compact || !IsConstantOneCurve(r.WidthOverLength)) el.Add(WriteCurveXml("widthOverLength", r.WidthOverLength, path + ".widthOverLength", compact, warnings));
        if (!compact || !IsConstantOneGradient(r.ColorOverLength)) el.Add(WriteGradientXml("colorOverLength", r.ColorOverLength));
        if (!compact || r.HdrIntensity != 1f) el.Add(new XElement("hdrIntensity", FormatFloat(r.HdrIntensity)));
        if (!compact || r.TextureKey is not null) el.Add(NullableStringEl("textureKey", r.TextureKey));
        if (!compact || r.BlendMode != LumenBlendMode.Additive) el.Add(new XElement("blendMode", r.BlendMode.ToString()));
        if (!compact || r.TextureMode != LumenRibbonTextureMode.Stretch) el.Add(new XElement("textureMode", r.TextureMode.ToString()));
        if (!compact || r.Alignment != LumenRibbonAlignment.View) el.Add(new XElement("alignment", r.Alignment.ToString()));
        if (!compact || r.SoftParticleFadeDistance != 0.5f) el.Add(new XElement("softParticleFadeDistance", FormatFloat(r.SoftParticleFadeDistance)));
        return el;
    }

    private static XElement WriteTrailsXml(ParticleTrailSpec t, string path, bool compact, List<string> warnings)
    {
        var el = new XElement("trails");
        if (!compact || t.Ratio != 1f) el.Add(new XElement("ratio", FormatFloat(t.Ratio)));
        if (!compact || t.Lifetime != 1f) el.Add(new XElement("lifetime", FormatFloat(t.Lifetime)));
        if (!compact || t.MinVertexDistance != 0.2f) el.Add(new XElement("minVertexDistance", FormatFloat(t.MinVertexDistance)));
        if (!compact || t.MaxPoints != 32) el.Add(new XElement("maxPoints", t.MaxPoints.ToString(CultureInfo.InvariantCulture)));
        if (!compact || !t.DieWithParticles) el.Add(new XElement("dieWithParticles", t.DieWithParticles ? "true" : "false"));
        if (!compact || !t.WorldSpace) el.Add(new XElement("worldSpace", t.WorldSpace ? "true" : "false"));
        if (!compact || !t.SizeAffectsWidth) el.Add(new XElement("sizeAffectsWidth", t.SizeAffectsWidth ? "true" : "false"));
        if (!compact || t.SizeAffectsLifetime) el.Add(new XElement("sizeAffectsLifetime", t.SizeAffectsLifetime ? "true" : "false"));
        if (!compact || !t.InheritParticleColor) el.Add(new XElement("inheritParticleColor", t.InheritParticleColor ? "true" : "false"));
        if (!compact || t.Width != 1f) el.Add(new XElement("width", FormatFloat(t.Width)));
        if (!compact || !IsConstantOneCurve(t.WidthOverTrail)) el.Add(WriteCurveXml("widthOverTrail", t.WidthOverTrail, path + ".widthOverTrail", compact, warnings));
        if (!compact || !IsConstantOneGradient(t.ColorOverTrail)) el.Add(WriteGradientXml("colorOverTrail", t.ColorOverTrail));
        if (!compact || !IsConstantOneGradient(t.ColorOverLifetime)) el.Add(WriteGradientXml("colorOverLifetime", t.ColorOverLifetime));
        if (!compact || t.TextureKey is not null) el.Add(NullableStringEl("textureKey", t.TextureKey));
        if (!compact || t.MaterialTint != Vector4.One) el.Add(new XElement("materialTint", FormatColor(t.MaterialTint)));
        if (!compact || t.ColorSpace != LumenColorSpace.Stored) el.Add(new XElement("colorSpace", t.ColorSpace.ToString()));
        if (!compact || t.BlendMode != LumenBlendMode.Additive) el.Add(new XElement("blendMode", t.BlendMode.ToString()));
        if (!compact || t.TextureMode != LumenRibbonTextureMode.Stretch) el.Add(new XElement("textureMode", t.TextureMode.ToString()));
        if (!compact || t.Alignment != LumenRibbonAlignment.View) el.Add(new XElement("alignment", t.Alignment.ToString()));
        if (!compact || t.HdrIntensity != 1f) el.Add(new XElement("hdrIntensity", FormatFloat(t.HdrIntensity)));
        if (!compact || t.SoftParticleFadeDistance != 0.5f) el.Add(new XElement("softParticleFadeDistance", FormatFloat(t.SoftParticleFadeDistance)));
        return el;
    }

    private static void WriteVec3Json(Utf8JsonWriter w, string name, Vector3 v)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(v.X);
        w.WriteNumberValue(v.Y);
        w.WriteNumberValue(v.Z);
        w.WriteEndArray();
    }

    private static void WriteQuatJson(Utf8JsonWriter w, string name, Quaternion q)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(q.X);
        w.WriteNumberValue(q.Y);
        w.WriteNumberValue(q.Z);
        w.WriteNumberValue(q.W);
        w.WriteEndArray();
    }

    private static void WriteColorJson(Utf8JsonWriter w, string name, Vector4 c)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(c.X);
        w.WriteNumberValue(c.Y);
        w.WriteNumberValue(c.Z);
        w.WriteNumberValue(c.W);
        w.WriteEndArray();
    }

    private static void WriteCurveJson(Utf8JsonWriter w, string name, ScalarCurve curve)
    {
        if (curve.Multiplier == 1f)
        {
            WriteCurveKeysArrayJson(w, name, curve.Keys);
            return;
        }
        w.WriteStartObject(name);
        WriteCurveKeysArrayJson(w, "keys", curve.Keys);
        w.WriteNumber("multiplier", curve.Multiplier);
        w.WriteEndObject();
    }

    private static void WriteCurveKeysArrayJson(Utf8JsonWriter w, string name, CurveKey[] keys)
    {
        w.WriteStartArray(name);
        foreach (CurveKey k in keys)
        {
            w.WriteStartArray();
            w.WriteNumberValue(k.Time);
            w.WriteNumberValue(k.Value);
            if (k.InTangent != 0f || k.OutTangent != 0f)
            {
                w.WriteNumberValue(k.InTangent);
                w.WriteNumberValue(k.OutTangent);
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static void WriteGradientJson(Utf8JsonWriter w, string name, ColorGradient gradient)
    {
        w.WriteStartArray(name);
        foreach (ColorStop s in gradient.Stops)
        {
            w.WriteStartArray();
            w.WriteNumberValue(s.Time);
            w.WriteStartArray();
            w.WriteNumberValue(s.Rgba.X);
            w.WriteNumberValue(s.Rgba.Y);
            w.WriteNumberValue(s.Rgba.Z);
            w.WriteNumberValue(s.Rgba.W);
            w.WriteEndArray();
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static XElement WriteCurveXml(string tagName, ScalarCurve curve, string path, bool compact, List<string> warnings)
    {
        if (curve.Multiplier != 1f)
        {
            warnings.Add($"{path}: XML <ScalarCurve> has no 'multiplier' field (got {FormatFloat(curve.Multiplier)}); folded multiplier into keyframe values and tangents.");
        }
        return WriteCurveKeysXml(tagName, curve.Keys, curve.Multiplier, compact);
    }

    private static XElement WriteCurveKeysXml(string tagName, CurveKey[] keys, float scale, bool compact)
    {
        var el = new XElement(tagName);
        foreach (CurveKey k in keys)
        {
            var li = new XElement("li");
            li.Add(new XElement("time", FormatFloat(k.Time)));
            li.Add(new XElement("value", FormatFloat(k.Value * scale)));
            float inT = k.InTangent * scale;
            float outT = k.OutTangent * scale;
            if (!compact || inT != 0f) li.Add(new XElement("inTangent", FormatFloat(inT)));
            if (!compact || outT != 0f) li.Add(new XElement("outTangent", FormatFloat(outT)));
            el.Add(li);
        }
        return el;
    }

    private static XElement WriteGradientXml(string tagName, ColorGradient gradient)
    {
        var el = new XElement(tagName);
        foreach (ColorStop s in gradient.Stops)
        {
            var li = new XElement("li");
            li.Add(new XElement("time", FormatFloat(s.Time)));
            li.Add(new XElement("color", FormatColor(s.Rgba)));
            el.Add(li);
        }
        return el;
    }

    private static XElement NullableStringEl(string name, string? value)
        => value is null
            ? new XElement(name, new XAttribute("IsNull", "true"))
            : new XElement(name, EscapeStringXml(value));

    private static string EscapeStringXml(string value) => value.Replace("\n", "\\n");

    private static string FormatFloat(float v) => v == 0f ? "0" : v.ToString(CultureInfo.InvariantCulture);

    private static string FormatVec3(Vector3 v) => $"({FormatFloat(v.X)}, {FormatFloat(v.Y)}, {FormatFloat(v.Z)})";

    private static string FormatQuat(Quaternion q) => $"({FormatFloat(q.X)}, {FormatFloat(q.Y)}, {FormatFloat(q.Z)}, {FormatFloat(q.W)})";

    private static string FormatColor(Vector4 c) => $"({FormatFloat(c.X)}, {FormatFloat(c.Y)}, {FormatFloat(c.Z)}, {FormatFloat(c.W)})";

    private static bool TryExtractPureYawDegrees(Quaternion q, out float yawDegrees)
    {
        float yawRad = 2f * MathF.Atan2(q.Y, q.W);
        yawDegrees = yawRad * (180f / MathF.PI);
        if (MathF.Abs(q.X) > 1e-5f || MathF.Abs(q.Z) > 1e-5f) return false;
        Quaternion rebuilt = yawDegrees == 0f
            ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * (MathF.PI / 180f));
        return MathF.Abs(rebuilt.Y - q.Y) <= 1e-5f && MathF.Abs(rebuilt.W - q.W) <= 1e-5f;
    }

    private static bool IsConstantShape(ScalarCurve curve)
        => curve.Keys.Length == 1 && curve.Keys[0] == new CurveKey(0f, 1f, 0f, 0f);

    private static bool IsConstantOneCurve(ScalarCurve curve)
        => curve.Multiplier == 1f && IsConstantShape(curve);

    private static bool IsConstantOneGradient(ColorGradient g)
        => g.Stops.Length == 1 && g.Stops[0].Time == 0f && g.Stops[0].Rgba == Vector4.One;

    private static bool IsDefaultCollision(CollisionSpec c)
        => c.Mode == LumenCollisionMode.None && c.Bounce == 1f && c.Dampen == 0f && c.LifetimeLoss == 0f && !c.KillUnderRoof;
}
