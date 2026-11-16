# RimLumen

RimWorld 1.7（Waylith / Vulkan）的 HDR 视效、可编程光影包与 GPU Compute 粒子框架。
HDR VFX, programmable ShaderPack, and GPU compute particle framework for RimWorld 1.7 (Waylith / Vulkan).

## 模组作者 / Modders

| 能力 / Feature | 说明 / Detail |
|---|---|
| HDR & Bloom | 原生 `RGBA16F` 渲染 + `0–7` 级可配 Bloom / native `RGBA16F` + configurable `0–7` mip bloom |
| ShaderPacks | 声明式多 Pass 光影包（`ShaderPacks/*/pack.json` + GLSL）：自定义渲染目标、持久化历史缓冲、场景深度与 `mapLightField`（屋顶/地面亮度/室内封闭度/迷雾）、实时滑杆与热重载隔离 / declarative multi-pass packs, custom targets, persistent history, scene depth & `mapLightField`, live sliders & isolated hot reload |
| Soft Particles | 读取场景深度的软粒子 / depth-aware soft particles (`R32SFloat`) |
| L2 | Vulkan Compute 粒子（力场模拟 + `InstancedDrawInfo.Direct` 实例化绘制）/ Vulkan compute simulation + instanced drawing |
| L1 | 原生 CPU 粒子（贴图、Flipbook 序列帧、速度拉伸）/ native CPU particles (textures, flipbooks, velocity stretch) |
| Vertical3D | 朝天真 3D 物理模拟，自动适配原版 2D 与 `IsoRim Waylith` 3D 视角 / skyward 3D simulation adapting across vanilla 2D and 3D views like `IsoRim Waylith` |
| GlowGrid | 效果节点上的动态点光，写入原版 `GlowGrid` / dynamic point lights feeding vanilla `GlowGrid` |

三种原生接入方式 / three entry points：XML `LumenEffectDef`、`1.7/Data/RimLumen/**/*.lumen.json`、C# `RimLumenApi`。
可选 `Chezhou.RimUnity` 桥接位于 `1.7/Compat/RimUnity`，仅在该模组启用时加载（`LoadFolders.xml` 的 `IfModActive`）/ optional bridge, loaded only when that mod is active.

## 玩家 / Players

装上即用：原版与其他模组的加色发光特效自动获得 HDR Bloom 泛光，内置 5 档即切光影包，并可加载第三方模组或 `Config/RimLumen/ShaderPacks/` 下的自定义光影包。
Plug and play: vanilla and third-party additive glow effects automatically get HDR bloom, with 5 built-in ShaderPacks (`Standard`, `Atmospheric`, `Cinematic`, `Vibrant`, `Crisp`) and support for mod or `Config/RimLumen/ShaderPacks/` packs.

- 设置：选项 → 模组设置 → RimLumen / Options -> Mod Settings -> RimLumen
  - `EnhanceVanillaAdditiveGlow`（默认开 / default on）：无其他 RimLumen 特效播放时，关闭后画面与不装本模组逐像素一致 / off = pixel-identical when no RimLumen effects are active
  - `ParticleDensityScale`（0.1 – 1.0，默认 1.0 / default 1.0）：粒子密度 / particle density
  - `ShaderPacks`：切包、实时参数滑杆、热重载（Reload）、重扫目录（Rescan）与重置默认 / pack selector, live sliders, hot reload, rescan, and reset
  - `EnableDemoBuildings`（默认关 / default off）：启用娱乐演示建筑（烟花发射器、粒子喷泉）/ enable demo Joy buildings (Firework Launcher & Particle Fountain)

*(兼容性说明：基于 Waylith 公开渲染管线扩展，0 处 Harmony 补丁，内置渲染异常熔断机制。 / Built on Waylith's public render-graph APIs with zero Harmony patches and an automatic fail-safe.)*

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
          <startColorLinear>(1, 0.45, 0.1, 1)</startColorLinear>
          <hdrIntensity>4</hdrIntensity>
          <shape>Sphere</shape>
          <shapeExtents>(0.3, 0.3, 0.3)</shapeExtents>
          <blendMode>Additive</blendMode>
          <softParticleFadeDistance>0.5</softParticleFadeDistance>
        </emitter>
        <pointLight>
          <colorLinear>(1, 0.5, 0.15, 1)</colorLinear>
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
        "startColorLinear": [1.0, 0.45, 0.1, 1.0],
        "hdrIntensity": 4,
        "shape": "Sphere",
        "shapeExtents": [0.3, 0.3, 0.3],
        "blendMode": "Additive",
        "softParticleFadeDistance": 0.5,
        "sizeOverLifetime": [[0, 1], [1, 0]],
        "colorOverLifetime": [[0, [1, 1, 1, 1]], [1, [1, 0.3, 0.1, 0]]]
      },
      "pointLight": { "glowRadius": 6, "colorRgb": [1, 0.5, 0.15], "intensity": 1.5 }
    }
  ]
}
```

播放 / play：`RimLumenApi.PlayAtCell("Fireball", map, cell)`；同一 id 也可写成 `<packageId>:<effectId>`。

JSON 规则 / rules：键名 camelCase、大小写不敏感，允许 `//` 注释与尾逗号，**未知键直接报错**。枚举写名称字符串（或整数）。向量 `[x,y,z]` / `{x,y,z}` / 单个数字（等比）；颜色为线性 RGBA `[r,g,b,a]`（`[r,g,b]` 时 a=1）。`startLifetime` / `startSpeed` / `startSize` 写单值即 min=max，也可分写 `...Min` / `...Max`。点光键为 `glowRadius`（格）、`overlightRadius`（格）、`colorRgb`、`intensity`（XML 里对应 `radiusCells` / `colorLinear`）。

Keys are camelCase and case-insensitive; `//` comments and trailing commas are allowed; unknown keys throw. Enums are name strings or integers. Vectors: `[x,y,z]`, `{x,y,z}` or one number (uniform); colors are linear RGBA. A single `startLifetime` / `startSpeed` / `startSize` sets min = max. Point-light keys differ from XML: JSON uses `glowRadius` / `colorRgb`, XML uses `radiusCells` / `colorLinear`.

## 构建与发布 / Build and release

使用本机便携版 .NET 10 SDK（`Tools/build.ps1` 自动定位），需要 PowerShell 7：

```powershell
pwsh -NoProfile -File Tools/build.ps1                 # 构建 / build
pwsh -NoProfile -File Tools/test-offline.ps1          # 离线测试 / offline tests
pwsh -NoProfile -File Tools/pack-release.ps1          # 构建 + 测试 + 打包到 Dist/RimLumen / build + test + package
pwsh -NoProfile -File Tools/pack-release.ps1 -SkipTests
pwsh -NoProfile -File Tools/launch-showcase.ps1       # 启动六站演示场景 / launch the 6-station showcase
```

`pack-release.ps1` 只输出可上传内容：`About/`、`LoadFolders.xml`、`README.md`、`docs/api.md`、剥离展示脚手架后的两个 dll（无 pdb）、`1.7/Defs`、`1.7/Textures`、`1.7/ShaderPacks`、`1.7/Shaders/Particles` 与 `PostFx`；不含源码、测试；结束时自检并打印文件数与字节数。
`launch-showcase.ps1` 会在已有 RimWorld 进程时拒绝启动。

策划与定稿决策见 `docs/02-plan.md`、`docs/03-decisions.md`（源码仓库内，不随 Workshop 包发布）。
