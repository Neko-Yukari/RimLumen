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
- `+Y`：从地面向上
- `+Z`：北（屏幕向上）

地面是固定的世界平面 `Y = 0`。播放原点 `LumenPlayRequest.OriginWorld.Y` 和所有世界位置的 Y 都是离地高度，单位为格，不是《环世界》的绘制层高度。节点的 `LocalPosition` 仍是相对父节点的偏移；根节点相对播放原点。

### 2. 节点空间平面（`LumenSpacePlane`）

| 枚举值 | 数值 | 旋转行为 | 常见用途 |
| --- | --- | --- | --- |
| `GroundXZ` | `0` | 原样使用节点自身的 `LocalRotation`，不加额外俯仰旋转；显示不做相机适配 | 贴地法阵、地面光环、水平扩散波、任意 3D 朝向子发射器（如 RimUnity 导入资产） |
| `Vertical3D` | `3` | 节点矩阵与 `GroundXZ` 相同，模拟保持真 3D。只有显示随相机变化：离地 `h` 格，在原版正俯视 2D 相机下向屏幕上方偏移 `0.5 × h` 格；在 3D 相机下按完整世界高度显示，IsoRim 的 Y 轴压缩会被抵消。 | 烟花、喷泉、上升弹体的拖尾 |

例如，播放原点在离地 `2` 格处时，原点本身在 2D 中比地面高 `1` 格。若 XZ 不变，原点再升 `4` 格，拖尾就向屏幕上方延伸 `2` 格。`GroundXZ` 不使用这套投影。空间平面取值 `1`、`2` 均无效。

原版 2D 使用俯角 60° 的正交投影：沿屏幕上方的地面分量乘以 `sin 60° ≈ 0.866`，高度分量乘以 `cos 60° = 0.5`，沿屏幕右方的分量不变。球投影为正圆，水平圆环投影为纵向压缩的椭圆。投影方向随相机旋转。`StretchedVelocity` 的速度和 `Local` 的轴使用同一线性映射；绘制层为 `MoteOverhead`。

地面锚点取播放原点的 XZ，同一特效的所有节点共用。本地空间粒子取当前原点的 XZ，世界空间粒子取出生时的 XZ，发射器移动不会带走已留下的拖尾。L2 以 `1/32` 格精度记录出生锚点，范围为 `±1024` 格。

### 地面接触过期（`EmitterSpec.KillBelowFloor` / `killBelowFloor`）

`killBelowFloor`（默认 `false`）在 C# `EmitterSpec.KillBelowFloor`、`.lumen.json` 发射器对象以及 XML `LumenEffectDef` 的 `<emitter>` 中均可设置。为 `true` 时，粒子在世界位置 `Y < 0` **且**世界速度 `vY < 0`（正在下落）的那一帧过期并被回收；在地面以下但仍上升、或恰在地面上刚发射的粒子不受影响。L1（CPU）与 L2（GPU compute）行为一致，与空间平面无关，局部空间模拟的发射器按其世界矩阵换算后判定。

> 提示：所有发射形状中，`Cone`（圆锥）的默认主喷射轴是局部 `+Y`。想让粒子沿真实高度向上喷出并落回，请给节点设 `Vertical3D`，把世界空间重力设为 `(0, -1, 0)`，需要落地即消失时再设 `KillBelowFloor`。

### 3. 游戏时间同步

默认情况下，粒子模拟与节点时间轴使用本帧实际完成的 tick 数 / 60 作为步长；没有完成 tick 或宿主因 `LongEventHandler.ShouldWaitForEvent` 跳过游戏更新时为 `0`，暂停仍照常绘制；真实时间照常推进。不再用墙钟步长乘速度档位。原版 `TickRateMultiplier`：Normal=1、Fast=3、Superfast=6（无事发生时 12、无地图时 18）；Ultrafast=15（无地图 150、UltraSpeedBoost 20000），强制普通速度时为 1。实际 tick 循环还受每帧耗时预算限制。来源：`Verse.TickManager`，`Assembly-CSharp.decompiled.cs:52605–52646,52853–52888`。需要实时时钟的特效，传入 `realTime: true`。

---

## 三、资产格式 1：`.lumen.json` 数据文件（推荐纯资产定义）

### 1. 文件位置与 ID 解析

RimLumen 在启动阶段自动扫描所有启用模组的以下两个目录（递归匹配 `*.json`）：

- `<ModRoot>/1.7/Data/RimLumen/`
- `<ModRoot>/Data/RimLumen/`

假设你的模组 `packageId` 为 `Author.MyMod`，文件位于 `1.7/Data/RimLumen/Spells/Fireball.lumen.json`，注册后会生成两组可用 ID：

1. **全限定 ID**：`Author.MyMod:Spells/Fireball`（始终指向你自己的资产，不会撞名）。如果 JSON 内声明了不同的 `effectId`，也会同时注册 `Author.MyMod:<effectId>`。
2. **裸短名**：`Spells/Fireball`（以及 JSON 内的 `effectId`）。裸短名采用**先到先得**规则，后加载的模组不会静默覆盖先加载模组的同名短名。

开发调试菜单中的「Reload effect assets」会按当前启用模组的顺序重建一次资产快照：先扫描 `1.7/Data/RimLumen/`，再扫描 `Data/RimLumen/`，最后加入已加载的 XML `LumenEffectDef`。本次扫描得到的 ID 会以新解析的不可变实例替换同名资产，包括代码注册的资产。文件被删除或解析失败时，仅释放仍由扫描拥有的旧 ID；裸短名按先到先得交给下一个仍存在的候选，日志会记录旧的来源与新的来源。无关的代码注册保持不变，旧扫描键后来被代码覆盖的也不会被删除；已播放实例继续使用其旧资产。该操作不会重读 XML 文件，修改 XML 后仍需重启游戏。

使用 `RimUnityAssetProvider` 的调用方可再次调用 `RegisterPackageRoot`，即使路径未变也会清空延迟加载的目录和已翻译资产缓存。之后解析时会重新读取目录和资产；已取得的旧资产实例保持不变。

### 2. JSON 解析规则

