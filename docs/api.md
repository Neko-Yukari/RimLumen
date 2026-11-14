# RimLumen 模组开发 API 手册

本文面向需要在《环世界》1.7（Waylith / Vulkan 引擎）中使用 RimLumen 制作特效的模组作者。所有接口、字段与限制均对应当前 `RimLumen.Core` 源码。

---

## 一、快速接入方式

按接入需求，可以选以下三种方式之一：

1. **纯数据特效定义（不需要引用 DLL）**：在你的模组 `About/About.xml` 中把 `NekoYukari.RimLumen` 加入 `modDependencies` / `loadAfter`，然后编写 XML `LumenEffectDef` 或 `.lumen.json` 特效文件。
2. **C# 调用播放（只读名称或动态造特效）**：在你的 C# 项目引用 `RimLumen.Core.dll`（位于本模组 `1.7/Assemblies/RimLumen.Core.dll`），通过 `RimLumen.Core.Api.RimLumenApi` 播放或注册特效。
3. **可编程光影包（ShaderPacks，不需要引用 DLL）**：在模组 `1.7/ShaderPacks/<PackFolder>/` 下编写 `pack.json` 与 GLSL `#version 450` 片段着色器，声明自定义后处理 Pass、渲染目标、历史缓冲与玩家实时滑杆。

---

## 二、坐标系、空间平面与时间同步

### 1. 世界坐标系约定

《环世界》1.7 为 2.5D 正交/透视俯视视角，世界坐标轴定义如下：

- `+X`：东（屏幕向右）
- `+Y`：高度轴（指向镜头上方）
- `+Z`：北（屏幕向上）

### 2. 节点空间平面（`LumenSpacePlane`）

| 枚举值 | 数值 | 旋转行为 | 常见用途 |
| --- | --- | --- | --- |
| `GroundXZ` | `0` | 单位矩阵，不加额外俯仰旋转 | 贴地法阵、地面光环、水平扩散波 |
| `HeightVertical90` | `1` | 局部方向绕 X 轴转 `+90°`，将局部 `+Y` 映射为世界 `+Z`（屏幕上方），**不影响 `LocalPosition.Y` 高度偏移** | 烟花升空、喷泉、垂直火柱、向上喷射 |
| `Free3D` | `2` | 完全保留节点自身的 3D `LocalRotation`；显示不做任何相机适配 | 任意 3D 朝向子发射器（如 RimUnity 导入资产） |
| `Vertical3D` | `3` | 与 `Free3D` 相同使用单位旋转，局部 `+Y` 即世界 `+Y`；模拟保持真 3D，**仅显示**按相机适配：IsoRim（Y 被压扁）下把高于 `baseY` 的高度除以压扁系数，还原为真实高度；原版正俯视 2D 相机下把高度投影到屏幕上方（`+Z`），绘制高度取 `max(baseY, MoteOverhead)`；`StretchedVelocity` 的拉伸方向同步取真 3D 速度 | 烟花、喷泉等真正向上喷射并落回地面的特效 |

`Vertical3D` 的 `baseY` 是播放原点（`LumenPlayRequest.OriginWorld.Y`）的高度；节点 `LocalPosition.Y` 是相对该原点的高度（例如烟花爆点 `4.8`）。`GroundXZ`、`HeightVertical90`、`Free3D` 的行为保持不变。

### 地面接触过期（`EmitterSpec.KillBelowFloor` / `killBelowFloor`）

`killBelowFloor`（默认 `false`）在 C# `EmitterSpec.KillBelowFloor`、`.lumen.json` 发射器对象以及 XML `LumenEffectDef` 的 `<emitter>` 中均可设置。为 `true` 时，粒子在世界高度低于播放原点高度 `baseY` **且**世界速度 `Y < 0`（正在下落）的那一帧过期并被回收；在地面以下但仍上升、或恰在地面上刚发射的粒子不受影响。L1（CPU）与 L2（GPU compute）行为一致，与空间平面无关，局部空间模拟的发射器按其世界矩阵换算后判定。

> 提示：所有发射形状中，`Cone`（圆锥）的默认主喷射轴是局部 `+Y`。想让粒子沿真实高度向上喷出并落回，请给节点设 `Vertical3D`，把世界空间重力设为 `(0, -1, 0)`，需要落地即消失时再设 `KillBelowFloor`。若仍想让粒子在俯视屏幕里朝屏幕上方（`+Z`）喷出（旧写法，显示不随相机适配），可用 `HeightVertical90` 并把重力设为 `(0, 0, -1)`。

### 3. 游戏时间同步

默认情况下，粒子模拟与节点时间轴严格跟随游戏时间：游戏暂停时步长为 `0`（空中粒子和力场定格并照常绘制），`1x` / `2x` / `3x` 时同步倍速推进。需要无视游戏暂停的特效，可在播放请求中显式传入 `realTime: true`。

---

## 三、资产格式 1：`.lumen.json` 数据文件（推荐纯资产定义）

### 1. 文件位置与 ID 解析

RimLumen 在启动阶段自动扫描所有启用模组的以下两个目录（递归匹配 `*.json`）：

