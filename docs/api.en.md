# RimLumen Mod Developer API Manual

This manual is for mod authors creating visual effects and post-processing packs with RimLumen in RimWorld 1.7 (Waylith / Vulkan engine). All interfaces, fields, and constraints match the current `RimLumen.Core` source code.

---

## 1. Quick Integration Options

Choose one of the following three approaches based on your needs:

1. **Data-Only Effect Definitions (no DLL reference required)**: Add `NekoYukari.RimLumen` to `modDependencies` / `loadAfter` in your mod's `About/About.xml`, then author XML `LumenEffectDef` or `.lumen.json` effect files.
2. **C# Playback & Dynamic Registration**: Reference `RimLumen.Core.dll` (located at `1.7/Assemblies/RimLumen.Core.dll` in this mod) in your C# project and play or register effects via `RimLumen.Core.Api.RimLumenApi`.
3. **Programmable ShaderPacks (no DLL reference required)**: Place `pack.json` and GLSL `#version 450` fragment shaders under `1.7/ShaderPacks/<PackFolder>/` in your mod to declare custom post-processing passes, render targets, persistent history buffers, and live player sliders.

---

## 2. Coordinate System, Space Planes, and Time Sync

### 1. World Coordinate Convention

RimWorld 1.7 uses a 2.5D orthographic/perspective top-down view with the following world axes:

- `+X`: East (screen right)
- `+Y`: Height axis (toward the camera)
- `+Z`: North (screen up)

### 2. Node Space Planes (`LumenSpacePlane`)

| Enum Value | Numeric Value | Rotation Behavior | Typical Usage |
| --- | --- | --- | --- |
| `GroundXZ` | `0` | Uses the node's own `LocalRotation` verbatim with no extra pitch rotation; no camera adaptation at render time | Ground sigils, ground halos, horizontal shockwaves, arbitrary 3D-oriented child emitters (such as RimUnity-imported assets) |
| `Vertical3D` | `3` | Same node matrix as `GroundXZ`, so local `+Y` is world `+Y`; simulates in true 3D, with **render-only** camera adaptation: under IsoRim (where Y is squashed), height above `baseY` is divided by the squash factor to restore true altitude; under the vanilla straight-down 2D camera, height is projected upward onto `+Z` with draw height `max(baseY, MoteOverhead)`; `StretchedVelocity` uses true 3D velocity | Fireworks, fountains, and other effects that launch truly skyward and fall back to the ground |

`Vertical3D`'s `baseY` is the height of the play origin (`LumenPlayRequest.OriginWorld.Y`); a node's `LocalPosition.Y` is relative to that origin (for example, `4.8` for a firework burst apex). `GroundXZ` display is unaffected. The numeric values `1` and `2` are no longer valid and fail at parse time.

### Ground-Contact Expiration (`EmitterSpec.KillBelowFloor` / `killBelowFloor`)

`killBelowFloor` (default `false`) can be set on `EmitterSpec.KillBelowFloor` in C#, inside `.lumen.json` emitter objects, and under `<emitter>` in XML `LumenEffectDef`. When `true`, a particle expires and is reclaimed on the frame its world height drops below the play origin height `baseY` **and** its world velocity `Y < 0` (descending). Particles still rising below floor height or freshly spawned right on the floor are unaffected. L1 (CPU) and L2 (GPU compute) behave identically regardless of space plane; local-space emitters are evaluated after applying their world matrix.

> Tip: Across all emitter shapes, `Cone` defaults its primary emission axis to local `+Y`. To make particles shoot skyward in real altitude and fall back down, set the node to `Vertical3D`, set world-space gravity to `(0, -1, 0)` (or desired magnitude), and enable `KillBelowFloor` if they should vanish on impact.

### 3. Game Time Synchronization

Particle simulation and node timelines use ticks actually completed this frame / 60 as their step, not wall-clock delta multiplied by a speed setting. No completed tick, or a host update skipped for `LongEventHandler.ShouldWaitForEvent`, means a zero game step; paused effects still render and real time continues. Vanilla TickRateMultiplier is Normal=1, Fast=3, Superfast=6 (12 when nothing happens, 18 without maps), Ultrafast=15 (150 without maps, 20000 with UltraSpeedBoost), and 1 when forced normal. The actual tick loop also has a frame-time budget. Source: Verse.TickManager, Assembly-CSharp.decompiled.cs:52605–52646,52853–52888. Pass `realTime: true` for effects using real time.

---

## 3. Asset Format 1: `.lumen.json` Data Files (Recommended Data Format)

### 1. File Locations and ID Resolution

At startup, RimLumen automatically scans the following two directories in every enabled mod (recursively matching `*.json`):

- `<ModRoot>/1.7/Data/RimLumen/`
- `<ModRoot>/Data/RimLumen/`

Suppose your mod's `packageId` is `Author.MyMod` and the file is at `1.7/Data/RimLumen/Spells/Fireball.lumen.json`. Registration produces two sets of usable IDs:

1. **Fully qualified ID**: `Author.MyMod:Spells/Fireball` (always points to your asset without collisions). If a different `effectId` is declared inside the JSON, `Author.MyMod:<effectId>` is also registered.
2. **Bare short name**: `Spells/Fireball` (as well as the JSON's `effectId`). Bare short names are **first-writer-wins**; later-loaded mods will not silently overwrite another mod's bare short name.

### 2. JSON Parsing Rules

- Keys use camelCase, are **case-insensitive**, and allow `//` comments and trailing commas.
- **Unknown keys throw an error** to catch typos early (keys prefixed with `$` or `_` are ignored for documentation metadata).
- Vectors accept `[x, y, z]`, `{ "x": ..., "y": ..., "z": ... }`, or a single number (uniform `x = y = z`).
- Author particle colours in the vanilla scene's stored encoding: RGB must be finite and nonnegative with no upper bound; alpha must be finite in `[0,1]`. RGB `1` is vanilla full brightness and values above `1` can drive HDR. `hdrIntensity` and runtime `intensity` must be finite and nonnegative and scale RGB only; invalid runtime `intensity` throws `ArgumentOutOfRangeException` before mutation. Point-light RGB remains in `[0,1]`. JSON colours accept `[r, g, b, a]`, `[r, g, b]` (alpha 1), or `{ "r": ..., "g": ..., "b": ..., "a": ... }`.
- Removed or renamed legacy keys are rejected with an error naming the new key or the removal: `startColorLinear` is renamed to `startColor`; gradient-stop `rgbaLinear` and point-light `colorLinear` / `colorRgb` are renamed to `color`; `bloomWeight` and `hdrMultiplier` were removed. No compatibility alias is provided.
- Enums accept either member name strings (case-insensitive) or integer values.
- Writing a single number for `startLifetime`, `startSpeed`, or `startSize` sets `Min = Max`; you can also write `...Min` / `...Max` separately.

### 3. Complete Schema and Field Defaults

```json
{
  "effectId": "MyFireball",
  "defaultDuration": 2.0,
  "nodes": [
    {
      "name": "core",
      "parentIndex": -1,
      "spacePlane": "GroundXZ",
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
        "overlightRadius": 0,
        "color": [1, 1, 1],
        "intensity": 1,
        "intensityOverTime": [[0, 1], [1, 0]]
      }
    }
  ],
  "timelineEvents": [
    { "time": 0.5, "eventName": "explode", "targetNodeIndex": 0, "state": null, "notify": false }
  ],
  "states": [],
  "initialState": null,
  "stopState": null
}
```

#### Additional Field Notes

- **Root**: `nodes` must be non-empty; `defaultDuration` must be finite and `>= 0`.
- **Node hierarchy (`EffectNodeSpec`)**: `parentIndex` must be `-1` (attached to root) or strictly less than the current node index `[0, i)`, structurally preventing parent-child cycles. If `localRotation` (`[x, y, z, w]` or `{ x, y, z, w }`) is omitted, `localYawDegrees` rotates around world `+Y`.
- **Scalar curves (`sizeOverLifetime` / `intensityOverTime`)**: Use a single number (constant), an array `[[time, value], ...]`, or an object `{ "keys": [...], "multiplier": 1 }`. Keyframes may include tangents as `[time, value, inTangent, outTangent]`. Times must be finite and non-decreasing: particle-curve time is normalized lifetime `[0,1]`, while point-light curve time is seconds since node activation and must be nonnegative.

  Values and tangents must be finite; tangents are slopes per unit of curve time. Distinct times use cubic Hermite interpolation, followed by multiplication by `multiplier`.

  A time may occur twice or more. If all key times are equal, every time evaluates to the last value. Otherwise, `t <= first` gives the first value and `t >= last` gives the last. At an interior repeated time, the left-hand limit is the group's first value, the exact time gives its last value, and subsequent interpolation starts from that value toward the next key. If the first time is repeated, the exact first time still gives the first value, so the rule is not universally right-continuous.

  For example, `[[0, 0], [0.5, 1], [0.5, 2], [0.5, 3], [1, 4]]` approaches `1` from the left of `0.5`, evaluates to `3` at `0.5`, and then interpolates from `3` toward `4`. The same rule applies to a pair of repeated keys. Source: [RimUnity `AnimationCurve.cs:23,30-35` at pinned revision `45ca90987f7d3721715b45c021cab986ee14c468`](https://github.com/chezhou9413/RimUnity/blob/45ca90987f7d3721715b45c021cab986ee14c468/Source/RimUnity/Animation/AnimationCurve.cs#L23-L35).

  For particle size curves, both L1 and L2 use the same 64-sample lookup table (LUT): samples are at `i/63` (`i = 0…63`), including both endpoints, with interpolation between adjacent samples. A step can be smoothed across a sample interval of width `1/63`; exact preservation is not guaranteed. Point-light curves are evaluated directly in seconds, not through the particle LUT.
- **Material colour**: `materialTint` defaults to `(1,1,1,1)` and follows the colour domain above, as do `startColor` and the gradient; `colorSpace` is `Stored` (default) or `Linear`. Let `vertex = startColor × gradient`, with only its RGB multiplied by `hdrIntensity × intensity`. Stored RGB is `vertex × tint × texel`; Linear RGB is `E(vertex × D(tint) × (texture is sRGB ? D(texel) : texel))`. `vertex` is not decoded. `D` / `E` are sRGB decode / encode extended to nonnegative values above 1 without clamping. Alpha is always the product of the three raw alpha values, followed by soft-particle coverage and any blend-mode premultiplication. Componentwise, `D(x) = x/12.92` for `x <= 0.04045`, otherwise `((x+0.055)/1.055)^2.4`; `E(x) = 12.92*x` for `x <= 0.0031308`, otherwise `1.055*x^(1/2.4)-0.055`. Source: RimUnity `Source/Shared/ShaderColorSpace.cs:69-103,122-134` at revision `45ca90987f7d3721715b45c021cab986ee14c468`. This describes material calculation only, not the player blend space.
- **Gradients (`colorOverLifetime`)**: Supports a stop array `[[time, [r,g,b,a]], ...]` or an object `{ "stops": [{ "time": 0, "color": [r,g,b,a] }] }`, requiring strictly increasing `time`.
- **`pointLight`**: The JSON colour key is `color` (RGBA); radius keys are `glowRadius` and `overlightRadius` (in map cells).
- **Force-field `strength`**: Required explicitly in JSON, XML, and C#. Omission rejects the asset; no default is used.
- **`timelineEvents` / `states` / `initialState` / `stopState`**: see the next section, "State Machine".
- **Particle density setting**: The public setter `RimLumenRenderHost.ParticleDensityScale` accepts a range of `0.1`–`1.0`; finite values outside it are silently clamped without logging, while NaN and positive or negative infinity throw `ArgumentOutOfRangeException`. The settings slider is continuous and displays a rounded whole percentage. Density `1` is exact; after clamping to `d`, the effective burst density is `floor(d * 65536) / 65536`.
- **Particle density and bursts**: Each emitter keeps its own `Int64` accumulator `a`, initially `0`, in units of `1/65536` particle. Compute `q = floor(density * 65536)`; for every nonzero burst, apply `a += count * q`, `n = ceil(a / 65536)` using integer arithmetic, then `a -= n * 65536`. Here `n` is the request budget; `a <= 0` and may be negative. The effective density is `floor(density * 65536) / 65536`, never above the authored value; density `1` is exact and has no `0.999` threshold.

  At density `0.25`, four consecutive bursts with `count = 1` yield budgets `1, 0, 0, 0`; eight such bursts total `2`. Two consecutive bursts with `count = 7` yield `2, 2`. At density `0.1`, `q = 6553`, so ten `count = 1` bursts accumulate `65530` and request `1`; at density `0.2`, five such bursts request `1`. The cumulative request budget uses the effective density: `ceil(sum(count * q) / 65536)`. A scheduled budget is not the actual live-particle count or GPU particle count: requests dropped for insufficient capacity or free slots are not replayed. `rateOverTime` must be finite and nonnegative, with no additional rate cap. Before capacity admission, finite continuous `rate × dt` budgets and all crossed bursts accepted by probability and scaled by density are accumulated in wide numeric types; exceeding Int32 is ordinary capacity dropping, not a budget-range exception. Even when no slots are free, every planned occurrence gets its probability draw and every accepted occurrence updates density carry.

  Bursts are scaled once when queued on the CPU; GPU Capture does not scale them again. `Play`, explicit `Restart`, and `GoTo` state re-entry reset `a` to `0`; loop wrap does not. For example, at density `0.25`, one `count = 1` burst per loop requests `2` particles over eight loops. Continuous rate emission uses a separate `double` fractional carry, floors the accumulated count and retains the remainder. The runtime `rate` multiplier does not scale bursts.

### 4. State Machine (`states`)

An effect is made of named states, and exactly one state plays at a time. A state can advance on its own when its duration elapses or be switched from C#, and it can report key moments back to C#.

**Without `states`**, the whole effect is one implicit state that holds every node and lasts `defaultDuration`. If any emitter is `looping: true`, that state loops until `Stop`; otherwise new emission stops when the duration elapses, and the handle dies and raises `Finished` once its particles and lights are gone. The implicit state has no name and raises no `StateChanged`.

**`states[]` fields**:

| Field | Default | Meaning |
| --- | --- | --- |
| `name` | required | State name, unique within the asset |
| `nodes` | `[]` | Names of the nodes that emit in this state; every node with that name belongs to it. A node may belong to several states or to none |
| `duration` | `defaultDuration` | State length in seconds (`>= 0`) |
| `loop` | `false` | When `true` the state never ends on its own, and `duration` is its timeline-event cycle |
| `next` | `null` | State entered when a non-loop state ends; `null` ends the effect. Cannot be combined with `loop` |

**Root fields**: `initialState` is the state entered on play, defaulting to `states[0]`; `LumenPlayRequest.InitialState` overrides it for a single play. `stopState` is the outro entered by `Stop(true)`; it must end on its own (no `loop`, no `next`). Without it, `Stop(true)` starts fading out at once.

**Entering a state** (the initial state, `next`, `GoTo`, including `GoTo` of the current state, which re-enters it):

1. The state clock resets to zero and `StateChanged(name)` is raised once.
2. Every node of the state restarts its own emission schedule: `bursts` times, the emitter `duration` and `rateOverTime` all count from the moment of entry. L1 (CPU) and L2 (GPU) behave identically.
3. Every other node stops new emission; its live particles finish their lifetime and its point light fades out over `0.25s`.

Emission pause is distinct from game pause: after an emitter's first activation, its emission schedule clock advances while emission is disabled for both looping and non-looping emitters. Planned bursts crossed during that interval are skipped without catch-up, while live particles continue simulating. A never-activated node remains at emission time 0. Explicit Restart resets the clock, density carry, and probability RNG while retaining already accepted pending bursts. Global game pause still uses `dt=0`; `SetParam("rate", 0)` pauses continuous emission only and does not affect authored bursts.

States switch on frame boundaries: on the frame a switch happens, emitters simulate the whole frame under the new state. A frame that crosses several states walks through each of them and their events in order.

**`timelineEvents[]` and states**:

| Field | Default | Meaning |
| --- | --- | --- |
| `time` | required | Seconds after the owning state is entered |
| `eventName` | required | Event name, used by `Trigger` and `Notify` |
| `targetNodeIndex` | `-1` | When not `-1`, emits one pulse on that node: `bursts[0].count`, or the rounded `rateOverTime` without `bursts` |
| `state` | `initialState` | Owning state; the event fires on its time only while that state plays |
| `notify` | `false` | When `true`, firing raises `ILumenEffectHandle.Notify(eventName)` |

A loop state fires its events again every `duration` cycle, and events with `time >= duration` never fire on the timer; a loop state with `duration` `0` fires them once. In a non-loop state an event at exactly `duration` still fires.

**Load-time validation** (`.lumen.json` and XML fail at parse time; a C# `EffectAsset` throws `InvalidDataException` from `Play`): an empty or duplicate state name; a `nodes` entry naming no node; a negative or non-finite duration; `loop` together with `next`; `next`, `initialState`, `stopState` or an event `state` naming an unknown state; a `stopState` that loops or has `next`; zero-duration states that cycle through `next`; `initialState`, `stopState` or an event `state` without `states`.

#### Example: a tornado that spawns, sustains and collapses

```json
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
```

```csharp
ILumenEffectHandle tornado = RimLumenApi.PlayAtCell("Author.MyMod:Tornado", map, cell);
tornado.Notify += name =>
{
    if (name == "touchdown") StartDamagingCells();   // the funnel touches down 1.0 s into spawn
    if (name == "impact") ScatterDebris();           // debris lands 0.3 s into collapse
};
tornado.Finished += StopDamagingCells;
// ...when the storm ends:
tornado.Stop();                                      // enters collapse, fades out after its 1.2 s
```

Step by step:

| Time | State | What happens |
| --- | --- | --- |
| `0` | `spawn` | `StateChanged("spawn")`; `dust` bursts 300 particles, `funnel` starts emitting, `debris` does not emit |
| `1.0` | `spawn` | `touchdown` raises `Notify("touchdown")` |
| `1.5` | `sustain` | Duration elapsed, enter `next`: `StateChanged("sustain")`; `funnel` and `debris` restart their schedules, `dust` stops new emission |
| `2.0`, `4.0`, ... | `sustain` | 0.5 s into each 2 s cycle, `gust` pulses 40 particles on `debris` |
| `Stop()` called | `collapse` | `StateChanged("collapse")`; `collapse` bursts 400 particles, funnel and debris stop new emission; `GoTo` for declared states is ignored from now on; undeclared states still throw |
| `Stop` + `0.3 s` | `collapse` | `Notify("impact")` |
| `Stop` + `1.2 s` | fading | New emission stops, lights fade, particles finish their lifetime |
| Last particle gone | ended | `IsAlive == false`, `Finished` is raised |

---

## 4. Asset Format 2: XML `LumenEffectDef`

You can define `RimLumen.Core.Assets.LumenEffectDef` in any mod's `Defs/**/*.xml`. Except for the four XML naming conventions below, all defaults and behavior match `.lumen.json`:

1. **ID**: Uses `<defName>`, registering both the bare short name and `<packageId>:<defName>`.
2. **Node fields**: Adds `<heightOffset>` (added to `localPosition.y`), `localScale` is a single float scalar, and `localRotation` quaternion is omitted (use `<localYawDegrees>`).
3. **Point light keys**: Uses `<radiusCells>` (matching JSON `glowRadius`), `<overlightRadius>`, `<color>` (raw float syntax `(r, g, b)` or `(r, g, b, a)`), `<intensity>`, and `<intensityOverTime>` (`<keys>` made of `<li><time>..</time><value>..</value></li>`).
4. **List fields**: `<states>`, `<timelineEvents>` and a state's `<nodes>` are `<li>` lists; state fields are `<name>`, `<nodes>`, `<duration>`, `<loop>`, `<next>`, event fields are `<time>`, `<eventName>`, `<targetNodeIndex>`, `<state>`, `<notify>`; the root also takes `<defaultDuration>`, `<initialState>` and `<stopState>`. Meanings and defaults match the "State Machine" section above.

XML emitters use `<colorSpace>` and `<materialTint>`, defaulting to `Stored` and `(1,1,1,1)`. XML `<startColor>`, gradient-stop `<color>`, `<materialTint>`, and point-light `<color>` use parenthesized raw float components: three components imply alpha 1; four preserve the given values, without byte-colour interpretation. Malformed text or component counts report the asset and field.

Legacy XML keys are rejected too: `<startColorLinear>` is renamed to `<startColor>`; gradient-stop `<rgbaLinear>` and point-light `<colorLinear>` / `<colorRgb>` are renamed to `<color>`; `<bloomWeight>` and `<hdrMultiplier>` were removed.

```xml
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
```

---

## 5. Enums and Force Fields Reference

### 1. Particle System Enums (`RimLumen.Core.Contracts`)

- **`LumenParticleTier`**: `External` (external bridge player) / `L1_NativeCpu` (native CPU simulation, suited for low counts or frequent script interaction) / `L2_GpuCompute` (Vulkan Compute simulation, default, suited for high particle counts and force fields).
- **`LumenShapeKind`**: `Point` (0), `Sphere` (1), `CircleXZ` (2), `Cone` (3), `Box` (4), `SphereShell` (5), `Hemisphere` (6), `HemisphereShell` (7), `CircleEdgeXZ` (8), `ConeShell` (9), `ConeVolume` (10), `ConeVolumeShell` (11), `SingleSidedEdge` (12), `BoxShell` (13), `BoxEdge` (14), `Donut` (15), `Rectangle` (16).
- **`LumenBlendMode`**: `Additive` (`0`, recommended for glow and fire) / `AlphaBlend` (`1`, smoke and mist) / `Premultiplied` (`2`).
- **`LumenBillboardMode`**: `Camera` (`0`, camera-facing) / `Horizontal` (`1`, flat ground-aligned quad) / `StretchedVelocity` (`2`, stretched along velocity using `VelocityStretchFactor` and `LengthScale`) / `Mesh` (`3`, deferred) / `Vertical` (`4`) / `Local` (`5`).

### Shapes and probabilistic bursts

`ShapeExtents` defaults to `(1,1,1)` in cells: sphere / hemisphere use XYZ radii, circle / cone use XZ base radii, box uses XYZ half-extents, `SingleSidedEdge` uses X half-length, `Donut` uses XZ major-ring radii, and `Rectangle` uses XZ half-extents. The shape axis is +Y and hemispheres occupy +Y; the edge spans X and emits -Z. Static partial-arc radial direction is `(cos θ, 0, -sin θ)`.

| Emitter field (JSON / XML use lower camel case) | Default | Domain and meaning |
| --- | --- | --- |
| `ConeLength` | 5 | finite nonnegative cells; cone volumes only |
| `DonutRadius` | 0.2 | finite nonnegative cells; tube radius |
| `RadiusThickness` | 1 | finite `[0,1]` radial thickness; explicit Shell / Edge variants remain on surface / edge |
| `BoxThickness` | `(0,0,0)` | finite `[0,1]` per component; inward box shell / edge thickness |
| `ArcDegrees` | 360 | finite `[0,360]` degrees |
| `ArcSpread` | 0 | finite `[0,1]`; nonzero phase quantization `floor(u/step)*step` |
| `RadiusSpread` | 0 | finite `[0,1]`; same quantization, single-sided edge X only |
| `RandomDirectionAmount`, `SphericalDirectionAmount` | 0 | each finite `[0,1]`; mix toward an independent sphere direction and outward shape-center direction |
| `RandomPositionAmount` | 0 | finite nonnegative cells; add an independent uniform-volume sphere offset |

Cone base radius uses `r=sqrt(lerp((1-RadiusThickness)^2,1,u))`, or `r=1` for shells; `d=normalize(radial*sin(angle)*r + +Y*cos(angle))` and position is `radial*r*baseRadius`. Volume shapes then add `d*u*ConeLength/max(0.001,d.y)`. Normalization returns zero for input length `<=1e-5`, allowing a stationary birth. Box shells weight faces by areas from `abs(ShapeExtents*ShapeScale)`; box edges weight lengths, selecting X when total weight is zero.

After sampling, apply shape-local TRS, then mix random direction, spherical direction, and random position in that order; shape scale does not scale direction. Imported oriented shapes use shape-only `R*Rx(+90°)` and scale `(sx,sz,sy)`, without rotating the prefab, world, or forces. L1 / L2 use the same shape formulas and distributions; seeded per-particle birth identity is not promised. Native inputs outside the stated domains are rejected without clamping; imported values route to External with a named field reason.

`Bursts` is `ParticleBurstSpec(Time, Count, Probability=1)[]`: time is finite seconds in `[0, Duration]` inclusive, count is a nonnegative integer, and probability is finite `[0,1]`; JSON `probability` and XML `<probability>` are optional, for example `<li><time>0.5</time><count>100</count><probability>0.5</probability></li>`. C#, JSON, and XML validate time at the asset boundary; values above Duration produce an error naming the exact field, without repeating the check in the Play hot path. The shared CPU scheduler draws once per planned occurrence before density; rejected occurrences do not consume density carry, and explicit `EmitBurst` is never gated. L1 / L2 agree on acceptance for the same node seed. The service assigns play and node seeds; no public seed or cross-Play reproducibility is promised. Restart resets schedule, carry and probability RNG while retaining accepted pending bursts; loop wrap does not reset them. Continuous rate and the shared fixed-point density carry rule remain unchanged; see “Particle density and bursts” above.

`Vertical` in `GroundXZ` uses `normalize(cross(worldUp, rawCameraForward))`; at exact overhead, the defined camera-right limit with +Y up gives a finite but edge-on invisible quad. Use `Vertical3D` for standing sprites in 2D; it reuses the already compensated camera right / up without a second inverse Y or normalization. `Local` projects the full world-quaternion axes geometrically, applying the `Vertical3D` display mapping once; edge-on collapse is retained without camera-facing fallback. The only space planes are `GroundXZ=0` and `Vertical3D=3`.

### 2. Four Force Fields (`ForceFieldSpec` / `ForceFieldKind`)

| Kind | `CenterOrAxis` | `Parameters` | `Radius` | `Strength` | `Frequency` | Effect Description |
| --- | --- | --- | --- | --- | --- | --- |
| `GravityDrag` (`0`) | Acceleration direction (not normalized), e.g. `(0, 0, -1)` | `x` = air drag coefficient `drag` (`>= 0`, damps velocity by `exp(-drag * dt)`) | Unused | Acceleration magnitude | Unused | Applies constant directional gravity with velocity damping |
| `Vortex` (`1`) | Rotation axis (default `(0, 1, 0)`) | `x` = inward pull toward axis (negative pushes outward); `y` = lift force along axis | Effective radius (`<= 0` means map-wide without falloff) | Tangential spin strength | Unused | Swirls particles around the origin axis with linear distance falloff |
| `CurlTurbulence` (`2`) | Unused | Spatial sampling offset | Unused | Turbulence amplitude | Spatial noise frequency | Divergence-free 3D curl noise for fluid sway and wispy drift |
| `PointAttractor` (`3`) | Attractor center position | Unused | Effective radius (`<= 0` means map-wide without falloff) | Attraction strength (negative repels) | Unused | Pulls toward (or pushes away from) target point with linear distance falloff |

> Note: When `WorldSpaceSimulation == true`, force fields evaluate in world space (for example `(0, 0, -1)` for falling down in top-down view); when `false`, they evaluate in emitter local space.

---

## 6. C# Runtime API (`RimLumen.Core.Api`)

> Threading rule: Every method on `RimLumenApi` and `LumenTextureRegistry` **must be called on the game main thread**.

### 1. Capability Queries and Dynamic Asset Registration

```csharp
using RimLumen.Core.Api;
using RimLumen.Core.Contracts;

bool ready = RimLumenApi.IsReady;
LumenCapabilities caps = RimLumenApi.Capabilities;
// caps.IsHdrActive / IsSceneDepthAvailable / IsGpuComputeAvailable / MaxGpuParticlesPerFrame

RimLumenApi.RegisterEffect(myEffectAsset); // Registers or replaces an asset with the same EffectId
bool exists = RimLumenApi.TryGetEffect("Author.MyMod:Spells/Fireball", out EffectAsset? asset);
```

### 2. Playback API

```csharp
// 1. Play at map cell center (most common)
ILumenEffectHandle handle = RimLumenApi.PlayAtCell(
    effectId: "Author.MyMod:Spells/Fireball",
    map: pawn.Map,
    cell: pawn.Position,
    scale: 1f,
    rotationDegrees: 0f,
    realTime: false);

// 2. Play at world position (note Vector3 is System.Numerics.Vector3)
ILumenEffectHandle handle2 = RimLumenApi.Play(
    effectId: "MyFireball",
    map: map,
    originWorld: new System.Numerics.Vector3(x, y, z),
    scale: 1.2f,
    rotationDegrees: 90f,
    realTime: false);

// 3. Full request (aim toward target / per-frame follow / initial active state)
ILumenEffectHandle handle3 = RimLumenApi.Play(new LumenPlayRequest
{
    EffectId = "MyBeam",
    MapIndex = map.uniqueID,
    OriginWorld = startPos,
    TargetWorld = targetPos, // Automatically computes XZ yaw toward TargetWorld
    Scale = 1f,
    Follow = (out System.Numerics.Vector3 pos, out float yaw) =>
    {
        if (pawn.Destroyed || !pawn.Spawned)
        {
            pos = default;
            yaw = 0f;
            return false; // Returning false freezes at the last known transform instead of snapping back
        }
        var p = pawn.DrawPos;
        pos = new System.Numerics.Vector3(p.x, p.y, p.z);
        yaw = pawn.Rotation.AsAngle;
        return true;
    },
    InitialState = "charge", // Overrides the asset's initialState; Play throws ArgumentException for a state the asset does not declare
    RealTime = false,
});
```

- If `EffectId` does not exist, `Play` returns an inert handle with `IsAlive == false` whose methods are no-ops, and logs `Log.Warning` once per ID per service instance. An asset rejected by validation also returns an inert handle; it retains the registry's validation error without adding an "unknown ID" warning.

### 3. Handle Controls (`ILumenEffectHandle`)

| Member | Behavior & Notes |
| --- | --- |
| `bool IsAlive` | `true` while playing or fading/draining; becomes `false` once all particles and point lights are cleaned up. |
| `void Trigger(string name)` | Fires every timeline event with that name now, whatever its state and time (target-node pulse, `Notify`), and restarts the emission schedule of every node with that name until the next state entry decides again; the current state is unchanged. Unmatched names do nothing; no effect after fading out or death. L1 and L2 behave identically. |
| `void GoTo(string stateName)` | Enters that state now (going to the current state restarts it). On an asset-backed handle, an undeclared state name always throws `ArgumentException`, including after `Stop(true)`, while draining, or once dead; a declared state is entered only while playing before `Stop(true)`, otherwise it has no effect. `Stop(false)` tears down immediately; `GoTo` on an inert handle is always a no-op. |
| `event Action<string> Notify` | Raised with the event name when a timeline event with `notify: true` fires (on its time or through `Trigger`). |
| `event Action<string> StateChanged` | Raised with the state name each time a declared state is entered, including the initial state. |
| `event Action Finished` | Raised once when `IsAlive` becomes `false`, for any reason (played out, `Stop`, `Dispose`). |
| `void SetParam(string name, float value)` | Updates runtime scalar parameters. Three built-in names take effect immediately: `intensity` (scales particle and light brightness), `rate` (scales only continuous L1 and L2 emission, not `bursts`; must be finite and nonnegative or throws `ArgumentOutOfRangeException`; zero pauses only continuous emission), and `scale` (multiplies root scale). Other names are stored without affecting simulation. |
| `void Stop(bool allowFadeOut = true)` | When `true`: if the asset has a `stopState`, enters it and plays it to its end; then stops new emission, fades point lights over `0.25s`, and lets live particles finish their lifetime before reclamation. Without a `stopState` the fade starts at once. When `false`, immediately destroys all particles and lights. Repeated calls do nothing. |
| `void Dispose()` | Equivalent to `Stop(false)`, idempotent. Always dispose long-lived handles in `Thing.DeSpawn` or when a map is destroyed. |

All three events are raised on the main thread from the per-frame advance, in the order they happened, so calling `GoTo` or `Stop` from a handler never re-enters the playback that raised it: the call takes effect at once (the nodes of the state being left stop new emission on that same frame), and the events it causes are raised on the next frame. After `Stop(false)` or `Dispose`, `Finished` is raised on the next frame. RimUnity compatibility playback (`External`) has no states or timeline events: `Notify` and `StateChanged` come only from the native RimLumen part, and `Finished` is raised once both parts have ended.

---

`LumenEmitterTransform` is a readonly pair of `World` matrix and unit world `Rotation` quaternion: rotation composes the full parent chain and request / Follow yaw independently of scale. It is not inferred from the matrix and is distinct from `ShapeRotation`. Node `LocalRotation`, like `ShapeRotation`, must be finite and unit length within `1e-5` squared-norm tolerance; XML nonfinite yaw is rejected. The sole GPU entry is `CreateInstance(spec, LumenEmitterTransform, mapIndex, realTime, uint emissionSeed)`; `UpdateInstance` likewise takes the complete transform, and CPU `AcquireEmitter` requires the node seed. Internal emitter / particle / material / vertex layouts are 320 / 64 / 128 / 44 bytes, not a public raw GPU ABI.

### 4. Native HDR Blend Space and Extension Draws

`LumenColorBlendSpace : byte` offers `Legacy = 0` (default) and `Linear = 1` for native HDR-layer blending, independently of an emitter's material `ColorSpace`. `RimLumenRenderHost.ColorBlendSpace` is the requested setting; read-only `CurrentFrameColorBlendSpace` is the frame latch. It is initialized at installation and captured each frame in `EngineApplication.PrePollEvents` (584), before input, `OnUpdate` (715), and `BeginMainThreadFrame` (730). A setting changed during Update takes effect on the next frame.

Main-thread layer 15 and explicit HDR-pass producers must read only the frame latch, never the requested setting for a draw. Contributors use `LumenCaptureContext.ColorBlendSpace`, copy it into immutable frame data, and use `LumenHdrDrawContext.ColorBlendSpace` during recording; rendering never reads mutable settings. Built-in L1 / L2 target the HDR pass directly; there are currently no built-in layer 15 producers, and Showcase's original reference quads remain on layer 0.

After the host registers its shader root, external GLSL can include `RimLumen/RimLumenColorSpace.glsl`. It provides D / E as `lumenDecodeStored(vec3)` / `lumenEncodeStored(vec3)`, and `lumenColorForBlendSpace(vec4 storedColor, bool linearBlendSpace)` converts **straight** Stored RGB to the layer space without changing alpha. Call the helper before any blend-mode premultiplication; never decode already-premultiplied RGB:

```glsl
#include "RimLumen/RimLumenColorSpace.glsl"
// storedColor is straight RGBA from material calculation; linearBlendSpace comes from this frame's latch.
vec4 layerColor = lumenColorForBlendSpace(storedColor, linearBlendSpace);
if (premultipliedBlend) layerColor.rgb *= layerColor.a;
```

Linear mode decodes straight material RGB before alpha / hardware blending; the host encodes the accumulated native HDR layer once before the group 0 ShaderPack DAG. AlphaBlend and Premultiplied follow the same rule, and alpha remains coverage. Equality with Legacy holds only for alpha 1 over an empty layer: for `c=0.5, a=0.5` over an empty layer, Legacy gives `0.25` and Linear approximately `0.36078`. These are formula results, not measured pixels. Pack `hdr`, `glow`, and `scene` remain Stored; vanilla glow and late groups 1–3 are unchanged. When Linear conversion is needed, one extra full-resolution RGBA16F image per submission slot is allocated lazily and retained safely until target retirement.

## 7. Texture Registry and Compatibility Notes

### 1. Custom Particle Textures (`LumenTextureRegistry`)

An emitter's `textureKey` is resolved on the main thread in the following order:

1. Check textures explicitly registered via `LumenTextureRegistry.Register(string key, Waylith.Rendering.Texture texture)`.
2. Otherwise call vanilla `ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)` to load from a mod's `Textures/` directory (for example `"RimLumen/Effects/Spark"`).
3. If still missing, fall back to the procedural soft circle and log one warning per missing `key`. An unspecified `textureKey` also uses the procedural circle. Its alpha is `max(0, 1 - r²)²`, where `r` is the fragment's radial coordinate; it is independent of the flipbook atlas UV and does not change per frame.

For Linear materials, main-thread texture resolution reads `Texture2D.CpuData.Linear`: native ContentFinder textures use `false`, and RimUnity textures retain the export flag. There is no author `textureSrgb` field; UNorm, BC-UNorm, and SFloat formats do not automatically decode sRGB on the GPU. A present non-`Texture2D` or texture without `CpuData` fails for Linear materials with the texture key and missing colour metadata in the error. Stored materials and public `Register` behavior are unchanged; a missing texture still falls back to the procedural circle.

> Never call texture registration from worker threads or render callbacks.

### 2. Automatic Vanilla Additive Glow Enhancement

By default, RimLumen draws an extra copy of vanilla and third-party additive draws (`Blend.Enabled`, `DstColor == One`, `ColorOp == Add`, `SrcColor` is `SrcAlpha` or `One`, and `!Depth.Write`) for HDR bloom, with five built-in fullscreen/ambient shader exclusions: `GlowOverlay`, `OxygenOverlayIndoors`, `GravshipLensFlare`, `UnlitAdditive`, and `MoteWater`.

The vanilla draw is left untouched, so the vanilla scene still draws the glow core. Copies go only into a separate `glow` target, never into the `hdr` layer or the composite's core term. Early copies feed the built-in bloom prefilter and the pack's `glow` input; later copies feed host bloom only. The ground under a glow is therefore never darkened. Turning the toggle off, or its fail-safe tripping, removes every enhancement of vanilla glow; the image is vanilla when no other native HDR effect is active and the current pack does not rewrite the scene.

Four groups use the effective `FinalDrawItem.RenderQueue` and execute at map-pass boundaries: `q < 3152` → 3152, `[3152, 3169)` → 3169, `[3169, 3178)` → 3178, and `[3178, int.MaxValue)` → map end `int.MaxValue`. Groups are not merged; each boundary runs before vanilla scene grabs at the same queue.

The 3152 group runs the full native HDR, early glow, bloom and ShaderPack DAG / composite once, under the existing native draw, contributor and `runOnEmptyFrames` triggers; it is not suppressed merely because early glow is empty. It precedes vanilla's 3152 Distortion grab. The pack's `glow` contains only `q < 3152` copies; `scene` and `depth` are separate same-frame snapshots at this boundary, not later captures. The final composite writes map colour without sampling the image being written. The `pack.json` `"backbuffer"` name and PostEffectPass composite ABI remain unchanged: the name denotes the final composite output, not a swapchain slot.

Later groups only redraw their additive glow copies, run host bloom prefilter, downsample and upsample, then add bloom RGB to the scene while preserving destination alpha, without tone mapping. The original core is already in the scene and is not added again; native HDR particles, grading, fog, custom DAG and history do not rerun. Later groups use the same frame's captured active-pack `BloomParams` (intensity, threshold, knee, scatter) and requested mip count. An absent group or zero mips adds no boundary or passes for that group.

Custom `bloomPrefilterShader`, `bloomDownShader` and `bloomUpShader` apply only to the 3152 group; later groups use host shaders, not an equivalent of arbitrary custom bloom. Later Cinematic copies do not create streaks or affect Adaptation; Atmospheric late bloom does not light mist or use fog-dependent scatter gain; Crisp, Vibrant and other pack composite colour mapping does not apply to late bloom. The pack DAG and history run once with the 3152 group, not once per group.

In the installed vanilla / DLC static inventory, ritual candle clusters at 3155 bloom at 3169 before weather FogOverlay 3169 and SunShadow 3170; orbital / power beams and endpoints at 3177 bloom at 3178 after FogOfWar 3175 and before the Gravship grab and Darkness 3180; grav-field 3181, grav engine 3185, thruster flame 3201 and Noctol eyes 4000 bloom at map end. The inventory has no eligible draws in `[3169, 3174]` or `[3178, 3179]`. The last group's bloom is later than the 3201 Screen and 3251 VoidNode grabs and is not included in them; ordering is not guaranteed for all overlays, grabs or third-party draws. This is a static ordering contract; in-game behaviour has not been verified.

To opt a custom `ShaderDef` out of automatic HDR redraw, attach `<li Class="RimLumen.Core.Compat.LumenEmissiveModExtension"><EnableHdrBloom>false</EnableHdrBloom></li>`. (`HdrMultiplier` was removed; writing `<hdrMultiplier>` fails Def validation. Only `EnableHdrBloom` takes effect.)

---

## 8. Optional `Chezhou.RimUnity` Bridge

When `Chezhou.RimUnity` is active, `RimLumen.Adapter.RimUnity` reads its exported `catalog.json`, prefab JSON (including `.json.gz`), and `.rutex` textures, automatically routing each particle node:

- **Native HDR / GPU (or CPU)**: supported basic shapes with shape-local position, rotation, and scale (TRS), curves, gradients, textures, flipbooks, rate-over-time emission, and probability-controlled scheduled bursts.
- **Retained on RimUnity (`External`)**: nodes using `trails`, `subEmitters` (including sub-emitter dependencies), `collision`, `trigger`, `renderMode.Mesh` or an unknown `renderMode`, a material `_Color` outside the native colour domain, linear `velocityOverLifetime`, `rotationOverLifetime`, curve-based `forceOverLifetime`, any enabled `lights` module (including `ratio = 1`), as well as deferred dynamic arc / radius modes, alignment, varying velocity / force / rotation curves, dual-curve randomness, geometry, and shape textures.

Imported burst schedule times must be strictly less than the system `Duration`. With `cycleCount=0`, repetition restarts from the same local phase in every system loop, even when the interval does not divide the duration. The total expanded occurrences across all burst definitions in one emitter are limited to 4096; above that, the whole emitter routes to External with a reason, never truncation. A nonfinite interval routes to External; a repeated burst with interval `<=0` also routes to External. A one-shot burst permits a finite nonpositive interval but does not use it. The reasons are `emission.bursts.schedule.domain` for an invalid schedule and `emission.bursts.occurrenceLimit` for exceeding the total limit. Deferred curve classification inspects only active modules and axes; stored curves in disabled emission are ignored. Active `main.startSize3D`, active `main.startRotation3D`, or nonzero `startRotation` route to External with reasons `main.startSize3D`, `main.startRotation3D`, and `main.startRotation`, respectively. Active dual-curve `rateOverDistance` uses reason `emission.rateOverDistance.dualCurve`; inactive 3D-axis fields do not cause rejection. Native initial rotation and 3D size remain deferred.

The adapter maps material `_Color` separately to `materialTint` and routes Linear materials natively when no other unsupported reason applies. Imported material colours outside the domain above route to External with a material identity and field reason, `material[<asset-id>]._Color.domain`, without clamping or folding into vertex colour.

Callers using `RimLumenApi.Play` do not need to care which backend handles each node; a single `ILumenEffectHandle` controls both sides together.

---

## 9. Programmable ShaderPack Specification (`ShaderPacks`)

RimLumen supports declarative multi-pass ShaderPacks. Mod authors and players can drop a `pack.json` and GLSL `#version 450` fragment shaders—with zero C# code—to add custom fullscreen post-processing passes, override Bloom shaders, sample scene depth and the map light field, and expose live tuning sliders in Mod Settings.

### 1. Directory Layout, Scan Order, and Override Rules

Each ShaderPack is a standalone subdirectory containing at least a `pack.json` manifest:

```text
1.7/ShaderPacks/
  MyShaderPack/
    pack.json
    Composite.frag.glsl
    Common.glslinc
```

#### Scan Roots and Override Priority

`ShaderPackCatalog` scans only **immediate subdirectories** under each root (sorted case-insensitively by folder name) and checks for `pack.json`. Sources are scanned in ascending priority below; later packs overwrite earlier entries with the same case-insensitive `id` while preserving the first-seen ordering position:

1. **Built-in baseline**: `StandardEntry` (`id` is `"default"`, backed by `ShaderPackSpec.Default`, no `pack.json` on disk).
2. **RimLumen built-in packs**: `<RimLumen>/1.7/ShaderPacks/`, then `<RimLumen>/ShaderPacks/` (source label `内置`).
3. **Enabled mod packs**: in `LoadedModManager.RunningMods` load order, `<ModRoot>/1.7/ShaderPacks/`, then `<ModRoot>/ShaderPacks/` (source label `模组: <ModName>`).
4. **Local player directory**: `Config/RimLumen/ShaderPacks/` (resolved via `GenFilePaths.ConfigFolderPath`, source label `本地`). Local packs take highest priority, ideal for player tweaks or hot-reload iteration.

> Note: `"default"` is reserved for the built-in `Standard` baseline. Any `pack.json` declaring `"id": "default"` (case-insensitive) is rejected with an error during scan.

#### Path Security and Release Packaging Gate

- **Relative paths only**: All shader paths in `pack.json` (`compositeShader`, `bloomPrefilterShader`, `bloomDownShader`, `bloomUpShader`, and `passes[].shader`) must be relative to the pack directory; rooted paths and `..` traversal are rejected.
- **Physical path verification**: The loader resolves symlinks and Windows junctions on both the pack root and the combined shader path via `ResolveLinkTarget` (up to 32 hops), rejecting any link escaping outside the pack directory while still allowing the pack root itself to be mounted through a junction (e.g., `Mods/RimLumen` during development).
- **Release packaging gate**: `Tools/pack-release.ps1` enforces that `1.7/ShaderPacks/` contains only `.json`, `.glsl`, and `.glslinc` files.

---

### 2. `pack.json` Schema and Field Defaults

`pack.json` parsing is case-insensitive on property names and allows `//` / `/* */` comments and trailing commas.

```json
{
  "id": "my_night_glow",
  "name": "My Night Glow",
  "author": "Author",
  "description": "Optional description",
  "runOnEmptyFrames": true,
  "requiresSceneDepth": true,
  "requiresMapLightField": true,
  "bloomMipLevels": 5,
  "compositeShader": null,
  "bloomPrefilterShader": null,
  "bloomDownShader": null,
  "bloomUpShader": null,
  "targets": [
    {
      "name": "halfFog",
      "format": "R16G16B16A16SFloat",
      "scale": 0.5,
      "filter": "Linear",
      "persistent": false
    }
  ],
  "passes": [
    {
      "name": "FogPass",
      "shader": "FogPass.frag.glsl",
      "output": "halfFog",
      "blend": "Opaque",
      "extraParams": [1.0, 0.0],
      "inputs": [
        { "binding": 0, "source": "scene" },
        { "binding": 1, "source": "depth" },
        { "binding": 2, "source": "mapLightField" }
      ]
    },
    {
      "name": "Composite",
      "shader": "Composite.frag.glsl",
      "output": "backbuffer",
      "blend": "Opaque",
      "inputs": [
        { "binding": 0, "source": "hdr" },
        { "binding": 1, "source": "bloom" },
        { "binding": 2, "source": "scene" },
        { "binding": 3, "source": "halfFog" }
      ]
    }
  ],
  "options": [
    {
      "key": "fogStrength",
      "label": "Fog strength",
      "target": "packParam0",
      "default": 0.5,
      "min": 0.0,
      "max": 1.0,
      "step": 0.02,
      "tooltip": "Adjust ground mist density."
    }
  ]
}
```

#### Root Fields

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `id` | `string` | Folder name | Unique pack identifier (case-insensitive); cannot be `"default"`. |
| `name` | `string` | `id` | Display name in the Mod Settings selector and slider header. |
| `author` | `string` | `"Unknown"` | Author name. |
| `description` | `string` | `""` | Short summary shown in Mod Settings. |
| `runOnEmptyFrames` | `bool` | `false` | When `true`, forces the HDR pipeline to execute even on frames with zero HDR particle or additive redraw items; required for ambient darkening, fog, or fullscreen grading packs. |
| `requiresSceneDepth` | `bool` | `false` | When `true`, copies scene depth to `depth` on every active frame including empty frames (frames with HDR draws already populate scene depth automatically). |
| `requiresMapLightField` | `bool` | `false` | When `true`, maintains and uploads the active map's `mapLightField` RGBA8 texture. |
| `bloomMipLevels` | `int` | `5` | Built-in Bloom pyramid mip count, valid in `[0, 7]`; `0` disables built-in Bloom. |
| `compositeShader` | `string?` | `null` | Custom final composite fragment shader used when `passes` is empty or the final pass omits `shader`; falls back to built-in `RimLumenComposite.frag.glsl`. |
| `bloomPrefilterShader` | `string?` | `null` | Overrides built-in Bloom prefilter shader (`RimLumenBloomPrefilter.frag.glsl`). |
| `bloomDownShader` | `string?` | `null` | Overrides built-in Bloom downsample shader (`RimLumenBloomDown.frag.glsl`). |
| `bloomUpShader` | `string?` | `null` | Overrides built-in Bloom upsample shader (`RimLumenBloomUp.frag.glsl`). |
| `targets` | `Target[]` | `[]` | Custom offscreen render target declarations. |
| `passes` | `Pass[]` | `[]` | Custom render graph passes; when empty, runs built-in Bloom + composite shader only. |
| `options` | `Option[]` | `[]` | Live tuning sliders exposed in Mod Settings. |

#### `targets[]` Render Target Fields

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | `string` | Required | Target name; must be unique (case-insensitive), must not match a reserved source name, and cannot start with `prev:`. |
| `format` | `string` | `"R16G16B16A16SFloat"` | One of `"R16G16B16A16SFloat"`, `"R8G8B8A8UNorm"`, or `"R16SFloat"`. |
| `scale` | `float` | `1.0` | Resolution scale relative to the scene target, valid in `(0.05, 1.0]`; pixel dimensions are `max(1, round(sceneDim * scale))`. |
| `filter` | `string` | `"Linear"` | Sampling filter: `"Linear"` (bilinear) or `"Point"` (nearest); wrap mode is fixed to `ClampToEdge`. |
| `persistent` | `bool` | `false` | When `true`, allocates a ring of `max(2, FramesInFlight)` textures (instead of `FramesInFlight`), cleared to `(0, 0, 0, 0)` on first frame or after resize/rebuild, and sampleable from the previous rendered frame via `prev:<name>`. |

#### `passes[]` Render Pass Fields

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | `string` | Required | Non-empty pass name. |
| `shader` | `string?` | `null` | Relative fragment shader path. Required on all offscreen passes; optional only on the final `backbuffer` pass (falls back to `compositeShader` or `RimLumenComposite.frag.glsl`). |
| `output` | `string` | Required | Destination target: a declared `targets[].name` or `"backbuffer"`. |
| `blend` | `string` | `"Opaque"` | Blend mode: `"Opaque"`, `"PremultipliedAlpha"` (`One / OneMinusSrcAlpha`), or `"Additive"` (RGB `One / One`, A `Zero / One`). |
| `extraParams` | `float[]` | `[0, 0]` | Up to 2 floats passed into `pc.value.z` and `pc.value.w` (more than 2 throws). Note: not passed when the final pass omits `shader`. |
| `inputs` | `Input[]` | `[]` | Input bindings `{ "binding": 0..15 (except 5), "source": "<name>" }`. |

#### `options[]` Live Option Fields and Slider Behavior

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `key` | `string` | Required | Option key (unique within the pack), persisted under `<packId>:<key>`. |
| `label` | `string` | `key` | Slider label shown in Mod Settings. |
| `target` | `string` | Required | Target UBO slot (unique within the pack): `bloomIntensity`, `bloomThreshold`, `bloomKnee`, `bloomScatter`, `packParam0`..`packParam15`. |
| `default` | `float` | `0.0` | Default value (clamped to `[min, max]` on load). |
| `min` | `float` | `0.0` | Minimum value (finite, `min <= max`). |
| `max` | `float` | `1.0` | Maximum value (finite). |
| `step` | `float` | `0.0` | Step increment (`>= 0`); when `> 0`, snaps via `min + round((val - min) / step) * step`. |
| `tooltip` | `string?` | `null` | Hover tooltip text. |

When switching packs, `ShaderPackCatalog.ApplyOptionsToRuntime` resets the four Bloom parameters to baseline defaults (`bloomIntensity = 0.16`, `bloomThreshold = 1.0`, `bloomKnee = 0.5`, `bloomScatter = 0.65`) and all 16 `packParam0..15` slots to `0.0`, applies the pack's option overrides, and commits everything atomically via `ShaderPackRuntimeParams.SetAll` under lock. Dragging sliders in Mod Settings updates only the UBO values in memory—**no Vulkan pipelines or render targets are rebuilt**.

---

### 3. Render Graph Validation Rules and Runtime Isolation

#### Static DAG Validation (`ShaderPackGraphValidator`)

1. **Reserved names**: `hdr`, `glow`, `bloom`, `scene`, `depth`, `mapLightField`, and `backbuffer` are reserved source names and cannot be used as `targets[].name`; target names also cannot begin with `prev:`.
2. **Single trailing `backbuffer`**: When `passes` is non-empty, only the final pass may—and must—output to `"backbuffer"`. All earlier offscreen passes must target a declared `targets[].name`, use `"blend": "Opaque"`, and specify a non-empty `shader`.
3. **No `backbuffer` sampling**: No pass may list `"backbuffer"` in `inputs[].source`.
4. **No forward references or same-pass self-reads**: Sampling a regular target requires an earlier pass in the array to have written it; a pass cannot read its own `output` directly in the same pass.
5. **Persistent history prerequisite**: `"prev:X"` is valid only if target `X` was declared with `"persistent": true`.
6. **Binding slot limits**: `inputs[].binding` must be in `[0, 15]`, cannot be `5` (reserved for the UBO), and cannot repeat within the same pass.

The default Bloom prefilter extracts `hdr + glow`. The default threshold is `1.0` in both settings and pack baseline, and peaks ≤ threshold extract exactly zero. `bloomKnee=0` is a hard threshold without division by zero. A positive knee softens only above the threshold using `excess * smoothstep(0,knee,excess)`, where `excess=max(peak-threshold,0)`.

#### 3152 Group Empty-Frame Bloom Skip Rule

Even when `runOnEmptyFrames` is `true`, the runtime skips the built-in Bloom downsample/upsample chain and binds a `1x1` black placeholder (`black1x1`) to `bloom` whenever either condition holds:

- `bloomMipLevels == 0`;
- There are no HDR draws on the current frame, vanilla glow copies included (`!hasHdrDraws`), **and** none of `bloomPrefilterShader`, `bloomDownShader`, or `bloomUpShader` is specified.

If your pack needs custom Bloom scattering on empty frames (e.g., from `scene`, `depth`, or `mapLightField`), specify at least one custom Bloom shader (such as `bloomPrefilterShader`) and set `runOnEmptyFrames: true`.

#### Two-Tier Fault Isolation

- **Scan & validation isolation**: Malformed `pack.json`, path escapes, reserved `"default"` IDs, or invalid DAGs are isolated inside that entry's `LoadError` (shown as `[损坏]` / disabled in the menu) without affecting other packs; `FindOrDefault` falls back to `default`.
- **Compile & record isolation**: `RimLumenRenderHost.ReloadShaderPack` precompiles all custom `.frag.glsl` shaders through Slang before swapping the live runtime; compilation errors keep the currently active pack running and populate `LastShaderPackError`. If a non-default pack throws while recording on the render thread, the runtime records `LastShaderPackError` and reverts to `default` on the main thread (`RevertFailedPackToDefault`) **without tripping `FailSafeTripped`**.

---

### 4. GLSL `#version 450` Shader Contract

#### Built-in Sources and 1x1 Placeholders

| Source Name | Format & Filter | Inactive Placeholder | Description |
| --- | --- | --- | --- |
| `hdr` | `R16G16B16A16SFloat` / `Linear` | — | RimLumen-native HDR draw layer: RGB is premultiplied colour; A is the coverage of native alpha / premultiplied particles, and additive particles write 0. Holds no vanilla glow copies. After compositing, later map overlays process it. |
| `glow` | `R16G16B16A16SFloat` / `Linear` | `black1x1` (zero) | Copies of vanilla and third-party additive glow, accumulated without clipping. The scene already contains their cores, so do not add this back onto the scene in a composite; use it for bloom, halos or streaks. Captured before the 3152 boundary, before later map overlays. A is unused. |
| `bloom` | `R16G16B16A16SFloat` / `Linear` | `black1x1` (`R8G8B8A8UNorm` zero) | Built-in multi-mip Bloom pyramid output. |
| `scene` | Main scene format / `Linear` | — | Separate same-frame copy of main scene colour (map, pawns, buildings, vanilla lighting) at the 3152 boundary. |
| `depth` | `R32SFloat` / `Point` | `depth1x1` (`R32SFloat`, far plane `0.0`) | Colour snapshot copied from same-frame scene depth at the 3152 boundary for regular shader sampling. |
| `mapLightField` | `R8G8B8A8UNorm` / `Linear` | `black1x1` (zero) | Map-sized (`mapSizeX x mapSizeZ`) roof, artificial-light, has-outdoor-temperature-Room, and fog-of-war texture. |

The RGB of `hdr`, `glow` and `scene` share one encoding, so they add directly with no gamma or linear conversion: the scene target is `B8G8R8A8UNorm` and vanilla blends the stored values directly, while RimLumen's HDR targets use the same shader outputs and blend modes without clipping. `1.0` is vanilla full brightness; anything above `1.0` is the extra brightness vanilla clips.

#### Default Input Bindings (When Not Overridden in `passes[]`)

- **Default composite pass** (when `passes` is empty, or the final pass omits both `shader` and `inputs`; uses `PremultipliedAlpha` when `passes` is empty):
  - `binding = 0`: `hdr`
  - `binding = 1`: `bloom`
  - `binding = 2`: `scene`
  - `binding = 3`: `depth`
  - `binding = 4`: `mapLightField`
- **Custom `bloomPrefilterShader`**:
  - `binding = 0`: `hdr`, `binding = 1`: `glow`, `binding = 2`: `scene`, `binding = 3`: `depth`, `binding = 4`: `mapLightField`.
- **Custom `bloomDownShader`**:
  - `binding = 0`: previous downsample mip, `binding = 4`: `mapLightField`.
- **Custom `bloomUpShader`**:
  - `binding = 0`: lower-resolution upsample input, `binding = 1`: same-level downsample mip, `binding = 4`: `mapLightField`.

> Blend & alpha note: When `passes` is empty, the composite pass is hardcoded to `PremultipliedAlpha` blending onto map colour at the 3152 boundary. If your composite outputs the full scene (e.g., color grading or fog), output `vec4(finalRgb, 1.0)` or use an explicit `passes` entry with `"blend": "Opaque"`. If you only want to layer HDR particles and bloom over the untouched scene, output `vec4(mappedHdr, hdr.a)`: the result is `mappedHdr + scene * (1 - hdr.a)`, so pixels where both `hdr` and bloom are empty keep the scene unchanged. Each pack's composite shader owns its tone curve; the built-in default composite passes colour through unchanged while its peak is at most 1, and above 1 holds the peak at 1 and compresses the excess toward white along the colour's own hue.

> Numeric safety note: `hdr` is `RGBA16F`; heavy additive overlap can overflow to `Inf`, some drivers can also produce `NaN`, and drivers handle both inconsistently. Any pass that reads `hdr` directly (custom prefilter, composite, luminance statistics), and especially one that writes a `persistent` history target, should zero `NaN`/`Inf` and cap the value first; otherwise one bad pixel spreads through blurs or stays in the history buffer. See `sanitizeHdr` in the built-in `Cinematic/Common.glslinc`.

#### Fixed Vertex Interface and `#include`

The engine supplies the fullscreen triangle; fragment shaders declare:

```glsl
#version 450
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
```

Relative `#include "Common.glslinc"` files inside the same pack directory are supported.

#### Uniform Buffer (`set = 0, binding = 5`, 208-byte `std140`)

```glsl
layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection; // Offset   0 (64 B): Inverse ViewProjection (Row-Major storage)
    vec4 ScreenAndMapSize;  // Offset  64 (16 B): (1.0/sceneW, 1.0/sceneH, mapSizeX, mapSizeZ)
    float GameSeconds;      // 80: TicksGame / 60
    float RealSeconds;      // 84: host elapsed seconds
    float SkyGlow;          // 88
    float ZoomRootSize;     // 92
    float HasHdrDraws;      // 96
    float HasSceneDepth;    // 100
    float HasMapLightField; // 104
    float IsPaused;         // 108
    float DepthValid;       // 112
    float GameDeltaSeconds; // 116: TicksThisFrame / 60; skipped update => 0
    float RealDeltaSeconds; // 120: host frame delta
    float Padding;          // 124
    vec4 BloomParams;       // 128: intensity, threshold, knee, scatter
    vec4 PackParams[4];     // 144: 4 rows, 16-byte stride, 64 bytes total
} ubo;
```

- **Parameter mapping**: `options[].target` value `packParamN` (`N = 0..15`) maps to `ubo.PackParams[N / 4][N % 4]`; existing shaders must replace the old vector members with the `PackParams` array.
- **Current ABI**: the old `EnvParams` bitmask is removed; external packs must adopt this 208-byte layout. `HasHdrDraws` (including early-group glow copies), `HasSceneDepth`, `HasMapLightField`, and `IsPaused` are separate 0/1 floats. `DepthValid=1` means a real depth snapshot at the boundary; 0 means the far-plane placeholder, not a per-pixel geometry test.
- `GameSeconds` is game ticks / 60 and stops on pause; `RealSeconds` is host elapsed real seconds. Their Delta fields hold this frame's step. Cinematic Adapt and StreakExtract read `hdr + glow`; the composite core remains `hdr` only. Adapt uses a 0.2-game-second time constant: `1-exp(-GameDeltaSeconds/0.2)`.

#### Push Constant (16-byte `vec4`)

```glsl
layout(push_constant) uniform Params { vec4 value; } pc;
```

| Pass Context | `pc.value.x` | `pc.value.y` | `pc.value.z` | `pc.value.w` |
| --- | --- | --- | --- | --- |
| Custom pass (including final pass with `shader`) | `1.0 / outputWidth` | `1.0 / outputHeight` | `extraParams[0]` | `extraParams[1]` |
| Default composite (`passes` empty or final `shader` omitted) | `bloomIntensity` | `0.0` | `1.0 / sceneWidth` | `1.0 / sceneHeight` |
| `bloomPrefilter` / `bloomDown` | `1.0 / inputWidth` | `1.0 / inputHeight` | `bloomThreshold` | `bloomKnee` |
| `bloomUp` | `1.0 / lowMipWidth` | `1.0 / lowMipHeight` | `bloomScatter` | `0.0` |

#### Reverse-Z Depth and World Ground-Plane Reconstruction

Waylith uses Reverse-Z depth: near plane is `1.0` and sky/unrendered background is `0.0`, while the inactive `depth1x1` placeholder is `0.0` with `DepthValid=0`. To test whether a pixel belongs to real 3D geometry (e.g., IsoRim walls) rather than flat background or placeholder:

```glsl
float depth = texture(depthImage, uv).r;
float is3DGeom = ubo.DepthValid * step(0.0001, min(depth, 1.0 - depth));
```

To reconstruct the world ground-plane (`y = 0`) position and `mapLightField` UV from screen `uv` (compatible with both vanilla 2D top-down and IsoRim oblique cameras):

```glsl
vec2 ndc = uv * 2.0 - 1.0;
vec4 nearW = ubo.InvViewProjection * vec4(ndc, 0.0, 1.0);
vec4 farW  = ubo.InvViewProjection * vec4(ndc, 1.0, 1.0);
vec3 nearP = nearW.xyz / nearW.w;
vec3 farP  = farW.xyz / farW.w;
float span = farP.y - nearP.y;
float t = clamp(-nearP.y / (abs(span) < 1e-6 ? 1e-6 : span), 0.0, 1.0);
vec3 world = mix(nearP, farP, t);
vec2 fieldUv = world.xz / max(ubo.ScreenAndMapSize.zw, vec2(1.0));
bool insideMap = fieldUv.x >= 0.0 && fieldUv.x <= 1.0 && fieldUv.y >= 0.0 && fieldUv.y <= 1.0;
```

#### `mapLightField` Four-Channel Encoding

`mapLightField` syncs to the GPU on map load and `SectionLayer` dirty updates. Each texel corresponds to one map cell with normalized `[0.0, 1.0]` channels (`MapLightFieldEncoder`):

| Channel | Meaning | Encoding Rule |
| --- | --- | --- |
| `R` | Roofed | `1.0` (255) iff `RoofGrid.Roofed` is true, otherwise 0. Source: Assembly-CSharp.decompiled.cs:92166–92168. |
| `G` | ArtificialGroundGlow | `min(0.5,(max(r,g,b)/255.0)*3.6)`, rounded to UNorm8, excluding sky light. Source: the artificial component of GlowGrid.GroundGlowAt, Assembly-CSharp.decompiled.cs:69541–69542. Half brightness quantizes to 128/255. |
| `B` | HasOutdoorTemperatureRoom | `1.0` (255) iff the cell has a Room AND `Room.UsesOutdoorTemperature` is true, otherwise 0. Otherwise explicitly includes indoor-temperature Rooms and room-less cells such as walls/rock. Property source: Assembly-CSharp.decompiled.cs:90349–90358. This is a defined fact, not a placeholder or an indoor/outdoor classification for room-less cells: vanilla TryGetDirectAirTemperatureForCell returns false/21f without a Room (:92707–92714), not that bool classification. This input does not infer from neighbours. |
| `A` | Fogged | `1.0` (255) iff `FogGrid.IsFogged` is true, otherwise 0. Source: Assembly-CSharp.decompiled.cs:68695–68701. |

#### Built-in ShaderPacks Reference

| Pack ID | Passes / Targets | `runOnEmptyFrames` | `requiresSceneDepth` | `requiresMapLightField` | `bloomMipLevels` | Source Reference |
| --- | --- | --- | --- | --- | --- | --- |
| `default` | Built-in composite / 0 | `false` | `false` | `false` | `5` | `1.7/Shaders/PostFx/RimLumenComposite.frag.glsl` |
| `atmospheric` | 3 passes / 2 targets | `true` | `true` | `true` | `7` | `1.7/ShaderPacks/Atmospheric/` |
| `cinematic` | 5 passes / 4 targets (incl. persistent `adapt`) | `false` | `false` | `false` | `6` | `1.7/ShaderPacks/Cinematic/` |
| `crisp` | 1 custom composite / 0 | `false` | `false` | `false` | `3` | `1.7/ShaderPacks/Crisp/` |
| `vibrant` | 1 custom composite / 0 | `false` | `false` | `false` | `6` | `1.7/ShaderPacks/Vibrant/` |

---

### 5. Minimal Runnable ShaderPack Example

Place the following two files in `Config/RimLumen/ShaderPacks/WarmNight/` (or a mod's `1.7/ShaderPacks/WarmNight/`) to select and hot-reload in Mod Settings.

`pack.json`:

```json
{
  "id": "warm_night",
  "name": "Warm Night (Example)",
  "author": "YourName",
  "description": "Preserves indoor and torch-lit cells while darkening unlit outdoor wilderness at night, composited with HDR and Bloom.",
  "runOnEmptyFrames": true,
  "requiresSceneDepth": false,
  "requiresMapLightField": true,
  "bloomMipLevels": 5,
  "passes": [
    {
      "name": "Composite",
      "shader": "Composite.frag.glsl",
      "output": "backbuffer",
      "blend": "Opaque",
      "inputs": [
        { "binding": 0, "source": "hdr" },
        { "binding": 1, "source": "bloom" },
        { "binding": 2, "source": "scene" },
        { "binding": 3, "source": "mapLightField" }
      ]
    }
  ],
  "options": [
    {
      "key": "nightDarkness",
      "label": "Wilderness night darkness",
      "target": "packParam0",
      "default": 0.45,
      "min": 0.0,
      "max": 0.85,
      "step": 0.05
    }
  ]
}
```

`Composite.frag.glsl`:

```glsl
#version 450

layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection;
    vec4 ScreenAndMapSize;
    float GameSeconds;
    float RealSeconds;
    float SkyGlow;
    float ZoomRootSize;
    float HasHdrDraws;
    float HasSceneDepth;
    float HasMapLightField;
    float IsPaused;
    float DepthValid;
    float GameDeltaSeconds;
    float RealDeltaSeconds;
    float Padding;
    vec4 BloomParams;
    vec4 PackParams[4];
} ubo;

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 3) uniform sampler2D mapLightField;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

vec3 unproject(vec2 ndc, float z)
{
    vec4 p = ubo.InvViewProjection * vec4(ndc, z, 1.0);
    return p.xyz / p.w;
}

void main()
{
    vec3 scene = texture(sceneImage, uv).rgb;
    vec4 hdr = texture(hdrImage, uv);
    vec3 bloom = texture(bloomImage, uv).rgb * ubo.BloomParams.x;

    vec2 ndc = uv * 2.0 - 1.0;
    vec3 nearP = unproject(ndc, 0.0);
    vec3 farP = unproject(ndc, 1.0);
    float span = farP.y - nearP.y;
    float t = clamp(-nearP.y / (abs(span) < 1e-6 ? 1e-6 : span), 0.0, 1.0);
    vec3 world = mix(nearP, farP, t);

    vec2 fieldUv = world.xz / max(ubo.ScreenAndMapSize.zw, vec2(1.0));
    bool insideMap = fieldUv.x >= 0.0 && fieldUv.x <= 1.0 && fieldUv.y >= 0.0 && fieldUv.y <= 1.0;

    float darkness = 0.0;
    if (ubo.HasMapLightField > 0.5 && insideMap)
    {
        vec4 field = texture(mapLightField, fieldUv);
        float enclosure = field.r * mix(1.0, 0.55, field.b);
        float outdoor = 1.0 - smoothstep(0.25, 0.90, enclosure);
        float litProtect = smoothstep(0.04, 0.45, field.g);
        float night = smoothstep(0.88, 0.22, clamp(ubo.SkyGlow, 0.0, 1.0));
        darkness = night * outdoor * (1.0 - litProtect) * (1.0 - field.a) * clamp(ubo.PackParams[0].x, 0.0, 0.9);
    }

    vec3 base = scene * mix(vec3(1.0), vec3(0.76, 0.86, 1.06), darkness) * (1.0 - darkness * 0.65);
    vec3 hdrAccum = hdr.rgb + bloom;
    float peak = max(hdrAccum.r, max(hdrAccum.g, hdrAccum.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desat = shoulder / (shoulder + 4.0);
    vec3 hue = hdrAccum / max(peak, 0.00001);
    vec3 mappedHdr = mix(hue, vec3(1.0), desat) * (1.0 - exp(-peak));

    outColor = vec4(base * (1.0 - hdr.a) + mappedHdr, 1.0);
}
```