- 键名采用 camelCase，解析时**大小写不敏感**，允许 `//` 注释和尾逗号。
- **未知键名会直接报错**，用于尽早暴露拼写错误。
- 向量支持 `[x, y, z]`、`{ "x": ..., "y": ..., "z": ... }` 或单个数字（等比 `x = y = z`）。
- 粒子颜色按原版场景存储编码编写：RGB 必须有限且非负，上限不限；alpha 必须有限且在 `[0,1]` 内。RGB 的 `1` 为原版满亮度，`>1` 可用于 HDR。`hdrIntensity` 和运行时 `intensity` 必须有限且非负，只缩放 RGB；无效的运行时 `intensity` 在修改前抛出 `ArgumentOutOfRangeException`。点光颜色 RGB 仍须在 `[0,1]` 内。JSON 颜色支持 `[r, g, b, a]`、`[r, g, b]`（alpha 为 1）或 `{ "r": ..., "g": ..., "b": ..., "a": ... }`。
- 已移除或改名的旧键会被拒绝，错误信息指出新键或移除状态：`startColorLinear` 改名为 `startColor`；渐变停靠点 `rgbaLinear`、点光 `colorLinear` / `colorRgb` 改名为 `color`；`bloomWeight` 与 `hdrMultiplier` 已移除。不提供兼容别名。
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
  "states": [],
  "initialState": null,
  "stopState": null
}
```

#### 字段补充说明

- **根节点**：`nodes` 必须非空；`defaultDuration` 必须有限且 `>= 0`。
- **节点层级（`EffectNodeSpec`）**：`parentIndex` 必须为 `-1`（挂在根节点下）或严格小于当前节点索引 `[0, i)`，以此从结构上禁止循环父子引用。若不写 `localRotation`（JSON 支持四元数或 `[x,y,z,w]`），可用 `localYawDegrees` 绕世界 `+Y` 旋转。
- **标量曲线（`sizeOverLifetime` / `intensityOverTime`）**：可写为单数值（常量）、数组 `[[time, value], ...]`，或对象 `{ "keys": [...], "multiplier": 1 }`。关键帧也可写为 `[time, value, inTangent, outTangent]`。时间必须有限且非递减；粒子曲线的时间是归一化寿命 `[0,1]`，点光曲线的时间是从节点激活起算的秒数，且不小于 0。

  值和切线必须有限；切线是每单位曲线时间的变化率。不同时间之间采用三次 Hermite 插值，结果再乘以 `multiplier`。

  时间可以重复两次或更多次。若所有关键帧时间相同，任意时刻均取最后一个值。否则，`t <= first` 取首值，`t >= last` 取末值；在内部重复时间处，左极限是该组第一个值，恰好到达时取该组最后一个值，此后从该值向下一关键帧插值。首个时间恰好重复时仍取首值，因此不能将整个规则称为右连续。

  例如 `[[0, 0], [0.5, 1], [0.5, 2], [0.5, 3], [1, 4]]`：接近 `0.5` 的左极限为 `1`，在 `0.5` 时为 `3`，随后从 `3` 向 `4` 插值。两个重复帧同样适用该规则。来源：[RimUnity `AnimationCurve.cs:23,30-35`，固定版本 `45ca90987f7d3721715b45c021cab986ee14c468`](https://github.com/chezhou9413/RimUnity/blob/45ca90987f7d3721715b45c021cab986ee14c468/Source/RimUnity/Animation/AnimationCurve.cs#L23-L35)。

  粒子尺寸曲线由 L1 和 L2 使用相同的 64 点查找表（LUT）：采样点为 `i/63`（`i = 0…63`），包含两端，再对相邻样本插值。阶跃可能在宽度为 `1/63` 的采样区间内被平滑，不能保证精确保留。点光曲线按秒直接求值，不使用该粒子查找表。
- **材质颜色**：`materialTint` 默认为 `(1,1,1,1)`，与 `startColor` 和渐变遵循上述颜色范围；`colorSpace` 为 `Stored`（默认）或 `Linear`。令 `vertex = startColor × gradient`。Stored 的 RGB 为 `vertex × tint × texel`；Linear 的 RGB 为 `E(vertex × D(tint) × (贴图为 sRGB ? D(texel) : texel))`。`vertex` 不解码。标量增益 `g = hdrIntensity × intensity` 在材质颜色转入本帧混合空间之后才乘到 RGB 上（Legacy 为 `g × rgb`，Linear 混合空间为 `g × D(rgb)`），不经过任何 sRGB 传递函数放大或压缩；`D` / `E` 分别为 sRGB 解码 / 编码，在非负值上延伸至大于 1，不截断。alpha 始终为三个原始 alpha 的乘积，再应用软粒子覆盖度和混合模式要求的预乘。逐分量计算中，`D(x) = x/12.92`（`x <= 0.04045`），否则为 `((x+0.055)/1.055)^2.4`；`E(x) = 12.92*x`（`x <= 0.0031308`），否则为 `1.055*x^(1/2.4)-0.055`。依据 RimUnity 固定版本 `45ca90987f7d3721715b45c021cab986ee14c468` 的 `Source/Shared/ShaderColorSpace.cs:69-103,122-134`。此处只描述材质计算，不改变玩家混合空间。
- **渐变（`colorOverLifetime`）**：支持颜色数组（单色常量）或停靠点数组 `[[time, [r,g,b,a]], ...]` 或对象 `{ "stops": [{ "time": 0, "color": [r,g,b,a] }] }`，要求 `time` 严格递增。
- **`pointLight`**：JSON 下颜色键为 `color`（RGBA），半径键为 `glowRadius`（单位：地图格）。点光是纯视觉光池，不写入原版 `GlowGrid`，不影响心情、种植和工作速度。圆心取节点世界位置在地面上的投影，半径 `R = glowRadius`，衰减公式为 `w = (1 - (d/R)^2)^2`，在 `d >= R` 处精确为 `0`。各光源项 `L = color × intensityOverTime × w` 在重叠处线性相加，总和只作用一次：场景变为 `scene × (1 + C(ΣL))`，其中 `C` 在峰值不超过 1 时为恒等，超过后峰值保持 1 并沿色相趋白，因此重叠处最多提亮 2 倍。绘制在原版光照层之后，迷雾、黑暗和屋顶照常覆盖。推荐 `intensity`：常亮灯具 `0.5–1.2`，短促强闪光 `1.0–2.0`；不要把原版 `GlowGrid` 的 `3.6` 尺度照搬到视觉光池上。光池每帧跟随节点。`intensity` 和曲线关键帧值须有限且 `>= 0`，无上限；有效强度 `<= 0` 时不绘制。`overlightRadius` 已移除，JSON 和 XML 解析均直接拒绝。需要游戏逻辑光时，见第七章第 3 节的 `CompRimLumenLogicalLight`。
- **力场 `strength`**：JSON、XML 与 C# 均须显式提供；省略时拒绝资产，不使用默认值。
- **`timelineEvents` / `states` / `initialState` / `stopState`**：见下一节「状态机」。
- **粒子密度设置**：公开 setter `RimLumenRenderHost.ParticleDensityScale` 接受范围为 `0.1`～`1.0`；范围外的有限值静默截到边界，不记录日志，NaN 和正负无穷抛出 `ArgumentOutOfRangeException`。设置滑条连续取值，显示时四舍五入为整数百分比。密度 `1` 精确；截断后的密度为 `d` 时，脉冲有效密度为 `floor(d * 65536) / 65536`。
- **粒子密度与脉冲**：每个发射器保存独立的 `Int64` 累积量 `a`，初值为 `0`，单位为 `1/65536` 粒子。先计算 `q = floor(density * 65536)`；每次非零脉冲依次计算 `a += count * q`、`n = ceil(a / 65536)`（用整数运算）和 `a -= n * 65536`。`n` 是本次请求预算，`a <= 0` 且可以为负。有效密度为 `floor(density * 65536) / 65536`，永不高于设定值；密度为 `1` 时精确，不使用 `0.999` 阈值。

  密度为 `0.25` 时，连续四次 `count = 1` 的预算为 `1, 0, 0, 0`；连续八次总计为 `2`。连续两次 `count = 7` 的预算为 `2, 2`。密度为 `0.1` 时，`q = 6553`，十次 `count = 1` 累计 `65530`，总请求为 `1`；密度为 `0.2` 时，五次 `count = 1` 的总请求为 `1`。累计请求预算按有效密度计算，为 `ceil(sum(count * q) / 65536)`。预算不是实际存活粒子数或 GPU 粒子计数：容量或空闲槽位不足时，丢弃的请求不会补发。`rateOverTime` 须有限且非负，没有额外速率上限。容量准入前使用宽数值累计有限的连续发射预算 `rate × dt` 和全部跨过的、通过概率判定并按密度缩放的脉冲；超过 Int32 范围属于普通容量丢弃，不因预算范围抛异常。即使没有空闲槽位，仍对全部计划发生次数抽取概率，并对通过的次数更新密度余量。

  脉冲在 CPU 侧入队时只计算一次，GPU Capture 不再缩放。`Play`、显式 `Restart` 和 `GoTo` 状态重新进入会将 `a` 重置为 `0`；循环回绕不会重置。例如，密度为 `0.25` 时，每个循环一次 `count = 1`，八个循环的总请求为 `2`。连续发射率使用独立的 `double` 小数余量，向下取整并保留余数；运行时 `rate` 倍率不缩放脉冲。

### 4. 状态机（`states`）

一个特效由若干命名状态组成，同一时刻只播放一个状态。状态可以按时长自动推进，也可以由 C# 切换，并把关键时刻回调给 C#。

**不写 `states` 时**，整个特效是一个隐式状态：包含全部节点，时长 `defaultDuration`。只要有一个发射器 `looping: true`，这个状态就一直循环到被 `Stop`；否则到时停止新发射，粒子与点光清空后句柄自动死亡并触发 `Finished`。隐式状态没有名字，不触发 `StateChanged`。

**`states[]` 字段**：

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `name` | 必填 | 状态名，在本资产内唯一 |
| `nodes` | `[]` | 本状态发射的节点名列表；同名节点都属于本状态。一个节点可属于多个状态，也可不属于任何状态 |
| `duration` | `defaultDuration` | 状态时长（秒，`>= 0`） |
| `loop` | `false` | `true` 时状态永不自行结束，`duration` 是它的时间轴事件周期 |
| `next` | `null` | 非循环状态到时进入的状态；为 `null` 时特效到此结束。不能与 `loop` 同时写 |

**根字段**：`initialState` 是播放时进入的状态，默认 `states[0]`；`LumenPlayRequest.InitialState` 可在单次播放时覆盖它。`stopState` 是 `Stop(true)` 进入的收尾状态，它必须能自行结束（不能 `loop`，也不能写 `next`）；不写时 `Stop(true)` 立即开始淡出。

**进入状态**（包括初始状态、`next`、`GoTo`，以及 `GoTo` 当前状态的重入）时：

1. 状态时钟归零，并触发一次 `StateChanged(name)`。
2. 属于该状态的节点从头开始自己的发射计划：`bursts` 的时刻、发射器 `duration` 与 `rateOverTime` 都从进入时刻起算。L1（CPU）与 L2（GPU）行为一致。
3. 不属于该状态的节点停止新发射，已发出的粒子跑完寿命，点光在 `0.25s` 内淡出。

发射暂停不等于游戏暂停：发射器首次激活后，即使停止新发射，循环和非循环发射器的发射计划时钟仍随时间推进；期间跨过的计划脉冲直接跳过，不补发，已有粒子继续模拟。从未激活的节点仍停在发射时间 0。显式 Restart 重置时钟、密度余量和概率随机状态，保留已接受的待发脉冲。游戏全局暂停仍使用 `dt=0`；`SetParam("rate", 0)` 只暂停连续发射，不影响已编写的脉冲。

状态在帧边界切换：切换发生的那一帧，发射器按新状态模拟整帧。一帧推进跨过多个状态时，会依次走完每个状态与其中的事件。

**`timelineEvents[]` 与状态**：

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `time` | 必填 | 相对所属状态进入时刻的秒数 |
| `eventName` | 必填 | 事件名，`Trigger` 与 `Notify` 用它 |
| `targetNodeIndex` | `-1` | 非 `-1` 时向该节点补发一次脉冲：`bursts[0].count`，没有 `bursts` 时取整的 `rateOverTime` |
| `state` | `initialState` | 所属状态名；只在该状态播放时按时间触发 |
| `notify` | `false` | `true` 时触发后回调 `ILumenEffectHandle.Notify(eventName)` |

循环状态每过一个 `duration` 周期重新触发一遍它的事件，`time >= duration` 的事件不会按时间触发；`duration` 为 `0` 的循环状态只触发一遍。非循环状态里 `time` 恰好等于 `duration` 的事件仍会触发。

**加载时校验**（`.lumen.json` 与 XML 在解析时报错，C# `EffectAsset` 在 `Play` 时抛 `InvalidDataException`）：状态名为空或重复；`nodes` 中的名字没有对应节点；时长为负或非有限值；`loop` 与 `next` 同时出现；`next`、`initialState`、`stopState` 或事件的 `state` 指向不存在的状态；`stopState` 是循环状态或带 `next`；零时长状态经 `next` 成环；没有 `states` 却写了 `initialState`、`stopState` 或事件 `state`。

#### 示例：龙卷风的生成、持续与消散

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
    if (name == "touchdown") StartDamagingCells();   // 生成 1.0s 时漏斗触地
    if (name == "impact") ScatterDebris();           // 消散 0.3s 时碎片落地
};
tornado.Finished += StopDamagingCells;
// ……风暴结束时：
tornado.Stop();                                      // 进入 collapse，播完 1.2s 后淡出
```