- `<ModRoot>/1.7/Data/RimLumen/`
- `<ModRoot>/Data/RimLumen/`

假设你的模组 `packageId` 为 `Author.MyMod`，文件位于 `1.7/Data/RimLumen/Spells/Fireball.lumen.json`，注册后会生成两组可用 ID：

1. **全限定 ID**：`Author.MyMod:Spells/Fireball`（始终指向你自己的资产，不会撞名）。如果 JSON 内声明了不同的 `effectId`，也会同时注册 `Author.MyMod:<effectId>`。
2. **裸短名**：`Spells/Fireball`（以及 JSON 内的 `effectId`）。裸短名采用**先到先得**规则，后加载的模组不会静默覆盖先加载模组的同名短名。

### 2. JSON 解析规则

- 键名采用 camelCase，解析时**大小写不敏感**，允许 `//` 注释和尾逗号。
- **未知键名会直接报错**，用于尽早暴露拼写错误。
- 向量支持 `[x, y, z]`、`{ "x": ..., "y": ..., "z": ... }` 或单个数字（等比 `x = y = z`）。
- 颜色为线性 RGBA，支持 `[r, g, b, a]` 或 `[r, g, b]`（此时 `a = 1`）。
- 枚举支持名称字符串或整数。
- `startLifetime`、`startSpeed`、`startSize` 写单个数字时等价于 `Min = Max`，也可以分开写 `...Min` / `...Max`。

### 3. 完整结构与字段默认值

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

#### 字段补充说明

- **根节点**：`nodes` 必须非空；`defaultDuration` 必须有限且 `>= 0`。
- **节点层级（`EffectNodeSpec`）**：`parentIndex` 必须为 `-1`（挂在根节点下）或严格小于当前节点索引 `[0, i)`，以此从结构上禁止循环父子引用。若不写 `localRotation`（JSON 支持四元数或 `[x,y,z,w]`），可用 `localYawDegrees` 绕世界 `+Y` 旋转。
- **曲线（`sizeOverLifetime` / `intensityOverTime`）**：支持三种写法——单数值（常量）、数组 `[[time, value], ...]`（时间必须非递减）、对象 `{ "keys": [...], "multiplier": 1 }`。关键帧还可选带切线 `[time, value, inTangent, outTangent]`。
- **渐变（`colorOverLifetime`）**：支持颜色数组（单色常量）或停靠点数组 `[[time, [r,g,b,a]], ...]` 或对象 `{ "stops": [{ "time": 0, "rgbaLinear": [r,g,b,a] }] }`，同样要求 `time` 非递减。
- **`pointLight`**：JSON 下颜色支持 `colorRgb` 或 `colorLinear`（二者均取线性 RGB 分量），半径键为 `glowRadius` 与 `overlightRadius`（单位：地图格）。

---

## 四、资产格式 2：XML `LumenEffectDef`

可在任意模组 `Defs/**/*.xml` 中定义 `RimLumen.Core.Assets.LumenEffectDef`，除以下四处字段命名跟随环世界 XML 习惯外，其余默认行为与 `.lumen.json` 一致：

1. **ID**：使用 `<defName>`，同样会同时注册裸短名与 `<packageId>:<defName>`。
2. **节点字段**：额外提供 `<heightOffset>`（会累加到 `localPosition.y`），`localScale` 为单个浮点标量，不提供 `localRotation` 四元数（用 `<localYawDegrees>`）。
3. **点光键名**：使用 `<radiusCells>`（对应 JSON 的 `glowRadius`）、`<overlightRadius>`、`<colorLinear>`（环世界 `Color` 语法 `(r, g, b, a)`）、`<intensity>` 与 `<intensityOverTime>`（`<keys>` 由 `<li><time>..</time><value>..</value></li>` 组成）。
4. **当前限制**：XML Def 暂未暴露 `timelineEvents` 节点（复杂多阶段时间轴事件请使用 `.lumen.json` 或 C# `EffectAsset`）。

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

## 五、枚举与物理力场参考

### 1. 粒子系统枚举（`RimLumen.Core.Contracts`）

- **`LumenParticleTier`**：`L0_ExternalCpu`（外部兼容播放器）/ `L1_NativeCpu`（原生 CPU 模拟，适合少粒子或需频繁代码交互）/ `L2_GpuCompute`（Vulkan Compute 模拟，默认，适合大数量与力场粒子）。
- **`LumenShapeKind`**：`Point`（0）/ `Sphere`（1）/ `CircleXZ`（2）/ `Cone`（3）/ `Box`（4）。
- **`LumenBlendMode`**：`Additive`（0，推荐发光与火焰）/ `AlphaBlend`（1，烟雾与水雾）/ `Premultiplied`（2）。
- **`LumenBillboardMode`**：`Camera`（0，面向相机）/ `Horizontal`（1，水平贴地面片）/ `StretchedVelocity`（2，按速度方向拉伸，配合 `VelocityStretchFactor` 与 `LengthScale`）/ `Mesh`（3，网格枚举保留值）。

### 2. 四种力场（`ForceFieldSpec` / `ForceFieldKind`）

