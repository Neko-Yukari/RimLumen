# RimLumen Mod Developer API Manual

This manual is for mod authors creating visual effects and post-processing packs with RimLumen in RimWorld 1.7 (Waylith / Vulkan engine). All interfaces, fields, and constraints match the current `RimLumen.Core` source code.

---

## 1. Quick Integration Options

Choose one of the following three approaches based on your needs:

1. **Data-Only Effect Definitions (no DLL reference required)**: Add `NekoYukari.RimLumen` to `modDependencies` / `loadAfter` in your mod's `About/About.xml`, then author XML `LumenEffectDef` or `.lumen.json` effect files.
2. **C# Playback & Dynamic Registration**: Reference `RimLumen.Core.dll` (located at `1.7/Assemblies/RimLumen.Core.dll` in this mod) and the game assemblies in a `.NET 10` (`net10.0`) C# project, build using RimWorld's bundled `Moddev` .NET 10 SDK, and play or register effects via `RimLumen.Core.Api.RimLumenApi` (see Chapter 6, "Project Setup and Build Guide").
3. **Programmable ShaderPacks (no DLL reference required)**: Place `pack.json` and GLSL `#version 450` fragment shaders under `1.7/ShaderPacks/<PackFolder>/` in your mod to declare custom post-processing passes, render targets, persistent history buffers, and live player sliders.

---

## 2. Coordinate System, Space Planes, and Time Sync

### 1. World Coordinate Convention

RimWorld 1.7 uses a 2.5D orthographic/perspective top-down view with the following world axes:

- `+X`: East (screen right)
- `+Y`: Up from the ground
- `+Z`: North (screen up)

The ground is the fixed world plane `Y = 0`. The play origin (`LumenPlayRequest.OriginWorld.Y`) and every world position use Y as height above ground in cells, not as a RimWorld draw-altitude layer. A node's `LocalPosition` remains an offset from its parent, or from the play origin for a root node.

### 2. Node Space Planes (`LumenSpacePlane`)

| Enum Value | Numeric Value | Rotation Behavior | Typical Usage |
| --- | --- | --- | --- |
| `GroundXZ` | `0` | Uses the node's own `LocalRotation` verbatim with no extra pitch rotation | Ground sigils, ground halos, horizontal shockwaves, arbitrary 3D-oriented child emitters (such as RimUnity-imported assets) |
| `Vertical3D` | `3` | Same node matrix as `GroundXZ`; simulation is true 3D. Only rendering adapts to the camera: a height of `h` cells appears `0.5 × h` cells up the screen in vanilla straight-down 2D. In 3D the camera projects the full world height, with IsoRim's Y squash cancelled. | Fireworks, fountains, trails behind rising projectiles |

For example, a play origin at `Y = 2` appears one cell above its ground position in 2D. If it rises another four cells without moving sideways, its trail extends two cells up the screen. `GroundXZ` does not use this projection. Space-plane values `1` and `2` are invalid.

Vanilla 2D uses a 60° orthographic pitch: the ground component along screen-up is scaled by `sin 60° ≈ 0.866`, height contributes `cos 60° = 0.5` along screen-up, and the screen-right component is unchanged. A sphere projects to a circle and a horizontal ring to a foreshortened ellipse. The projection follows camera yaw. `StretchedVelocity` velocity and `Local` axes use the same linear map; the draw layer is `MoteOverhead`.

Under the vanilla straight-down 2D camera, every node draws on the `MoteOverhead` layer. `GroundXZ` particles, ribbons and distortion retain their world XZ and draw at `MoteOverhead` height, so terrain never hides them. Each `Vertical3D` node anchors at its own ground XZ (`Y = 0`); its surrounding shape draws through the 60° orthographic pitch, and the node lands on its own cell.

Local-space particles use the node's current ground XZ; world-space particles use the node's ground XZ at birth. A world-space particle born from a sub-emitter event uses the event point's XZ. Each ribbon sample anchors at its own XZ, so both ends of a beam land on their cells. L2 records the birth anchor at `1/32` cell precision within `±1024` cells. Under tilted cameras such as IsoRim, `GroundXZ` keeps true world geometry and `Vertical3D` un-squashes world height.

### Ground-Contact Expiration (`EmitterSpec.KillBelowFloor` / `killBelowFloor`)

`killBelowFloor` (default `false`) can be set on `EmitterSpec.KillBelowFloor` in C#, inside `.lumen.json` emitter objects, and under `<emitter>` in XML `LumenEffectDef`. When `true`, a particle expires and is reclaimed on the frame its world position has `Y < 0` **and** its world velocity has `vY < 0` (descending). Particles still rising below floor height or freshly spawned right on the floor are unaffected. L1 (CPU) and L2 (GPU compute) behave identically regardless of space plane; local-space emitters are evaluated after applying their world matrix.

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

The development menu action **Reload effect assets** rebuilds one snapshot in current mod order: it scans `1.7/Data/RimLumen/`, then `Data/RimLumen/`, then adds the already-loaded XML `LumenEffectDef` entries. Every ID in the fresh scan replaces any same-named asset with a new immutable instance, including code-registered assets. When a file is deleted or fails to load, only its old scanner-owned IDs are released; a bare short name goes to the next surviving candidate under first-writer-wins, with the old and new sources logged. Unrelated code registrations remain unchanged, and later code overrides of stale scanned keys are not deleted; already-playing instances keep their old asset. The action does not re-read XML files, so XML edits still require a game restart.

With Dev Mode on and a map open, **Debug Actions → RimLumen → Effect preview window...** opens an effect preview window: it lists registered effects with their source (JSON, XML, code registration) under a multi-token search; plays the selected effect at the camera centre or a picked cell (`Y = 0`, supplying a ground reference Z and a default target when the asset uses `DepthMask` or `EndAtTarget`); switches states on the live handle or restarts in a chosen state; and offers Replay and Stop All. Its reload button runs the reload above and shows the errors and warnings that reload logged at the top of the window. Closing the window stops and disposes every instance it started. The window is non-modal, so mouse `interactionEvents` on the map keep working.

