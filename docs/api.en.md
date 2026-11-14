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
| `GroundXZ` | `0` | Identity matrix, no extra pitch rotation | Ground sigils, ground halos, horizontal shockwaves |
| `HeightVertical90` | `1` | Rotates local direction by `+90°` around the X axis, mapping local `+Y` to world `+Z` (screen up); **does not affect `LocalPosition.Y` height offset** | Legacy upward jets, vertical fire columns, screen-upward bursts |
| `Free3D` | `2` | Preserves the node's own 3D `LocalRotation` verbatim; no camera adaptation at render time | Arbitrary 3D-oriented child emitters (such as RimUnity-imported assets) |
| `Vertical3D` | `3` | Uses identity rotation like `Free3D`, so local `+Y` is world `+Y`; simulates in true 3D, with **render-only** camera adaptation: under IsoRim (where Y is squashed), height above `baseY` is divided by the squash factor to restore true altitude; under the vanilla straight-down 2D camera, height is projected upward onto `+Z` with draw height `max(baseY, MoteOverhead)`; `StretchedVelocity` uses true 3D velocity | Fireworks, fountains, and other effects that launch truly skyward and fall back to the ground |

`Vertical3D`'s `baseY` is the height of the play origin (`LumenPlayRequest.OriginWorld.Y`); a node's `LocalPosition.Y` is relative to that origin (for example, `4.8` for a firework burst apex). `GroundXZ`, `HeightVertical90`, and `Free3D` behave unchanged.

### Ground-Contact Expiration (`EmitterSpec.KillBelowFloor` / `killBelowFloor`)

`killBelowFloor` (default `false`) can be set on `EmitterSpec.KillBelowFloor` in C#, inside `.lumen.json` emitter objects, and under `<emitter>` in XML `LumenEffectDef`. When `true`, a particle expires and is reclaimed on the frame its world height drops below the play origin height `baseY` **and** its world velocity `Y < 0` (descending). Particles still rising below floor height or freshly spawned right on the floor are unaffected. L1 (CPU) and L2 (GPU compute) behave identically regardless of space plane; local-space emitters are evaluated after applying their world matrix.

> Tip: Across all emitter shapes, `Cone` defaults its primary emission axis to local `+Y`. To make particles shoot skyward in real altitude and fall back down, set the node to `Vertical3D`, set world-space gravity to `(0, -1, 0)` (or desired magnitude), and enable `KillBelowFloor` if they should vanish on impact. If you specifically want legacy screen-upward (`+Z`) projection without camera adaptation, use `HeightVertical90` with gravity `(0, 0, -1)`.

### 3. Game Time Synchronization