| 类型（`Kind`） | `CenterOrAxis` | `Parameters` | `Radius` | `Strength` | `Frequency` | 效果说明 |
| --- | --- | --- | --- | --- | --- | --- |
| `GravityDrag` (`0`) | 加速度方向（不自动归一化），如 `(0, 0, -1)` | `x` = 空气阻尼系数 `drag`（`>= 0`，按 `exp(-drag * dt)` 衰减速度） | 不用 | 加速度大小 | 不用 | 施加恒定方向重力并带速度阻尼 |
| `Vortex` (`1`) | 旋转轴方向（默认 `(0, 1, 0)`） | `x` = 向轴心拉力（负值为推离）；`y` = 沿轴上升力 | 有效半径（`<= 0` 表示全图不衰减） | 切向旋转力度 | 不用 | 围绕原点轴线做线性衰减的旋涡旋转 |
| `CurlTurbulence` (`2`) | 不用 | 空间采样偏移量 | 不用 | 湍流扰动振幅 | 噪声空间频率 | 无散度 3D 旋度噪声，制造流体摆动与丝状漂浮 |
| `PointAttractor` (`3`) | 吸引中心位置 | 不用 | 有效半径（`<= 0` 表示全图不衰减） | 吸引力强度（负值排斥） | 不用 | 指向（或背离）目标点做线性距离衰减 |

> 注意：`WorldSpaceSimulation == true` 时，力场在世界坐标空间计算（例如顶视角往下掉用 `(0, 0, -1)`）；为 `false` 时在发射器局部空间计算。

---

## 六、C# 运行时接口（`RimLumen.Core.Api`）

> 线程约束：`RimLumenApi` 与 `LumenTextureRegistry` 的所有方法均**必须在游戏主线程调用**。

### 1. 状态查询与动态资产注册

```csharp
using RimLumen.Core.Api;
using RimLumen.Core.Contracts;

bool ready = RimLumenApi.IsReady;
LumenCapabilities caps = RimLumenApi.Capabilities;
// caps.IsHdrActive / IsSceneDepthAvailable / IsGpuComputeAvailable / MaxGpuParticlesPerFrame

RimLumenApi.RegisterEffect(myEffectAsset); // 运行时注册或覆盖同 EffectId 的资产
bool exists = RimLumenApi.TryGetEffect("Author.MyMod:Spells/Fireball", out EffectAsset? asset);
```

### 2. 播放接口

```csharp
// 1. 按格子中心播放（最常用）
ILumenEffectHandle handle = RimLumenApi.PlayAtCell(
    effectId: "Author.MyMod:Spells/Fireball",
    map: pawn.Map,
    cell: pawn.Position,
    scale: 1f,
    rotationDegrees: 0f,
    realTime: false);

// 2. 按世界坐标播放（注意 Vector3 为 System.Numerics.Vector3）
ILumenEffectHandle handle2 = RimLumenApi.Play(
    effectId: "MyFireball",
    map: map,
    originWorld: new System.Numerics.Vector3(x, y, z),
    scale: 1.2f,
    rotationDegrees: 90f,
    realTime: false);

// 3. 完整请求（朝向目标点 / 每帧动态跟随 / 指定初始激活节点）
ILumenEffectHandle handle3 = RimLumenApi.Play(new LumenPlayRequest
{
    EffectId = "MyBeam",
    MapIndex = map.uniqueID,
    OriginWorld = startPos,
    TargetWorld = targetPos, // 自动计算朝向 TargetWorld 的 XZ 方位角
    Scale = 1f,
    Follow = (out System.Numerics.Vector3 pos, out float yaw) =>
    {
        if (pawn.Destroyed || !pawn.Spawned)
        {
            pos = default;
            yaw = 0f;
            return false; // 返回 false 时保持最后已知位置（冻结位置而不弹回原点）
        }
        var p = pawn.DrawPos;
        pos = new System.Numerics.Vector3(p.x, p.y, p.z);
        yaw = pawn.Rotation.AsAngle;
        return true;
    },
    InitialState = "charge", // 非 null 时，仅 Name == "charge" 的节点初始发射，其余暂停等待 Trigger
    RealTime = false,
});
```

- 如果 `EffectId` 不存在，`Play` 不会抛异常，而是返回一个 `IsAlive == false` 的静默空句柄，所有调用安全无副作用。

### 3. 句柄控制（`ILumenEffectHandle`）

| 成员 | 作用与注意事项 |
| --- | --- |
| `bool IsAlive` | 播放中或淡出排空期间为 `true`，全部粒子与点光清理完毕后变为 `false`。 |
| `void Trigger(string eventOrStateName)` | 触发同名 `TimelineEventSpec`，并激活同名节点开始发射。**注意**：`L1_NativeCpu` 节点会顺带按 `Bursts[0].Count`（或取整的 `RateOverTime`）补发一次脉冲；`L2_GpuCompute` 节点只会开启发射状态，不会在 `Trigger` 时额外做一次性脉冲。 |
| `void SetParam(string name, float value)` | 实时修改运行时参数。当前内置三个立即生效的参数名：`intensity`（缩放粒子和点光亮度）、`rate`（缩放 L1 发射速率）、`scale`（乘算根节点缩放）。其他名字会被记录下来但不会影响模拟。 |
| `void Stop(bool allowFadeOut = true)` | `true` 时立刻停止新发射、现存点光在 `0.25s` 内淡出、空中粒子自然跑完寿命后自动回收；`false` 时立刻切断所有粒子与点光。 |
| `void Dispose()` | 等价于 `Stop(false)`，幂等。在 `Thing.DeSpawn` 或地图销毁时务必调用，避免长期持有句柄。 |

