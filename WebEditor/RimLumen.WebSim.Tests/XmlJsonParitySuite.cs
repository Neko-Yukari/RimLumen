using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Demo;
using RimLumen.Core.Scene;

namespace RimLumen.WebSim.Tests;

internal static class XmlJsonParitySuite
{
    public static void Register(List<(string Name, Action Run)> cases)
    {
        cases.Add(("XmlJsonParity.AllSixBuiltInDemosMatchFieldForField", AllSixBuiltInDemosMatchFieldForField));
        cases.Add(("XmlJsonParity.DocsApiJsonExamplesRoundTripXml", DocsApiJsonExamplesRoundTripXml));
        cases.Add(("XmlJsonParity.DocsApiXmlExamplesRoundTripJson", DocsApiXmlExamplesRoundTripJson));
        cases.Add(("XmlJsonParity.FullVfxContractsXmlEqualsJson", FullVfxContractsXmlEqualsJson));
        cases.Add(("XmlJsonParity.LossyXmlConversionsEmitWarnings", LossyXmlConversionsEmitWarnings));
    }

    // Exact canonical structural serializer matching Tests/RimLumen.Core.Tests/VfxContracts/VfxContractParitySuite.cs:280-292.
    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        Enum or bool or int or uint => value.ToString()!,
        Vector3 v => $"({Describe(v.X)},{Describe(v.Y)},{Describe(v.Z)})",
        Vector4 v => $"({Describe(v.X)},{Describe(v.Y)},{Describe(v.Z)},{Describe(v.W)})",
        Quaternion q => $"({Describe(q.X)},{Describe(q.Y)},{Describe(q.Z)},{Describe(q.W)})",
        Array array => "[" + string.Join(", ", array.Cast<object?>().Select(Describe)) + "]",
        _ => value.GetType().Name + "{" + string.Join(", ", value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => $"{property.Name}={Describe(property.GetValue(value))}")) + "}",
    };

    private static void AssertSameAsset(EffectAsset expected, EffectAsset actual, string label)
    {
        string left = Describe(expected);
        string right = Describe(actual);
        if (left != right)
        {
            throw new InvalidOperationException($"{label} differs.\nExpected: {left}\nActual:   {right}");
        }
    }

    private static void AllSixBuiltInDemosMatchFieldForField()
    {
        LumenSceneBootstrap.EnsureInitialized();
        RimLumenDemoEffects.EnsureRegistered();
        string[] demoIds =
        {
            RimLumenDemoEffects.FireworkGoldId,
            RimLumenDemoEffects.FireworkCyanId,
            RimLumenDemoEffects.FireworkMagentaId,
            RimLumenDemoEffects.ParticleFountainId,
            RimLumenDemoEffects.StreetLampId,
            RimLumenDemoEffects.DistortionPylonId,
        };

        foreach (string id in demoIds)
        {
            TestAssert.True(LumenSceneBootstrap.Catalog.TryResolveEffect(id, out EffectAsset? original) && original is not null, $"resolve {id}");
            string json = WebEffectSerializer.ToJson(original!, compact: true);
            EffectAsset fromJson = LumenJsonAssetLoader.Parse(json);
            AssertSameAsset(original!, fromJson, $"{id} JSON compact");

            string jsonFull = WebEffectSerializer.ToJson(original!, compact: false);
            EffectAsset fromJsonFull = LumenJsonAssetLoader.Parse(jsonFull);
            AssertSameAsset(original!, fromJsonFull, $"{id} JSON full");

            string xml = WebEffectSerializer.ToXml(original!, out List<string> warnings, compact: true);
            TestAssert.Equal(0, warnings.Count, $"{id} XML should be lossless (got: {string.Join("; ", warnings)})");
            EffectAsset fromXml = WebDirectXmlLoader.ParseSingle(xml);
            AssertSameAsset(original!, fromXml, $"{id} XML compact");

            string xmlFull = WebEffectSerializer.ToXml(original!, out List<string> warningsFull, compact: false);
            TestAssert.Equal(0, warningsFull.Count, $"{id} XML full lossless");
            EffectAsset fromXmlFull = WebDirectXmlLoader.ParseSingle(xmlFull);
            AssertSameAsset(original!, fromXmlFull, $"{id} XML full");
        }
    }

    private static void DocsApiJsonExamplesRoundTripXml()
    {
        // From docs/api.md:89-161
        const string myFireballJson = """
        {
          "effectId": "MyFireball",
          "defaultDuration": 2.0,
          "nodes": [
            {
              "name": "core",
              "parentIndex": -1,
              "spacePlane": "GroundXZ",
              "depthMask": "None",
              "localPosition": [0, 0.5, 0],
              "localYawDegrees": 0,
              "localScale": 1,
              "emitter": {
                "name": "core",
                "preferredTier": "L2_GpuCompute",
                "maxParticles": 1024,
                "duration": 1.0,
                "looping": false,
                "worldSpaceSimulation": true,
                "rateOverTime": 0,
                "bursts": [[0.0, 200], { "time": 0.5, "count": 100, "probability": 0.5 }],
                "shape": "Point",
                "shapeExtents": [1, 1, 1],
                "coneAngleDegrees": 25,
                "startLifetimeMin": 1.0,
                "startLifetimeMax": 1.0,
                "startSpeedMin": 0,
                "startSpeedMax": 0,
                "startSizeMin": 1.0,
                "startSizeMax": 1.0,
                "startColor": [1, 1, 1, 1],
                "materialTint": [1, 1, 1, 1],
                "colorSpace": "Stored",
                "sizeOverLifetime": [[0, 1], [1, 0]],
                "colorOverLifetime": [[0, [1, 1, 1, 1]], [1, [1, 0.3, 0.1, 0]]],
                "forceFields": [
                  {
                    "kind": "GravityDrag",
                    "centerOrAxis": [0, 0, -1],
                    "parameters": [0.5, 0, 0],
                    "radius": 5,
                    "strength": 9.8,
                    "frequency": 1
                  }
                ],
                "textureKey": null,
                "flipbookTilesX": 1,
                "flipbookTilesY": 1,
                "flipbookCycles": 1,
                "blendMode": "Additive",
                "billboardMode": "Camera",
                "velocityStretchFactor": 0,
                "lengthScale": 1,
                "softParticleFadeDistance": 0.5,
                "hdrIntensity": 1,
                "killBelowFloor": false
              },
              "pointLight": {
                "glowRadius": 5,
                "color": [1, 1, 1],
                "intensity": 1,
                "intensityOverTime": [[0, 1], [1, 0]]
              }
            }
          ],
          "timelineEvents": [
            { "time": 0.5, "eventName": "explode", "targetNodeIndex": 0, "state": null, "notify": false }
          ],
          "interactionEvents": [],
          "states": [],
          "initialState": null,
          "stopState": null
        }
        """;

        // From docs/api.md:260-289
        const string tornadoJson = """
        {
          "effectId": "Tornado",
          "nodes": [
            { "name": "funnel", "spacePlane": "Vertical3D", "emitter": {
                "maxParticles": 2048, "duration": 2, "looping": true, "rateOverTime": 600,
                "shape": "Cone", "coneAngleDegrees": 8, "startLifetime": 1.6, "startSpeed": 4,
                "forceFields": [ { "kind": "Vortex", "centerOrAxis": [0, 1, 0], "parameters": [1.5, 2, 0], "radius": 3, "strength": 6 } ] } },
            { "name": "debris", "spacePlane": "Vertical3D", "emitter": {
                "maxParticles": 256, "duration": 2, "looping": true, "rateOverTime": 40,
                "shape": "CircleXZ", "shapeExtents": 2, "startLifetime": 1.2, "startSpeed": 2 } },
            { "name": "dust", "emitter": {
                "maxParticles": 512, "duration": 0.5, "bursts": [[0, 300]],
                "shape": "CircleXZ", "shapeExtents": 2.5, "startLifetime": 1.0, "startSpeed": 1 } },
            { "name": "collapse", "spacePlane": "Vertical3D", "emitter": {
                "maxParticles": 512, "duration": 0.5, "bursts": [[0, 400]],
                "shape": "Sphere", "startLifetime": 1.0, "startSpeed": 6 } }
          ],
          "states": [
            { "name": "spawn", "nodes": ["dust", "funnel"], "duration": 1.5, "next": "sustain" },
            { "name": "sustain", "nodes": ["funnel", "debris"], "duration": 2, "loop": true },
            { "name": "collapse", "nodes": ["collapse"], "duration": 1.2 }
          ],
          "initialState": "spawn",
          "stopState": "collapse",
          "timelineEvents": [
            { "time": 1.0, "eventName": "touchdown", "state": "spawn", "notify": true },
            { "time": 0.5, "eventName": "gust", "state": "sustain", "targetNodeIndex": 1 },
            { "time": 0.3, "eventName": "impact", "state": "collapse", "notify": true }
          ]
        }
        """;

        foreach ((string name, string json) in new[] { ("MyFireball", myFireballJson), ("Tornado", tornadoJson) })
        {
            EffectAsset fromJson = LumenJsonAssetLoader.Parse(json);
            string xml = WebEffectSerializer.ToXml(fromJson, out List<string> warnings, compact: true);
            TestAssert.Equal(0, warnings.Count, $"{name} XML warnings");
            EffectAsset fromXml = WebDirectXmlLoader.ParseSingle(xml);
            AssertSameAsset(fromJson, fromXml, $"{name} JSON -> XML");

            string reJson = WebEffectSerializer.ToJson(fromXml, compact: true);
            EffectAsset roundJson = LumenJsonAssetLoader.Parse(reJson);
            AssertSameAsset(fromJson, roundJson, $"{name} XML -> JSON");
        }
    }

    private static void DocsApiXmlExamplesRoundTripJson()
    {
        // From docs/api.md:333-380
        const string myTorchBurstXml = """
        <Defs>
          <RimLumen.Core.Assets.LumenEffectDef>
            <defName>MyTorchBurst</defName>
            <nodes>
              <li>
                <name>sparks</name>
                <spacePlane>Vertical3D</spacePlane>
                <heightOffset>0.3</heightOffset>
                <emitter>
                  <preferredTier>L2_GpuCompute</preferredTier>
                  <maxParticles>1500</maxParticles>
                  <duration>1.5</duration>
                  <rateOverTime>400</rateOverTime>
                  <startLifetimeMin>0.5</startLifetimeMin>
                  <startLifetimeMax>1.1</startLifetimeMax>
                  <startSpeedMin>3</startSpeedMin>
                  <startSpeedMax>6</startSpeedMax>
                  <startSizeMin>0.15</startSizeMin>
                  <startSizeMax>0.3</startSizeMax>
                  <hdrIntensity>5</hdrIntensity>
                  <killBelowFloor>false</killBelowFloor>
                  <shape>Cone</shape>
                  <coneAngleDegrees>22</coneAngleDegrees>
                  <forceFields>
                    <li>
                      <kind>GravityDrag</kind>
                      <centerOrAxis>(0, -1, 0)</centerOrAxis>
                      <parameters>(0.6, 0, 0)</parameters>
                      <strength>8</strength>
                    </li>
                  </forceFields>
                </emitter>
                <pointLight>
                  <color>(1, 0.55, 0.15, 1)</color>
                  <radiusCells>6</radiusCells>
                  <intensity>1.4</intensity>
                </pointLight>
              </li>
            </nodes>
            <timelineEvents>
              <li>
                <time>0.75</time>
                <eventName>peak</eventName>
                <notify>true</notify>
              </li>
            </timelineEvents>
          </RimLumen.Core.Assets.LumenEffectDef>
        </Defs>
        """;

        // From docs/api.md:387-425
        const string mySmokeFadeXml = """
        <Defs>
          <RimLumen.Core.Assets.LumenEffectDef>
            <defName>MySmokeFade</defName>
            <defaultDuration>3.0</defaultDuration>
            <nodes>
              <li>
                <name>smoke</name>
                <spacePlane>Vertical3D</spacePlane>
                <heightOffset>0.2</heightOffset>
                <emitter>
                  <maxParticles>128</maxParticles>
                  <duration>2.0</duration>
                  <looping>false</looping>
                  <rateOverTime>24</rateOverTime>
                  <startLifetimeMin>1.8</startLifetimeMin>
                  <startLifetimeMax>2.6</startLifetimeMax>
                  <startSpeedMin>0.35</startSpeedMin>
                  <startSpeedMax>0.65</startSpeedMax>
                  <startSizeMin>0.35</startSizeMin>
                  <startSizeMax>0.55</startSizeMax>
                  <startColor>(0.55, 0.55, 0.58, 0.45)</startColor>
                  <blendMode>AlphaBlend</blendMode>
                  <shape>Cone</shape>
                  <coneAngleDegrees>14</coneAngleDegrees>
                  <sizeOverLifetime>
                    <li><time>0</time><value>0.6</value></li>
                    <li><time>0.5</time><value>1.25</value></li>
                    <li><time>1</time><value>1.8</value></li>
                  </sizeOverLifetime>
                  <colorOverLifetime>
                    <li><time>0</time><color>(1, 1, 1, 0)</color></li>
                    <li><time>0.2</time><color>(1, 1, 1, 1)</color></li>
                    <li><time>1</time><color>(1, 1, 1, 0)</color></li>
                  </colorOverLifetime>
                </emitter>
              </li>
            </nodes>
          </RimLumen.Core.Assets.LumenEffectDef>
        </Defs>
        """;

        // From docs/api.md:430-464
        const string myShockwaveXml = """
        <Defs>
          <RimLumen.Core.Assets.LumenEffectDef>
            <defName>MyShockwaveAndDim</defName>
            <defaultDuration>1.0</defaultDuration>
            <nodes>
              <li>
                <name>shockwave</name>
                <distortion>
                  <kind>RingWave</kind>
                  <radius>6</radius>
                  <strength>0.45</strength>
                  <thickness>0.75</thickness>
                  <speed>12</speed>
                  <duration>0.6</duration>
                  <strengthOverTime>
                    <li><time>0</time><value>1</value></li>
                    <li><time>0.6</time><value>0</value></li>
                  </strengthOverTime>
                </distortion>
              </li>
              <li>
                <name>dim</name>
                <screenDim>
                  <strength>0.5</strength>
                  <duration>0.8</duration>
                  <strengthOverTime>
                    <li><time>0</time><value>0</value></li>
                    <li><time>0.12</time><value>1</value></li>
                    <li><time>0.8</time><value>0</value></li>
                  </strengthOverTime>
                </screenDim>
              </li>
            </nodes>
          </RimLumen.Core.Assets.LumenEffectDef>
        </Defs>
        """;

        foreach ((string name, string xml) in new[] { ("MyTorchBurst", myTorchBurstXml), ("MySmokeFade", mySmokeFadeXml), ("MyShockwaveAndDim", myShockwaveXml) })
        {
            EffectAsset fromXml = WebDirectXmlLoader.ParseSingle(xml);
            string json = WebEffectSerializer.ToJson(fromXml, compact: true);
            EffectAsset fromJson = LumenJsonAssetLoader.Parse(json);
            AssertSameAsset(fromXml, fromJson, $"{name} XML -> JSON");

            string reXml = WebEffectSerializer.ToXml(fromJson, out List<string> warnings, compact: true);
            TestAssert.Equal(0, warnings.Count, $"{name} re-XML warnings");
            EffectAsset roundXml = WebDirectXmlLoader.ParseSingle(reXml);
            AssertSameAsset(fromXml, roundXml, $"{name} XML -> XML");
        }
    }

    private static void FullVfxContractsXmlEqualsJson()
    {
        const string fullJson = """
        { "effectId": "vfx.parity", "nodes": [
          { "name": "source", "emitter": {
              "rateOverTime": 5, "environmentChannels": "Cursor", "environmentForceScale": 0.5,
              "collision": { "mode": "Bounce", "bounce": 0.4, "dampen": 0.2, "lifetimeLoss": 0.1, "killUnderRoof": true },
              "subEmitters": [{ "trigger": "Collision", "targetNodeIndex": 1, "count": 3, "probability": 0.5, "inherit": "Color, Size" }],
              "forceFields": [{ "kind": "RingWave", "centerOrAxis": [1,0,2], "parameters": [4,0.5,0], "radius": 6, "strength": -2 }],
              "trails": { "ratio": 0.5, "lifetime": 0.75, "minVertexDistance": 0.15, "maxPoints": 24,
                  "dieWithParticles": false, "worldSpace": false, "sizeAffectsWidth": false,
                  "sizeAffectsLifetime": true, "inheritParticleColor": false, "width": 0.4,
                  "widthOverTrail": [[0,1],[1,0.2]],
                  "colorOverTrail": [[0,[1,0.8,0.4,1]],[1,[0.5,0.1,0,0]]],
                  "colorOverLifetime": [[0,[1,1,1,0.8]],[1,[1,0.5,0.5,0.2]]],
                  "textureKey": "Lumen/ParticleTrail", "materialTint": [0.8,0.7,0.6,0.9],
                  "colorSpace": "Linear", "blendMode": "Premultiplied", "textureMode": "Tile",
                  "alignment": "Ground", "hdrIntensity": 3.5, "softParticleFadeDistance": 0.2 } } },
          { "name": "spark", "emitter": { "environmentChannels": "None" } },
          { "name": "ring", "distortion": { "kind": "RingWave", "radius": 4, "strength": 0.3, "thickness": 0.5, "speed": 6, "duration": 0.8,
              "strengthOverTime": [[0,1],[0.8,0]] } },
          { "name": "ripple", "distortion": { "kind": "Ripple", "radius": 3, "strength": 0.2, "thickness": 0.3, "speed": 2, "frequency": 2 } },
          { "name": "haze", "distortion": { "kind": "HeatHaze", "radius": 2, "strength": 0.1, "frequency": 1.5, "duration": 3 } },
          { "name": "trail", "ribbon": { "mode": "Trail", "lifetime": 0.6, "minVertexDistance": 0.1, "maxPoints": 32, "width": 0.3,
              "widthOverLength": [[0,1],[1,0]], "colorOverLength": [[0,[1,0.5,0,1]],[1,[1,0,0,0]]], "hdrIntensity": 2,
              "textureKey": "Lumen/Trail", "blendMode": "AlphaBlend", "textureMode": "Tile", "alignment": "Ground", "softParticleFadeDistance": 0.25 } },
          { "name": "beam", "ribbon": { "mode": "Beam", "endLocal": [0,0,5], "segments": 8, "noiseAmplitude": 0.2, "noiseFrequency": 10 } },
          { "name": "zap", "ribbon": { "mode": "Beam", "endAtTarget": true, "segments": 1 } },
          { "name": "dim", "screenDim": { "strength": 0.7, "duration": 1.5, "strengthOverTime": [[0,0],[0.3,1],[1.5,0]] } } ],
          "states": [
            { "name": "idle", "nodes": ["source"], "duration": 2, "loop": true },
            { "name": "hit", "nodes": ["spark"], "duration": 0.5 } ],
          "initialState": "idle",
          "interactionEvents": [
            { "trigger": "CursorStay", "eventName": "attune", "radius": 2.5, "anchorNodeIndex": 0, "minCursorSpeed": 0.1, "maxCursorSpeed": 1.5,
              "holdSeconds": 0.4, "cooldownSeconds": 1.2, "state": "idle", "goToState": "hit", "targetNodeIndex": 1, "notify": true },
            { "trigger": "Explosion", "eventName": "rupture", "radius": 0.75, "minExplosionStrength": 30, "notify": true } ] }
        """;

        const string fullXml = """
        <Defs>
          <RimLumen.Core.Assets.LumenEffectDef>
            <defName>vfx.parity</defName>
            <nodes>
              <li>
                <name>source</name>
                <emitter>
                  <rateOverTime>5</rateOverTime>
                  <environmentChannels>Cursor</environmentChannels>
                  <environmentForceScale>0.5</environmentForceScale>
                  <collision><mode>Bounce</mode><bounce>0.4</bounce><dampen>0.2</dampen><lifetimeLoss>0.1</lifetimeLoss><killUnderRoof>true</killUnderRoof></collision>
                  <subEmitters><li><trigger>Collision</trigger><targetNodeIndex>1</targetNodeIndex><count>3</count><probability>0.5</probability><inherit>Color, Size</inherit></li></subEmitters>
                  <forceFields><li><kind>RingWave</kind><centerOrAxis>(1,0,2)</centerOrAxis><parameters>(4,0.5,0)</parameters><radius>6</radius><strength>-2</strength></li></forceFields>
                  <trails>
                    <ratio>0.5</ratio><lifetime>0.75</lifetime><minVertexDistance>0.15</minVertexDistance><maxPoints>24</maxPoints>
                    <dieWithParticles>false</dieWithParticles><worldSpace>false</worldSpace><sizeAffectsWidth>false</sizeAffectsWidth>
                    <sizeAffectsLifetime>true</sizeAffectsLifetime><inheritParticleColor>false</inheritParticleColor><width>0.4</width>
                    <widthOverTrail><li><time>0</time><value>1</value></li><li><time>1</time><value>0.2</value></li></widthOverTrail>
                    <colorOverTrail><li><time>0</time><color>(1,0.8,0.4,1)</color></li><li><time>1</time><color>(0.5,0.1,0,0)</color></li></colorOverTrail>
                    <colorOverLifetime><li><time>0</time><color>(1,1,1,0.8)</color></li><li><time>1</time><color>(1,0.5,0.5,0.2)</color></li></colorOverLifetime>
                    <textureKey>Lumen/ParticleTrail</textureKey><materialTint>(0.8,0.7,0.6,0.9)</materialTint>
                    <colorSpace>Linear</colorSpace><blendMode>Premultiplied</blendMode><textureMode>Tile</textureMode>
                    <alignment>Ground</alignment><hdrIntensity>3.5</hdrIntensity><softParticleFadeDistance>0.2</softParticleFadeDistance>
                  </trails>
                </emitter>
              </li>
              <li><name>spark</name><emitter><environmentChannels>None</environmentChannels></emitter></li>
              <li><name>ring</name><distortion><kind>RingWave</kind><radius>4</radius><strength>0.3</strength><thickness>0.5</thickness><speed>6</speed><duration>0.8</duration><strengthOverTime><li><time>0</time><value>1</value></li><li><time>0.8</time><value>0</value></li></strengthOverTime></distortion></li>
              <li><name>ripple</name><distortion><kind>Ripple</kind><radius>3</radius><strength>0.2</strength><thickness>0.3</thickness><speed>2</speed><frequency>2</frequency></distortion></li>
              <li><name>haze</name><distortion><kind>HeatHaze</kind><radius>2</radius><strength>0.1</strength><frequency>1.5</frequency><duration>3</duration></distortion></li>
              <li><name>trail</name><ribbon><mode>Trail</mode><lifetime>0.6</lifetime><minVertexDistance>0.1</minVertexDistance><maxPoints>32</maxPoints><width>0.3</width><widthOverLength><li><time>0</time><value>1</value></li><li><time>1</time><value>0</value></li></widthOverLength><colorOverLength><li><time>0</time><color>(1,0.5,0,1)</color></li><li><time>1</time><color>(1,0,0,0)</color></li></colorOverLength><hdrIntensity>2</hdrIntensity><textureKey>Lumen/Trail</textureKey><blendMode>AlphaBlend</blendMode><textureMode>Tile</textureMode><alignment>Ground</alignment><softParticleFadeDistance>0.25</softParticleFadeDistance></ribbon></li>
              <li><name>beam</name><ribbon><mode>Beam</mode><endLocal>(0,0,5)</endLocal><segments>8</segments><noiseAmplitude>0.2</noiseAmplitude><noiseFrequency>10</noiseFrequency></ribbon></li>
              <li><name>zap</name><ribbon><mode>Beam</mode><endAtTarget>true</endAtTarget><segments>1</segments></ribbon></li>
              <li><name>dim</name><screenDim><strength>0.7</strength><duration>1.5</duration><strengthOverTime><li><time>0</time><value>0</value></li><li><time>0.3</time><value>1</value></li><li><time>1.5</time><value>0</value></li></strengthOverTime></screenDim></li>
            </nodes>
            <states>
              <li><name>idle</name><nodes><li>source</li></nodes><duration>2</duration><loop>true</loop></li>
              <li><name>hit</name><nodes><li>spark</li></nodes><duration>0.5</duration></li>
            </states>
            <initialState>idle</initialState>
            <interactionEvents>
              <li><trigger>CursorStay</trigger><eventName>attune</eventName><radius>2.5</radius><anchorNodeIndex>0</anchorNodeIndex><minCursorSpeed>0.1</minCursorSpeed><maxCursorSpeed>1.5</maxCursorSpeed><holdSeconds>0.4</holdSeconds><cooldownSeconds>1.2</cooldownSeconds><state>idle</state><goToState>hit</goToState><targetNodeIndex>1</targetNodeIndex><notify>true</notify></li>
              <li><trigger>Explosion</trigger><eventName>rupture</eventName><radius>0.75</radius><minExplosionStrength>30</minExplosionStrength><notify>true</notify></li>
            </interactionEvents>
          </RimLumen.Core.Assets.LumenEffectDef>
        </Defs>
        """;

        EffectAsset fromJson = LumenJsonAssetLoader.Parse(fullJson);
        EffectAsset fromXml = WebDirectXmlLoader.ParseSingle(fullXml);
        AssertSameAsset(fromJson, fromXml, "full vfx.parity JSON vs XML");

        string xmlFromJson = WebEffectSerializer.ToXml(fromJson, out List<string> w, compact: true);
        TestAssert.Equal(0, w.Count, "vfx.parity lossless XML export");
        AssertSameAsset(fromJson, WebDirectXmlLoader.ParseSingle(xmlFromJson), "vfx.parity serialized XML");
    }

    private static void LossyXmlConversionsEmitWarnings()
    {
        const string lossyJson = """
        {
          "effectId": "LossyCase",
          "nodes": [
            {
              "name": "n0",
              "localRotation": [0.5, 0.5, 0.5, 0.5],
              "localScale": [1, 2, 1],
              "emitter": {
                "sizeOverLifetime": { "keys": [[0, 1], [1, 2]], "multiplier": 3 }
              }
            }
          ]
        }
        """;

        EffectAsset asset = LumenJsonAssetLoader.Parse(lossyJson);
        string xml = WebEffectSerializer.ToXml(asset, out List<string> warnings, compact: true);
        TestAssert.Equal(3, warnings.Count, "expects 3 lossy warnings (rotation, scale, curve multiplier)");
        EffectAsset parsed = WebDirectXmlLoader.ParseSingle(xml);
        // Multiplier 3 folded into keys: 1*3 = 3, 2*3 = 6
        TestAssert.Equal(1f, parsed.Nodes[0].Emitter!.SizeOverLifetime.Multiplier, "folded multiplier is 1");
        TestAssert.Equal(6f, parsed.Nodes[0].Emitter!.SizeOverLifetime.Keys[1].Value, "folded end key value = 6");
    }
}