By default, particle simulation and node timelines strictly follow the game clock: step size is `0` when paused (in-flight particles and force fields freeze in place while continuing to render), and scales with `1x` / `2x` / `3x` game speeds. To ignore game pause, explicitly pass `realTime: true` in the play request.

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
- Colors are linear RGBA, accepting `[r, g, b, a]`, `[r, g, b]` (where `a = 1`), or `{ "r": ..., "g": ..., "b": ..., "a": ... }`.
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
        "bursts": [[0.0, 200], { "time": 0.5, "count": 100 }],
        "shape": "Point",
        "shapeExtents": [1, 1, 1],
        "coneAngleDegrees": 25,
        "startLifetimeMin": 1.0,
        "startLifetimeMax": 1.0,
        "startSpeedMin": 0,
        "startSpeedMax": 0,
        "startSizeMin": 1.0,
        "startSizeMax": 1.0,
        "startColorLinear": [1, 1, 1, 1],
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
        "bloomWeight": 1,
        "killBelowFloor": false
      },
      "pointLight": {
        "glowRadius": 5,
        "overlightRadius": 0,
        "colorRgb": [1, 1, 1],
        "intensity": 1,
        "intensityOverTime": [[0, 1], [1, 0]]
      }
    }
  ],
  "timelineEvents": [
    { "time": 0.5, "eventName": "explode", "targetNodeIndex": 0 }
  ]
}
```

#### Additional Field Notes

- **Root**: `nodes` must be non-empty; `defaultDuration` must be finite and `>= 0`.
- **Node hierarchy (`EffectNodeSpec`)**: `parentIndex` must be `-1` (attached to root) or strictly less than the current node index `[0, i)`, structurally preventing parent-child cycles. If `localRotation` (`[x, y, z, w]` or `{ x, y, z, w }`) is omitted, `localYawDegrees` rotates around world `+Y`.
- **Curves (`sizeOverLifetime` / `intensityOverTime`)**: Supports three forms—a single number (constant), an array `[[time, value], ...]` (non-decreasing `time`), or an object `{ "keys": [...], "multiplier": 1 }`. Keyframes may optionally include tangents `[time, value, inTangent, outTangent]`.
- **Gradients (`colorOverLifetime`)**: Supports a stop array `[[time, [r,g,b,a]], ...]` or an object `{ "stops": [{ "time": 0, "rgbaLinear": [r,g,b,a] }] }`, also requiring non-decreasing `time`.
- **`pointLight`**: JSON supports `colorRgb` or `colorLinear` (both use linear RGB components); radius keys are `glowRadius` and `overlightRadius` (in map cells).

---

## 4. Asset Format 2: XML `LumenEffectDef`

You can define `RimLumen.Core.Assets.LumenEffectDef` in any mod's `Defs/**/*.xml`. Except for the four XML naming conventions below, all defaults and behavior match `.lumen.json`:

1. **ID**: Uses `<defName>`, registering both the bare short name and `<packageId>:<defName>`.
2. **Node fields**: Adds `<heightOffset>` (added to `localPosition.y`), `localScale` is a single float scalar, and `localRotation` quaternion is omitted (use `<localYawDegrees>`).
3. **Point light keys**: Uses `<radiusCells>` (matching JSON `glowRadius`), `<overlightRadius>`, `<colorLinear>` (RimWorld `Color` syntax `(r, g, b, a)`), `<intensity>`, and `<intensityOverTime>` (`<keys>` made of `<li><time>..</time><value>..</value></li>`).
4. **Current limitation**: XML Def does not expose `timelineEvents` (use `.lumen.json` or C# `EffectAsset` for multi-stage timeline events).

```xml
<Defs>
  <RimLumen.Core.Assets.LumenEffectDef>
    <defName>MyTorchBurst</defName>
    <nodes>
      <li>
        <name>sparks</name>
        <spacePlane>HeightVertical90</spacePlane>
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
              <centerOrAxis>(0, 0, -1)</centerOrAxis>
              <parameters>(0.6, 0, 0)</parameters>
              <strength>8</strength>
            </li>
          </forceFields>
        </emitter>
        <pointLight>
          <colorLinear>(1, 0.55, 0.15, 1)</colorLinear>
          <radiusCells>6</radiusCells>
          <intensity>1.4</intensity>
        </pointLight>
      </li>
    </nodes>
  </RimLumen.Core.Assets.LumenEffectDef>
</Defs>
```

---

## 5. Enums and Force Fields Reference

### 1. Particle System Enums (`RimLumen.Core.Contracts`)

- **`LumenParticleTier`**: `L0_ExternalCpu` (external bridge player) / `L1_NativeCpu` (native CPU simulation, suited for low counts or frequent script interaction) / `L2_GpuCompute` (Vulkan Compute simulation, default, suited for high particle counts and force fields).
- **`LumenShapeKind`**: `Point` (`0`) / `Sphere` (`1`) / `CircleXZ` (`2`) / `Cone` (`3`) / `Box` (`4`).
- **`LumenBlendMode`**: `Additive` (`0`, recommended for glow and fire) / `AlphaBlend` (`1`, smoke and mist) / `Premultiplied` (`2`).
- **`LumenBillboardMode`**: `Camera` (`0`, camera-facing) / `Horizontal` (`1`, flat ground-aligned quad) / `StretchedVelocity` (`2`, stretched along velocity using `VelocityStretchFactor` and `LengthScale`) / `Mesh` (`3`, reserved mesh enum value).

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
// caps.IsHdrActive / IsSceneDepthAvailable / IsGpuComputeAvailable / ValidationClean / MaxGpuParticlesPerFrame

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
    InitialState = "charge", // When non-null, only nodes whose Name == "charge" emit initially; others wait for Trigger
    RealTime = false,
});
```

- If `EffectId` does not exist, `Play` does not throw; it returns an inert handle with `IsAlive == false` whose methods are safe no-ops.