---

## 七、贴图注册与兼容注意事项

### 1. 自定义粒子贴图（`LumenTextureRegistry`）

发射器填写的 `textureKey` 按以下顺序在主线程解析：

1. 先查 `LumenTextureRegistry.Register(string key, Waylith.Rendering.Texture texture)` 显式注册过的贴图；
2. 未命中时，调用游戏原版 `ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)` 加载模组 `Textures/` 目录下的图片（例如 `"RimLumen/Effects/Spark"`）；
3. 仍找不到时，退回程序化默认圆斑，并且每个缺失的 `key` 只在日志打印一次警告。

> 不要在多线程或渲染回调里调用贴图注册。

### 2. 原版加法发光自动增强的兼容现状

RimLumen 默认对原版及第三方模组的加法混合绘制（`Blend.Enabled` 且 `DstColor == One`、`ColorOp == Add`、`SrcColor` 为 `SrcAlpha` 或 `One`、且不写深度）进行重画以进入 HDR 泛光，并内置排除了 5 个全屏/环境遮罩键：`GlowOverlay`、`OxygenOverlayIndoors`、`GravshipLensFlare`、`UnlitAdditive`、`MoteWater`。

如果在自定义 `ShaderDef` 上不想被自动重画增强，可以挂载 `<li Class="RimLumen.Core.Compat.LumenEmissiveModExtension"><EnableHdrBloom>false</EnableHdrBloom></li>` 进行逐 `ShaderDef` 排除。（注：该扩展类中的 `HdrMultiplier` 字段当前尚未接入重画强度调节，仅 `EnableHdrBloom` 生效。）

---

## 八、可选 `Chezhou.RimUnity` 桥接说明

当游戏同时启用 `Chezhou.RimUnity` 时，`RimLumen.Adapter.RimUnity` 会读取其导出的 `catalog.json`、预制体 JSON（含 `.json.gz`）和 `.rutex` 贴图，并将粒子节点按功能自动分流：

- **转入原生 HDR / GPU（或 CPU）**：基础形状、曲线、渐变、贴图、序列帧、周期发射及确定性脉冲。
- **保留在原 RimUnity 播放器混播（`L0_ExternalCpu`）**：节点启用了 `trails`、`subEmitters`（含子发射器依赖）、`collision`、`trigger`、`renderMode.Mesh`、线性 `velocityOverLifetime`、`rotationOverLifetime`、变化曲线 `forceOverLifetime`、非 `1` 的 `lights.ratio`、概率脉冲，或使用了核心暂未建模的形状（如非零底面半径圆锥、球壳、圆环、网格形状或局部形状偏移）。

上层调用 `RimLumenApi.Play` 时无需感知背后走哪一路，同一个 `ILumenEffectHandle` 会同时接管两端生命周期。

---

## 九、可编程光影包开发规范（ShaderPacks）

RimLumen 支持声明式多 Pass 光影包（ShaderPacks）。模组作者或玩家只需放置 `pack.json` 与 GLSL `#version 450` 片段着色器，无需编写 C# 代码即可扩展全屏后处理、替换 Bloom 算法、读取场景深度与地图光场，并在模组设置中暴露实时调节滑杆。

### 1. 目录结构、扫描顺序与覆盖规则

每个光影包是一个独立子目录，至少包含一份 `pack.json` 清单文件：

```text
1.7/ShaderPacks/
  MyShaderPack/
    pack.json
    Composite.frag.glsl
    Common.glslinc
```

#### 扫描根目录与覆盖优先级

`ShaderPackCatalog` 只扫描每个根目录下的**直接子目录**（按目录名不区分大小写排序），并检查其中是否存在 `pack.json`。加载顺序从低到高如下，后加载的包按不区分大小写的 `id` 覆盖先前的同名包（首次出现的 `id` 保留其在列表中的排序位置）：

1. **内置基线**：`StandardEntry`（`id` 为 `"default"`，对应 `ShaderPackSpec.Default`，无磁盘 `pack.json`）。
2. **RimLumen 内置包**：`<RimLumen>/1.7/ShaderPacks/`，随后 `<RimLumen>/ShaderPacks/`（来源标记为 `内置`）。
3. **已启用模组包**：按 `LoadedModManager.RunningMods` 模组加载顺序，依次扫描每个模组的 `<ModRoot>/1.7/ShaderPacks/`，随后 `<ModRoot>/ShaderPacks/`（来源标记为 `模组: <ModName>`）。
4. **玩家本地目录**：`Config/RimLumen/ShaderPacks/`（由游戏 `GenFilePaths.ConfigFolderPath` 解析，来源标记为 `本地`）。本地包优先级最高，适合玩家微调或开发者热重载调试。