逐步状态变化：

| 时刻 | 状态 | 发生了什么 |
| --- | --- | --- |
| `0` | `spawn` | `StateChanged("spawn")`；`dust` 喷出 300 粒，`funnel` 开始发射，`debris` 不发射 |
| `1.0` | `spawn` | `touchdown` 触发 `Notify("touchdown")` |
| `1.5` | `sustain` | 到时进入 `next`：`StateChanged("sustain")`；`funnel`、`debris` 从头开始发射计划，`dust` 停止新发射 |
| `2.0`、`4.0`…… | `sustain` | 每个 2s 周期的第 0.5s 触发 `gust`，向 `debris` 补发 40 粒 |
| 调用 `Stop()` | `collapse` | `StateChanged("collapse")`；`collapse` 喷出 400 粒，漏斗与碎片停止新发射；此后指向已声明状态的 `GoTo` 被忽略；未声明状态仍报错 |
| `Stop` 后 `0.3s` | `collapse` | `Notify("impact")` |
| `Stop` 后 `1.2s` | 淡出 | 停止新发射，点光淡出，粒子跑完寿命 |
| 最后一粒消失 | 已结束 | `IsAlive == false`，触发 `Finished` |

---

## 四、资产格式 2：XML `LumenEffectDef`

可在任意模组 `Defs/**/*.xml` 中定义 `RimLumen.Core.Assets.LumenEffectDef`，除以下四处字段命名跟随环世界 XML 习惯外，其余默认行为与 `.lumen.json` 一致：

1. **ID**：使用 `<defName>`，同样会同时注册裸短名与 `<packageId>:<defName>`。
2. **节点字段**：额外提供 `<heightOffset>`（会累加到 `localPosition.y`），`localScale` 为单个浮点标量，不提供 `localRotation` 四元数（用 `<localYawDegrees>`）。
3. **点光键名**：使用 `<radiusCells>`（对应 JSON 的 `glowRadius`）、`<color>`（原始浮点语法 `(r, g, b)` 或 `(r, g, b, a)`）、`<intensity>` 与 `<intensityOverTime>`（`<keys>` 由 `<li><time>..</time><value>..</value></li>` 组成）。
4. **列表字段**：`<states>`、`<timelineEvents>` 与状态的 `<nodes>` 用 `<li>` 列表；状态字段为 `<name>`、`<nodes>`、`<duration>`、`<loop>`、`<next>`，事件字段为 `<time>`、`<eventName>`、`<targetNodeIndex>`、`<state>`、`<notify>`；根上另有 `<defaultDuration>`、`<initialState>`、`<stopState>`。含义与默认值同上一节「状态机」。

