# 任务书：RimLumen 基建阶段

## 你要交付什么

一份**阶段策划**：`docs/02-plan.md`。不是代码。

策划要覆盖「一次性查清楚」的所有未知，把每条未知变成「已查清 + 证据」或「必须实测 + 为什么代码读不出来」。之后用户批准，才进入实现。

## 背景（已核实，不要重复调研）

- 游戏 `E:\SteamLibrary\steamapps\common\RimWorld`，1.7.5005 rev1082，Steam beta 渠道 `unstable`。
- 1.7 的引擎是 Waylith：`Licenses.txt:5` 原文 “RimWorld is built on the Waylith engine, a Ludeon Studios engine built on the Brutal engine framework of Ahwoo Limited (RocketWerkz), and runs on a self-contained build of the .NET runtime.” 游戏根目录没有 Unity 播放器，也没有 `*_Data/Managed`。
- Waylith 自带计算管线和多渲染目标通道（`Waylith.dll` 内含 `ComputePipeline`、`ComputeRenderPass`、`ComputeRenderFeature`、`CreateMrtColorRenderPass`）；根目录 `Rendering\Shaders\` 有 19 份引擎着色器源码（`SceneDefault.vert/frag.glsl`、`PostFxComposite.frag.glsl`、`EncodeBC7Fast.comp.hlsl`、`Mipmap*.comp.hlsl`、`GpuProbe.comp.hlsl` 等），Slang 编译器 `slang*.dll` 在场。
- `UnityStubs.dll` 暴露了 `CommandBuffer`、`ComputeShader`、`DispatchCompute`、`allowHDR`、`AddCommandBuffer` 等名字。**只从 DLL 字符串确认存在，是真实现还是空桩未验证——这属于代码可查项，用 IL 读，不要靠跑游戏。**
- RimUnity：`E:\SteamLibrary\steamapps\workshop\content\294100\3819570122`，MIT（`LICENSE`），依赖 Harmony。文档在 `Docs\en`、`Docs\zh-CN`（`Status.md`、`ColorAndBloom.md`、`RenderDiagnostics.md`、`Workflow.md`、`CustomShaderConversion.md` 等）。游戏内是 0.1.23。
- 制作侧：`E:\SteamLibrary\steamapps\common\RimWorld\Moddev\UnityVfx\WotF-Vfx`，Unity 2022.3.53f1c1、Built-in RP、URP 14 也可导；引用的 RimUnity UPM 包是 0.1.22（与游戏内 0.1.23 不一致）。
- 已查明的 RimUnity 能力边界与现有流水线问题，在 `docs/01-research.md`。

## 必须回答的问题（每条给出证据：文档原文 / 导出器 file:line / IL 方法签名）

1. **扩展点**：RimUnity 的泛光/后处理管线是否留下可挂新通道的接口？内容模组能否在不改它源码的前提下插入一个后处理阶段、一个渲染目标、或一个自定义渲染 pass？读 `Docs\en`、导出器源码、`RimUnity.dll` 的 IL。
2. **计算着色器**：`ComputeShader` 在 Waylith 上是真实现还是空桩？支持哪些 Op（`UnityStubs.dll`、`Waylith.dll` 的 IL 与 `Rendering\Shaders\*.comp.hlsl` 的编译路径）。判据：能 Dispatch 一个简单 kernel 并读回结果需要什么 API。
3. **着色器语言**：内容模组的自定义着色器走哪条编译路径（GLSL/HLSL/Slang），支持哪些特性（MRT、Stencil、Blend 模式、uniform 布局、`StructuredBuffer`、`RWTexture`）。
4. **RimWorld 原版可复用件**：`PostFxComposite.frag.glsl` 已实现了什么后处理；原版是否已有 HDR 目标、色调映射、暗角。如果原版就有，扩展点和成本会完全不同。
5. **导出格式**：RimUnity 的导出产物（`catalog.json` + `exports/<guid>/`）结构能否被独立实现直接读？如果不能，缺什么。
6. **复用比例**：把 RimUnity 的代码按「粒子运行时 / 材质转换 / 着色器编译器 / 导出格式 / 后处理」五块拆开，逐块估计复用比例，据此给「提 PR」还是「独立实现」的推荐，并给出理由与风险（游戏更新后谁先坏）。
7. **两套后端并存**：模组侧如何做到「装 RimUnity 用 RimUnity，装 RimLumen 用 RimLumen，装着两个也不打架」，且法术代码零改动。给出接口形状（方法签名级）、能力查询方式、资源格式要求、降级规则。
8. **后处理对原版画面的影响边界**：HDR/色调映射/暗角这类全局效果，怎么保证原版美术不变色。给出可验证的判据（不是「看起来还行」）。

## 边界

- 只写本仓库。不修改 `Whisper-of-the-Forest`、`Moddev/UnityVfx/WotF-Vfx`、也不改 workshop 目录里已安装的 RimUnity。
- 不启动 Unity、不启动 RimWorld、不跑游戏的 build/test 脚本。
- 不为了「先跑个探针」而跳过代码可查的部分。任何一条能用源码/IL/文档回答的，必须读出结论并附锚点；只有真正读不出来的（例如驱动行为、`UnityStubs` 是真实现但语义未定）才允许列为「需实测」，并写明为什么读不出来。
- 不 patch Waylith 私有结构作为方案前提。

## 阶段目标（用户原话）

「应该把我们这个基础设施作为阶段目标，在这个阶段目标准备策划完后一次性查清楚需要的东西。」

所以：策划要一次把需要查的东西列全，然后成批查清，不要边走边发现。

## 参考

- `docs/01-research.md`：已核实的事实与证据锚点。
- WotF 侧的方案文档在用户 Obsidian 库 `环世界模组-森林的低语/技术/08-视效平台方案.md`（只读参考，不要改）。