> 注意：`"default"` 是内置 `Standard` 基线的保留 ID。任何 `pack.json` 将 `id` 设为 `"default"`（不区分大小写）都会在加载阶段被拒绝并标记错误。

#### 路径安全与发布打包限制

- **相对路径约束**：`pack.json` 中的所有着色器路径（`compositeShader`、`bloomPrefilterShader`、`bloomDownShader`、`bloomUpShader` 与 `passes[].shader`）必须是相对于包目录的路径，禁止绝对路径或 `..` 上级目录穿越。
- **物理路径校验**：加载器会对包根目录与解析后的着色器路径执行 `ResolveLinkTarget`（最多解析 32 跳），拒绝任何指向包目录外部的符号链接或 Windows Junction；包目录自身通过 Junction 挂载（例如开发期的 `Mods/RimLumen`）仍可正常通过校验。
- **发行打包门禁**：`Tools/pack-release.ps1` 会校验 `1.7/ShaderPacks/` 下所有文件扩展名，仅允许 `.json`、`.glsl` 与 `.glslinc`。

---

### 2. `pack.json` 完整结构与字段默认值

`pack.json` 解析器忽略属性名大小写，支持 `//` / `/* */` 注释与尾逗号。

```json
{
  "id": "my_night_glow",
  "name": "My Night Glow",
  "author": "Author",
  "description": "可选说明文本",
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
      "label": "夜雾强度 (Fog strength)",
      "target": "packParam0",
      "default": 0.5,
      "min": 0.0,
      "max": 1.0,
      "step": 0.02,
      "tooltip": "调节地面薄雾浓度。"
    }
  ]
}
```

#### 根级字段

| 字段 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `id` | `string` | 文件夹名 | 包唯一标识（大小写不敏感）；不可为 `"default"`。 |
| `name` | `string` | `id` | 在设置下拉菜单与参数标题栏显示的名称。 |
| `author` | `string` | `"Unknown"` | 作者名称。 |
| `description` | `string` | `""` | 包功能说明，显示在设置摘要区。 |
| `runOnEmptyFrames` | `bool` | `false` | 为 `true` 时，即使当前帧没有任何 HDR 粒子或原版加色重画也强制执行管线；环境压暗、迷雾、全局调色类包应设为 `true`。 |
| `requiresSceneDepth` | `bool` | `false` | 为 `true` 时，空帧也强制将场景深度拷贝到 `depth` 纹理（有 HDR 绘制的帧会自动提供场景深度）。 |
| `requiresMapLightField` | `bool` | `false` | 为 `true` 时，维护并上传当前地图的 `mapLightField` RGBA8 纹理。 |
| `bloomMipLevels` | `int` | `5` | 内置 Bloom 金字塔级数，合法范围 `[0, 7]`；设为 `0` 完全关闭内置 Bloom。 |
| `compositeShader` | `string?` | `null` | 默认合成片段着色器；当 `passes` 为空或最后一个 Pass 未填写 `shader` 时使用，均为空则回退内置 `RimLumenComposite.frag.glsl`。 |
| `bloomPrefilterShader` | `string?` | `null` | 替换内置 Bloom 预过滤着色器（`RimLumenBloomPrefilter.frag.glsl`）。 |
| `bloomDownShader` | `string?` | `null` | 替换内置 Bloom 降采样着色器（`RimLumenBloomDown.frag.glsl`）。 |
| `bloomUpShader` | `string?` | `null` | 替换内置 Bloom 升采样着色器（`RimLumenBloomUp.frag.glsl`）。 |
| `targets` | `Target[]` | `[]` | 自定义离屏渲染目标声明。 |
| `passes` | `Pass[]` | `[]` | 自定义 Render Graph Pass 列表；为空时仅运行内置 Bloom 与合成着色器。 |
| `options` | `Option[]` | `[]` | 模组设置页中的实时滑杆定义。 |

#### `targets[]` 渲染目标字段

| 字段 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `name` | `string` | 必填 | 目标名称；不可与保留源名重复，不可用 `prev:` 开头，同包内大小写不敏感唯一。 |
| `format` | `string` | `"R16G16B16A16SFloat"` | 可选 `"R16G16B16A16SFloat"`、`"R8G8B8A8UNorm"`、`"R16SFloat"`。 |
| `scale` | `float` | `1.0` | 相对场景目标宽高比例，合法范围 `(0.05, 1.0]`；像素尺寸为 `max(1, round(sceneDim * scale))`。 |
| `filter` | `string` | `"Linear"` | 采样滤波：`"Linear"`（双线性）或 `"Point"`（最近邻）；寻址固定为 `ClampToEdge`。 |
| `persistent` | `bool` | `false` | 为 `true` 时分配长度为 `max(2, FramesInFlight)` 的跨帧环形缓冲（普通目标为 `FramesInFlight`），首帧与重建后自动清零为 `(0, 0, 0, 0)`，允许通过 `prev:<name>` 采样上一渲染帧结果。 |

#### `passes[]` 渲染通道字段