XML 发射器使用 `<colorSpace>` 和 `<materialTint>`，默认分别为 `Stored` 和 `(1,1,1,1)`。XML 的 `<startColor>`、渐变停靠点 `<color>`、`<materialTint>` 和点光 `<color>` 使用带括号的原始浮点分量：三分量时 alpha 为 1，四分量时保留给定值，不按字节颜色解释。格式错误或分量数错误会报告资产和字段。

旧键同样被拒绝：`<startColorLinear>` 改名为 `<startColor>`；渐变停靠点 `<rgbaLinear>`、点光 `<colorLinear>` / `<colorRgb>` 改名为 `<color>`；`<bloomWeight>` 与 `<hdrMultiplier>` 已移除。

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

## 五、枚举与物理力场参考

### 1. 粒子系统枚举（`RimLumen.Core.Contracts`）

- **`LumenParticleTier`**：`External`（外部兼容播放器）/ `L1_NativeCpu`（原生 CPU 模拟，适合少粒子或需频繁代码交互）/ `L2_GpuCompute`（Vulkan Compute 模拟，默认，适合大数量与力场粒子）。
- **`LumenShapeKind`**：`Point`（0）、`Sphere`（1）、`CircleXZ`（2）、`Cone`（3）、`Box`（4）、`SphereShell`（5）、`Hemisphere`（6）、`HemisphereShell`（7）、`CircleEdgeXZ`（8）、`ConeShell`（9）、`ConeVolume`（10）、`ConeVolumeShell`（11）、`SingleSidedEdge`（12）、`BoxShell`（13）、`BoxEdge`（14）、`Donut`（15）、`Rectangle`（16）。
- **`LumenBlendMode`**：`Additive`（0，推荐发光与火焰）/ `AlphaBlend`（1，烟雾与水雾）/ `Premultiplied`（2）。
- **`LumenBillboardMode`**：`Camera`（0，面向相机）/ `Horizontal`（1，水平贴地面片）/ `StretchedVelocity`（2，按速度方向拉伸，配合 `VelocityStretchFactor` 与 `LengthScale`）/ `Mesh`（3，延期）/ `Vertical`（4）/ `Local`（5）。

### 形状与概率脉冲

`ShapeExtents` 默认 `(1,1,1)`，单位为格：球 / 半球使用 XYZ 半径，圆 / 圆锥使用 XZ 底面半径，盒体使用 XYZ 半尺寸，`SingleSidedEdge` 使用 X 半长，`Donut` 使用 XZ 主环半径，`Rectangle` 使用 XZ 半尺寸。形状轴为 +Y，半球在 +Y；边沿 X 分布并向 -Z 发射。静态部分圆弧的径向向量为 `(cos θ, 0, -sin θ)`。

| 发射器字段（JSON / XML 使用首字母小写） | 默认 | 有效范围及含义 |
| --- | --- | --- |
| `ConeLength` | 5 | 有限非负格，仅圆锥体积使用 |
| `DonutRadius` | 0.2 | 有限非负格，圆环管半径 |
| `RadiusThickness` | 1 | 有限 `[0,1]`，径向厚度；显式 Shell / Edge 保持表面 / 边缘 |
| `BoxThickness` | `(0,0,0)` | 各分量有限 `[0,1]`，盒壳 / 棱边向内厚度 |
| `ArcDegrees` | 360 | 有限 `[0,360]` 度 |
| `ArcSpread` | 0 | 有限 `[0,1]`，非零时相位量化为 `floor(u/step)*step` |
| `RadiusSpread` | 0 | 有限 `[0,1]`，同样量化，仅用于单边 X 位置 |
| `RandomDirectionAmount`、`SphericalDirectionAmount` | 0 | 各有限 `[0,1]`，分别向独立球面方向及形状中心向外方向混合 |
| `RandomPositionAmount` | 0 | 有限非负格；添加独立均匀体积球形偏移 |

圆锥以 `r=sqrt(lerp((1-RadiusThickness)^2,1,u))` 采样底面，壳用 `r=1`；`d=normalize(radial*sin(angle)*r + +Y*cos(angle))`，位置为 `radial*r*底面半径`。体积形状再加 `d*u*ConeLength/max(0.001,d.y)`。归一化输入长度 `<=1e-5` 时返回零方向，因此可产生静止粒子。盒壳按 `abs(ShapeExtents*ShapeScale)` 的面面积加权，盒棱按长度加权；总权重为零时选择 X 轴。

形状采样后先应用形状局部 TRS，再依次混合随机方向、球向方向、随机位置；方向不受形状缩放。导入的定向形状只在形状局部使用 `R*Rx(+90°)` 和缩放 `(sx,sz,sy)`，不旋转 prefab、世界或力场。L1 / L2 使用相同的形状公式和分布，不承诺同种子的出生粒子逐一相同。上述范围外的原生输入被拒绝，不截断；导入值走 External 并给出字段原因。

`Bursts` 为 `ParticleBurstSpec(Time, Count, Probability=1)[]`：时间为有限秒数且在 `[0, Duration]` 内（包含两端），数量为非负整数，概率有限 `[0,1]`；JSON 的 `probability` 和 XML 的 `<probability>` 可省略，例如 `<li><time>0.5</time><count>100</count><probability>0.5</probability></li>`。C#、JSON 和 XML 都在资产入口校验时间；超过 Duration 时错误指出具体字段，不在 Play 热路径重复校验。共享 CPU 调度器对每个计划发生的脉冲先抽一次概率，接受后才计算密度；拒绝不消耗密度余量，显式 `EmitBurst` 不受概率控制。同一节点种子的 L1 / L2 接受结果一致，服务为每次 Play 和节点分配种子，不提供公开种子，也不承诺跨 Play 重现。Restart 重置计划、余量和概率随机状态，但保留已接受待发脉冲；循环回绕不重置。连续发射及共享定点密度余量规则不变，见上文「粒子密度与脉冲」。

`Vertical` 在 `GroundXZ` 使用 `normalize(cross(worldUp, rawCameraForward))`；正顶视时采用定义好的相机右轴极限、上轴取 +Y，有限但边缘朝向相机而不可见。2D 中站立面片应使用 `Vertical3D`；它直接使用已补偿的相机右 / 上轴，不再做逆 Y 或归一化。`Local` 使用完整世界四元数轴做真实几何投影，`Vertical3D` 显示映射只应用一次；边缘视角允许塌缩，不转向相机。空间平面只有 `GroundXZ=0` 和 `Vertical3D=3`。

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

