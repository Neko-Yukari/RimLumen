# RimLumen

RimWorld 1.7（Waylith / Vulkan）的**旁路叠加（sidecar/overlay）HDR 视效增强层**、可编程后处理光影包宿主与 GPU Compute 粒子框架。
A **sidecar/overlay HDR VFX enhancement layer**, programmable post-processing ShaderPack host, and GPU compute particle framework for RimWorld 1.7 (Waylith / Vulkan).

- **原版管线保持原样 / Vanilla pipeline untouched**：原版地形、建筑、殖民者与 `LightOverlay` 仍留在 Waylith 原生 8-bit `UNorm` 管线中，因此 RimLumen 可与 RimIris、IsoRim Waylith 和 RimUnity ScreenEfx 自然兼容。可选的「接管地图照明与日影」默认关闭；开启后与 RimIris 已知不兼容，IsoRim 3D 视图下自动关闭。 / Vanilla terrain, buildings, pawns, and `LightOverlay` stay untouched in Waylith's native 8-bit `UNorm` pipeline, keeping RimLumen naturally compatible with RimIris, IsoRim Waylith, and RimUnity ScreenEfx. The optional map lighting and sun shadow takeover is off by default; when on it is known to be incompatible with RimIris, and it turns itself off in IsoRim's 3D view.
- **旁路 `RGBA16F` 浮点层与分段队列合成 / Sidecar `RGBA16F` layer & segmented queue compositing**：真正的 `RGBA16F` 浮点渲染在 RimLumen 的旁路 HDR 层执行（原生 L1/L2 粒子、拷贝符合条件的原版/模组加法发光，以及 `0–7` 级 HDR Bloom），并在分段地图渲染队列边界回填合成，使屋顶、迷雾和下游模组调色仍可自然作用于画面。 / True `RGBA16F` floating-point rendering runs in RimLumen's sidecar HDR layer (native L1/L2 particles, copied qualifying vanilla/mod additive glows, and `0–7` mip HDR bloom), composited back at segmented map render-queue boundaries so roofs, fog, and downstream mod color grading still apply naturally.

## 模组作者 / Modders

| 能力 / Feature | 说明 / Detail |
|---|---|
| HDR & Bloom | 旁路 `RGBA16F` 浮点层 + `0–7` 级可配 Bloom，按地图渲染队列分段回填合成 / sidecar `RGBA16F` layer + configurable `0–7` mip bloom, composited at segmented map render-queue boundaries |
| ShaderPacks | 声明式多 Pass 光影包（`ShaderPacks/*/pack.json` + GLSL）：自定义渲染目标、持久化历史缓冲、场景深度与 `mapLightField`（屋顶/人工地面光/存在使用室外温度的房间/迷雾）、实时滑杆与热重载隔离 / declarative multi-pass packs, custom targets, persistent history, scene depth & `mapLightField`, live sliders & isolated hot reload |
| Soft Particles | 读取场景深度的软粒子 / depth-aware soft particles (`R32SFloat`) |
| L2 | Vulkan Compute 粒子（力场模拟 + `InstancedDrawInfo.Direct` 实例化绘制）/ Vulkan compute simulation + instanced drawing |
| L1 | 原生 CPU 粒子（贴图、Flipbook 序列帧、速度拉伸）/ native CPU particles (textures, flipbooks, velocity stretch) |
| 拖尾 / Trails | L1 与 L2 均支持逐粒子拖尾，配合死亡子发射器可纯资产写出升空后在顶点炸开的烟花 / per-particle trails on both L1 and L2; with death sub-emitters, an asset alone can launch a rocket that bursts at its apex |
| Vertical3D | 朝天真 3D 物理模拟，自动适配原版 2D 与 `IsoRim Waylith` 3D 视角 / skyward 3D simulation adapting across vanilla 2D and 3D views like `IsoRim Waylith` |
| 点光 / Point lights | 效果节点上的纯视觉光池（渲染队列 `3101`），不写入原版 `GlowGrid`、不影响游戏逻辑；需要逻辑照明且不想显示原版八角形光斑的自定义建筑可挂载 RimLumen 的纯逻辑照明 `ThingComp` / purely visual light pools (queue `3101`) on effect nodes, never written to vanilla `GlowGrid`; custom buildings needing gameplay lighting without vanilla octagonal overlays can attach RimLumen's pure-logical light `ThingComp` |
| 遮挡与投影 / Occlusion & shadows | 点光受墙、门、岩石遮挡，有高度的物体按贴图轮廓投影（光影包 `objectShadows` 开关）；点光 `castShadows: false` 时不受任何遮挡也不投影 / point lights are occluded by walls, doors and rock, and height-bearing things cast sprite-silhouette shadows (ShaderPack `objectShadows`); `castShadows: false` makes a light ignore all occlusion and cast no shadows |