| 字段 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `name` | `string` | 必填 | Pass 名称（非空）。 |
| `shader` | `string?` | `null` | 片段着色器相对路径。所有离屏 Pass 必填；仅末尾 `backbuffer` Pass 可省略（此时使用 `compositeShader` 或 `RimLumenComposite.frag.glsl`）。 |
| `output` | `string` | 必填 | 输出目标：已声明的 `targets[].name` 或 `"backbuffer"`。 |
| `blend` | `string` | `"Opaque"` | 混合模式：`"Opaque"`、`"PremultipliedAlpha"`（`One / OneMinusSrcAlpha`）或 `"Additive"`（RGB `One / One`，A `Zero / One`）。 |
| `extraParams` | `float[]` | `[0, 0]` | 最多 2 个浮点数，分别传入 `pc.value.z` 与 `pc.value.w`（超过 2 个会报错）。注意：末尾 Pass 省略 `shader` 时不传递 `extraParams`。 |
| `inputs` | `Input[]` | `[]` | 输入绑定 `{ "binding": 0..15 (不含 5), "source": "<name>" }`。 |

#### `options[]` 实时选项字段与滑杆机制

| 字段 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `key` | `string` | 必填 | 选项键（同包内唯一），持久化保存键为 `<packId>:<key>`。 |
| `label` | `string` | `key` | 设置界面的显示名称。 |
| `target` | `string` | 必填 | 绑定的 UBO 槽位（同包内唯一）：`bloomIntensity`、`bloomThreshold`、`bloomKnee`、`bloomScatter`、`packParam0`..`packParam15`。 |
| `default` | `float` | `0.0` | 默认值（加载时自动截断到 `[min, max]`）。 |
| `min` | `float` | `0.0` | 最小值（有限数且 `min <= max`）。 |
| `max` | `float` | `1.0` | 最大值（有限数）。 |
| `step` | `float` | `0.0` | 步进（`>= 0`）；`> 0` 时按 `min + round((val - min) / step) * step` 对齐。 |
| `tooltip` | `string?` | `null` | 悬停提示文本。 |

切换光影包时，`ShaderPackCatalog.ApplyOptionsToRuntime` 先将 4 个 Bloom 参数恢复为基线值（`bloomIntensity = 0.16`、`bloomThreshold = 1.0`、`bloomKnee = 0.5`、`bloomScatter = 0.65`），将 `packParam0..15` 全部清零，再写入本包选项并通过 `ShaderPackRuntimeParams.SetAll` 在锁内一次原子提交。玩家在设置页拖动滑杆时只更新内存中的 UBO 数值，**不会重建 Vulkan 管线或离屏纹理**。

---

### 3. Render Graph 校验规则与运行时隔离

#### 静态 DAG 校验规则（`ShaderPackGraphValidator`）

1. **保留源名**：`hdr`、`bloom`、`scene`、`depth`、`mapLightField`、`backbuffer` 为保留源名，不可作为自定义 `targets[].name`，也不能用 `prev:` 作为自定义目标前缀。
2. **末尾唯一 `backbuffer`**：若 `passes` 非空，仅最后一个 Pass 必须输出到 `"backbuffer"`；其余所有离屏 Pass 必须输出到已声明的 `targets[].name`，且必须使用 `"blend": "Opaque"` 与非空 `shader`。
3. **禁止采样 `backbuffer`**：任何 Pass 的 `inputs[].source` 均不能填 `"backbuffer"`。
4. **无前向引用与无同帧自读**：采样普通 `target` 时，必须有排在当前 Pass 之前的 Pass 已向其写入；禁止在同一 Pass 中既写 `X` 又直接以 `X` 作为输入。
5. **历史目标要求**：仅声明了 `"persistent": true` 的目标 `X` 才允许通过 `"prev:X"` 采样上一帧写入的槽位。
6. **绑定槽位约束**：`inputs[].binding` 必须位于 `[0, 15]`，且不得占用保留 UBO 槽位 `5`，同一 Pass 内不可重复。

#### 空帧 Bloom 跳过规则

即使 `runOnEmptyFrames` 为 `true`，当满足以下任一条件时，运行时仍会跳过内置 Bloom 降采样与升采样链，并将 `bloom` 输入源绑定为 1×1 全黑占位纹理 `black1x1`：

- `bloomMipLevels == 0`；
- 当前帧没有任何 HDR 绘制（`!hasHdrDraws`），**且**包未设置 `bloomPrefilterShader`、`bloomDownShader`、`bloomUpShader` 中的任意一个路径。

需要基于 `scene` / `depth` / `mapLightField` 在空帧计算自定义 Bloom 散射时，请至少指定一个自定义 Bloom 着色器（如 `bloomPrefilterShader`）并启用 `runOnEmptyFrames: true`。

#### 两级错误隔离