// 3. 完整请求（朝向目标点 / 每帧动态跟随 / 指定初始状态）
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
    InitialState = "charge", // 覆盖资产的 initialState；资产未声明该状态时 Play 抛 ArgumentException
    RealTime = false,
});
```

- 如果 `EffectId` 不存在，`Play` 返回 `IsAlive == false` 的空句柄，句柄方法无效果；每个服务实例对同一 ID 只记录一次 `Log.Warning`。资产存在但被校验拒绝时也返回空句柄，沿用注册表的校验错误，不追加“未知 ID”警告。

### 3. 句柄控制（`ILumenEffectHandle`）

| 成员 | 作用与注意事项 |
| --- | --- |
| `bool IsAlive` | 播放中或淡出排空期间为 `true`，全部粒子与点光清理完毕后变为 `false`。 |
| `void Trigger(string name)` | 立即触发所有同名时间轴事件（不论所属状态与时间：目标节点脉冲、`Notify`），并让同名节点从头开始发射计划，直到下一次进入状态时重新决定；不改变当前状态。名字无匹配时无效果；淡出后与死亡后无效果。L1 与 L2 行为一致。 |
| `void GoTo(string stateName)` | 立即进入该状态（重入当前状态即重启它）。对绑定资产的句柄，未声明的状态名始终抛 `ArgumentException`，即使已调用 `Stop(true)`、正在排空或已经死亡；声明过的状态只在播放中且未调用 `Stop(true)` 时进入，否则无效果。`Stop(false)` 立即销毁；空句柄的 `GoTo` 始终无效果。 |
| `event Action<string> Notify` | `notify: true` 的时间轴事件触发时回调事件名（按时间触发或经 `Trigger`）。 |
| `event Action<string> StateChanged` | 每次进入声明过的状态时回调状态名，包括初始状态。 |
| `event Action Finished` | `IsAlive` 变为 `false` 时回调一次，无论原因（播完、`Stop`、`Dispose`）。 |
| `void SetParam(string name, float value)` | 实时修改运行时参数。当前内置三个立即生效的参数名：`intensity`（缩放粒子和点光亮度）、`rate`（只缩放 L1 与 L2 的连续发射速率，不缩放 `bursts`；须为有限的非负值，否则抛 `ArgumentOutOfRangeException`；0 只暂停连续发射）、`scale`（乘算根节点缩放）。其他名字会被记录下来但不会影响模拟。 |
| `void Stop(bool allowFadeOut = true)` | `true` 时：资产有 `stopState` 则进入它并播完；随后停止新发射、点光在 `0.25s` 内淡出、空中粒子跑完寿命后回收。没有 `stopState` 时立即开始淡出。`false` 时立刻切断所有粒子与点光。重复调用无效果。 |
| `void Dispose()` | 等价于 `Stop(false)`，幂等。在 `Thing.DeSpawn` 或地图销毁时务必调用，避免长期持有句柄。 |

三个事件都在主线程的每帧推进中按发生顺序派发，所以在回调里调用 `GoTo` 或 `Stop` 不会重入正在派发的播放：调用立即生效（离开的状态的节点在当帧就停止新发射），由它引起的事件在下一帧派发。`Stop(false)` 与 `Dispose` 之后的 `Finished` 在下一帧派发。RimUnity 兼容播放（`External`）没有状态与时间轴事件：`Notify` 与 `StateChanged` 只来自 RimLumen 原生部分，`Finished` 在两部分都结束后触发。

---

`LumenEmitterTransform` 是只读的 `World` 矩阵与单位世界 `Rotation` 四元数对：旋转来自完整父级旋转链及请求 / Follow 偏航，与缩放无关，不从矩阵推断，也不是 `ShapeRotation`。节点 `LocalRotation` 与 `ShapeRotation` 一样要求有限单位四元数，平方范数误差不超过 `1e-5`；XML 非有限 yaw 被拒绝。GPU 唯一入口为 `CreateInstance(spec, LumenEmitterTransform, mapIndex, realTime, uint emissionSeed)`，`UpdateInstance` 同样接收完整 transform；CPU `AcquireEmitter` 也要求节点种子。内部 emitter / particle / material / vertex 布局分别为 320 / 64 / 128 / 44 字节，不是公开原始 GPU ABI。

### 4. 原生 HDR 混合空间与扩展绘制

`LumenColorBlendSpace : byte` 提供 `Legacy = 0`（默认）和 `Linear = 1`，控制原生 HDR 层的混合，与发射器的 `ColorSpace` 材质计算相互独立。`RimLumenRenderHost.ColorBlendSpace` 是请求设置；只读的 `CurrentFrameColorBlendSpace` 是当前帧锁存值。安装时初始化锁存值，每帧在 `EngineApplication.PrePollEvents`（584）采集，早于输入、`OnUpdate`（715）和 `BeginMainThreadFrame`（730）。Update 中的设置修改从下一帧生效。

主线程 layer 15 或显式 HDR pass 绘制的生产者只能读取当前帧锁存值，不能读取请求设置作为绘制值。贡献者使用 `LumenCaptureContext.ColorBlendSpace`，将其复制进不可变帧数据，并在录制时使用 `LumenHdrDrawContext.ColorBlendSpace`；渲染线程不读取可变设置。内置 L1 / L2 直接进入 HDR pass；当前没有内置 layer 15 生产者，Showcase 原有参考面片保持 layer 0。

外部 GLSL 在宿主注册着色器根目录后可包含 `RimLumen/RimLumenColorSpace.glsl`。其中 `lumenDecodeStored(vec3)` / `lumenEncodeStored(vec3)` 提供 D / E，`lumenColorForBlendSpace(vec4 storedColor, bool linearBlendSpace)` 转换**未预乘**的 Stored RGB，保留 alpha。须先调用辅助函数，再按混合模式预乘；不得解码已经预乘的 RGB：

```glsl
#include "RimLumen/RimLumenColorSpace.glsl"
// storedColor 是材质计算得到的直通 RGBA；linearBlendSpace 来自本帧锁存值。
vec4 layerColor = lumenColorForBlendSpace(storedColor, linearBlendSpace);
if (premultipliedBlend) layerColor.rgb *= layerColor.a;
```

Linear 模式先对直通材质 RGB 解码，再进行 alpha / 硬件混合。原生 HDR 层和 group 0 宿主 Bloom 保持线性辐射（宿主 Bloom prefilter 先解码 Stored 的 glow 拷贝）；合成时在线性光下叠到解码后的场景上，只编码一次：`E(compress(D(scene) × (1 - a) + hdr + bloom))`，其中 `compress` 在峰值不超过 1 时为恒等。`hdr` 与 `bloom` 均为零的像素保持场景不变。AlphaBlend 与 Premultiplied 遵循同一规则，alpha 始终表示覆盖度。例如 `c=0.5, a=0.5` 叠在 `0.5` 灰场景上，两种模式都得 `0.5`；叠在黑色上，Legacy 得 `0.25`，Linear 约为 `0.36078`；这是公式示例，并非实测像素。光影包的 `hdr` 和 `bloom` 跟随 `ubo.ColorBlendSpace`，`glow` 和 `scene` 仍为 Stored，晚组 1～3 不变。自定义光影包在 Linear 模式下须自行完成同样的解码、混合、编码；不再额外分配图像。

## 七、贴图注册与兼容注意事项

### 1. 自定义粒子贴图（`LumenTextureRegistry`）

发射器填写的 `textureKey` 按以下顺序在主线程解析：

1. 先查 `LumenTextureRegistry.Register(string key, Waylith.Rendering.Texture texture)` 显式注册过的贴图；
2. 未命中时，调用游戏原版 `ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)` 加载模组 `Textures/` 目录下的图片（例如 `"RimLumen/Effects/Spark"`）；
3. 仍找不到时，退回程序化默认圆斑，并且每个缺失的 `key` 只在日志打印一次警告。未指定 `textureKey` 时也使用程序化圆斑。其 alpha 为 `max(0, 1 - r²)²`，`r` 为片元内的径向坐标；它与序列帧 atlas UV 独立，不随帧变化。

Linear 材质在主线程解析贴图时读取 `Texture2D.CpuData.Linear`：原生 ContentFinder 贴图为 `false`，RimUnity 贴图保留导出标志。无需也不能编写 `textureSrgb` 字段；UNorm、BC-UNorm 和 SFloat 格式不触发 GPU 自动 sRGB 解码。存在的贴图若不是 `Texture2D` 或缺少 `CpuData`，Linear 材质会报错并指出贴图键及缺失的颜色元数据。Stored 材质和公开 `Register` 行为不变；找不到贴图时仍回退到程序化圆斑。

> 不要在多线程或渲染回调里调用贴图注册。

### 2. 原版加法发光自动增强的兼容现状

RimLumen 默认对原版及第三方模组的加法混合绘制（`Blend.Enabled` 且 `DstColor == One`、`ColorOp == Add`、`SrcColor` 为 `SrcAlpha` 或 `One`、且不写深度）额外画一份拷贝进入 HDR 泛光，并内置排除了 5 个全屏/环境遮罩键：`GlowOverlay`、`OxygenOverlayIndoors`、`GravshipLensFlare`、`UnlitAdditive`、`MoteWater`。

原版绘制保持原样，发光核心仍由原版场景画出；拷贝只写入独立的 `glow` 目标，不进入 `hdr` 层，也不参与合成的核心项。早组拷贝供内置 Bloom 预过滤和光影包的 `glow` 输入使用，后续组只供宿主 Bloom 使用。因此发光下方的地面不会被压暗。关闭该开关或触发其熔断后，原版发光不再有任何增强；没有其他原生 HDR 效果、且当前光影包不改写场景时，画面与原版一致。

按有效 `FinalDrawItem.RenderQueue` 分为四组，分别在地图 Pass 内的边界执行：`q < 3152` → 3152、`[3152, 3169)` → 3169、`[3169, 3178)` → 3178、`[3178, int.MaxValue)` → 地图末尾 `int.MaxValue`。各组不合并；边界在同队列的原版场景抓取之前执行。

3152 组在有 HDR 绘制、早组发光拷贝或扭曲绘制，或光影包启用 `runOnEmptyFrames` 时运行一次完整渲染链。直接通过 `RecordHdrDraws` 绘制的贡献者（包括已有实现），必须在该帧捕获后让 `ILumenFrameContributor.RequiresDirectHdrPass` 返回 `true`。默认值为 `false`：路由的绘制按实际数量计入，仅有生命周期更新或计算不会开启 HDR Pass。

该组位于原版 3152 Distortion 抓取之前；包的 `glow` 只含 `q < 3152` 拷贝，`scene` 和 `depth` 是该边界的独立同帧快照，不是后续边界的快照。最终合成写回地图颜色，不采样正在写入的颜色图像。`pack.json` 的 `"backbuffer"` 名称与 PostEffectPass 合成 ABI 不变，名称表示最终合成输出，而不是交换链插槽。

后续组只重画各自的加法发光拷贝，执行宿主 Bloom 预过滤、降采样、升采样，再将 Bloom 的 RGB 加到场景并保留目标 Alpha，不做色调映射。原始核心已经在场景中，不会再次加入；原生 HDR 粒子、调色、雾、自定义 DAG 和历史缓冲也不会重跑。后续组使用同一帧捕获的当前包 `BloomParams`（强度、阈值、knee、scatter）及请求的 mip 级数；组为空或 mip 级数为 0 时，不添加该组边界或 Pass。

自定义 `bloomPrefilterShader`、`bloomDownShader` 和 `bloomUpShader` 只用于 3152 组，后续组使用宿主着色器，并不等价于任意自定义 Bloom。Cinematic 的后续组不会产生光丝或影响 Adaptation；Atmospheric 的后续 Bloom 不照亮薄雾，也不使用依赖雾的 scatter 增益；Crisp、Vibrant 等包的合成色彩映射不作用于后续 Bloom。光影包 DAG 和历史缓冲只随 3152 组执行一次，不按组重复。

已安装原版及 DLC 的静态清单中，3155 仪式蜡烛在 3169 组增强，位于天气雾 3169 和太阳阴影 3170 之前；3177 轨道 / 电力光束及端点在 3178 组增强，位于战争迷雾 3175 之后、Gravship 抓取及 Darkness 3180 之前；3181 重力场、3185 引擎、3201 推进火焰和 4000 Noctol 眼睛在地图末尾增强。清单中 `[3169, 3174]` 和 `[3178, 3179]` 没有符合条件的绘制。末组 Bloom 晚于 3201 Screen 和 3251 VoidNode 抓取，因此不包含在这两次抓取中；不保证所有覆盖层、抓取或第三方绘制顺序。这些是静态顺序契约，实机表现尚未验证。

如果在自定义 `ShaderDef` 上不想被自动重画增强，可以挂载 `<li Class="RimLumen.Core.Compat.LumenEmissiveModExtension"><EnableHdrBloom>false</EnableHdrBloom></li>` 进行逐 `ShaderDef` 排除。（`HdrMultiplier` 字段已移除，写 `<hdrMultiplier>` 会在 Def 校验时报错；只有 `EnableHdrBloom` 生效。）

### 3. 游戏逻辑光（`CompRimLumenLogicalLight`）

`pointLight` 只照亮画面。建筑需要影响心情、工作速度、植物生长和光照读数时，在 ThingDef 上挂逻辑光组件：

```xml
<comps>
  <li Class="RimLumen.Core.Lighting.CompProperties_RimLumenLogicalLight">
    <glowRadius>6</glowRadius>
  </li>