Callers using `RimUnityAssetProvider` can call `RegisterPackageRoot` again, even with the same path, to clear its lazy catalogs and translated-asset cache. The next resolution re-reads the catalog and asset; previously acquired instances remain unchanged.

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
        "intensityOverTime": [[0, 1], [1, 0]],
        "castShadows": true
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
```

#### Additional Field Notes

- **Root**: `nodes` must be non-empty; `defaultDuration` must be finite and `>= 0`.
- **Node hierarchy (`EffectNodeSpec`)**: `parentIndex` must be `-1` (attached to root) or strictly less than the current node index `[0, i)`, structurally preventing parent-child cycles. If `localRotation` (`[x, y, z, w]` or `{ x, y, z, w }`) is omitted, `localYawDegrees` rotates around world `+Y`.
- **Node depth mask (`EffectNodeSpec.DepthMask`)**: JSON uses `"depthMask"` and XML uses `<depthMask>`; the default is `None`. `NorthBehindReference` applies only under the vanilla straight-down 2D camera: parts of the node's particles and ribbons with world Z greater than the reference Z (north) are occlusion-tested at the `Projectile` layer, just below `Pawn`, so the caster at the reference and every other pawn-level thing hide them. Parts at or south of the reference draw on `MoteOverhead` as usual, over the caster. It assumes, without verification, that pawns write scene depth. Only `GroundXZ` nodes with an emitter or ribbon may use it; validation rejects `Vertical3D` and distortion-only nodes. Tilted cameras such as IsoRim ignore the mask and use real geometry and depth for occlusion. See `LumenPlayRequest.DepthReferenceZ` below.
- **Scalar curves (`sizeOverLifetime` / `intensityOverTime`)**: Use a single number (constant), an array `[[time, value], ...]`, or an object `{ "keys": [...], "multiplier": 1 }`. Keyframes may include tangents as `[time, value, inTangent, outTangent]`. Times must be finite and non-decreasing: particle-curve time is normalized lifetime `[0,1]`, while point-light curve time is seconds since node activation and must be nonnegative.

  Values and tangents must be finite; tangents are slopes per unit of curve time. Distinct times use cubic Hermite interpolation, followed by multiplication by `multiplier`.

  A time may occur twice or more. If all key times are equal, every time evaluates to the last value. Otherwise, `t <= first` gives the first value and `t >= last` gives the last. At an interior repeated time, the left-hand limit is the group's first value, the exact time gives its last value, and subsequent interpolation starts from that value toward the next key. If the first time is repeated, the exact first time still gives the first value, so the rule is not universally right-continuous.

  For example, `[[0, 0], [0.5, 1], [0.5, 2], [0.5, 3], [1, 4]]` approaches `1` from the left of `0.5`, evaluates to `3` at `0.5`, and then interpolates from `3` toward `4`. The same rule applies to a pair of repeated keys. Source: [RimUnity `AnimationCurve.cs:23,30-35` at pinned revision `45ca90987f7d3721715b45c021cab986ee14c468`](https://github.com/chezhou9413/RimUnity/blob/45ca90987f7d3721715b45c021cab986ee14c468/Source/RimUnity/Animation/AnimationCurve.cs#L23-L35).

  For particle size curves, both L1 and L2 use the same 64-sample lookup table (LUT): samples are at `i/63` (`i = 0…63`), including both endpoints, with interpolation between adjacent samples. A step can be smoothed across a sample interval of width `1/63`; exact preservation is not guaranteed. Point-light curves are evaluated directly in seconds, not through the particle LUT.
- **Material colour**: `materialTint` defaults to `(1,1,1,1)` and follows the colour domain above, as do `startColor` and the gradient; `colorSpace` is `Stored` (default) or `Linear`. Let `vertex = startColor × gradient`. Stored RGB is `vertex × tint × texel`; Linear RGB is `E(vertex × D(tint) × (texture is sRGB ? D(texel) : texel))`. `vertex` is not decoded. The scalar gain `g = hdrIntensity × intensity` multiplies RGB only after that material colour is in the frame's blend space (Legacy `g × rgb`, Linear blend space `g × D(rgb)`), so no sRGB transfer raises or crushes it. `D` / `E` are sRGB decode / encode extended to nonnegative values above 1 without clamping. Alpha is always the product of the three raw alpha values, followed by soft-particle coverage and any blend-mode premultiplication. Componentwise, `D(x) = x/12.92` for `x <= 0.04045`, otherwise `((x+0.055)/1.055)^2.4`; `E(x) = 12.92*x` for `x <= 0.0031308`, otherwise `1.055*x^(1/2.4)-0.055`. Source: RimUnity `Source/Shared/ShaderColorSpace.cs:69-103,122-134` at revision `45ca90987f7d3721715b45c021cab986ee14c468`. This describes material calculation only, not the player blend space.
- **Gradients (`colorOverLifetime`)**: Supports a stop array `[[time, [r,g,b,a]], ...]` or an object `{ "stops": [{ "time": 0, "color": [r,g,b,a] }] }`, requiring strictly increasing `time`.
- **`pointLight`**: The JSON colour key is `color` (RGBA); the radius key is `glowRadius` (in map cells). A point light is a purely visual light pool: it never writes vanilla `GlowGrid` and has no effect on mood, plant growth or work speed. Its centre is the node's world position projected to the ground, and the node's height Y (cells above the ground) sets how long its height shadows are; see section 7.4. A light below the ground (Y < 0) sits on the ground. There is no engine minimum: a light at Y = 0 sits on the ground, and every thing with a height casts a shadow that reaches the pool edge and fades along the ground. Raise the node for short shadows; for lamp-like effect lights use Y = 1–2 cells (a vanilla wall is 1 cell high). its radius is `R = glowRadius`, and its falloff is `w = (1 - (d/R)^2)^2`, zero at `d >= R`. Each light term `L = color × intensityOverTime × w` is summed linearly over overlapping pools, and the sum is applied once: the scene becomes `scene × (1 + C(ΣL))`, where `C` is identity up to a peak of 1 and above it whitens along the hue with the peak held at 1, so overlaps brighten a pixel at most 2x. This is drawn right after vanilla lighting, so fog, darkness and roofs cover it as usual. Recommended `intensity`: `0.5–1.2` for steady lamps, `1.0–2.0` for short bright flashes; do not copy vanilla `GlowGrid`'s `3.6` scale onto visual light pools. The pool follows its node every frame. `intensity` and curve key values must be finite and `>= 0`, with no upper bound; at effective intensity `<= 0` nothing is drawn. `overlightRadius` has been removed and is rejected in both JSON and XML. Walls, doors and cover occlude light pools through an occlusion map; cover uses its sprite; see section 7.4. Each effect point light supports a boolean asset field `castShadows` (`"castShadows"` in JSON, `<castShadows>` in XML, `PointLightSpec.CastShadows` in C#, default `true`, with no runtime setter API): when `true`, it is occluded by walls, rock, doors and cover and casts object silhouette shadows when global `objectShadows` is enabled; when `false`, it skips the occlusion map, generates no object shadow geometry, and consumes no shadow mask channel regardless of global `objectShadows`. Vanilla glowers and the takeover sun have no such field, always cast shadows, and are governed solely by global `objectShadows`. Vanilla sun and moon shadows (`SunShadow` at 3170, `SunShadowFade` at 3175) that fall inside a light pool remove only sunlight: the shadow multiplier changes from `c` to the per-channel `(c+L)/(1+L)`, where `L` is the compressed pool term at that pixel; pixels outside every pool stay pixel-identical to vanilla. Only draws using the vanilla material through the vanilla shader are replaced; shadows another mod has replaced (for example RimIris water shadows) stay as they are and log one warning. Known approximations: where a pool saturates the image the shadow restores too little; refraction, additive draws and fog drawn after 3101 are lifted too; vanilla lamp light is not separated; the HDR particle/bloom layer and IsoRim 3D shadows are not covered. For gameplay light, see `CompRimLumenLogicalLight` in section 7.3.
- **Force-field `strength`**: Required explicitly in JSON, XML, and C#. Omission rejects the asset; no default is used.
- **`timelineEvents` / `states` / `initialState` / `stopState`**: see the next section, "State Machine".
- **Particle density setting**: The public setter `RimLumenRenderHost.ParticleDensityScale` accepts a range of `0.1`–`1.0`; finite values outside it are silently clamped without logging, while NaN and positive or negative infinity throw `ArgumentOutOfRangeException`. The settings slider is continuous and displays a rounded whole percentage. Density `1` is exact; after clamping to `d`, the effective burst density is `floor(d * 65536) / 65536`.
- **Particle density and bursts**: Each emitter keeps its own `Int64` accumulator `a`, initially `0`, in units of `1/65536` particle. Compute `q = floor(density * 65536)`; for every nonzero burst, apply `a += count * q`, `n = ceil(a / 65536)` using integer arithmetic, then `a -= n * 65536`. Here `n` is the request budget; `a <= 0` and may be negative. The effective density is `floor(density * 65536) / 65536`, never above the authored value; density `1` is exact and has no `0.999` threshold.

  At density `0.25`, four consecutive bursts with `count = 1` yield budgets `1, 0, 0, 0`; eight such bursts total `2`. Two consecutive bursts with `count = 7` yield `2, 2`. At density `0.1`, `q = 6553`, so ten `count = 1` bursts accumulate `65530` and request `1`; at density `0.2`, five such bursts request `1`. The cumulative request budget uses the effective density: `ceil(sum(count * q) / 65536)`. A scheduled budget is not the actual live-particle count or GPU particle count: requests dropped for insufficient capacity or free slots are not replayed. `rateOverTime` must be finite and nonnegative, with no additional rate cap. Before capacity admission, finite continuous `rate × dt` budgets and all crossed bursts accepted by probability and scaled by density are accumulated in wide numeric types; exceeding Int32 is ordinary capacity dropping, not a budget-range exception. Even when no slots are free, every planned occurrence gets its probability draw and every accepted occurrence updates density carry.

  Ordinary emitter bursts are scaled when queued on the CPU; GPU sub-emitter targets keep a shared density carry for events and explicit bursts on the GPU, and explicit requests retain their enqueue-time density. `Play`, explicit `Restart`, and `GoTo` state re-entry reset `a` to `0`; loop wrap does not. For example, at density `0.25`, one `count = 1` burst per loop requests `2` particles over eight loops. Continuous rate emission uses a separate `double` fractional carry, floors the accumulated count and retains the remainder. The runtime `rate` multiplier does not scale bursts.

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

**Entering a state** (the initial state, `next`, `GoTo`, re-entering the current state via `GoTo(current)`, and `interactionEvents[].goToState`) (**even when a node was already active in the previous state — this is intentional design**):

1. The state clock resets to zero and `StateChanged(name)` is raised once.
2. **Every node belonging to the state restarts from scratch**:
   - Particle emission schedules: `bursts` times, emitter `duration`, `rateOverTime` scheduling, and shared density carry / probability RNG all start from entry time (particles already in flight finish their remaining lifetime). L1 (CPU) and L2 (GPU) behave identically.
   - Point lights & node features: the `pointLight.intensityOverTime` curve clock (`nodeActivatedAt`) restarts from `0` seconds; `distortion` and `screenDim` reset their `duration` / `strengthOverTime` clocks; `ribbon` `Trail` clears existing points and resets elapsed time, while `Beam` resets its noise clock.
   - To keep a point light steady or continuous across state changes, place it on a separate handle that does not transition states, or adjust a single state dynamically with `SetLightIntensity` / `SetLightColor`.
3. Every other node stops activating immediately: particle emitters stop new emission and let live particles finish their lifetime; point lights fade out over `0.25s`; `Trail` stops recording new points and lets existing points age out over `lifetime`; `Beam` hides immediately; `distortion` / `screenDim` with `duration > 0` finish their already-started `duration` (`duration == 0` hides immediately).

Emission pause is distinct from game pause: after an emitter's first activation, its emission schedule clock advances while emission is disabled for both looping and non-looping emitters. Planned bursts crossed during that interval are skipped without catch-up, while live particles continue simulating. A never-activated node remains at emission time 0. Explicit Restart resets the clock, density carry, and probability RNG while retaining already accepted pending bursts. Global game pause still uses `dt=0`; `SetParam("rate", 0)` pauses continuous emission only and does not affect authored bursts.

A frame that crosses several states processes each state and its events in order. If a node restarts during the frame, the built-in L1 and L2 simulators advance its emission schedule only from the last restart to the end of the frame. Their existing particles still move and age for the full frame. Distortion and ribbon nodes also use the time since the last restart within the frame.

**`timelineEvents[]` and states**:

| Field | Default | Meaning |
| --- | --- | --- |
| `time` | required | Seconds after the owning state is entered |
| `eventName` | required | Event name, used by `Trigger` and `Notify` |
| `targetNodeIndex` | `-1` | When not `-1`, emits one pulse on that node: `bursts[0].count`, or the rounded `rateOverTime` without `bursts` |
| `state` | `initialState` | Owning state; the event fires on its time only while that state plays |
| `notify` | `false` | When `true`, firing raises `ILumenEffectHandle.Notify(eventName)` |

A loop state fires its events again every `duration` cycle, and events with `time >= duration` never fire on the timer; a loop state with `duration` `0` fires them once. In a non-loop state an event at exactly `duration` still fires.

**`interactionEvents[]` (environment interaction events)**:

| Field | Default | Meaning |
| --- | --- | --- |
| `trigger` | required | `CursorEnter`, `CursorExit`, `CursorStay`, `CursorSwipe`, or `Explosion` (`LumenInteractionTrigger`) |
| `eventName` | required | Non-empty event name passed to `Notify` when `notify: true` (`Trigger(name)` does not fire `interactionEvents`) |
| `radius` | `0` | Horizontal ground XZ radius in map cells. Must be finite `> 0` for `Cursor*`, and finite `>= 0` for `Explosion` (added to the explosion's own outer radius) |
| `anchorNodeIndex` | `-1` | `-1` uses the effect's current world origin `OriginWorld` (follows `Follow`); `[0, nodes.Length)` uses that node's world translation |
| `minCursorSpeed` | `0` | Minimum cursor speed in **screen heights per real second** (`vh/s`). `CursorSwipe` requires finite `> 0`; other `Cursor*` require finite `>= 0`; `Explosion` requires `0` |
| `maxCursorSpeed` | `null` | Optional upper cursor speed bound in `vh/s` (`null` means unbounded). On `Cursor*`, non-null values must be finite and `>= minCursorSpeed`; `Explosion` requires `null` |
| `holdSeconds` | `0` | Continuous dwell duration in seconds required by `CursorStay` (finite `>= 0`); must remain `0` on all other triggers |
| `minExplosionStrength` | `0` | Minimum pulse peak acceleration magnitude `|Strength|` in cells/s² required by `Explosion` (finite `>= 0`); must remain `0` on `Cursor*` |
| `cooldownSeconds` | `0` | Minimum cooldown in seconds on the handle's clock after firing (finite `>= 0`) |
| `state` | `null` | `null` applies in any playing state before `Stop(true)`; non-null restricts evaluation to while that named state is playing |
| `goToState` | `null` | When non-null and before `Stop(true)`, enters that state first upon firing (same semantics as `GoTo`), before `targetNodeIndex` and `Notify` |
| `targetNodeIndex` | `-1` | When not `-1`, emits one pulse on that node (same rule as `timelineEvents[].targetNodeIndex`) |
| `notify` | `false` | When `true`, firing raises `ILumenEffectHandle.Notify(eventName)` and `ILumenEffectHandle.InteractionNotify(LumenInteractionNotification)` with trigger and cursor source details |

- **Lazy host mouse sampling**: Whenever an active handle on the current map declares any `Cursor*` rule (or a `FollowHostMouse` source lease is active), the host samples the mouse ground position (`Y = 0`) once at the start of main-thread capture if not obscured by UI (`!MouseObscuredNow`) and inside map bounds, allowing pure `.lumen.json` / XML assets to react to the cursor without C#. With no active sources and no `Cursor*` subscribers, neither mouse nor window state is read.
- **Screen-space cursor speed & EMA smoothing**: Cursor velocity is measured in isotropic screen heights per real second (`vh/s`)—both X and Y screen-pixel deltas are divided by viewport pixel height `ViewportHeight` (`Screen.height`), i.e., `v_vh = v_px / ViewportHeight = v_uiPx × UIScale / ViewportHeight` (traversing one full screen height in `1` real second equals `1.0`). This unit is invariant to camera zoom, screen resolution, and UI scale. Each active cursor independently smooths velocity with a first-order EMA (`tau = 0.05 s`, `alpha = 1 - exp(-dt_real / 0.05)`): first appearance or re-entry after UI occlusion/off-map starts at speed `0`; `dt_real <= 1e-5 s` holds the previous velocity; frame hitches `dt_real > 0.25 s` reset velocity to `0`.
- **Trigger and state-reset rules**: Distances use ground horizontal `XZ` (ignoring node height `Y`). `CursorEnter` fires once when at least one active cursor enters `d_xz <= radius` (from none inside) and a cursor inside satisfies `[minCursorSpeed, maxCursorSpeed]`; `CursorExit` fires once when all cursors leave `radius` (either still active outside within `[minCursorSpeed, maxCursorSpeed]`, or obscured/off-map when `minCursorSpeed == 0`); `CursorStay` accumulates handle `dt` while a cursor inside satisfies the speed range, resets to `0` immediately otherwise, and fires + resets dwell at `holdSeconds`; `CursorSwipe` fires once on the rising edge when a cursor inside reaches the speed range, re-arming only after leaving `radius` or dropping outside the speed range; `Explosion` checks active `Explosion`-channel pulses on the same map with `|Strength| >= minExplosionStrength` and `d_xz <= pulse.Radius + radius`—`SphericalWave` pulses fire on the frame the expanding shockwave front reaches the detection circle (`waveRadius + shellHalfThickness >= d_xz - radius`; near explosions with `d_xz <= radius + shellHalfThickness` fire on frame 1), radial pulses fire on frame 1, and each pulse triggers a rule at most once. Rules scoped to `state` reset inside/swipe latches and dwell time when leaving that state while preserving remaining cooldown and seen pulse IDs; non-`realTime` handles do not advance interaction events while paused (`dt == 0`).

**Load-time validation** (`.lumen.json` and XML fail at parse time; a C# `EffectAsset` throws `InvalidDataException` from `Play`): an empty or duplicate state name; a `nodes` entry naming no node; a negative or non-finite duration; `loop` together with `next`; `next`, `initialState`, `stopState`, an event `state`, or an `interactionEvents` `goToState` naming an unknown state; a `stopState` that loops or has `next`; zero-duration states that cycle through `next`; `initialState`, `stopState`, an event `state`, or `goToState` without `states`; unused trigger-specific fields or out-of-range node indices on `interactionEvents`.

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
3. **Point light keys**: Uses `<radiusCells>` (matching JSON `glowRadius`), `<color>` (raw float syntax `(r, g, b)` or `(r, g, b, a)`), `<intensity>`, `<castShadows>` (bool, default `true`), and `<intensityOverTime>`. A curve directly contains `<li>` keys, each with `<time>`, `<value>`, `<inTangent>`, and `<outTangent>`; both tangents default to 0. XML curves have no `multiplier` field.
4. **List fields**: `<states>`, `<timelineEvents>`, `<interactionEvents>`, and a state's `<nodes>` are `<li>` lists; state fields are `<name>`, `<nodes>`, `<duration>`, `<loop>`, `<next>`, timeline event fields are `<time>`, `<eventName>`, `<targetNodeIndex>`, `<state>`, `<notify>`, and interaction event (`LumenInteractionEventXml`) fields are `<trigger>`, `<eventName>`, `<radius>`, `<anchorNodeIndex>`, `<minCursorSpeed>`, `<maxCursorSpeed>`, `<holdSeconds>`, `<minExplosionStrength>`, `<cooldownSeconds>`, `<state>`, `<goToState>`, `<targetNodeIndex>`, `<notify>`; the root also takes `<defaultDuration>`, `<initialState>`, and `<stopState>`. Meanings and defaults match the "State Machine" section above.

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

The following two snippets show complete XML usage for **smoke size/alpha fade over lifetime** and for **`<distortion>` and `<screenDim>` nodes**:

```xml
<!-- Example A: Particle size growth and alpha fade over lifetime (AlphaBlend smoke) -->
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
```

```xml
<!-- Example B: Combining Distortion (RingWave) and ScreenDim nodes in XML -->
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
```

> Tip: Unused `<distortion>` fields for the chosen `<kind>` must remain `0` (for example, omit `<frequency>` for `RingWave`, and omit `<thickness>` and `<speed>` for `HeatHaze`); see section 5.4.

---

## 5. Enums and Force Fields Reference

### 1. Particle System Enums (`RimLumen.Core.Contracts`)

- **`LumenParticleTier`**: `External` (external bridge player) / `L1_NativeCpu` (native CPU simulation, suited for small particle systems that read game state for visual or audio feedback; particles never change game logic; the picture trails main-thread state by one frame) / `L2_GpuCompute` (Vulkan Compute simulation, default, suited for high particle counts and force fields). Both tiers are chosen explicitly through `preferredTier`.
- **`LumenShapeKind`**: `Point` (0), `Sphere` (1), `CircleXZ` (2), `Cone` (3), `Box` (4), `SphereShell` (5), `Hemisphere` (6), `HemisphereShell` (7), `CircleEdgeXZ` (8), `ConeShell` (9), `ConeVolume` (10), `ConeVolumeShell` (11), `SingleSidedEdge` (12), `BoxShell` (13), `BoxEdge` (14), `Donut` (15), `Rectangle` (16).
- **`LumenBlendMode`**: `Additive` (`0`, recommended for glow and fire) / `AlphaBlend` (`1`, smoke and mist) / `Premultiplied` (`2`).
- **`LumenDepthMask`**: `None` (`0`, default) / `NorthBehindReference` (`1`, north of the reference is occlusion-tested at the `Projectile` layer under the vanilla straight-down 2D camera).
- **`LumenBillboardMode`**: `Camera` (`0`, camera-facing) / `Horizontal` (`1`, flat ground-aligned quad) / `StretchedVelocity` (`2`, stretched along velocity using `VelocityStretchFactor` and `LengthScale`) / `Mesh` (`3`, not supported yet: both the JSON and XML parsers reject it with a path-qualified error such as `node 0 ('root').emitter.billboardMode`) / `Vertical` (`4`) / `Local` (`5`).
- **`LumenEnvironmentChannel`**: `[Flags]` bitmask: `None` (`0`), `Explosion` (`1`), `Cursor` (`2`).
- **`LumenEnvironmentForceShape`**: `HorizontalRadial` (`0`, ground XZ linear falloff) / `SphericalRadial` (`1`, 3D spherical linear falloff) / `SphericalWave` (`2`, expanding 3D spherical shell wave, pulses only).
- **`LumenInteractionTrigger`**: `CursorEnter` (`0`) / `CursorExit` (`1`) / `CursorStay` (`2`) / `CursorSwipe` (`3`) / `Explosion` (`4`).
- **`LumenCursorSourceKind`**: `None` (`0`, non-cursor triggers such as `Explosion`) / `HostMouse` (`1`, real host mouse) / `ModCursor` (`2`, mod-created world-space cursor via `CreateCursor` / `CreateEnvironmentSource`).

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

### 2. Force Fields (`ForceFieldSpec` / `ForceFieldKind`)

| Kind | `CenterOrAxis` | `Parameters` | `Radius` | `Strength` | `Frequency` | Effect Description |
| --- | --- | --- | --- | --- | --- | --- |
| `GravityDrag` (`0`) | Acceleration direction (not normalized), e.g. `(0, 0, -1)` | `x` = air drag coefficient `drag` (`>= 0`, damps velocity by `exp(-drag * dt)`) | Unused | Acceleration magnitude | Unused | Applies constant directional gravity with velocity damping |
| `Vortex` (`1`) | Rotation axis (default `(0, 1, 0)`) | `x` = inward pull toward axis (negative pushes outward); `y` = lift force along axis | Effective radius (`<= 0` means map-wide without falloff) | Tangential spin strength | Unused | Swirls particles around the origin axis with linear distance falloff |
| `CurlTurbulence` (`2`) | Unused | Spatial sampling offset | Unused | Turbulence amplitude | Spatial noise frequency | Divergence-free 3D curl noise for fluid sway and wispy drift |
| `PointAttractor` (`3`) | Attractor center position | Unused | Effective radius (`<= 0` means map-wide without falloff) | Attraction strength (negative repels) | Unused | Pulls toward (or pushes away from) target point with linear distance falloff |
| `RingWave` (`4`) | Shell center | `x` expansion speed, `y` half-thickness, both finite and > 0 | End radius, > 0 | Outward acceleration; negative pulls inward | Unused | Spherical shell expanding with node time |

> Note: When `WorldSpaceSimulation == true`, force fields evaluate in world space (for example `(0, 0, -1)` for falling down in top-down view); when `false`, they evaluate in emitter local space.

---

### 3. Environment Forces, Collision, and Sub-emitters

These fields belong to `emitter`; JSON and XML use lower camel case. Flags can use the JSON string `"Explosion, Cursor"` or XML `<environmentChannels>Explosion, Cursor</environmentChannels>`; vectors use `[x,y,z]` or `(x,y,z)`, and curves use `[[time,value], ...]` or XML `<li>` entries containing `time`, `value`, and optional `inTangent` / `outTangent`; tangents default to 0. `environmentChannels` is a flag set: `None`, `Explosion`, `Cursor`, default `Explosion, Cursor`; `environmentForceScale` is finite and nonnegative, default 1. Vanilla explosions (`Explosion.StartExplosion`) and `RimLumenApi.EmitExplosion` emit `Explosion`-channel `SphericalWave` pulses on their map: for radius `R` the shell travels at `2R` cells/s, has half-thickness `R/8`, peak acceleration `24R × strengthScale`, center lowered by `R/4`, and lasts `0.5` game seconds; each map retains at most `8` active pulses. Ambient host mouse movement applies no particle force by itself; sustained mouse or custom force sources are created from C# via `RimLumenApi.CreateMouseForce`, `CreateCursor`, or `CreateEnvironmentSource` (up to `8` active sustained sources per map), and custom transient pulses use `EmitEnvironmentPulse`. Positive `Strength` repels outward and negative pulls inward: `HorizontalRadial` attenuates linearly by ground XZ distance `max(0, 1 - d_xz / R)` (ignoring particle height `Y`), `SphericalRadial` by 3D distance `max(0, 1 - d_xyz / R)`, and `SphericalWave` by shell weight `max(0, 1 - |d_xyz - r_w| / h)`. Matching world forces are summed and scaled by `environmentForceScale`, then transformed back for local simulation. A local emitter with a noninvertible transform (such as zero scale) skips world environment forces and wall collision. L1 and L2 evaluate identical force formulas.

`RingWave` radius is speed times seconds since node restart; weight is `max(0, 1 - abs(distance - waveRadius) / halfThickness)`, and zero beyond the end radius. Loop wraps do not reset node time.

`collision` defaults to `mode: None`, `bounce: 1`, `dampen: 0`, `lifetimeLoss: 0`, `killUnderRoof: false`. Mode is `None`, `Bounce`, or `Kill`; the three numeric values are finite `[0,1]`. Bounce uses `v' = (v - (1+bounce)*dot(v,n)*n)*(1-dampen)`: damping reduces both normal and tangential velocity. Each hit removes `lifetimeLoss` of total lifetime; `killUnderRoof` is independent of mode. Blocking cells use the impassable predicate; their visual collision volume spans world `Y=[0,2]`. Particles sweep as points with at most one wall contact per step and can pass above walls. Kill emits Collision then Death; roof expiry emits Death only.