### 3. Handle Controls (`ILumenEffectHandle`)

| Member | Behavior & Notes |
| --- | --- |
| `bool IsAlive` | `true` while playing or fading/draining; becomes `false` once all particles and point lights are cleaned up. |
| `void Trigger(string eventOrStateName)` | Fires matching `TimelineEventSpec` entries and activates matching nodes. **Note**: `L1_NativeCpu` nodes also emit a pulse of `Bursts[0].Count` (or rounded `RateOverTime`); `L2_GpuCompute` nodes only enable emission without an extra one-shot burst on `Trigger`. |
| `void SetParam(string name, float value)` | Updates runtime scalar parameters. Three built-in names take effect immediately: `intensity` (scales particle and light brightness), `rate` (scales L1 emission rate), and `scale` (multiplies root scale). Other names are stored without affecting simulation. |
| `void Stop(bool allowFadeOut = true)` | When `true`, immediately stops new emission, fades active point lights over `0.25s`, and lets live particles finish their lifetime before reclamation. When `false`, immediately destroys all particles and lights. |
| `void Dispose()` | Equivalent to `Stop(false)`, idempotent. Always dispose long-lived handles in `Thing.DeSpawn` or when a map is destroyed. |

---

## 7. Texture Registry and Compatibility Notes

### 1. Custom Particle Textures (`LumenTextureRegistry`)

An emitter's `textureKey` is resolved on the main thread in the following order:

1. Check textures explicitly registered via `LumenTextureRegistry.Register(string key, Waylith.Rendering.Texture texture)`.
2. Otherwise call vanilla `ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)` to load from a mod's `Textures/` directory (for example `"RimLumen/Effects/Spark"`).
3. If still missing, fall back to the procedural soft circle and log one warning per missing `key`.

> Never call texture registration from worker threads or render callbacks.

### 2. Automatic Vanilla Additive Glow Enhancement

By default, RimLumen redraws vanilla and third-party additive draws (`Blend.Enabled`, `DstColor == One`, `ColorOp == Add`, `SrcColor` is `SrcAlpha` or `One`, and `!Depth.Write`) into the HDR bloom pass, with five built-in fullscreen/ambient shader exclusions: `GlowOverlay`, `OxygenOverlayIndoors`, `GravshipLensFlare`, `UnlitAdditive`, and `MoteWater`.

To opt a custom `ShaderDef` out of automatic HDR redraw, attach `<li Class="RimLumen.Core.Compat.LumenEmissiveModExtension"><EnableHdrBloom>false</EnableHdrBloom></li>`. (Note: `HdrMultiplier` on that extension class is not yet wired into redraw intensity scaling; only `EnableHdrBloom` takes effect.)

---

## 8. Optional `Chezhou.RimUnity` Bridge

When `Chezhou.RimUnity` is active, `RimLumen.Adapter.RimUnity` reads its exported `catalog.json`, prefab JSON (including `.json.gz`), and `.rutex` textures, automatically routing each particle node:

- **Native HDR / GPU (or CPU)**: basic shapes, curves, gradients, textures, flipbooks, rate-over-time emission, and deterministic bursts.
- **Retained on RimUnity (`L0_ExternalCpu`)**: nodes using `trails`, `subEmitters` (including sub-emitter dependencies), `collision`, `trigger`, `renderMode.Mesh`, linear `velocityOverLifetime`, `rotationOverLifetime`, curve-based `forceOverLifetime`, `lights.ratio != 1`, probabilistic bursts, or shapes not yet modeled in Core (such as non-zero-radius base cones, shells, rings, meshes, or local shape offsets).

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

1. **Reserved names**: `hdr`, `bloom`, `scene`, `depth`, `mapLightField`, and `backbuffer` are reserved source names and cannot be used as `targets[].name`; target names also cannot begin with `prev:`.
2. **Single trailing `backbuffer`**: When `passes` is non-empty, only the final pass may—and must—output to `"backbuffer"`. All earlier offscreen passes must target a declared `targets[].name`, use `"blend": "Opaque"`, and specify a non-empty `shader`.
3. **No `backbuffer` sampling**: No pass may list `"backbuffer"` in `inputs[].source`.
4. **No forward references or same-pass self-reads**: Sampling a regular target requires an earlier pass in the array to have written it; a pass cannot read its own `output` directly in the same pass.
5. **Persistent history prerequisite**: `"prev:X"` is valid only if target `X` was declared with `"persistent": true`.
6. **Binding slot limits**: `inputs[].binding` must be in `[0, 15]`, cannot be `5` (reserved for the UBO), and cannot repeat within the same pass.