</comps>
```

- **作用范围**：只抬高 `GlowGrid.GroundGlowAt`，效果等同同半径的白色、无过亮原版 `CompGlower`。不进入原版 `GlowGrid`，`VisualGlowAt`、原版八角形光照层和光影包 `mapLightField` 的 G 通道都保持不变。
- **光照曲线**：按原版八邻域洪泛计算距离，墙体等 `blockLight` 建筑会遮挡，光线可以绕过门洞和墙角。光源附近为 50%（「明亮」），`glowRadius` 外为 0。多个逻辑光重叠时取最大值，再与原版结果（含天光）取最大值；真空地形没有地面光。
- **点亮条件**：与 `CompGlower` 相同。建筑已生成，`FlickUtility.WantsToBeOn` 为真，且建筑及其全部实现 `IThingGlower` 的组件都返回 `ShouldBeLitNow()`。因此电力、开关、燃料和日程表组件无需额外配置，组件也响应同一组信号（`PowerTurnedOn`、`FlickedOff`、`RanOutOfFuel` 等）。
- **生效时机**：与原版光照一致，在每帧 `GlowGridUpdate_First` 重算并触发 `GlowChanged` 事件。光源开关或光照范围内的墙体变化时才重算。
- **校验**：`glowRadius` 必须是有限值，取值范围为 `(0, 40]` 格，越界时 Def 校验报错。组件没有颜色、强度和过亮半径字段。
- **搭配视觉光**：需要可见的光池时，再播放带 `pointLight` 的特效。两者互不影响，不会在画面上叠加原版光照。
- **不要同时挂 `CompGlower`**：原版发光组件会写入 `GlowGrid`，并在画面上画出八角形光照。

启用「娱乐演示建筑」设置后，「娱乐」建造菜单提供三盏对照路灯：`RimLumen_StreetLamp_Visual` 只有视觉光，`RimLumen_StreetLamp_Logic` 只有逻辑光，`RimLumen_StreetLamp_Both` 两者都有。

---

## 八、可选 `Chezhou.RimUnity` 桥接说明

当游戏同时启用 `Chezhou.RimUnity` 时，`RimLumen.Adapter.RimUnity` 会读取其导出的 `catalog.json`、预制体 JSON（含 `.json.gz`）和 `.rutex` 贴图，并将粒子节点按功能自动分流：

- **转入原生 HDR / GPU（或 CPU）**：受支持的基础形状及其形状局部位置、旋转、缩放（TRS）、曲线、渐变、贴图、序列帧、周期发射及受概率控制的计划脉冲。
- **保留在原 RimUnity 播放器混播（`External`）**：节点启用了 `trails`、`subEmitters`（含子发射器依赖）、`collision`、`trigger`、`renderMode.Mesh` 或未知 `renderMode`、超出原生颜色范围的材质 `_Color`、线性 `velocityOverLifetime`、`rotationOverLifetime`、变化曲线 `forceOverLifetime`、任何启用的 `lights` 模块（含 `ratio = 1`），以及动态圆弧 / 半径模式、align、变化速度 / 力 / 旋转曲线、双曲线随机、几何或形状纹理等尚未覆盖的功能。

导入脉冲的计划时间须严格小于系统 `Duration`。`cycleCount=0` 时，每个系统循环都从同一本地相位重新重复，即使间隔不能整除时长。一个发射器所有脉冲定义展开后的发生次数合计最多 4096；超限时整个发射器转 External 并给出原因，不截断。间隔非有限时转 External；重复时，间隔 `<=0` 也转 External。单次脉冲允许有限的非正间隔，但不使用该间隔。无效计划原因是 `emission.bursts.schedule.domain`，总次数超限原因是 `emission.bursts.occurrenceLimit`。曲线分流只检查启用的模块及有效轴；禁用 emission 中保存的曲线不参与判定。启用 `main.startSize3D`、`main.startRotation3D` 或非零 `startRotation` 时转 External，原因分别为 `main.startSize3D`、`main.startRotation3D`、`main.startRotation`；启用的 `rateOverDistance` 双曲线原因是 `emission.rateOverDistance.dualCurve`。未启用的 3D 轴字段不触发拒绝，初始旋转和 3D 尺寸的原生支持仍延期。

适配器将材质 `_Color` 单独映射到 `materialTint`；Linear 材质在没有其他不支持原因时走原生路径。导入的材质颜色超出上述范围时转外部播放，原因包含材质标识和字段：`material[<asset-id>]._Color.domain`；不截断或折入顶点颜色。

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

1. **保留源名**：`hdr`、`glow`、`bloom`、`scene`、`depth`、`mapLightField`、`backbuffer` 为保留源名，不可作为自定义 `targets[].name`，也不能用 `prev:` 作为自定义目标前缀。
2. **末尾唯一 `backbuffer`**：若 `passes` 非空，仅最后一个 Pass 必须输出到 `"backbuffer"`；其余所有离屏 Pass 必须输出到已声明的 `targets[].name`，且必须使用 `"blend": "Opaque"` 与非空 `shader`。
3. **禁止采样 `backbuffer`**：任何 Pass 的 `inputs[].source` 均不能填 `"backbuffer"`。
4. **无前向引用与无同帧自读**：采样普通 `target` 时，必须有排在当前 Pass 之前的 Pass 已向其写入；禁止在同一 Pass 中既写 `X` 又直接以 `X` 作为输入。
5. **历史目标要求**：仅声明了 `"persistent": true` 的目标 `X` 才允许通过 `"prev:X"` 采样上一帧写入的槽位。
6. **绑定槽位约束**：`inputs[].binding` 必须位于 `[0, 15]`，且不得占用保留 UBO 槽位 `5`，同一 Pass 内不可重复。

默认 Bloom prefilter 对 `hdr` 应用阈值后提取，`glow` 不经阈值、整体并入 Bloom：原版加色发光拷贝画在 8 位场景里，单份不超过 1，经阈值会被全部去掉；其泛光强弱由 `bloomIntensity` 控制。阈值默认 `1.0`（设置与包基线一致），峰值 ≤ 阈值时严格输出 0；`bloomKnee=0` 是硬阈值，无除零。正 knee 在阈值之上按 `excess * smoothstep(0,knee,excess)` 平滑起辉，`excess=max(peak-threshold,0)`。

#### 3152 组空帧 Bloom 跳过规则

即使 `runOnEmptyFrames` 为 `true`，当满足以下任一条件时，运行时仍会跳过内置 Bloom 降采样与升采样链，并将 `bloom` 输入源绑定为 1×1 全黑占位纹理 `black1x1`：

- `bloomMipLevels == 0`；
- 当前帧没有任何 HDR 绘制（含原版发光拷贝，`!hasHdrDraws`），**且**包未设置 `bloomPrefilterShader`、`bloomDownShader`、`bloomUpShader` 中的任意一个路径。

需要基于 `scene` / `depth` / `mapLightField` 在空帧计算自定义 Bloom 散射时，请至少指定一个自定义 Bloom 着色器（如 `bloomPrefilterShader`）并启用 `runOnEmptyFrames: true`。

#### 两级错误隔离

- **扫描与校验期隔离**：`pack.json` 语法错、路径越界、使用保留 ID `"default"` 或图校验不通过时，错误仅保存在该条目的 `LoadError` 中（菜单显示 `[损坏]` 且不可选），不会波及同目录或其它模组的光影包；`FindOrDefault` 会自动回退到内置 `default` 包。
- **编译与录制期隔离**：调用热重载或切包时，`RimLumenRenderHost.ReloadShaderPack` 会先用 Slang 预编译所有自定义 `.frag.glsl`；编译失败则保留当前生效包并在 `LastShaderPackError` 记录详情。若非 `default` 包在渲染线程录制阶段抛异常，运行时立即记录 `LastShaderPackError` 并在主线程自动切回 `default` 包，**不会触发全局管线熔断（`FailSafeTripped`）**。

---

### 4. GLSL `#version 450` 着色器契约