- **扫描与校验期隔离**：`pack.json` 语法错、路径越界、使用保留 ID `"default"` 或图校验不通过时，错误仅保存在该条目的 `LoadError` 中（菜单显示 `[损坏]` 且不可选），不会波及同目录或其它模组的光影包；`FindOrDefault` 会自动回退到内置 `default` 包。
- **编译与录制期隔离**：调用热重载或切包时，`RimLumenRenderHost.ReloadShaderPack` 会先用 Slang 预编译所有自定义 `.frag.glsl`；编译失败则保留当前生效包并在 `LastShaderPackError` 记录详情。若非 `default` 包在渲染线程录制阶段抛异常，运行时立即记录 `LastShaderPackError` 并在主线程自动切回 `default` 包，**不会触发全局管线熔断（`FailSafeTripped`）**。

---

### 4. GLSL `#version 450` 着色器契约

#### 内置采样源与占位纹理

| 源名称 | 格式与滤波 | 未激活时的占位纹理 | 说明 |
| --- | --- | --- | --- |
| `hdr` | `R16G16B16A16SFloat` / `Linear` | — | 线性 HDR 粒子与发光绘制层（A 为遮罩/不透明度）。 |
| `bloom` | `R16G16B16A16SFloat` / `Linear` | `black1x1`（`R8G8B8A8UNorm` 全 0） | 内置多级 Bloom 金字塔输出。 |
| `scene` | 场景主色彩格式 / `Linear` | — | Waylith 渲染的地图、角色与建筑主场景画面。 |
| `depth` | `R32SFloat` / `Point` | `depth1x1`（`R32SFloat`，值为 `1.0`） | 由场景深度拷贝得到的线性可读颜色快照。 |
| `mapLightField` | `R8G8B8A8UNorm` / `Linear` | `black1x1`（全 0） | 尺寸为 `mapSizeX x mapSizeZ` 的全图屋顶/光照/封闭度/迷雾场。 |

#### 默认输入绑定（未使用 `passes` 自定义绑定时）

- **默认合成阶段**（`passes` 为空，或末尾 Pass 省略 `shader` 且 `inputs` 为空；`passes` 为空时固定使用 `PremultipliedAlpha` 混合）：
  - `binding = 0`：`hdr`
  - `binding = 1`：`bloom`
  - `binding = 2`：`scene`
  - `binding = 3`：`depth`
  - `binding = 4`：`mapLightField`
- **自定义 `bloomPrefilterShader`**：
  - `binding = 0`：`hdr`、`binding = 2`：`scene`、`binding = 3`：`depth`、`binding = 4`：`mapLightField`（注意：**没有 `binding = 1`**，请勿声明）。
- **自定义 `bloomDownShader`**：
  - `binding = 0`：上一级降采样纹理、`binding = 4`：`mapLightField`。
- **自定义 `bloomUpShader`**：
  - `binding = 0`：更低分辨率升采样输入、`binding = 1`：同级降采样纹理、`binding = 4`：`mapLightField`。

> 混合与 Alpha 提示：`passes` 为空时默认合成 Pass 固定使用 `PremultipliedAlpha` 混合到 Backbuffer。若你的合成着色器直接输出最终全屏图像（如含调色、雾效），请输出 `vec4(finalRgb, 1.0)` 或使用显式 `passes` 并设 `"blend": "Opaque"`；若只想把 HDR 粒子与 Bloom 叠加到场景之上，则输出 `vec4(mappedHdr, hdr.a)`。

#### 固定顶点接口与 `#include`

全屏三角形由引擎提供，片段着色器只需声明：

```glsl
#version 450
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
```

同一光影包内可使用相对路径 `#include "Common.glslinc"` 复用公共函数与 UBO 声明。

#### Uniform Buffer（`set = 0, binding = 5`，176 字节 `std140`）

```glsl
layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection; // 偏移   0 (64 B): ViewProjection 逆矩阵（Row-Major 存储）
    vec4 ScreenAndMapSize;  // 偏移  64 (16 B): (1.0/sceneW, 1.0/sceneH, mapSizeX, mapSizeZ)
    vec4 EnvParams;         // 偏移  80 (16 B): (elapsedTime, skyGlow, zoomRootSize, flags)
    vec4 BloomParams;       // 偏移  96 (16 B): (bloomIntensity, bloomThreshold, bloomKnee, bloomScatter)
    vec4 PackParam0;        // 偏移 112 (16 B): (packParam0,  packParam1,  packParam2,  packParam3)
    vec4 PackParam1;        // 偏移 128 (16 B): (packParam4,  packParam5,  packParam6,  packParam7)
    vec4 PackParam2;        // 偏移 144 (16 B): (packParam8,  packParam9,  packParam10, packParam11)
    vec4 PackParam3;        // 偏移 160 (16 B): (packParam12, packParam13, packParam14, packParam15)
} ubo;
```

- **参数映射注意**：`options[].target` 指定的 `packParam0..3` 依次填入 `ubo.PackParam0.xyzw`，`packParam4..7` 填入 `ubo.PackParam1.xyzw`，以此类推。因此 `target` 为 `"packParam1"` 时在 GLSL 中应读取 `ubo.PackParam0.y`，而非 `ubo.PackParam1.x`。
- **`EnvParams.w` 状态位掩码**：着色器中使用 `uint flags = uint(ubo.EnvParams.w + 0.5);` 解包：
  - `1u`（bit 0）：`hasHdrDraws`（存在活动 HDR 绘制或直接 HDR 贡献者）
  - `2u`（bit 1）：`hasSceneDepth`（`depth` 绑定真实场景深度而非 `depth1x1`）
  - `4u`（bit 2）：`hasMapLightField`（`mapLightField` 已就绪且绑定真实地图光场）
  - `8u`（bit 3）：`isPaused`（游戏是否处于暂停状态）

