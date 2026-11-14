# RimLumen 模组开发 API 手册

本文面向需要在《环世界》1.7（Waylith / Vulkan 引擎）中使用 RimLumen 制作特效的模组作者。所有接口、字段与限制均对应当前 `RimLumen.Core` 源码。

---

## 一、快速接入方式

按是否需要写 C# 代码，可以选以下三种方式之一：

1. **纯数据定义（不需要引用 DLL）**：在你的模组 `About/About.xml` 中把 `NekoYukari.RimLumen` 加入 `modDependencies` / `loadAfter`，然后写 XML `LumenEffectDef` 或 `.lumen.json` 特效文件。
2. **C# 调用播放（只读名称或动态造特效）**：在你的 C# 项目引用 `RimLumen.Core.dll`（位于本模组 `1.7/Assemblies/RimLumen.Core.dll`），通过 `RimLumen.Core.Api.RimLumenApi` 播放或注册特效。

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

### 地面接触过期（`EmitterSpec.KillBelowFloor`）

`KillBelowFloor`（默认 `false`）目前只能在 C# 的 `EmitterSpec` 上设置，`.lumen.json` 与 XML `LumenEffectDef` 暂无对应字段。为 `true` 时，粒子在世界高度低于播放原点高度 `baseY` **且**世界速度 `Y < 0`（正在下落）的那一帧过期并被回收；在地面以下但仍上升、或恰在地面上刚发射的粒子不受影响。L1（CPU）与 L2（GPU compute）行为一致，与空间平面无关，局部空间模拟的发射器按其世界矩阵换算后判定。

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
        "bloomWeight": 1
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
// caps.IsHdrActive / IsSceneDepthAvailable / IsGpuComputeAvailable / ValidationClean / MaxGpuParticlesPerFrame

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