#### Empty-Frame Bloom Skip Rule

Even when `runOnEmptyFrames` is `true`, the runtime skips the built-in Bloom downsample/upsample chain and binds a `1x1` black placeholder (`black1x1`) to `bloom` whenever either condition holds:

- `bloomMipLevels == 0`;
- There are no HDR draws on the current frame (`!hasHdrDraws`) **and** none of `bloomPrefilterShader`, `bloomDownShader`, or `bloomUpShader` is specified.

If your pack needs custom Bloom scattering on empty frames (e.g., from `scene`, `depth`, or `mapLightField`), specify at least one custom Bloom shader (such as `bloomPrefilterShader`) and set `runOnEmptyFrames: true`.

#### Two-Tier Fault Isolation

- **Scan & validation isolation**: Malformed `pack.json`, path escapes, reserved `"default"` IDs, or invalid DAGs are isolated inside that entry's `LoadError` (shown as `[损坏]` / disabled in the menu) without affecting other packs; `FindOrDefault` falls back to `default`.
- **Compile & record isolation**: `RimLumenRenderHost.ReloadShaderPack` precompiles all custom `.frag.glsl` shaders through Slang before swapping the live runtime; compilation errors keep the currently active pack running and populate `LastShaderPackError`. If a non-default pack throws while recording on the render thread, the runtime records `LastShaderPackError` and reverts to `default` on the main thread (`RevertFailedPackToDefault`) **without tripping `FailSafeTripped`**.

---

### 4. GLSL `#version 450` Shader Contract

#### Built-in Sources and 1x1 Placeholders

| Source Name | Format & Filter | Inactive Placeholder | Description |
| --- | --- | --- | --- |
| `hdr` | `R16G16B16A16SFloat` / `Linear` | — | Linear HDR particle and emissive draw target (A is mask/opacity). |
| `bloom` | `R16G16B16A16SFloat` / `Linear` | `black1x1` (`R8G8B8A8UNorm` zero) | Built-in multi-mip Bloom pyramid output. |
| `scene` | Main scene format / `Linear` | — | Waylith's main scene color output (map, pawns, buildings, vanilla lighting). |
| `depth` | `R32SFloat` / `Point` | `depth1x1` (`R32SFloat`, value `1.0`) | Color snapshot copied from scene depth for regular shader sampling. |
| `mapLightField` | `R8G8B8A8UNorm` / `Linear` | `black1x1` (zero) | Map-sized (`mapSizeX x mapSizeZ`) roof, glow, enclosure, and fog-of-war texture. |

#### Default Input Bindings (When Not Overridden in `passes[]`)

- **Default composite pass** (when `passes` is empty, or the final pass omits both `shader` and `inputs`; uses `PremultipliedAlpha` when `passes` is empty):
  - `binding = 0`: `hdr`
  - `binding = 1`: `bloom`
  - `binding = 2`: `scene`
  - `binding = 3`: `depth`
  - `binding = 4`: `mapLightField`
- **Custom `bloomPrefilterShader`**:
  - `binding = 0`: `hdr`, `binding = 2`: `scene`, `binding = 3`: `depth`, `binding = 4`: `mapLightField` (note: **`binding = 1` is not bound**; do not declare it).
- **Custom `bloomDownShader`**:
  - `binding = 0`: previous downsample mip, `binding = 4`: `mapLightField`.
- **Custom `bloomUpShader`**:
  - `binding = 0`: lower-resolution upsample input, `binding = 1`: same-level downsample mip, `binding = 4`: `mapLightField`.

> Blend & alpha note: When `passes` is empty, the composite pass is hardcoded to `PremultipliedAlpha` blending onto the backbuffer. If your composite outputs the full scene (e.g., color grading or fog), output `vec4(finalRgb, 1.0)` or use an explicit `passes` entry with `"blend": "Opaque"`. If you only want to layer HDR particles and bloom over the untouched scene, output `vec4(mappedHdr, hdr.a)`.