#### Push Constant（16 字节 `vec4`）

```glsl
layout(push_constant) uniform Params { vec4 value; } pc;
```

| 所在 Pass | `pc.value.x` | `pc.value.y` | `pc.value.z` | `pc.value.w` |
| --- | --- | --- | --- | --- |
| 自定义 Pass（含带 `shader` 的末尾 Pass） | `1.0 / outputWidth` | `1.0 / outputHeight` | `extraParams[0]` | `extraParams[1]` |
| 默认合成 Pass（`passes` 为空或末尾无 `shader`） | `bloomIntensity` | `0.0` | `1.0 / sceneWidth` | `1.0 / sceneHeight` |
| `bloomPrefilter` / `bloomDown` | `1.0 / inputWidth` | `1.0 / inputHeight` | `bloomThreshold` | `bloomKnee` |
| `bloomUp` | `1.0 / lowMipWidth` | `1.0 / lowMipHeight` | `bloomScatter` | `0.0` |

#### Reverse-Z 深度与地面坐标反投影

Waylith 采用 Reverse-Z 深度：近平面为 `1.0`，空背景/远平面为 `0.0`；深度未激活时的 `depth1x1` 占位纹理值为 `1.0`。区分真实 3D 几何体（例如 IsoRim 墙体）与平铺地面/天空背景可使用：

```glsl
float depth = texture(depthImage, uv).r;
float is3DGeom = step(0.0001, min(depth, 1.0 - depth));
```

使用 `ubo.InvViewProjection` 从屏幕 `uv` 重建世界地面（`y = 0`）坐标与地图光场 UV（同时兼容原版俯视与 IsoRim 倾斜视角）：

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

#### `mapLightField` 四通道编码规范

`mapLightField` 在地图加载与区域网格重建（`SectionLayer` 增量更新）时按需同步到 GPU，每像素对应一个地图格，归一化 `[0.0, 1.0]` 通道语义如下：

| 通道 | 含义 | 编码规则（`MapLightFieldEncoder`） |
| --- | --- | --- |
| `R` | 屋顶（`Roof`） | 有屋顶为 `1.0`（`255`），无屋顶为 `0.0`（`0`）。 |
| `G` | 地表光亮（`GroundGlow`） | 若 `glowGrid.VisualGlowAt` 的 `a == 1`（日光直射露天地格），直接编码为 `1.0`（`255`）；否则取 `min(0.5, (max(r, g, b) / 255.0) * 3.6)`（人工光源上限 `0.5`，借此区分日光与火把）。 |
| `B` | 封闭度（`Enclosure`） | 无屋顶为 `0.0`（`0`）；有屋顶且房间使用室外温度（半开放棚/门廊）为 `~0.502`（`128/255`）；完全封闭室内为 `1.0`（`255`）。 |
| `A` | 战争迷雾（`FogOfWar`） | 未探索迷雾格为 `1.0`（`255`），已探索格为 `0.0`（`0`）。 |

#### 内置光影包参考

| 包 ID | Pass / 目标数 | `runOnEmptyFrames` | `requiresSceneDepth` | `requiresMapLightField` | `bloomMipLevels` | 典型技术参考文件 |
| --- | --- | --- | --- | --- | --- | --- |
| `default` | 内置合成 / 0 | `false` | `false` | `false` | `5` | `1.7/Shaders/PostFx/RimLumenComposite.frag.glsl` |
| `atmospheric` | 3 Pass / 2 目标 | `true` | `true` | `true` | `7` | `1.7/ShaderPacks/Atmospheric/` |
| `cinematic` | 5 Pass / 4 目标（含持久化 `adapt`） | `false` | `false` | `false` | `6` | `1.7/ShaderPacks/Cinematic/` |
| `crisp` | 单自定义合成 / 0 | `false` | `false` | `false` | `3` | `1.7/ShaderPacks/Crisp/` |
| `vibrant` | 单自定义合成 / 0 | `false` | `false` | `false` | `6` | `1.7/ShaderPacks/Vibrant/` |

---

### 5. 最小可运行光影包示例

将以下两个文件放入 `Config/RimLumen/ShaderPacks/WarmNight/`（或模组的 `1.7/ShaderPacks/WarmNight/`），即可在游戏内设置页切换并热重载。

`pack.json`：

```json
{
  "id": "warm_night",
  "name": "Warm Night (示例夜暗)",
  "author": "YourName",
  "description": "保留室内与火把附近亮度，仅在夜晚压暗未照明野外，并叠加 HDR 与 Bloom。",
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
      "label": "野外夜暗强度 (Night darkness)",
      "target": "packParam0",
      "default": 0.45,
      "min": 0.0,
      "max": 0.85,
      "step": 0.05
    }
  ]
}
```

`Composite.frag.glsl`：

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
