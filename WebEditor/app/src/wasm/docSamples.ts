import type { AssetFormat } from './types';

export interface DocPreset {
  id: string;
  label: string;
  format: AssetFormat;
  source: string;
}

// Standalone examples mirrored verbatim from docs/api.md:260-289 and 333-464.
export const DOC_PRESETS: DocPreset[] = [
  {
    id: 'doc:Tornado',
    label: '文档 · Tornado（多状态状态机）',
    format: 'json',
    source: `{
  "effectId": "Tornado",
  "nodes": [
    {
      "name": "funnel",
      "spacePlane": "Vertical3D",
      "emitter": {
        "maxParticles": 2048,
        "duration": 2,
        "looping": true,
        "rateOverTime": 600,
        "shape": "Cone",
        "coneAngleDegrees": 8,
        "startLifetime": 1.6,
        "startSpeed": 4,
        "forceFields": [
          { "kind": "Vortex", "centerOrAxis": [0, 1, 0], "parameters": [1.5, 2, 0], "radius": 3, "strength": 6 }
        ]
      }
    },
    {
      "name": "debris",
      "spacePlane": "Vertical3D",
      "emitter": {
        "maxParticles": 256,
        "duration": 2,
        "looping": true,
        "rateOverTime": 40,
        "shape": "CircleXZ",
        "shapeExtents": 2,
        "startLifetime": 1.2,
        "startSpeed": 2
      }
    },
    {
      "name": "dust",
      "emitter": {
        "maxParticles": 512,
        "duration": 0.5,
        "bursts": [[0, 300]],
        "shape": "CircleXZ",
        "shapeExtents": 2.5,
        "startLifetime": 1.0,
        "startSpeed": 1
      }
    },
    {
      "name": "collapse",
      "spacePlane": "Vertical3D",
      "emitter": {
        "maxParticles": 512,
        "duration": 0.5,
        "bursts": [[0, 400]],
        "shape": "Sphere",
        "startLifetime": 1.0,
        "startSpeed": 6
      }
    }
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
}`,
  },
  {
    id: 'doc:MyTorchBurst',
    label: '文档 · MyTorchBurst（XML 火炬）',
    format: 'xml',
    source: `<Defs>
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
</Defs>`,
  },
  {
    id: 'doc:MySmokeFade',
    label: '文档 · MySmokeFade（XML 烟雾淡出）',
    format: 'xml',
    source: `<Defs>
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
</Defs>`,
  },
  {
    id: 'doc:MyShockwaveAndDim',
    label: '文档 · MyShockwaveAndDim（XML 冲击波）',
    format: 'xml',
    source: `<Defs>
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
</Defs>`,
  },
];