#### 内置采样源与占位纹理

| 源名称 | 格式与滤波 | 未激活时的占位纹理 | 说明 |
| --- | --- | --- | --- |
| `hdr` | `R16G16B16A16SFloat` / `Linear` | — | RimLumen 原生 HDR 绘制层：RGB 为预乘颜色；A 为原生 Alpha / 预乘粒子的覆盖度，加法粒子写 0。不含原版发光拷贝。合成后受后续地图覆盖层处理。 |
| `glow` | `R16G16B16A16SFloat` / `Linear` | `black1x1`（全 0） | 原版及第三方加法发光的拷贝，未截断的叠加值。场景里已有这些发光的核心，合成时不要再把它加回场景；用于泛光、光晕、拖尾等。在 3152 边界前捕获，尚未经过后续地图覆盖层。A 未使用。 |
| `bloom` | `R16G16B16A16SFloat` / `Linear` | `black1x1`（`R8G8B8A8UNorm` 全 0） | 内置多级 Bloom 金字塔输出。 |
| `scene` | 场景主色彩格式 / `Linear` | — | 3152 边界处地图、角色与建筑主场景的独立同帧颜色拷贝。 |
| `depth` | `R32SFloat` / `Point` | `depth1x1`（`R32SFloat`，远平面 `0.0`） | 由 3152 边界同帧场景深度拷贝得到、供常规采样的颜色快照。 |
| `mapLightField` | `R8G8B8A8UNorm` / `Linear` | `black1x1`（全 0） | 尺寸为 `mapSizeX x mapSizeZ` 的全图屋顶/人工光/存在使用室外温度的房间/迷雾场。 |