`subEmitters` defaults to empty. Each entry requires `trigger` (`Death` or `Collision`), and `targetNodeIndex` pointing to another node with an emitter whose `rateOverTime` is 0, has no `bursts`, and has the same `preferredTier`; the graph must be acyclic. `count` is required and nonnegative; `probability` defaults to 1 in `[0,1]`, drawn once per event; `inherit` defaults to `None` and may combine `Color` and `Size`. Children spawn at the event position; inherited values multiply their own birth values. Capacity drops are final.

#### Per-particle trails (`emitter.trails` / `ParticleTrailSpec`)

Any emitter can attach optional per-particle trails via `trails` (`EmitterSpec.Trails` in C#, `"trails"` in JSON, `<trails>` in XML). Leaving it omitted (`null`) allocates zero extra CPU/GPU buffers, dispatches, or draw calls. Field semantics follow Unity Shuriken's `Trails` module (`0` = particle head, `1` = tail):

| Field | Default | Domain and meaning |
| --- | --- | --- |
| `ratio` | `1` | Finite `[0, 1]`; probability that a spawned particle carries a trail (deterministic PRNG). |
| `lifetime` | `1` | Finite `> 0`; **multiplier of owning particle lifetime**: each sample lives `particleLifetime × lifetime × (sizeAffectsLifetime ? particleSize : 1)` seconds (distinct from node `RibbonSpec.Lifetime` in seconds). |
| `minVertexDistance` | `0.2` | Finite `> 0` cells; minimum travel before appending a new history sample. |
| `maxPoints` | `32` | Integer `[2, 256]`; per-trail ring buffer capacity (excluding live head), overwriting oldest on overflow. |
| `dieWithParticles` | `true` | `true` clears the trail immediately on particle death; `false` records the final death sample, freezes inherited width/colour, and detaches the trail to shrink away cleanly. |
| `worldSpace` | `true` | Active when `EmitterSpec.WorldSpaceSimulation == false`: `true` stores samples in world space; `false` keeps them in emitter-local space. World-space emitters always record in world space. |
| `sizeAffectsWidth` / `sizeAffectsLifetime` | `true` / `false` | Multiplies particle display size into base trail width / new point lifetime. |
| `inheritParticleColor` | `true` | Multiplies current particle vertex colour (`StartColor × ColorOverLifetime`). |
| `width` | `1` | Finite `> 0` cells; base width scalar before `widthOverTrail(t) × size`. |
| `widthOverTrail` | constant `1` | Hermite curve over normalized length `[0, 1]` (0 = head, 1 = tail), finite nonnegative values and multiplier. |
| `colorOverTrail` / `colorOverLifetime` | constant `(1,1,1,1)` | Stored colour gradients over normalized length `[0, 1]` and over particle age `[0, 1]` (frozen at the particle's death age ratio when detached; `1` on natural expiry). |
| `textureKey` / `materialTint` / `colorSpace` | `null` / `(1,1,1,1)` / `Stored` | Independent trail texture, tint, and shading colour space (untextured uses soft disc falloff across width). |
| `blendMode` / `textureMode` / `alignment` | `Additive` / `Stretch` / `View` | Blend mode, UV mode (`Stretch` / `Tile`), and ribbon facing (`View` / `Ground`). |
| `hdrIntensity` / `softParticleFadeDistance` | `1` / `0.5` | Finite nonnegative independent HDR gain and eye-space soft fade distance. |

Known bounds and limits:

- **L2 GPU trail pool caps and overflow recycling**: On the first L2 emitter with `trails` enabled, the engine lazily allocates global device pools (`32,768` headers / `524,288` points / `32,768` particle-to-trail mappings, ~18.625 MB total VRAM, released after 30 s idle); exhaustion throws `ParticlePoolExhaustedException`, turning `Play` into an inert handle with a throttled warning. When `dieWithParticles = false`, each emitter reserves `clamp(1 + ceil(lifetime), 2, 4)` header banks per particle slot: Bank 0 is private to that slot, and Banks `1..N-1` act as shared overflow. Slot acquisition probes own Bank 0, own overflow banks, then up to **64** (`MaxLinearProbe = 64`) overflow slots linearly; if overflow is saturated, it deterministically recycles its own Bank 0 slot so new particle trails never silently disappear and compute cost remains bounded at O(1).
- **Map disposal and non-map frames**: `Map.Dispose` and `Stop(false)` immediately discard active and detached trails; when `LumenCaptureContext.RendersMap == false` (such as world map view), L1 and L2 continue advancing clocks, aging, and pending clears without staging geometry or draws.

Pure JSON firework example (rising rocket reaches 10 cells in 1.0 s under gravity, leaves an ember trail, and bursts at apex via `Death` sub-emitters with no C# `Follow`):

```json
{
  "effectId": "Examples.Firework.ParticleTrail",
  "defaultDuration": 3.2,
  "nodes": [
    {
      "name": "rocket",
      "spacePlane": "Vertical3D",
      "localPosition": [0, 0.35, 0],
      "emitter": {
        "preferredTier": "L2_GpuCompute",
        "maxParticles": 4,
        "duration": 1.0,
        "looping": false,
        "worldSpaceSimulation": true,
        "rateOverTime": 0,
        "bursts": [[0, 1]],
        "shape": "Point",
        "startLifetime": 1.0,
        "startSpeed": 19.3,
        "startSize": 0.16,
        "startColor": [1.0, 0.96, 0.82, 1.0],
        "hdrIntensity": 5.0,
        "forceFields": [
          { "kind": "GravityDrag", "centerOrAxis": [0, -1, 0], "parameters": [0, 0, 0], "radius": 0, "strength": 19.3 }
        ],
        "trails": {
          "ratio": 1.0,
          "lifetime": 0.65,
          "minVertexDistance": 0.06,
          "maxPoints": 64,
          "dieWithParticles": false,
          "width": 0.9,
          "widthOverTrail": [[0, 1.0], [1, 0.0]],
          "colorOverTrail": [
            [0.0, [1.0, 0.96, 0.82, 1.0]],
            [0.35, [1.0, 0.70, 0.28, 0.85]],
            [1.0, [0.70, 0.16, 0.03, 0.0]]
          ],
          "hdrIntensity": 5.0,
          "softParticleFadeDistance": 0.4
        },
        "subEmitters": [
          { "trigger": "Death", "targetNodeIndex": 1, "count": 900, "probability": 1.0 },
          { "trigger": "Death", "targetNodeIndex": 2, "count": 240, "probability": 1.0 }
        ]
      }
    },
    {
      "name": "apex_burst",
      "spacePlane": "Vertical3D",
      "emitter": {
        "preferredTier": "L2_GpuCompute",
        "maxParticles": 1000,
        "duration": 1.3,
        "looping": false,
        "worldSpaceSimulation": true,
        "rateOverTime": 0,
        "killBelowFloor": true,
        "shape": "Sphere",
        "shapeExtents": [0.18, 0.18, 0.18],
        "startLifetimeMin": 1.1,
        "startLifetimeMax": 2.1,
        "startSpeedMin": 6.0,
        "startSpeedMax": 14.5,
        "startSizeMin": 0.09,
        "startSizeMax": 0.22,
        "startColor": [1, 1, 1, 1],
        "hdrIntensity": 8.0,
        "billboardMode": "StretchedVelocity",
        "velocityStretchFactor": 0.12,
        "forceFields": [
          { "kind": "GravityDrag", "centerOrAxis": [0, -1, 0], "parameters": [0.55, 0, 0], "radius": 0, "strength": 5.4 }
        ],
        "collision": { "mode": "Bounce", "bounce": 0.4, "dampen": 0.25, "lifetimeLoss": 0.15, "killUnderRoof": true }
      }
    },
    {
      "name": "sparkle_core",
      "spacePlane": "Vertical3D",
      "emitter": {
        "preferredTier": "L2_GpuCompute",
        "maxParticles": 300,
        "duration": 1.35,
        "looping": false,
        "worldSpaceSimulation": true,
        "rateOverTime": 0,
        "shape": "Sphere",
        "shapeExtents": [0.30, 0.30, 0.30],
        "startLifetimeMin": 0.5,
        "startLifetimeMax": 1.0,
        "startSpeedMin": 0.6,
        "startSpeedMax": 3.8,
        "startSizeMin": 0.14,
        "startSizeMax": 0.32,
        "startColor": [1, 1, 1, 1],
        "hdrIntensity": 12.0
      }
    },
    {
      "name": "apex_flash",
      "localPosition": [0, 10.0, 0],
      "pointLight": {
        "glowRadius": 9.0,
        "color": [1.0, 0.62, 0.16],
        "intensityOverTime": [[0, 0], [0.97, 0], [1.05, 3.2], [1.50, 1.1], [2.55, 0]]
      }
    }
  ]
}
```

### 4. Distortion Nodes

A node's `distortion` projects onto the ground. Required `kind` is `RingWave`, `Ripple`, or `HeatHaze`; required `radius` and `strength` are finite positive cells. `thickness` is half-width, `speed` is cells/s, `frequency` is per cell, and `duration` is seconds, all default 0. `strengthOverTime` is an endpoint-held Hermite curve in seconds since node restart, default multiplier 1, with finite nonnegative keys and multiplier.

| Kind | thickness | speed | frequency | duration |
| --- | --- | --- | --- | --- |
| `RingWave` | > 0 | > 0 | 0 | > 0 |
| `Ripple` | > 0 | > 0 | > 0 | >= 0 |
| `HeatHaze` | 0 | 0 | > 0 | >= 0 |

Unused fields must be 0; `duration = 0` means while the node is active. See section 6.5 for screen writes and compositing.

```json
{ "name": "shock", "distortion": { "kind": "RingWave", "radius": 6, "strength": 0.45, "thickness": 0.75, "speed": 12, "duration": 0.6, "strengthOverTime": [[0, 1], [0.6, 0]] } }
```

```xml
<li><name>shock</name><distortion><kind>RingWave</kind><radius>6</radius><strength>0.45</strength><thickness>0.75</thickness><speed>12</speed><duration>0.6</duration>
  <strengthOverTime><li><time>0</time><value>1</value></li><li><time>0.6</time><value>0</value></li></strengthOverTime>
</distortion></li>
```

### 5. Ribbon Nodes

A node may contain both `emitter` and `ribbon`. Required `mode` is `Trail`, following the node, or `Beam`, connecting an endpoint. Trail requires finite positive `lifetime` and `minVertexDistance` and `maxPoints > 1`. It records points after the distance threshold, drops the oldest at capacity, and interpolates its lifetime cutoff. Restart clears points; fade-out lets points expire and immediate stop removes them.

Beam requires exactly one of `endLocal` (node-local coordinates) or `endAtTarget`, `segments >= 1`, and finite nonnegative `noiseAmplitude` and `noiseFrequency`; frequency 0 keeps the shape until restart. `endAtTarget` without a play target throws `ArgumentException`. A beam draws only while active. Fields for the other mode must remain 0, null, or false.

Common fields: `width` defaults to 1 and is finite positive; `widthOverLength` defaults to 1, with finite nonnegative keys and multiplier; `colorOverLength` defaults to white and follows the particle gradient colour contract. Normalized length is 0 at the head and 1 at the tail. `hdrIntensity` defaults to 1 and is finite nonnegative, `textureKey` defaults to null, `blendMode` to `Additive`, `textureMode` is `Stretch` (default) or `Tile` (one repeat per cell), and `alignment` is `View` (default) or `Ground`. `softParticleFadeDistance` defaults to 0.5, finite nonnegative. Ribbons draw in the HDR layer on their play map; `Vertical3D` uses the same display projection as particles.

### 6. Screen Dim Nodes

A node's `screenDim` darkens the full screen under RimLumen's HDR layer at the queue-3152 composite. It is purely visual and does not change game logic.

| Field | Default | Domain and meaning |
| --- | --- | --- |
| `strength` | required | Finite in `[0,1]`, the fraction of the scene hidden at full strength. |
| `duration` | `0` | Finite nonnegative seconds since the node's last start or restart. `0` applies while the node is active; a positive duration continues until time reaches `duration`, even after deactivation or `Stop(true)` begins draining. |
| `strengthOverTime` | none, treated as 1 | Endpoint-held Hermite scalar curve sampled in seconds since the node's last start or restart. Key times are finite, nonnegative and non-decreasing; values are finite and nonnegative, tangents are finite, and the JSON curve `multiplier` is finite and nonnegative. An explicit empty curve is rejected. |

A node's coverage is `min(1, strength × intensity × strengthOverTime(t))`, where `intensity` is the handle's intensity parameter. Visible nodes on the same map combine as `1 - Π(1 - a)`, where `a` is each node's coverage. Only full-screen dimming is supported.

Dimming affects content under the HDR layer at the queue-3152 composite; vanilla fog, the darkness overlay and late bloom groups (1–3) drawn afterward are unaffected. HDR-layer effects retain their colour; a custom ShaderPack must follow the `scene × (1 - hdr.a) + hdr.rgb` composite relationship to show the dimming.

```json
{ "name": "dim", "screenDim": { "strength": 0.6, "duration": 1.5, "strengthOverTime": [[0, 0], [0.3, 1], [1.5, 0]] } }
```

```xml
<li><name>dim</name><screenDim><strength>0.6</strength><duration>1.5</duration>
  <strengthOverTime><li><time>0</time><value>0</value></li><li><time>0.3</time><value>1</value></li><li><time>1.5</time><value>0</value></li></strengthOverTime>
</screenDim></li>
```

---

## 6. C# Runtime API (`RimLumen.Core.Api`)

> Threading rule: Every method on `RimLumenApi` and `LumenTextureRegistry` **must be called on the game main thread**.

#### Project Setup and Build Guide (.NET 10 SDK)

RimLumen targets **`net10.0`**. Running a system-default .NET 9 (or older) `dotnet` CLI fails with `error NETSDK1045: The current .NET SDK does not support targeting .NET 10.0`. **You must build your mod assembly with the portable .NET 10 SDK bundled inside RimWorld 1.7's `Moddev` directory**.

Minimal compilable `.csproj` (matching this repository's `Directory.Build.props` and `Source/RimLumen.Core/RimLumen.Core.csproj`; add `<Reference Include="Waylith">` from `$(GameRoot)/Waylith.dll` if your mod calls `LumenTextureRegistry.Register` or accesses `Waylith.Rendering` types directly):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <OutputPath>1.7/Assemblies/</OutputPath>
    <GameRoot Condition="'$(GameRoot)' == ''">E:/SteamLibrary/steamapps/common/RimWorld</GameRoot>
    <RimLumenRoot Condition="'$(RimLumenRoot)' == ''">$(GameRoot)/Mods/RimLumen</RimLumenRoot>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="RimLumen.Core">
      <HintPath>$(RimLumenRoot)/1.7/Assemblies/RimLumen.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(GameRoot)/Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="RimWorld">
      <HintPath>$(GameRoot)/RimWorld.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityStubs">
      <HintPath>$(GameRoot)/UnityStubs.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

PowerShell build command:

```powershell
$dotnet = "E:/SteamLibrary/steamapps/common/RimWorld/Moddev/tools/dotnet/dotnet.exe"
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
& $dotnet build MyRimLumenMod.csproj -c Release --nologo /warnaserror
```

### 1. Capability Queries and Dynamic Asset Registration

```csharp
using System.Numerics;
using RimLumen.Core.Api;
using RimLumen.Core.Contracts;
// Alias CurveKey when Verse is also in scope to avoid ambiguity with Verse.CurveKey
using CurveKey = RimLumen.Core.Contracts.CurveKey;

bool ready = RimLumenApi.IsReady;
LumenCapabilities caps = RimLumenApi.Capabilities;
// caps.IsHdrActive / IsSceneDepthAvailable / IsGpuComputeAvailable / MaxGpuParticlesPerFrame

// Dynamically build and register a full effect asset (validated at registration; treat asset and nested arrays as immutable afterward)
var bloodSparks = new EffectAsset
{
    EffectId = "Author.MyMod:BloodSparks",
    DefaultDuration = 1.2f,
    Nodes = new[]
    {
        new EffectNodeSpec
        {
            Name = "sparks",
            SpacePlane = LumenSpacePlane.Vertical3D,
            LocalPosition = new Vector3(0f, 0.5f, 0f),
            Emitter = new EmitterSpec
            {
                Name = "sparks",
                PreferredTier = LumenParticleTier.L2_GpuCompute,
                MaxParticles = 128,
                Duration = 1.0f,
                Looping = false,
                WorldSpaceSimulation = false, // Local simulation: particles move with Follow below
                RateOverTime = 0f,
                Bursts = new[] { new ParticleBurstSpec(Time: 0f, Count: 28) },
                Shape = LumenShapeKind.Sphere,
                ShapeExtents = new Vector3(0.18f, 0.18f, 0.18f),
                StartLifetimeMin = 0.4f,
                StartLifetimeMax = 0.8f,
                StartSpeedMin = 1.5f,
                StartSpeedMax = 3.5f,
                StartSizeMin = 0.08f,
                StartSizeMax = 0.16f,
                StartColor = new Vector4(0.95f, 0.12f, 0.10f, 1f),
                HdrIntensity = 2.5f,
                KillBelowFloor = true,
                SizeOverLifetime = new ScalarCurve(new[]
                {
                    new CurveKey(0f, 1f),
                    new CurveKey(1f, 0f),
                }),
                ColorOverLifetime = new ColorGradient(new[]
                {
                    new ColorStop(0f, new Vector4(1f, 1f, 1f, 1f)),
                    new ColorStop(1f, new Vector4(0.6f, 0.05f, 0.05f, 0f)),
                }),
                ForceFields = new[]
                {
                    new ForceFieldSpec(ForceFieldKind.GravityDrag,
                        CenterOrAxis: new Vector3(0f, -1f, 0f),
                        Parameters: new Vector3(0.8f, 0f, 0f),
                        Radius: 0f,
                        Strength: 6f),
                },
            },
            PointLight = new PointLightSpec(
                GlowRadius: 3.5f,
                ColorRgb: new Vector3(1f, 0.25f, 0.15f),
                IntensityOverTime: new ScalarCurve(new[]
                {
                    new CurveKey(0f, 1.2f),
                    new CurveKey(0.4f, 0f),
                })),
        },
    },
};

RimLumenApi.RegisterEffect(bloodSparks); // Registers or replaces an asset with the same EffectId
bool exists = RimLumenApi.TryGetEffect("Author.MyMod:BloodSparks", out EffectAsset? asset);
```

### 2. Playback API

```csharp
// 1. Play at map cell center (most common; OriginWorld.Y is automatically 0)
ILumenEffectHandle handle = RimLumenApi.PlayAtCell(
    effectId: "Author.MyMod:Spells/Fireball",
    map: pawn.Map,
    cell: pawn.Position,
    scale: 1f,
    rotationDegrees: 0f,
    realTime: false);

// 2. Play at world position (note Vector3 is System.Numerics.Vector3; Y is height above ground in cells, use 0f on the ground)
ILumenEffectHandle handle2 = RimLumenApi.Play(
    effectId: "MyFireball",
    map: map,
    originWorld: new System.Numerics.Vector3(x, 0f, z),
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
        // Pass 0f for ground-anchored tracking; never pass p.y (Thing.DrawPos.y is a 2D draw-altitude layer, not height in cells)
        pos = new System.Numerics.Vector3(p.x, 0f, p.z);
        yaw = pawn.Rotation.AsAngle;
        return true;
    },
    InitialState = "charge", // Overrides the asset's initialState; Play throws ArgumentException for a state the asset does not declare
    RealTime = false,
});
```

- **World Y vs. RimWorld draw altitude**: `Thing.DrawPos.y`, `ToVector3ShiftedWithAltitude(...)`, and `Altitudes.AltitudeFor(...)` are 2D draw-sorting layer altitudes — **never pass them to `OriginWorld.Y` or `Follow`'s `pos.Y`**. For ground-level things or pawns, keep `Y = 0f` and put any fixed vertical offset on the node (`localPosition.y` / XML `<heightOffset>`). Rising rockets and firework shells can be authored purely in data with initial speed, `GravityDrag`, `emitter.trails`, and `Death` sub-emitters without C# `Follow`. Pass a real non-zero `Y` only when the **play origin itself tracks an external airborne host** (for example an airborne projectile or hover craft driven by game code).
- `LumenPlayRequest.DepthReferenceZ` supplies the mask reference as world Z in cells. Play records it as a Z offset from `OriginWorld`, so it moves with `Follow`. If a node declares `depthMask` and the request has no reference, `Play` throws `ArgumentException`.
- `LumenPlayRequest.InteractionEvents` optionally attaches per-play `InteractionEventSpec[]` rules (validated once at `Play`, throwing `ArgumentException` on invalid entries) evaluated in order after the asset's own `InteractionEvents`, without cloning or mutating the shared `EffectAsset`.
- If `EffectId` does not exist, `Play` returns an inert handle with `IsAlive == false` whose methods are no-ops, and logs `Log.Warning` once per ID per service instance. An asset rejected by validation also returns an inert handle; it retains the registry's validation error without adding an "unknown ID" warning.

#### Local Simulation (`WorldSpaceSimulation = false`) vs. `Follow`

`Follow` updates the root's `OriginWorld` and yaw each frame, and different node types and simulation spaces respond differently:

- **Particle nodes — World-space simulation (`WorldSpaceSimulation = true`, default)**: `Follow` moves only the emitter's spawn transform; **already-emitted particles stay in world space** and do not follow subsequent motion (ideal for projectile trails or running dust). Under the vanilla 2D camera, world-space particles anchor `Vertical3D` at the node's ground XZ at birth.
- **Particle nodes — Local-space simulation (`WorldSpaceSimulation = false`)**: Particle positions and velocities live in node-local space, so **live particles translate, rotate, and scale with `Follow` every frame** (ideal for shields, on-body hit sparks, and orbiting halos). Emitter `ForceFields` evaluate in node-local space; world environment forces (`Explosion` / `Cursor`) and wall collisions transform into local space when the node matrix is invertible (and are skipped when noninvertible, such as zero scale). Under 2D cameras, `Vertical3D` uses the node's current ground XZ.
- **Non-particle nodes**: `pointLight` pools and `distortion` always follow the node's current world position; `Ribbon` `Trail` records historical world points (only the head follows the node while the tail stays behind), and `Beam` (`EndLocal`) translates and rotates rigidly with the node.

### 3. Handle Controls (`ILumenEffectHandle`)

| Member | Behavior & Notes |
| --- | --- |
| `bool IsAlive` | `true` while playing or fading/draining; becomes `false` once all particles and point lights are cleaned up. |
| `void Trigger(string name)` | Fires every timeline event with that name now, whatever its state and time (target-node pulse, `Notify`), and restarts the emission schedule of every node with that name until the next state entry decides again; the current state is unchanged. Unmatched names do nothing; no effect after fading out or death. L1 and L2 behave identically. |
| `void GoTo(string stateName)` | Enters that state now (going to the current state restarts it). On an asset-backed handle, an undeclared state name always throws `ArgumentException`, including after `Stop(true)`, while draining, or once dead; a declared state is entered only while playing before `Stop(true)`, otherwise it has no effect. `Stop(false)` tears down immediately; `GoTo` on an inert handle is always a no-op. |
| `event Action<string> Notify` | Raised with the event name when a timeline event or environment interaction event with `notify: true` fires (on its time, on an interaction trigger, or through `Trigger`). |
| `event Action<LumenInteractionNotification>? InteractionNotify` | Raised with `LumenInteractionNotification(EventName, Trigger, Cursor)` when an environment interaction event with `notify: true` fires, identifying the trigger and `LumenCursorSource` (interaction events only; timeline events and `Trigger` never raise it; zero overhead with no subscribers and no cursors). |
| `event Action<string> StateChanged` | Raised with the state name each time a declared state is entered, including the initial state. |
| `event Action Finished` | Raised once when `IsAlive` becomes `false`, for any reason (played out, `Stop`, `Dispose`, or map disposal). |
| `void SetParam(string name, float value)` | Updates runtime scalar parameters. Three built-in names take effect immediately: `intensity` (scales both particle and point-light brightness), `rate` (scales only continuous L1 and L2 emission, not `bursts`; must be finite and nonnegative or throws `ArgumentOutOfRangeException`; zero pauses only continuous emission), and `scale` (multiplies root scale). Other names are stored without affecting simulation. |
| `void SetLightIntensity(float intensity)` | Adjusts the runtime intensity multiplier for this handle's visual point lights only (final light scales by `intensity` param × this multiplier × fade × `intensityOverTime` curve), **without affecting particles, distortion, ribbons, or screen dim**. Must be finite and `>= 0` (unbounded above; `0` suppresses pool drawing); throws `ArgumentOutOfRangeException` before mutation on invalid input. Inert handles no-op; dead `EffectInstanceHandle` instances still validate input then no-op. |
| `void SetLightColor(Vector3 colorRgb)` | Overrides the base RGB colour of this handle's visual point lights (replacing `PointLightSpec.ColorRgb`), **without affecting particles**. Each RGB component must be finite in `[0, 1]`; throws `ArgumentOutOfRangeException` before mutation on invalid input. Inert handles no-op; dead `EffectInstanceHandle` instances still validate input then no-op. |
| `void Stop(bool allowFadeOut = true)` | When `true`: if the asset has a `stopState`, enters it and plays it to its end; then stops new emission, fades point lights over `0.25s`, and lets live particles finish their lifetime before reclamation. Without a `stopState` the fade starts at once. When `false`, immediately destroys all particles and lights. Repeated calls do nothing. |
| `void Dispose()` | Equivalent to `Stop(false)`, idempotent. Dispose long-lived handles when their host thing is removed on a live map (e.g., `Thing.DeSpawn`); map unload cleans up automatically (see below). |

All events (including `InteractionNotify`) are raised on the main thread from the per-frame advance, in the order they happened, so calling `GoTo` or `Stop` from a handler never re-enters the playback that raised it: the call takes effect at once (the nodes of the state being left stop new emission on that same frame), and the events it causes are raised on the next frame. After `Stop(false)` or `Dispose`, `Finished` is raised on the next frame. RimUnity compatibility playback (`External`) has no states or timeline events: `Notify`, `InteractionNotify`, and `StateChanged` come only from the native RimLumen part, and `Finished` is raised once both parts have ended.

#### Identifying Cursor Sources (`InteractionNotify`)

`LumenCursorSource` (`Kind`, `Source`) identifies what triggered an interaction event:

- Real host mouse (including while a `CreateMouseForce` lease is active): `Kind == LumenCursorSourceKind.HostMouse`, `Source == null`.
- Mod-created world cursor (`CreateCursor` or a non-host-mouse `CreateEnvironmentSource`): `Kind == LumenCursorSourceKind.ModCursor`, with `Source` holding the originating `ILumenEnvironmentSource` instance; even `CursorExit` caused by disposing or obscuring an inside cursor preserves attribution to that cursor.
- Explosion triggers (`LumenInteractionTrigger.Explosion`): `Kind == LumenCursorSourceKind.None`, `Source == null`.

```csharp
handle.InteractionNotify += info =>
{
    // React only to the real host mouse, ignoring custom mod cursors from CreateCursor
    if (info.Cursor.Kind != LumenCursorSourceKind.HostMouse) return;
    if (info.EventName == "cursorEnter")
        mouseForce = RimLumenApi.CreateMouseForce(map, radius: 3f, strength: -6f);
    else if (info.EventName == "cursorExit")
    {
        mouseForce?.Dispose();
        mouseForce = null;
    }
};
```

#### Map Unload Lifecycle (`Map.Dispose`)

- **Automatic immediate release**: When a map is disposed (`Map.Dispose`), RimLumen's prefix hooks immediately call `Dispose()` (equivalent to `Stop(allowFadeOut: false)`, **no fade-out**) on every native effect handle and every RimUnity `External` handle bound to that map, while also freeing that map's collision field, occlusion field, transient pulses, sustained sources, and cursor state. **Effects on other maps are unaffected** and keep playing.
- **No manual map-unload cleanup needed**: Modders only need to dispose long-lived handles when their host object is removed on a live map (such as `ThingComp.PostDeSpawn` / `PostDestroy`); you do not need a map-destruction hook to prevent leaks on map unload, and calling `Dispose()` again is safely idempotent.
- **Calling handle members after map-unload release**:
  - `IsAlive` immediately becomes `false`, and `Finished` fires once on the next main-thread frame (if subscribed).
  - `Stop(...)`, `Dispose()`, and `Trigger(...)` are safe no-ops.
  - `GoTo(stateName)` is a no-op for declared states and still throws `ArgumentException` for undeclared state names on asset-backed native handles (`External` handles always no-op).
  - `SetParam(...)`, `SetLightIntensity(...)`, and `SetLightColor(...)` still validate entry arguments (`ArgumentOutOfRangeException` on invalid values such as negative/non-finite `rate`, `intensity`, `SetLightIntensity`, or out-of-range `SetLightColor`), and are silent no-ops for valid inputs without mutating state.

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

Linear mode decodes straight material RGB before alpha / hardware blending. The native HDR layer and group 0 host bloom stay in linear radiance (the host bloom prefilter decodes the stored glow copies first); the composite blends them over the decoded scene in linear light and encodes once: `E(compress(D(scene) × (1 - a) + hdr + bloom))`, where `compress` is identity up to a peak of 1. Pixels with zero `hdr` and `bloom` leave the scene untouched. AlphaBlend and Premultiplied follow the same rule, and alpha remains coverage. Example: `c=0.5, a=0.5` over a `0.5` grey scene gives `0.5` in both modes; over black, Legacy gives `0.25` and Linear approximately `0.36078`. These are formula results, not measured pixels. Pack `hdr` and `bloom` follow `ubo.ColorBlendSpace`; `glow` and `scene` remain Stored, and late groups 1–3 are unchanged. Packs that composite in Linear mode must do the same decode-blend-encode themselves; no extra image is allocated.

### 5. Screen Distortion and Third-Party Writes

Draws on `RimLumenRenderHost.DistortionPassId` accumulate into the `R16G16SFloat` `distortion` input in group 0 at 3152. Third parties can draw on layer 14 or submit a dynamic `MeshDrawCommand` with that pass ID; persistent draw sources are unsupported. RG is the scene sample offset in framebuffer pixels, +x right and +y down; write the opposite offset to push the scene outward from a center. Blending is `One, One`, without a depth attachment; the target is cleared each frame. With no distortion draws, no target or distortion pass is allocated and a zero placeholder is used.

The player setting “Distortion also refracts RimLumen effects” defaults to on. Custom composites can include `PostFx/RimLumenDistortion.glslinc` after declaring the UBO: `lumenRefractedUv(distortionImage, uv)` returns the scene sample UV, and `lumenLayerUv(uv, sceneUv)` selects the effect-layer UV according to the setting. The latter requires the 224-byte UBO from `PostFx/RimLumenPackUniforms.glslinc`. Zero or nonfinite offsets retain the original UV.

### 6. Environment Pulses and Sustained Force / Cursor Sources

Mods can inject explosion shockwaves, radial pulses, host-mouse push/pull leases, or custom world-space cursors on the main thread via `RimLumenApi`:

```csharp
// 1. Vanilla-preset spherical explosion wave (drives both particle forces and Explosion interaction events)
RimLumenApi.EmitExplosion(map, new System.Numerics.Vector3(cell.x + 0.5f, 0f, cell.z + 0.5f), radius: 6f, strengthScale: 1f);

// 2. Custom environment pulse (HorizontalRadial / SphericalRadial / SphericalWave)
RimLumenApi.EmitEnvironmentPulse(new LumenEnvironmentPulseRequest
{
    MapIndex = map.uniqueID, // -1 resolves to Find.CurrentMap.uniqueID at call time
    Channel = LumenEnvironmentChannel.Explosion,
    Shape = LumenEnvironmentForceShape.SphericalRadial,
    CenterWorld = originWorld,
    Radius = 5f,
    Strength = -18f, // positive repels outward, negative pulls inward
    Duration = 0.35f,
    RealTime = false,
});

// 3. Sustained host-mouse push/pull lease (disposing stops the force immediately)
using ILumenEnvironmentSource mouseForce = RimLumenApi.CreateMouseForce(map, radius: 5f, strength: 4f);

// 4. Mod-controlled world-space cursor (strength == 0 triggers Cursor* events without pushing particles; nonzero also applies HorizontalRadial force)
using ILumenEnvironmentSource aimCursor = RimLumenApi.CreateCursor(map, initialWorldPos, radius: 4f, strength: -6f);
aimCursor.SetPosition(clampedAimWorldPos);
```

- **Lease contract (`ILumenEnvironmentSource`)**: Exposes `IsAlive`, `MapIndex`, `SetPosition(Vector3)`, `SetStrength(float)`, `SetRadius(float)`, and `Dispose()`. Calling `SetPosition` when `FollowHostMouse == true` throws `InvalidOperationException`; when `Channel == None` (event-only cursor, `TriggersCursorEvents == true`), `Radius` and `Strength` must remain `0`. Each map retains up to `8` transient pulses (evicting the oldest on overflow) and `8` active sustained sources (logging a throttled warning and returning an inert `IsAlive == false` handle when exceeded); when the render fail-safe is tripped, pulses are no-ops and source creation returns an inert handle. `Map.Dispose` immediately releases that map's pulses, sustained sources (`IsAlive` becomes `false`), and cursor state without affecting other maps.
- **World-space cursor velocity conversion & non-current-map behavior (`CreateCursor` / `FollowHostMouse = false`)**:
  - When `cursor.MapIndex == CurrentMapIndex` and the viewport is valid (`ViewportHeight > 0`), both the cursor's previous-frame world position and current-frame world position are projected through **the current frame's** `ViewProjection = ProjectionMatrix × ViewMatrix` into isotropic screen-height coordinates `((ndcX + 1) × 0.5 × (W / H), (1 - ndcY) × 0.5)`, then differentiated by `dt_real` and smoothed with the `tau = 0.05 s` EMA into `vh/s` (matching the host mouse unit). Projecting both endpoints with the same frame's `ViewProjection` guarantees that panning or zooming the camera alone adds zero spurious speed to a stationary world-space cursor.
  - When `cursor.MapIndex != CurrentMapIndex` (a background map not currently rendered), its force field and ground `PositionXZ` remain active on `cursor.MapIndex` (so particle push/pull and `CursorEnter` / `CursorExit` / `CursorStay` rules with `minCursorSpeed == 0` still evaluate on that map), while its screen velocity is `0` and its projection history is reset so switching back to that map enters cleanly at speed `0` without a cross-camera spike.

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

The 3152 group runs the full chain once when there are HDR draws, early glow copies or distortion draws, or the pack enables `runOnEmptyFrames`. Contributors that draw directly in `RecordHdrDraws`, including existing implementations, must return `true` from `ILumenFrameContributor.RequiresDirectHdrPass` after capturing a frame that needs the pass. The default is `false`: routed draws count on their own, and lifecycle or compute work alone does not open the HDR pass.

This group precedes vanilla's 3152 Distortion grab. The pack's `glow` contains only `q < 3152` copies; `scene` and `depth` are separate same-frame snapshots at this boundary, not later captures. The final composite writes map colour without sampling the image being written. The `pack.json` `"backbuffer"` name and PostEffectPass composite ABI remain unchanged: the name denotes the final composite output, not a swapchain slot.

Later groups only redraw their additive glow copies, run host bloom prefilter, downsample and upsample, then add bloom RGB to the scene while preserving destination alpha, without tone mapping. The original core is already in the scene and is not added again; native HDR particles, grading, fog, custom DAG and history do not rerun. Later groups use the same frame's captured active-pack `BloomParams` (intensity, threshold, knee, scatter) and requested mip count. An absent group or zero mips adds no boundary or passes for that group.

Custom `bloomPrefilterShader`, `bloomDownShader` and `bloomUpShader` apply only to the 3152 group; later groups use host shaders, not an equivalent of arbitrary custom bloom. Foggy late bloom does not light mist or use fog-dependent scatter gain; Crisp and other pack composite colour mapping does not apply to late bloom. The pack DAG and history run once with the 3152 group, not once per group.

In the installed vanilla / DLC static inventory, ritual candle clusters at 3155 bloom at 3169 before weather FogOverlay 3169 and SunShadow 3170; orbital / power beams and endpoints at 3177 bloom at 3178 after FogOfWar 3175 and before the Gravship grab and Darkness 3180; grav-field 3181, grav engine 3185, thruster flame 3201 and Noctol eyes 4000 bloom at map end. The inventory has no eligible draws in `[3169, 3174]` or `[3178, 3179]`. The last group's bloom is later than the 3201 Screen and 3251 VoidNode grabs and is not included in them; ordering is not guaranteed for all overlays, grabs or third-party draws. This is a static ordering contract; in-game behaviour has not been verified.

To opt a custom `ShaderDef` out of automatic HDR redraw, attach `<li Class="RimLumen.Core.Compat.LumenEmissiveModExtension"><EnableHdrBloom>false</EnableHdrBloom></li>`. (`HdrMultiplier` was removed; writing `<hdrMultiplier>` fails Def validation. Only `EnableHdrBloom` takes effect.)

### 3. Gameplay Light (`CompRimLumenLogicalLight`)

`pointLight` only lights the picture. When a building must affect mood, work speed, plant growth and the light readout, add the logical light comp to its ThingDef:

```xml
<comps>
  <li Class="RimLumen.Core.Lighting.CompProperties_RimLumenLogicalLight">
    <glowRadius>6</glowRadius>
  </li>
</comps>
```

- **Scope**: it only raises `GlowGrid.GroundGlowAt`, exactly like a white, non-overlit vanilla `CompGlower` of the same radius. It never enters vanilla `GlowGrid`, so `VisualGlowAt`, the vanilla octagonal lighting overlay and the G channel of the ShaderPack `mapLightField` stay unchanged.
- **Falloff**: distances come from vanilla's 8-neighbour flood fill. Walls and other `blockLight` buildings occlude it, and light wraps through doorways and around corners. Cells near the source read 50% (Lit); cells beyond `glowRadius` read 0. Overlapping logical lights combine by maximum, and the result is combined with vanilla's value (sky included) by maximum. Vacuum-exposed terrain has no ground glow.
- **When it is lit**: the same rule as `CompGlower`. The building is spawned, `FlickUtility.WantsToBeOn` holds, and the building and every comp implementing `IThingGlower` return `ShouldBeLitNow()`. Power, flick switches, fuel and schedules therefore need no extra setup, and the comp reacts to the same signals (`PowerTurnedOn`, `FlickedOff`, `RanOutOfFuel` and so on).
- **Timing**: like vanilla glow, it is recomputed in `GlowGridUpdate_First` each frame and raises `GlowChanged` for changed cells. It recomputes only when a light switches or a wall inside a light's reach changes.
- **Validation**: `glowRadius` must be finite and in `(0, 40]` cells; anything else fails Def validation. The comp has no colour, intensity or overlight fields.
- **Pairing with a visual light**: play an effect with a `pointLight` when the pool should be visible. The two are independent, and no vanilla lighting is drawn on top.
- **Do not add `CompGlower` as well**: the vanilla glower writes `GlowGrid` and draws its octagonal overlay on screen.

With the "demo recreation buildings" setting on, the Recreation build menu offers three comparison street lamps: `RimLumen_StreetLamp_Visual` has the visual light only, `RimLumen_StreetLamp_Logic` the logical light only, and `RimLumen_StreetLamp_Both` both.

---

### 4. Light-Pool Occlusion and Lighting Takeover

#### Light-pool occlusion map

RimLumen's visual light pools (`pointLight` effect pools, plus vanilla glower pools while the takeover is on) test line of sight against a dedicated occlusion map. The map is separate from the particle collision grid, kept per map, and only the current map holds GPU textures. Its resolution is 4×4 texels per cell.

**What blocks light**

Classification reads def fields only, never names, so modded buildings written the vanilla way work automatically.

| Kind | Test | Effect on pools |
| --- | --- | --- |
| Walls, smoothed walls, natural rock, doors, coolers and similar | `def.blockLight` | Infinitely high blocker over its occupied cells; the sprite is not read |
| Cover (sandbags, barricades, low walls, some plants and items) | not `blockLight`, `Fillage == Partial`, category Building, Plant or Item, and no height (see the next row) | Partial transmission set by `fillPercent` |
| Things with `Fillage == Full` but no `blockLight` | as above | Treated as cover with `fillPercent ≈ 1`: almost opaque, but without the wall rules |
| Things with a height (columns, sculptures, furniture, plants, sarcophagi) | not `blockLight`, and an edifice with `staticSunShadowHeight > 0` when sun shadows are enabled, or a thing with `graphicData.shadowData` (not used for `RealtimeOnly`) | Cast object shadows from their sprite outlines instead of transmitting light by `fillPercent` |

Pawns, animals, motes and projectiles are not in the map. Roofs never block, and neither do positions outside the map. A height-bearing thing whose sprite pixels cannot be read casts as a solid block over its occupied cells, with one warning per def; cover with an unreadable sprite does not occlude.

**Walls and doors**

- Walls, rock and doors block over whole cells; linked-wall corners and gaps in the art do not change the lighting.
- A lamp embedded in a wall is not blocked by the solid run it sits in.
- Only the first layer of wall cells is lit: each point of a wall cell takes the brightness of the ground point level with it just outside the cell edge that faces the light and does not border another wall, so a wall face follows the ground in front of it continuously, without alternating per-cell banding. Deeper wall cells, mountain interiors and rock touching a room only at a corner stay dark.
- A closed door blocks all light. An open door lets light through only the gap its leaves clear: the opening half-width is `0.45 × openness` for ordinary doors and `0.35 × openness` for multi-tile doors (in door widths). A multi-tile door that is `StuckOpen` draws no leaves and counts as fully open.
- Door state is polled every frame and only updates a small door-state texture, so opening and closing doors never rebuilds the whole occlusion map.

**Cover transmission**

Cover attenuates by the distance travelled through it, independent of step length:

```
T = Π (1 − o_i)^(Δs_i)
```

`o_i` is the opacity of segment i (sprite alpha × `fillPercent`) and `Δs_i` is the length of that segment in cells. Light crossing a one-cell sandbag head-on keeps `1 − fillPercent`; a diagonal crossing is longer and blocks more.

**Object shadows**

Things that do not block light but have a height cast shadows from their sprite outline, away from each light. Effect pools, vanilla glower pools and, under the lighting takeover, the sun all follow the same rule.

| Condition | Rule |
| --- | --- |
| Category | Building, Plant or Item, and not `blockLight` |
| Height | On maps with sun shadows, a positive `staticSunShadowHeight` for edifices; otherwise the `graphicData.shadowData` height (plants scaled by their growth size, at most 1 cell); `RealtimeOnly` things do not use `shadowData` |
| Shape | The outline of sprite alpha ≥ 0.5, simplified to within 1/32 cell |
| Unreadable sprite | Casts from its occupied cells as a solid block, with one warning per def |

A thing with a height only casts shadows. Its `fillPercent`, if any, does not apply as cover. Walls, rock and doors keep their own occlusion rules.

**Shadow length and strength**

For a light at height H and a thing of height h at horizontal distance d:

- `h < H`: the shadow reaches `d·h/(H − h)` cells behind the thing.
- `h ≥ H`: the shadow reaches the edge of the pool.
- When `h < H`, with t the height at which the ray from a ground point toward the light crosses the thing's outline, things with `shadowData` shade with strength `1 − t/h`, fading along their height. Buildings with `staticSunShadowHeight` shade with strength 1.
- When `h ≥ H`, every shadow fades linearly along the ground from the outline to 0 at the pool edge, the way vanilla's `SunShadowFade` fades along its extruded quad. A light on the ground (H = 0) casts no solid block either.

An effect pool's H is its node's world Y; vanilla glowers all use H = 1 cell. The sun casts along vanilla's `_CastVect` with the same strength rule.

**How shadows combine**

- Each light darkens only its own pool: its light term is multiplied by `1 − strength` before pools are summed. The composite after the pools is unchanged.
- Where shadows of several things overlap under one light, the strongest applies, so they darken once.
- A thing's own sprite is never darkened by its own or another thing's shadow. Shadows are drawn at ground height, and scene depth rejects them on sprite pixels.
- A thing whose occupied cells contain the light's cell casts nothing for that light; for example, a standing lamp does not block its own light. Other lights are still blocked by it.
- Shadow edges follow the outline geometry per pixel, with no penumbra.
- **Per-light `castShadows` toggle**: Effect point lights support the asset field `castShadows` (`"castShadows"` in JSON, `<castShadows>` in XML, `PointLightSpec.CastShadows` in C#, default `true`, with no runtime setter API). When `false`, that light skips the occlusion map completely (walls, rock, doors and cover do not block it), generates no object shadow geometry, and consumes no shadow mask channel even when global `objectShadows` is enabled; when `true`, it is occluded by walls, rock, doors and cover and casts object silhouette shadows when global `objectShadows` is on. Vanilla glowers and the takeover sun have no `castShadows` field, always cast shadows, and are governed solely by global `objectShadows`.

**Sun shadows under the takeover**

With the lighting takeover on, the sun shadows of things are drawn as object shadows too, shaped by the sprite outline instead of vanilla's `shadowData` box. Dynamic shadows still combine as `min(S, s_d)`; `S` includes the object sun shadow, so each pixel is darkened once.

**Smooth edges**

Only `blockLight` things write signed distances, from each texel to the edge of their occupied cells. Every step bilinearly interpolates the distances of the surrounding 2×2 texels, so shadow edges sit at sub-texel positions: occlusion starts on the outline and reaches full blocking within 1/8 texel (about 0.03 cell) inside it. Sun shadows and point lights follow the same rule. Cover bilinearly interpolates the opacity of the surrounding 2×2 texels, so its edges are soft.

**When the map updates**

The map is updated incrementally through a per-map stamp ledger that records the rectangle each thing last wrote:

1. When a thing spawns, despawns or changes its graphic (stuff, plant growth, a linked-wall neighbour changing), its old rectangle is cleared first.
2. Every other ledger entry that overlaps a cleared area is restamped, so a large tree whose art overhangs several cells leaves no hole or stale shadow.
3. When vanilla asks for its 8 neighbouring cells to be redrawn as well (linked walls), the things on those cells are restamped too.

The incremental result is byte-identical to a full rebuild. The main texture is uploaded only on frames where something changed, at most once per frame. Object outlines share this ledger: when a thing spawns, despawns or changes its graphic, its 8×8-cell chunk shadow mesh is rebuilt on the next frame. Frames without changes rebuild and upload nothing.

#### Lighting takeover setting

**The setting**

| Item | Value |
| --- | --- |
| Label | Replace map lighting and sun shadows |
| Field | `RimLumenSettings.LightingTakeover` |
| Scribe key | `lightingTakeover` |
| Default | off |

With the setting off, the picture is exactly what it was before the setting existed: pools are composited after vanilla lighting, and vanilla sun and moon shadows inside a pool are lifted to `(c+L)/(1+L)`.

**When it takes effect**

It is evaluated once per frame. The frame runs as if the setting were off when any of these holds:

- The setting is unchecked.
- IsoRim is active and its 3D view is ready (`IsoRim.Api.ViewReady` is true). The takeover returns on the frame after the 3D view closes.
- Another mod overrides vanilla `LightOverlay`, `SunShadow` or `SunShadowFade`, including with precompiled SPIR-V. The takeover then stays off for the rest of the session, and one warning names the overridden programs. The takeover never runs half on.

**What changes under the takeover**

- **Map lighting**: vanilla's lighting overlay (`LightOverlay`) is replaced by RimLumen's shader. Sky light, roof coverage and indoor darkening come from vanilla's own mesh data. With no shadow and no pool, the result equals vanilla per component.
- **Vanilla glowers**: lamps, campfires and every other vanilla `GlowGrid` light each become a light pool bounded by the occlusion map. Falloff follows vanilla: `lerp(1 − d′/R, 1/d′², 0.4)` with `d′ = 1 + distance`.
- **Effect pools become additive**: effect pools are added into the lighting term instead of `scene × (1 + L)`. At night they are noticeably brighter than with the setting off; this is intended.
- **Sun shadows**: vanilla's static sun shadows (`SunShadow`) and the shadows printed into the map mesh (persistent `SunShadowFade`) are no longer drawn; the occlusion map reproduces shadows from walls, rock and doors, while object shadows draw those from other height-bearing things. Direction, length and colour come from vanilla's cast vector and shadow colour, so weather, eclipses and night-time moon shadows still apply.
  - Buildings cast with their `staticSunShadowHeight`; furniture, trees and other things with `shadowData` cast at the height of vanilla's box and fade along it. Non-`blockLight` shadows take their shape from the sprite outline instead of vanilla's `shadowData` box, so a tree canopy casts a full canopy shadow.
  - Sun shadows are drawn at vanilla `SunShadow`'s ground altitude, so scene depth keeps them off walls, statues and other sprites, as in vanilla.
  - Indoors, natural rock tops and non-rendered terrain get no sun shadow; vanilla's stencil bit 4 decides.
  - On maps with sun shadows disabled (`disableSunShadows` or the biome's `disableShadows`), only static building shadows go away; `shadowData` shadows stay, as in vanilla.

**Dynamic shadow rule**

Shadows of things drawn in real time, such as pawns and door leaves (dynamic `SunShadowFade`), are still drawn by vanilla with only the fragment shader swapped; stencil state is unchanged. With `S` the ground sun factor and `s_d` vanilla's dynamic shadow factor, the final factor is:

```
min(S, s_d)
```

Each pixel is darkened once: where both shadows are `c`, the result is `c`, not `c²`. Where the ground is unshadowed, the result is `s_d`, as in vanilla. The shadow colour is applied per channel, so tinted weather shadows keep vanilla's tint.

**What does not change**

- Vanilla `GlowGrid` values are unchanged. Mood, plant growth, work speed and every other gameplay reading of light are the same as without RimLumen.
- Vanilla materials, meshes and `MatBases` are never modified. Toggling takes effect on the next frame with no state to restore.

#### Compatibility

| Mod | Behaviour |
| --- | --- |
| IsoRim | The whole takeover turns off while the 3D view is ready; pools composite as with the setting off, and the occlusion map still applies |
| RimIris | Known incompatible with the takeover and not adapted. Compatible as usual with the setting off |
| RimUnity | Particles, bloom and ScreenEfx are unaffected; the `GlowGrid` values it reads are unchanged |

#### Known differences

- Vanilla glow floods around wall corners by path distance; takeover pools use straight line of sight, so light shapes near corners differ from vanilla. `GlowGrid` values are unaffected.
- Vanilla `VisualGlowAt` is 0 on vacuum terrain; takeover pools do not reproduce this (Odyssey space maps).
- When a ray that has just left one wall grazes another wall by less than 1/8 texel (about 0.03 cell), that graze has no soft edge. A ray that actually crosses the other wall is still fully blocked.
- When a thing's height crosses the light's height, its shadow jumps from a finite length to a fading shadow that reaches the pool edge.
- No object shadow is drawn outside a pool's radius.
- Shadows are cast from the sprite's outline laid on the ground. Upright sprites such as columns in adjacent cells join outline to outline into one continuous shadow, with no light between them.

## 8. Optional RimUnity Bridge

When a RimUnity runtime is present (standalone `Chezhou.RimUnity`, or RimUnity embedded in another mod), `RimLumen.Adapter.RimUnity` reads its exported `catalog.json`, prefab JSON (including `.json.gz`), and `.rutex` textures, automatically routing each particle node:

- **Native HDR / GPU (or CPU)**: supported basic shapes with shape-local position, rotation, and scale (TRS), curves, gradients, textures, flipbooks, rate-over-time emission, probability-controlled scheduled bursts, and `PerParticle`-mode `trails` modules paired with `renderer.trailMaterial` (reading RimUnity's `worldSpace` default `false`, deriving `MaxPoints` as `clamp(ceil(maxParticleLifetime * trailLifetime * (sizeAffectsLifetime ? maxSize : 1) * 60) + 2, 16, 256)`, and registering trail textures through `RimUnityAssetProvider`).
- **Retained on RimUnity (`External`)**: nodes using `subEmitters` (including sub-emitter dependencies), `collision`, `trigger`, `renderMode.Mesh` or an unknown `renderMode`, a material `_Color` outside the native colour domain, linear `velocityOverLifetime`, `rotationOverLifetime`, curve-based `forceOverLifetime`, any enabled `lights` module (including `ratio = 1`), unsupported `trails` options (`trails.mode.Ribbon` / `trails.mode.<n>`, `trails.ratio.domain`, `trails.minVertexDistance.domain`, `trails.lifetime.dualCurve` / `.variable` / `.curve` / `.domain`, `trails.widthOverTrail.dualCurve` / `.variable` / `.weighted` / `.domain`, `trails.colorOverLifetime` or `trails.colorOverTrail` `.random` / `.perceptualBlend` / `.domain`, `trails.textureMode.<n>`, `trails.generateLightingData`, `trails.trailMaterial.null`, or `trailMaterial` `_Color.domain`), as well as deferred dynamic arc / radius modes, alignment, varying velocity / force / rotation curves, dual-curve randomness, geometry, and shape textures.

Imported burst schedule times must be strictly less than the system `Duration`. With `cycleCount=0`, repetition restarts from the same local phase in every system loop, even when the interval does not divide the duration. The total expanded occurrences across all burst definitions in one emitter are limited to 4096; above that, the whole emitter routes to External with a reason, never truncation. A nonfinite interval routes to External; a repeated burst with interval `<=0` also routes to External. A one-shot burst permits a finite nonpositive interval but does not use it. The reasons are `emission.bursts.schedule.domain` for an invalid schedule and `emission.bursts.occurrenceLimit` for exceeding the total limit. Deferred curve classification inspects only active modules and axes; stored curves in disabled emission are ignored. Active `main.startSize3D`, active `main.startRotation3D`, or nonzero `startRotation` route to External with reasons `main.startSize3D`, `main.startRotation3D`, and `main.startRotation`, respectively. Active dual-curve `rateOverDistance` uses reason `emission.rateOverDistance.dualCurve`; inactive 3D-axis fields do not cause rejection. Native initial rotation and 3D size remain deferred.

The adapter maps material `_Color` separately to `materialTint` and routes Linear materials natively when no other unsupported reason applies. Imported material colours outside the domain above route to External with a material identity and field reason, `material[<asset-id>]._Color.domain`, without clamping or folding into vertex colour.

Supported `TrailRenderer` / `LineRenderer` components translate to native ribbons; unsupported options remain External with a specific reason. Translated ribbons stay active until the effect stops. External lines end on stop; trails can stop recording and age out during fade-out. The adapter registers ribbon textures through its asset provider. This lifetime applies to RimUnity imports, not native ribbon state machines. Translated trail capacity is `max(2, ceil(time × 240))`; its gradient is multiplied by material `_Color`, with `hdrIntensity` 1. Unsupported cases include rounded corners or caps, TransformZ alignment, lighting data, unsupported texture mode or scale, weighted or looping width curves, perceptual gradients, invalid or Linear materials, paused or autodestructing trails, world-space or closed lines, lines with other than two points or a nonzero start, and multiple ribbons on one node. Diagnostics use the `trail.` / `line.` prefix, including `numCornerVertices`, `numCapVertices`, `alignment.TransformZ`, `generateLightingData`, `textureMode.N`, `textureScale`, `widthMultiplier.domain`, `widthCurve.empty` / `weighted` / `domain` / `wrapMode`, `colorGradient.perceptualBlend`, and `sharedMaterials.count`. Trail-specific reasons include `trail.time.domain`, `trail.minVertexDistance.domain`, `trail.emitting`, and `trail.autodestruct`; line-specific reasons include `line.useWorldSpace`, `line.loop`, `line.positions.count`, and `line.positions.start`; duplicate components use `ribbon.multipleOnNode`.

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
| `objectShadows` | `bool` | `true` | Height-bearing non-`blockLight` things cast silhouette shadows from point lights and the takeover sun. The “Things cast shadows from lights” setting is saved per shader pack under `objectShadowOverrides`; walls, rock and doors still occlude when off. Individual effect point lights with `castShadows: false` skip occlusion and object shadows even when global `objectShadows` is on; vanilla glowers and the takeover sun have no `castShadows` field and always follow global `objectShadows`. The built-in pack defaults to on; Crisp and Foggy both explicitly declare `true`. |
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

1. **Reserved names**: `hdr`, `glow`, `bloom`, `scene`, `depth`, `mapLightField`, `distortion`, `lightSum`, and `backbuffer` are reserved source names and cannot be used as `targets[].name`; target names also cannot begin with `prev:`.
2. **Single trailing `backbuffer`**: When `passes` is non-empty, only the final pass may—and must—output to `"backbuffer"`. All earlier offscreen passes must target a declared `targets[].name`, use `"blend": "Opaque"`, and specify a non-empty `shader`.
3. **No `backbuffer` sampling**: No pass may list `"backbuffer"` in `inputs[].source`.
4. **No forward references or same-pass self-reads**: Sampling a regular target requires an earlier pass in the array to have written it; a pass cannot read its own `output` directly in the same pass.
5. **Persistent history prerequisite**: `"prev:X"` is valid only if target `X` was declared with `"persistent": true`.
6. **Binding slot limits**: `inputs[].binding` must be in `[0, 15]`, cannot be `5` (reserved for the UBO), and cannot repeat within the same pass.

The default Bloom prefilter thresholds `hdr` and adds `glow` in full, without the threshold: vanilla additive glow copies are drawn into the 8-bit scene, so each copy stays at or below 1 and a threshold would remove them entirely; `bloomIntensity` sets how strongly they bloom. The default threshold is `1.0` in both settings and pack baseline, and peaks ≤ threshold extract exactly zero. `bloomKnee=0` is a hard threshold without division by zero. A positive knee softens only above the threshold using `excess * smoothstep(0,knee,excess)`, where `excess=max(peak-threshold,0)`.

#### 3152 Group Empty-Frame Bloom Skip Rule

Even when `runOnEmptyFrames` is `true`, the runtime skips the built-in Bloom downsample/upsample chain and binds a `1x1` black placeholder (`black1x1`) to `bloom` whenever either condition holds:

- `bloomMipLevels == 0`;
- There are no HDR draws on the current frame, vanilla glow copies included (`!hasHdrDraws`), **and** none of `bloomPrefilterShader`, `bloomDownShader`, or `bloomUpShader` is specified.

If your pack needs custom Bloom scattering on empty frames (e.g., from `scene`, `depth`, or `mapLightField`), specify at least one custom Bloom shader (such as `bloomPrefilterShader`) and set `runOnEmptyFrames: true`.

#### Two-Tier Fault Isolation

- **Scan & validation isolation**: Malformed `pack.json`, path escapes, reserved `"default"` IDs, or invalid DAGs are isolated inside that entry's `LoadError` (shown as `[损坏]` / disabled in the menu) without affecting other packs; `FindOrDefault` falls back to `default`. When a saved `selectedShaderPackId` no longer resolves to a valid pack, applying settings logs a `Log.Warning` naming the original id and falls back to `default`, and any stale `objectShadowOverrides` entries for missing packs are discarded.
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
| `distortion` | `R16G16SFloat` / `Linear` | `black1x1` (zero) | Screen-sized RG scene-sample offset in framebuffer pixels. |
| `lightSum` | `R16G16B16A16SFloat` / `Linear` | `black1x1` (zero) | Screen-sized per-pixel light-pool sum of a lighting-takeover frame: vanilla glowers and RimLumen effect pools added linearly, occlusion applied. Valid only when `ubo.LayerFlags.y = 1`, otherwise the placeholder. Not part of any default binding; declare it in `inputs`. |

The RGB of `hdr`, `glow` and `scene` share one encoding, so they add directly with no gamma or linear conversion: the scene target is `B8G8R8A8UNorm` and vanilla blends the stored values directly, while RimLumen's HDR targets use the same shader outputs and blend modes without clipping. `1.0` is vanilla full brightness; anything above `1.0` is the extra brightness vanilla clips.

#### Default Input Bindings (When Not Overridden in `passes[]`)

- **Default composite pass** (when `passes` is empty, or the final pass omits both `shader` and `inputs`; uses `PremultipliedAlpha` when `passes` is empty):
  - `binding = 0`: `hdr`
  - `binding = 1`: `bloom`
  - `binding = 2`: `scene`
  - `binding = 3`: `depth`
  - `binding = 4`: `mapLightField`
  - `binding = 6`: `distortion`
- **Custom `bloomPrefilterShader`**:
  - `binding = 0`: `hdr`, `binding = 1`: `glow`, `binding = 2`: `scene`, `binding = 3`: `depth`, `binding = 4`: `mapLightField`.
- **Custom `bloomDownShader`**:
  - `binding = 0`: previous downsample mip, `binding = 4`: `mapLightField`.
- **Custom `bloomUpShader`**:
  - `binding = 0`: lower-resolution upsample input, `binding = 1`: same-level downsample mip, `binding = 4`: `mapLightField`.

> Blend & alpha note: When `passes` is empty, the composite pass is hardcoded to `PremultipliedAlpha` blending onto map colour at the 3152 boundary. If your composite outputs the full scene (e.g., color grading or fog), output `vec4(finalRgb, 1.0)` or use an explicit `passes` entry with `"blend": "Opaque"`. If you only want to layer HDR particles and bloom over the untouched scene, output `vec4(mappedHdr, hdr.a)`: the result is `mappedHdr + scene * (1 - hdr.a)`, so pixels where both `hdr` and bloom are empty keep the scene unchanged. Each pack's composite shader owns its tone curve; the built-in default composite passes colour through unchanged while its peak is at most 1, and above 1 holds the peak at 1 and compresses the excess toward white along the colour's own hue.

> Numeric safety note: `hdr` is `RGBA16F`; heavy additive overlap can overflow to `Inf`, some drivers can also produce `NaN`, and drivers handle both inconsistently. Any pass that reads `hdr` directly (custom prefilter, composite, luminance statistics), and especially one that writes a `persistent` history target, should zero `NaN`/`Inf` and cap the value first; otherwise one bad pixel spreads through blurs or stays in the history buffer. See `sanitizeHdr` in the built-in `Foggy/Composite.frag.glsl`.

#### Fixed Vertex Interface and `#include`

The engine supplies the fullscreen triangle; fragment shaders declare:

```glsl
#version 450
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
```

Relative `#include "Common.glslinc"` files inside the same pack directory are supported.

#### Uniform Buffer (`set = 0, binding = 5`, 224-byte `std140`)

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
    float ColorBlendSpace;  // 124: 0 Legacy (hdr/bloom in stored units), 1 Linear (linear radiance)
    vec4 BloomParams;       // 128: intensity, threshold, knee, scatter
    vec4 PackParams[4];     // 144: 4 rows, 16-byte stride, 64 bytes total
    vec4 LayerFlags;        // 208: x = refract effects, y = lightSum valid, zw reserved 0
} ubo;
```

- **Parameter mapping**: `options[].target` value `packParamN` (`N = 0..15`) maps to `ubo.PackParams[N / 4][N % 4]`; existing shaders must replace the old vector members with the `PackParams` array.
- **Current ABI**: the layout is 224 bytes; packs declaring only the first 208 bytes remain valid but do not refract their own layer. `HasHdrDraws` (including early-group glow copies), `HasSceneDepth`, `HasMapLightField`, and `IsPaused` are separate 0/1 floats. `DepthValid=1` means a real depth snapshot at the boundary; 0 means the far-plane placeholder, not a per-pixel geometry test.
- **`LayerFlags.y`**: `1` when "RimLumen takes over lighting" is on and active this frame, so `lightSum` holds this frame's pool sum; `0` otherwise, `lightSum` is the black placeholder and vanilla's octagonal light overlay is drawn. Packs reading `lightSum` should fall back to `mapLightField.G` when `y = 0`. Packs that do not declare `lightSum` are unaffected; the UBO size and every existing offset are unchanged.
- `GameSeconds` is game ticks / 60 and stops on pause; `RealSeconds` is host elapsed real seconds. Their Delta fields hold this frame's step.

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

It is 1 in the vanilla top-down view too: vanilla `Cutout`, `CutoutPlant` and `CutoutComplex` write depth (`Data/Core/Defs/ShaderDefs/Shaders.Map.xml`), so wall, rock, plant and item sprite pixels all match; only terrain writes none.

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
| `crisp` | 1 custom composite / 0 | `false` | `false` | `false` | `3` | `1.7/ShaderPacks/Crisp/` |
| `foggy` | 3 passes / 2 targets | `true` | `false` | `true` | `7` | `1.7/ShaderPacks/Foggy/` |

`foggy`'s `FogAndMask` reads `lightSum` at binding 4. With `LayerFlags.y = 1` it replaces `mapLightField.G` by `min(0.5, 3.6 × luminance)` (Rec.709 weights) for its lit-area protection and warm mist; otherwise it keeps G. It reads no scene depth: under the IsoRim 3D view, mist over walls is no longer reduced.

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
    float ColorBlendSpace;
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

## 10. Framework and Adapter Extension SPI

This chapter is for asset adapters and custom backend authors. Content mods using `.lumen.json`, XML, or `RimLumenApi` do not need these interfaces. Chapters 1–9 cover content, playback, and ShaderPacks. These types are in `RimLumen.Core.Contracts`.

### 1. Registration and Asset Resolution

`LumenHostRegistry` registers services, contributors, GPU engines, node backends, and asset providers. `RegisterContributor` and `RegisterAssetProvider` ignore repeat registration of the same instance; contributors have `UnregisterContributor`, and the pipeline uses snapshots. `RegisterGpuParticleEngine` also registers the contributor; VFX service and GPU engine registration replace their respective properties; replacing the GPU engine does not unregister the old contributor, so the caller must unregister it. Only one distinct backend per feature is permitted; conflicts throw `InvalidOperationException`. Unregistering affects future plays; existing handles retain their backend.

Call `byte LumenHostRegistry.RegisterDrawAdapter(string passName, ILumenDrawAdapter adapter)` on the main thread at startup, after installing any hooks. The adapter and its private pass live for the process lifetime; the pass claims no Unity layer. Submit draws on it only when `RimLumenRenderHost.CanRouteAdapterDrawsNow` and `RimLumenRenderHost.IsMapMainLayer(layer)` are both true.

On each main-thread map capture the host combines all `int ExcludedGlowLayerMask` values with bitwise OR and skips vanilla glow copies on those layers. It ranks each private pass far to near and calls `MaterialBinding? AdaptBinding(FinalDrawItem source, LumenColorBlendSpace blendSpace)`. Borrow `source` only for the call. Return `null` to restore the original layer route, or return a depth-read-only binding that stays immutable across in-flight frames. The host copies accepted draws into the HDR pass and owns their pooled lifetime. It reuses the source mesh, uniforms and resources unchanged, so vertex inputs and material layouts must be compatible. Return `null` for unsupported draws; throwing trips the host render fail-safe.

`ILumenAssetProvider.TryResolveEffect` supplies an `EffectAsset` by ID. Providers are queried in reverse registration order; a found asset is validated on resolution. An invalid asset is rejected and logged without trying another provider. Treat resolved assets and nested arrays as immutable; hot reload returns a new instance. Core's `InMemoryAssetCatalog` and `RimUnityAssetProvider` implement it; Core resolves providers on the main-thread playback path.

`ILumenVfxService` exposes `Capabilities` and main-thread `Play`: a resolved asset produces an active handle, a missing one an inert dead handle. Core's `LumenVfxService` also returns a dead handle for rejected assets or an exhausted particle pool, logging the relevant error or warning. It provides native playback; the adapter's `RimUnityHybridVfxService` wraps it and handles external parts. `LumenSceneBootstrap.EnsureInitialized()` idempotently registers the Core catalog, service and contributors, service before simulator.

### 2. Frame Contributors and Four Contexts

`ILumenFrameContributor` runs in order: main-thread `CaptureMainThread(LumenCaptureContext)` freezes inputs, main-thread `EnqueuePreCompute(LumenComputeContext)` enqueues compute, render-thread `RecordBufferBarriers(LumenBarrierContext)` records barriers before the HDR pass, and `RecordHdrDraws(LumenHdrDrawContext)` runs inside it. An idle `HasActiveWork` permits work to be skipped. `RequiresDirectHdrPass` is read on the main thread after capture, defaults to false, and must be true for direct draws; routed mesh draws are counted separately.

Pair capture and recording by `FrameIndex`: recording may lag or a frame may be dropped. `SubmissionFrameIndex` is only the rotating resource slot. Contexts are not binary GPU layouts. Frames and textures borrowed by render-thread callbacks must not be retained or disposed, and game main-thread APIs must not be accessed there. Capture carries map ID, clocks, camera matrices, viewport and the frame latch `RendersMap` (initialized to `true` by `new LumenCaptureContext`, `false` for `default`; `false` on the world layer, during windowless long events, or while generating a map, when the pipeline still calls `CaptureMainThread` on active contributors to advance clocks and state while skipping `EnqueuePreCompute` and recording callbacks, so third-party contributors must advance state only and submit no draws or GPU staging); compute carries frame and clocks; barriers carry the frame command buffer; draw carries the HDR target, nullable depth and camera data. Core service, CPU simulator, GPU engine, distortion and ribbon backends register contributors; the adapter's external player also uses one. Pipeline callback exceptions trip the render fail-safe and are logged.

During main-thread `CaptureMainThread`, custom particle backends and adapters can call `LumenHostRegistry.GetEnvironmentForces(int mapIndex, LumenEnvironmentChannel channels)` and `LumenHostRegistry.GetCursors(int mapIndex)` to read the current frame's frozen world-space forces `ReadOnlySpan<LumenEnvironmentForce>` (`Channel`, `Shape`, `CenterWorld`, `Radius`, `Strength`, `WaveRadius`, `ShellHalfThickness`) and active cursor snapshots `ReadOnlySpan<LumenCursorSnapshot>` (`PositionXZ`, screen-height velocity `ScreenVelocity`, scalar `Speed`, and `Source` of type `LumenCursorSource`); empty spans are returned when none are active.

The L1 `CpuParticleSimulator` simulates on a Waylith worker thread. Each capture first completes the previous frame's simulation job and stages its result, then gathers all of this frame's host state on the main thread and schedules the next simulation, so L1 is always shown one frame late. A job exception is rethrown when the job is next completed and goes through the pipeline's render fail-safe; there is no fallback to synchronous simulation. Detaching or shutting down the pipeline also completes a job in flight; RimLumen then logs its exception once with `Log.Error` without rethrowing, and the rest of the cleanup still runs. The Waylith job runtime also logs a faulted job on its own.

`AcquireEmitter`, `SetEmitterTransform`, `SetEmitterParams`, `EmitBurst`, `RestartEmitter` and `ReleaseEmitter` are main-thread commands applied in call order by the next capture. `AcquireEmitter` returns its ID at once; `EmitBurst` thins by the particle density at the time of the call. `LiveParticleCount`, `GetLiveParticleCount` and the internal emitter liveness query read the most recently completed simulation. `LiveParticles` reads the simulation state directly and throws `InvalidOperationException` while a job is in flight. In game a job runs between captures whenever L1 has work, so it suits only hosts that call `CaptureMainThread`, such as WebSim and the offline tests. `ReleaseEmitter(id, immediate: true)` retires the ID at once, and the frame that applies it no longer draws that emitter's particles or trails. Calls on a retired ID are ignored. WebSim and the offline tests call `CaptureMainThread`, which runs the same simulation synchronously.

### 3. GPU Particles and Node Backends

`ILumenGpuParticleEngine` extends the frame contributor and is registered by `RegisterGpuParticleEngine`. Instance operations run on the main thread: after `CreateInstance`, call `BindNodeInstances` with node-indexed IDs, then `UpdateInstance` each frame. `RestartInstance` resets the schedule, `EmitBurst` queues extra particles, `DestroyInstance` supports immediate removal or drain, and `IsInstanceAlive` reports reclamation. `SetInstanceDisplayState(id, isVertical3D, depthReferenceZ)` is called at creation, and every frame for a node that declares `depthMask`, with whether the instance uses the `Vertical3D` projection and this frame's mask reference Z (null without a mask). `SetInstanceFrameDelta(id, frameDelta)` is called when a state transition restarts the node part-way through a frame; the next capture advances the emission schedule by that remaining time, once. IDs belong to the backend; capacity is the shared particle slot count. Core implements `LumenGpuParticleEngine`.

`ILumenNodeFeatureBackend` manages `LumenNodeFeatureFrame`: node world transform, target, map ID, intensity, active state, frame delta and `DepthReferenceZ`. `DepthReferenceZ` is this frame's world Z of the mask reference (`LumenPlayRequest.DepthReferenceZ` moved with `Follow`) when the node declares `depthMask`, and null otherwise. There is no play-origin field; backends read the node position from `Transform`. All instance calls run on the main thread. `CreateInstance` creates an inactive instance at Play; state entry or Trigger calls `RestartInstance`, each frame calls `UpdateInstance`, and state exit immediately updates it inactive. Fade-out stop calls `DestroyInstance(id, false)` and waits for `IsInstanceAlive` to become false; immediate stop or disposal uses true. Playing without a required backend throws `InvalidOperationException`. Core supplies distortion, ribbon and screen-dim backends; a backend that is also a contributor initializes the scene before registering the contributor, so node updates precede capture.

`LumenNodeFeature` is a closed enum containing only `Distortion`, `Ribbon`, and `ScreenDim`; it cannot currently add new node kinds.

### 4. Adapter Example

`Source/RimLumen.Adapter.RimUnity` scans package roots when its target mod is present, creates and registers `RimUnityAssetProvider`, then registers `RimUnityHybridVfxService` wrapping the native service. The provider translates prefabs on demand and registers particle and ribbon textures through its texture bridge. Calling `RegisterPackageRoot` again clears catalog and translation caches. Components that cannot be translated natively use the external player. This separation provides a reference for third-party asset adapters.