三种原生接入方式 / three entry points：XML `LumenEffectDef`、`1.7/Data/RimLumen/**/*.lumen.json`、C# `RimLumenApi`。
可选 RimUnity 桥接位于 `1.7/Compat/RimUnity`，独立版 `Chezhou.RimUnity` 或其他模组内嵌的 RimUnity 运行时在场时生效：翻译其导出特效，并把 RimUnity 世界特效的粒子、网格、拖尾接入 HDR 发光 / optional RimUnity bridge, active when standalone `Chezhou.RimUnity` or a RimUnity runtime embedded in another mod is present: it translates exported effects and routes RimUnity world-effect particles, meshes and trails into the HDR glow layer.

完整 API：[`docs/api.md`](docs/api.md)（中文）/ [`docs/api.en.md`](docs/api.en.md)（English）。

制作工具 / Authoring tools：
- 游戏内预览 / In-game preview：开发者模式下从调试动作的 RimLumen 分类打开，可搜索、播放、切换状态，重载资产后立即重播 / in Dev Mode, open it from the RimLumen debug-action category to search, play and switch states, replaying after asset reloads.
- 网页编辑器 / Web editor：<https://neko-yukari.github.io/RimLumen/editor/>，免开游戏编辑 `.lumen.json` 或 XML `LumenEffectDef` 并实时预览，使用与游戏相同的核心解析、模拟与校验代码；不还原游戏内光照、遮挡与后期效果。源码在 [`WebEditor/`](WebEditor/) / edit `.lumen.json` or XML `LumenEffectDef` with live preview without launching the game, using the same core parser, simulation and validation; in-game lighting, occlusion and post-processing are not reproduced. Source in `WebEditor/`.

## 玩家 / Players

装上即用：符合条件的原版与其他模组加色发光自动获得 Bloom 泛光，内置 3 档即切光影包（`Standard`、`Crisp`、`Foggy`），并可加载第三方模组或 `Config/RimLumen/ShaderPacks/` 下的自定义光影包。队列 ≥3152 的发光使用宿主 Bloom 着色器和当前光影包的参数，不包含光影包的完整自定义效果；地图末尾的泛光不会出现在此前的 Screen 和 VoidNode 抓取中。
Plug and play: eligible vanilla and third-party additive glows automatically get bloom, with 3 built-in ShaderPacks (`Standard`, `Crisp`, `Foggy`) and support for mod or `Config/RimLumen/ShaderPacks/` packs. Glows at render queues >=3152 use host bloom shaders and pack parameters, not the pack's complete custom effects; map-end bloom is absent from earlier Screen and VoidNode grabs.