`hdr`、`glow` 与 `scene` 的 RGB 使用同一种编码，可以直接相加，不需要做 gamma / 线性转换：场景目标是 `B8G8R8A8UNorm`，原版直接按存储值混合，RimLumen 的 HDR 目标用同样的着色器输出与混合方式，只是不截断。`1.0` 等于原版的满亮度，大于 `1.0` 的部分就是原版会截掉的额外亮度。

#### 默认输入绑定（未使用 `passes` 自定义绑定时）

- **默认合成阶段**（`passes` 为空，或末尾 Pass 省略 `shader` 且 `inputs` 为空；`passes` 为空时固定使用 `PremultipliedAlpha` 混合）：
  - `binding = 0`：`hdr`
  - `binding = 1`：`bloom`
  - `binding = 2`：`scene`
  - `binding = 3`：`depth`
  - `binding = 4`：`mapLightField`
- **自定义 `bloomPrefilterShader`**：
  - `binding = 0`：`hdr`、`binding = 1`：`glow`、`binding = 2`：`scene`、`binding = 3`：`depth`、`binding = 4`：`mapLightField`。
- **自定义 `bloomDownShader`**：
  - `binding = 0`：上一级降采样纹理、`binding = 4`：`mapLightField`。
- **自定义 `bloomUpShader`**：
  - `binding = 0`：更低分辨率升采样输入、`binding = 1`：同级降采样纹理、`binding = 4`：`mapLightField`。

> 混合与 Alpha 提示：`passes` 为空时默认合成 Pass 固定使用 `PremultipliedAlpha` 混合到 3152 边界的地图颜色。若你的合成着色器直接输出最终全屏图像（如含调色、雾效），请输出 `vec4(finalRgb, 1.0)` 或使用显式 `passes` 并设 `"blend": "Opaque"`；若只想把 HDR 粒子与 Bloom 叠加到场景之上，则输出 `vec4(mappedHdr, hdr.a)`：结果为 `mappedHdr + scene * (1 - hdr.a)`，`hdr` 与泛光都为空的像素保持场景原样。色调曲线由各包的合成着色器自行决定；内置默认合成在峰值不超过 1 时原样输出，超过 1 时峰值保持 1，超出部分沿原色相向白色压缩。

> 数值安全提示：`hdr` 是 `RGBA16F`，大量加色粒子叠加后可能溢出成 `Inf`，个别驱动上还可能出现 `NaN`，而不同驱动对它们的处理不一致。凡是直接读取 `hdr` 的 Pass（自定义 prefilter、合成、亮度统计），尤其是写入 `persistent` 历史目标的 Pass，都应先把 `NaN`/`Inf` 置 0 并设上限，否则一个坏像素会被模糊扩散，或长期留在历史缓冲里。内置包的写法见 `Cinematic/Common.glslinc` 中的 `sanitizeHdr`。

#### 固定顶点接口与 `#include`

全屏三角形由引擎提供，片段着色器只需声明：

```glsl
#version 450
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
```

同一光影包内可使用相对路径 `#include "Common.glslinc"` 复用公共函数与 UBO 声明。

#### Uniform Buffer（`set = 0, binding = 5`，208 字节 `std140`）

```glsl
layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection; // 偏移   0 (64 B): ViewProjection 逆矩阵（Row-Major 存储）
    vec4 ScreenAndMapSize;  // 偏移  64 (16 B): (1.0/sceneW, 1.0/sceneH, mapSizeX, mapSizeZ)
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
    float ColorBlendSpace;  // 124: 0 Legacy（hdr/bloom 为 stored 值），1 Linear（线性辐射）
    vec4 BloomParams;       // 128: intensity, threshold, knee, scatter
    vec4 PackParams[4];     // 144: 4 行，每行 16 字节，共 64 字节
} ubo;
```

- **参数映射**：`options[].target` 的 `packParamN`（`N = 0..15`）对应 `ubo.PackParams[N / 4][N % 4]`；已有着色器需将旧向量成员改为 `PackParams` 数组。
- **当前 ABI**：旧 `EnvParams` 位掩码已移除；外部包须同步上述 208 字节布局。`HasHdrDraws`（含早组 glow 拷贝）、`HasSceneDepth`、`HasMapLightField`、`IsPaused` 均为独立的 0/1 float。`DepthValid=1` 表示该边界的真实深度快照，`0` 表示远平面占位，不表示每个像素都有几何体。
- `GameSeconds` 为游戏 ticks / 60，暂停停止；`RealSeconds` 为宿主经过的真实秒数。对应 Delta 字段是本帧步长。Cinematic 的 Adapt 与 StreakExtract 读取 `hdr + glow`，合成核心仍只读取 `hdr`；Adapt 使用游戏秒时间常数 0.2 秒，权重 `1-exp(-GameDeltaSeconds/0.2)`。

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

Waylith 采用 Reverse-Z 深度：近平面为 `1.0`，空背景/远平面为 `0.0`；深度未激活时的 `depth1x1` 占位纹理值为 `0.0`，且 `DepthValid=0`。区分真实 3D 几何体（例如 IsoRim 墙体）与平铺地面/天空背景可使用：

```glsl
float depth = texture(depthImage, uv).r;
float is3DGeom = ubo.DepthValid * step(0.0001, min(depth, 1.0 - depth));
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
| `R` | 屋顶（`Roofed`） | `RoofGrid.Roofed` 为 true 时 `1.0`（255），否则 0。来源：`Assembly-CSharp.decompiled.cs:92166–92168`。 |
| `G` | 人工地表光（`ArtificialGroundGlow`） | `min(0.5,(max(r,g,b)/255.0)*3.6)`，按 UNorm8 四舍五入，不含天空光。来源：`GlowGrid.GroundGlowAt` 人工光分量，`Assembly-CSharp.decompiled.cs:69541–69542`。半亮度量化为 128/255。 |
| `B` | 存在使用室外温度的房间（`HasOutdoorTemperatureRoom`） | 该格存在 Room 且 `Room.UsesOutdoorTemperature` 为 true 时 `1.0`（255），否则 0；否则明确包含室内温度 Room，以及墙/岩石等无 Room 格。来源：`Assembly-CSharp.decompiled.cs:90349–90358`。这是定义的事实，不是占位或无 Room 格的室内/室外分类；原版 `TryGetDirectAirTemperatureForCell` 无 Room 时返回 false/21f（`:92707–92714`），未给出该 bool 分类。本输入不推断邻格。 |
| `A` | 战争迷雾（`Fogged`） | `FogGrid.IsFogged` 为 true 时 `1.0`（255），否则 0。来源：`Assembly-CSharp.decompiled.cs:68695–68701`。 |

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