#### Fixed Vertex Interface and `#include`

The engine supplies the fullscreen triangle; fragment shaders declare:

```glsl
#version 450
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
```

Relative `#include "Common.glslinc"` files inside the same pack directory are supported.

#### Uniform Buffer (`set = 0, binding = 5`, 176-byte `std140`)

```glsl
layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection; // Offset   0 (64 B): Inverse ViewProjection (Row-Major storage)
    vec4 ScreenAndMapSize;  // Offset  64 (16 B): (1.0/sceneW, 1.0/sceneH, mapSizeX, mapSizeZ)
    vec4 EnvParams;         // Offset  80 (16 B): (elapsedTime, skyGlow, zoomRootSize, flags)
    vec4 BloomParams;       // Offset  96 (16 B): (bloomIntensity, bloomThreshold, bloomKnee, bloomScatter)
    vec4 PackParam0;        // Offset 112 (16 B): (packParam0,  packParam1,  packParam2,  packParam3)
    vec4 PackParam1;        // Offset 128 (16 B): (packParam4,  packParam5,  packParam6,  packParam7)
    vec4 PackParam2;        // Offset 144 (16 B): (packParam8,  packParam9,  packParam10, packParam11)
    vec4 PackParam3;        // Offset 160 (16 B): (packParam12, packParam13, packParam14, packParam15)
} ubo;
```

- **Parameter packing note**: `options[].target` values `packParam0..3` map to `ubo.PackParam0.xyzw`, `packParam4..7` map to `ubo.PackParam1.xyzw`, and so on. Thus `"target": "packParam1"` is read from `ubo.PackParam0.y`, not `ubo.PackParam1.x`.
- **`EnvParams.w` bitmask**: Unpack with `uint flags = uint(ubo.EnvParams.w + 0.5);`:
  - `1u` (bit 0): `hasHdrDraws` (at least one HDR draw or direct HDR contributor is active)
  - `2u` (bit 1): `hasSceneDepth` (`depth` holds a real scene depth snapshot rather than `depth1x1`)
  - `4u` (bit 2): `hasMapLightField` (`mapLightField` is ready and bound to the real map field)
  - `8u` (bit 3): `isPaused` (game clock is paused)

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

Waylith uses Reverse-Z depth: near plane is `1.0` and sky/unrendered background is `0.0`, while the inactive `depth1x1` placeholder is `1.0`. To test whether a pixel belongs to real 3D geometry (e.g., IsoRim walls) rather than flat background or placeholder:

```glsl
float depth = texture(depthImage, uv).r;
float is3DGeom = step(0.0001, min(depth, 1.0 - depth));
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
| `R` | Roof | `1.0` (`255`) if roofed, `0.0` (`0`) if unroofed. |
| `G` | GroundGlow | `1.0` (`255`) when `glowGrid.VisualGlowAt` has `a == 1` (sunlit open cell); otherwise `min(0.5, (max(r, g, b) / 255.0) * 3.6)` (artificial lights cap at `0.5` so shaders can distinguish sunlight from torches). |
| `B` | Enclosure | `0.0` (`0`) if unroofed; `~0.502` (`128/255`) if roofed with `UsesOutdoorTemperature == true` (porches/semi-open sheds); `1.0` (`255`) if fully enclosed indoors. |
| `A` | FogOfWar | `1.0` (`255`) if fogged (unexplored), `0.0` (`0`) if revealed. |

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
    vec4 EnvParams;
    vec4 BloomParams;
    vec4 PackParam0;
    vec4 PackParam1;
    vec4 PackParam2;
    vec4 PackParam3;
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
    uint flags = uint(ubo.EnvParams.w + 0.5);

    float darkness = 0.0;
    if ((flags & 4u) != 0u && insideMap)
    {
        vec4 field = texture(mapLightField, fieldUv);
        float outdoor = 1.0 - smoothstep(0.25, 0.90, max(field.r * 0.55, field.b));
        float litProtect = smoothstep(0.04, 0.45, field.g);
        float night = smoothstep(0.88, 0.22, clamp(ubo.EnvParams.y, 0.0, 1.0));
        darkness = night * outdoor * (1.0 - litProtect) * (1.0 - field.a) * clamp(ubo.PackParam0.x, 0.0, 0.9);
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