- 设置：选项 → 模组设置 → RimLumen / Options -> Mod Settings -> RimLumen
  - `EnhanceVanillaAdditiveGlow`（默认开 / default on）：无其他 RimLumen 特效播放且当前光影包不重写场景时，关闭后画面与不装本模组逐像素一致 / off = pixel-identical only when no RimLumen effects are active and the active pack does not rewrite the scene
  - `ParticleDensityScale`（0.1 – 1.0，默认 1.0 / default 1.0）：粒子密度 / particle density
  - `ShaderPacks`：切包、实时参数滑杆、热重载（Reload）、重扫目录（Rescan）与重置默认 / pack selector, live sliders, hot reload, rescan, and reset
  - 接管地图照明与日影（默认关 / default off）：替换地图照明和日影的画面，游戏逻辑光照数值不变 / replaces the visuals of map lighting and sun shadows; gameplay light levels are unchanged
  - `EnableDemoBuildings`（默认关 / default off）：启用娱乐演示建筑（烟花发射器、粒子喷泉、扭曲塔与三盏照明对照路灯）/ enable demo Joy buildings (Firework Launcher, Particle Fountain, Distortion Pylon, and three lighting-comparison Street Lamps)

内置渲染异常熔断机制。 / Includes an automatic rendering fail-safe.

### Quickstart 1：XML Def

`Defs/Effects.xml`（任意模组 / any mod）：

```xml
<Defs>
  <RimLumen.Core.Assets.LumenEffectDef>
    <defName>MyFireball</defName>
    <nodes>
      <li>
        <name>core</name>
        <spacePlane>GroundXZ</spacePlane>
        <emitter>
          <name>core</name>
          <preferredTier>L2_GpuCompute</preferredTier>
          <maxParticles>2000</maxParticles>
          <duration>2</duration>
          <looping>false</looping>
          <rateOverTime>600</rateOverTime>
          <startLifetimeMin>0.4</startLifetimeMin>
          <startLifetimeMax>0.9</startLifetimeMax>
          <startSpeedMin>0.5</startSpeedMin>
          <startSpeedMax>2</startSpeedMax>
          <startSizeMin>0.2</startSizeMin>
          <startSizeMax>0.5</startSizeMax>
          <startColor>(1, 0.45, 0.1, 1)</startColor>
          <hdrIntensity>4</hdrIntensity>
          <shape>Sphere</shape>
          <shapeExtents>(0.3, 0.3, 0.3)</shapeExtents>
          <blendMode>Additive</blendMode>
          <softParticleFadeDistance>0.5</softParticleFadeDistance>
        </emitter>
        <pointLight>
          <color>(1, 0.5, 0.15, 1)</color>
          <radiusCells>6</radiusCells>
          <intensity>1</intensity>
        </pointLight>
      </li>
    </nodes>
  </RimLumen.Core.Assets.LumenEffectDef>
</Defs>
```

一行播放 / play in one line（主线程 / main thread）：

```csharp
RimLumen.Core.Api.RimLumenApi.PlayAtCell("MyFireball", map, pawn.Position);
```

`RimLumenApi.Play(effectId, map, worldPos)` 接受世界坐标；两者都返回 `ILumenEffectHandle`（`Trigger` / `SetParam` / `Stop`）。

`spacePlane`：`GroundXZ`（地面，原样使用节点旋转）、`Vertical3D`（朝天真 3D 空间 + 2D/3D 视角自适应）。`shape`：`Point` `Sphere` `CircleXZ` `Cone` `Box`。`billboardMode`：`Camera` `Horizontal` `StretchedVelocity`。`blendMode`：`Additive` `AlphaBlend` `Premultiplied`。

### Quickstart 2：`.lumen.json`

`1.7/Data/RimLumen/Fireball.lumen.json`（`effectId` 缺省时取文件名 / defaults to the file stem）：

```json
{
  "effectId": "Fireball",
  "defaultDuration": 2,
  "nodes": [
    {
      "name": "core",
      "spacePlane": "GroundXZ",
      "localPosition": [0, 0.5, 0],
      "emitter": {
        "preferredTier": "L2_GpuCompute",
        "maxParticles": 2000,
        "duration": 2,
        "rateOverTime": 600,
        "startLifetime": 0.9,
        "startSpeed": 2,
        "startSize": 0.5,
        "startColor": [1.0, 0.45, 0.1, 1.0],
        "hdrIntensity": 4,
        "shape": "Sphere",
        "shapeExtents": [0.3, 0.3, 0.3],
        "blendMode": "Additive",
        "softParticleFadeDistance": 0.5,
        "sizeOverLifetime": [[0, 1], [1, 0]],
        "colorOverLifetime": [[0, [1, 1, 1, 1]], [1, [1, 0.3, 0.1, 0]]]
      },
      "pointLight": { "glowRadius": 6, "color": [1, 0.5, 0.15], "intensity": 1.5 }
    }
  ]
}
```

播放 / play：`RimLumenApi.PlayAtCell("Fireball", map, cell)`；同一 id 也可写成 `<packageId>:<effectId>`。

JSON 规则 / rules：键名 camelCase、大小写不敏感，允许 `//` 注释与尾逗号，**未知键直接报错**。枚举写名称字符串（或整数）。向量 `[x,y,z]` / `{x,y,z}` / 单个数字（等比）；颜色为线性 RGBA `[r,g,b,a]`（`[r,g,b]` 时 a=1）。`startLifetime` / `startSpeed` / `startSize` 写单值即 min=max，也可分写 `...Min` / `...Max`。点光键为 `glowRadius`（格）、`color`、`intensity`、`castShadows`（默认 `true`；设为 `false` 时不受任何遮挡也不投射物件阴影，不受全局 `objectShadows` 影响）（XML 里对应 `radiusCells` / `color` / `intensity` / `castShadows`）。

Keys are camelCase and case-insensitive; `//` comments and trailing commas are allowed; unknown keys throw. Enums are name strings or integers. Vectors: `[x,y,z]`, `{x,y,z}` or one number (uniform); colors are linear RGBA. A single `startLifetime` / `startSpeed` / `startSize` sets min = max. Point-light keys differ from XML: JSON uses `glowRadius` / `color` / `intensity` / `castShadows` (default `true`; `false` ignores all occlusion and object shadows regardless of global `objectShadows`), XML uses `radiusCells` / `color` / `intensity` / `castShadows`.

## 构建与发布 / Build and release

需要 PowerShell 7 和已安装的 RimWorld 1.7，以及工坊订阅的 Harmony（编译引用）。脚本先读环境变量 `RIMWORLD_DIR`，未设置时在各 Steam 库中查找 RimWorld，并使用游戏自带的便携 .NET 10 SDK（`Moddev/tools/dotnet`）；找不到时报错。直接用 `dotnet` 或 IDE 构建时须先设置 `RIMWORLD_DIR`。
Requires PowerShell 7, an installed RimWorld 1.7 and the Workshop Harmony subscription (compile reference). The scripts read `RIMWORLD_DIR`, otherwise search your Steam libraries for RimWorld, and use the game's bundled .NET 10 SDK (`Moddev/tools/dotnet`); they fail if none is found. Set `RIMWORLD_DIR` when building with plain `dotnet` or an IDE.

```powershell
pwsh -NoProfile -File Tools/build.ps1                 # 构建 / build
pwsh -NoProfile -File Tools/test-offline.ps1          # 离线测试 / offline tests
pwsh -NoProfile -File Tools/pack-release.ps1          # 构建 + 测试 + 打包到 Dist/RimLumen / build + test + package
pwsh -NoProfile -File Tools/pack-release.ps1 -SkipTests
pwsh -NoProfile -File Tools/launch-showcase.ps1       # 启动六站演示场景 / launch the 6-station showcase
```

`pack-release.ps1` 只输出可上传内容：`About/`、`LoadFolders.xml`、`Languages/`、`README.md`、`docs/api.md`、剥离展示脚手架后的两个 dll（无 pdb）、`1.7/Defs`、`1.7/Textures`、`1.7/ShaderPacks`、`1.7/Shaders/Particles`、`PostFx` 与 `RimLumen`；不含源码、测试；结束时自检并打印文件数与字节数。
`launch-showcase.ps1` 会在已有 RimWorld 进程时拒绝启动。

## 许可证 / License

[MIT](LICENSE)。部分粒子与条带公式移植自 RimUnity（MIT），见 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) / portions are adapted from RimUnity (MIT); see `THIRD_PARTY_NOTICES.md`.
