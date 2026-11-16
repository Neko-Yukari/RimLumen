# RimLumen 阶段策划（02-plan）

修订日期 2026-11-13（首版 `27735ce` 写于 2026-11-12）。证据锚点相对 `.scratch/decompiled/`（ilspycmd 9.1.0 反编译 RimWorld 1.7.5005、RimUnity 0.1.23、RimIris v2026.10.29.3、Raycast Lighting、Celestial Lighting），`AC` = `Assembly-CSharp/Assembly-CSharp.decompiled.cs`，`WS/` = workshop `3819570122`，`SH/` = 游戏 `Rendering/Shaders/`。逐条明细在 `.scratch/findings-*.md`，定型摘要同步在 Obsidian `RimLumen/` 目录。

## 0. 定位与一页结论

**定位**：RimLumen 是在 RimWorld 1.7（Waylith / Vulkan）之上构建的**独立视效资产与 HDR 渲染子引擎**（覆盖场景节点层级、Mesh 与自定义材质着色器、贴图、时间轴触发、CPU/GPU 粒子与力场、高频地图随身点光、HDR 泛光与软粒子合成，并预留独立 3D 骨骼蒙皮实体接入能力；不碰原版 `PawnRenderTree` 2D 纸片小人），默认作为**零侵入基础框架层**运行——未播放视效且未开启可选增强时，对原版画面 **0 像素改动**。全局调色、昼夜氛围与逐像素全局光照不纳入范围，与 RimIris、Raycast Lighting、Celestial Lighting 正交共存。

**核心结论**：

1. **全公开 API 接入，0 处 Harmony 补丁**：`EngineApplication.Instance`、`FrameRenderer.InsertFeature`、`IRenderFeature`、`IRenderPass`、`PassAttachment`、`LayerPassRouter.RegisterLayerMask`、`MeshBatchRenderer`、`PostEffectPass`、`RenderTexture`、`ComputeKernelCache`、`ComputeDispatchQueue`、`GpuComputeBuffer` 全部是 public。RimUnity 自己也不在 `Waylith` 的 `InternalsVisibleTo` 名单内。
2. **原生内核自足，RimUnity 仅作过渡期借道**：
   - `RimLumen.Core.dll` 零外部模组依赖、零 Unity/RimUnity 命名耦合，内置原生资产内存模型（`EffectGraph` 节点树、Mesh/贴图/材质描述、`EmitterSpec`/`ForceFieldSpec`、时间轴）、**L1（原生轻量 CPU 模拟）+ L2（GPU Compute 模拟）** 双粒子引擎、HDR 渲染与合成管线、高频 `GlowGrid` 点光驱动。只靠 `Core` 即可独立运行完整特效。
   - `RimLumen.Adapter.RimUnity.dll`（`LoadFolders.xml` 按 `IfModActive="Chezhou.RimUnity"` 条件加载）仅为过渡插件：把 RimUnity 导出的 Prefab / Shuriken / `.rutex` 翻译为 `Core` 原生描述符，并对超出原生子集的冷门 Shuriken 模块（碰撞、子发射器、复杂拖尾）回退 L0（RimUnity CPU 模拟）。业务模组（WotF）只调 `RimLumen.Core` 接口；后续补齐轻量原生 JSON/`.rutex` 读取器（P4）即可无感拔除该适配器。
3. **计算与同步**：`UnityStubs` 里的 `ComputeShader`/`CommandBuffer` 全是空桩；真路径 `ComputeKernelCache.Load → ComputeDispatch.Rent → Enqueue/DispatchNow → GpuComputeBuffer` 全 public。引擎在帧内**未插入** compute→vertex/indirect 的 storage buffer barrier（`FrameRenderer.RecordFrame` 全文 0 处 `PipelineBarrier`）；`Core` 在自有 pre-backbuffer pass 内用 public `PipelineBarrier` 自录同步，驱动层行为在 P0 用 `-EnableVulkanValidation` 实测验收。
4. **合成位置修正（不写回 Scene RT，改为 UI 前尾端 Inline 混入 Swapchain）**：
   - 调查证实：RimUnity 开启地图 HDR 时，上屏使用的是它在 `MapMainPass` 内提前拷走的 `LinearScene` 图，写回原版 Scene RT 的内容会被整帧静默丢弃（`RimUnity.Rendering/BloomPipeline.cs:373-379`、`HdrMapHooks.cs:84`）；RimIris 则在 `ImGui` 前用 `BlendState.Opaque` 整屏覆盖 swapchain（`RimIris.NativeFx/NativeFxChain.cs:394,481`）。
   - 因此 RimLumen 自有 `RGBA16F` 画布与多级 Bloom 在离屏（`MapRenderFeature` 之后、`PostFxFeature` 之前）完成，最终合成走 `PostEffectPass.RecordInline` 直接混入 swapchain，并带**动态顺位守卫**确保始终排在 `ImGuiRenderFeature` 前最后一位。
5. **默认零差**：无活动特效且可选增强关闭时，离屏与 inline 步骤直接短路跳过（0 Draw、0 Clear），结构性保证与未安装时逐像素一致。

---

## 1. 系统架构与帧内拓扑

### 1.1 程序集与职责边界

| 层 / 对象 | 归属 | 职责 |
| --- | --- | --- |
| `RimLumen.Core.dll` | 本项目核心（永久底座） | 原生资产模型（2.5D 地面层 + 90° 高度层节点变换、Mesh、贴图、材质、时间轴）、L1/L2 粒子与力场、自有 `RGBA16F` 画布与深度软粒子、多级 HDR Bloom 与局部色调映射、屏幕扭曲采样、UI 前尾端合成、帧级脏更新 `RegisterCustomGlower` 点光、可选原版加色发光体重画（默认关） |
| `RimLumen.Adapter.RimUnity.dll` | 本项目过渡层（可拔除） | 条件加载；将 RimUnity 已载入的 Prefab / Mesh / Material / `.rutex` / Shuriken 参数映射为 `Core` 原生描述符；超集模块回退 L0 |
| 全局调色 / 昼夜氛围 / 逐灯软阴影 | 外部模组（不承接） | 让渡给 RimIris（ACES/暗角/写实黑夜/水面）、Raycast Lighting（逐灯视线软阴影）、Celestial Lighting（天文昼夜/云/极光） |

### 1.2 一帧内的渲染拓扑（Frame Graph）

1. **主线程 `CaptureFrame`**：收集活动 `EffectGraph` 提交；更新 GPU 发射参数与材质属性；若开启「原版发光体增强」，从 `MeshDrawSubmission.CaptureFlushedSnapshot` 筛出加色混合 draw 重新生成自有 `FinalDrawItem`。无活动内容时标记本帧空载。
2. **`ComputeRenderPass`（Engine index 0，`PreBackbuffer=true`）**：执行 L2 GPU 粒子模拟 Kernel（出生、积分、重力/阻力、Curl 湍流、Vortex 旋涡、吸引子、生命周期曲线）。
3. **`MapRenderFeature`（原版，`PreBackbuffer=true`）**：原版地图、小人、光照叠加层画入 8-bit Scene RT 与 `D32SFloatS8UInt` Scene Depth。
4. **`RimLumenVfxPass`（插在 `Map` 后、`PostFx` 前，`PreBackbuffer=true`）**：
   - 先在同一 `ctx.CommandBuffer` 录 `VkBufferMemoryBarrier`（`ComputeShaderWrite → VertexShaderRead | DrawIndirectCommandRead`）。
   - 打开自有 `R16G16B16A16SFloat` 颜色目标 + 只读挂载原版 Scene Depth（经 `CompositePass.Source` 取得，`LoadOp.Load`、`InitialLayout = DepthStencilAttachmentOptimal`）。
   - 绘制路由到自有 `passId` 的 Mesh / L1 粒子 / L0 回退项、L2 SSBO Instanced 粒子（Billboard / Stretched / Horizontal），以及可选重画的原版加色项；片元侧对比场景深度实现遮挡与软粒子羽化。
5. **`RimLumenBloomChain`（同上，`PreBackbuffer=true`）**：对自有 `RGBA16F` 做高亮提取与多级降/升采样模糊，产出 HDR Bloom RT。
6. **Swapchain RenderPass（`PreBackbuffer=false`，同一 pass 内按 feature 顺序 inline）**：
   - 原版 `CompositePass` / RimUnity `BloomCompositeStage` 贴屏；
   - RimIris `GradePass`（若安装）不透明整屏覆盖调色结果；
   - **`RimLumenCompositeInline`（动态守卫锁死在 `ImGuiRenderFeature` 前一位）**：对（HDR VFX + HDR Bloom）做局部高光去饱和色调映射（必要时按偏移采样 `CompositePass.Source` 做热浪/冲击波扭曲），以 Alpha / Additive 混入屏幕；
   - `ImGuiRenderFeature` 绘制游戏 UI。

---

## 2. 八个调研问题与新增核实结论

### Q1 扩展点 — 已查清

| 需要 | 公开入口 | 锚点 |
| --- | --- | --- |
| 拿到引擎 | `EngineApplication.Instance`、`.FrameRenderer`、`.GraphicsDevice`、`.RenderContext` | `Waylith/Waylith/EngineApplication.cs:130,142,158,160` |
| 插删 feature | `FrameRenderer.AddFeature / InsertFeature(index) / RemoveFeature / Features`（带锁 + `Volatile.Write` 快照数组） | `Waylith/Waylith.Rendering/FrameRenderer.cs:40,56,64-96` |
| 录制顺序 | 全部 `PreBackbuffer==true` → swapchain pass 内 inline pass → `IPostBackbufferPass` | `FrameRenderer.cs:282-368` |
| 原版 feature 链 | `Compute → World → Map → CameraCapture → PostFx(Composite) → ImGui → ScreenCapture` | `RimWorld/RimWorldWaylith/Bootstrap.cs:284-321` |
| 自定义 passId | `RenderPassRegistry.Register(name) → byte`；`LayerPassRouter.RegisterLayerMask(mask, id)`（8 槽，原版占 3 槽） | `RenderPassRegistry.cs:13`；`LayerPassRouter.cs:5,11-34`；`Bootstrap.cs:359-362` |
| 拿本帧 draw | `MeshDrawSubmission.CaptureFlushedSnapshot` + `DrawItemMaker.TryMake` + `MeshDrawFrame` | `MeshDrawSubmission.cs:140,430`；`DrawItemFactory.cs:39`；`MeshDrawFrame.cs:43` |
| 自有目标与绘制 | `new RenderTexture(...)`、`PassAttachment`、`new MeshBatchRenderer(...)`、`PostEffectPass.Record/RecordInline` | `RenderTexture.cs:105`；`MeshBatchRenderer.cs:478`；`PostEffectPass.cs:96,141,152` |
| 场景 RT 与深度 | `CompositePass.Source`（public get/set，宿主绑定 `_sceneTargets.Current`）→ `.DepthView` | `CompositePass.cs:11`；`Bootstrap.cs:302`；`SceneColorTargets.cs:190-211` |
| 法术随身点光 | `GlowGrid.RegisterCustomGlower / DeregisterCustomGlower`（上限 40 格，立标脏格） | `AC:69647-69683` |

与 RimUnity 并存：RimUnity transpiler 对非 Map/PostFx feature 原样透传（`BloomHooks.cs:209-222,258-267`）；其地图 HDR 只抓 `PassId==0 || PassId==MapMain`（`HdrDrawFrame.cs:238-245`），不吞自有 `passId`。

### Q2 计算着色器 — 已查清（同步驱动行为进 P0 实测）

- `UnityStubs` 的 `ComputeShader.cs:7-16`、`CommandBuffer.cs:49`、`GraphicsBuffer.cs` 全是空桩。
- 真路径：`ComputeKernelCache.Load`（`ComputeKernelCache.cs:27`，入口固定 `main` `:163`）→ `ComputeDispatch.Rent` + `SetStorageBuffer / SetStorageImage / SetSampledTexture / SetPushConstants`（`ComputeDispatch.cs:43-119`，set 0 ≤16 绑定，push ≤128B，无 UBO）→ `ComputeDispatchQueue.Enqueue`（帧内 index 0）或 `DispatchNow`（fence 同步，`ComputeDispatchQueue.cs:98,148-194`）→ `GpuComputeBuffer.Create / Upload<T> / Read`（`GpuComputeBuffer.cs:78,129,154`）。
- 同步：`RecordDispatch` 出口只管 storage image（dst 仅到 Fragment|Compute），storage buffer 无引擎级 barrier。`Core` 用 `Brutal.VulkanApi` 的 public `PipelineBarrier`（调用样板 `GpuComputeBuffer.RecordUploadBarrier:119-127`、`HdrSceneCopy.cs:13`）在绘制前补 `ComputeShaderBit → VertexShaderBit | DrawIndirectBit`（注意 SSBO 顶点读取走 `VertexShaderBit` 而非固定管线 `VertexInputBit`）。

### Q3 着色器语言 — 已查清

- 唯一后端 Slang（`slang_createGlobalSession2`），输入枚举 `Glsl`/`Hlsl`（vert/frag/comp，无 geom），不收 `.slang` 原文件；`ShaderSourceLoader.RegisterShaderSourceDirectory` public static 支持 `#include "..."` 递归展开（`findings-shader.md` §1）。
- 原版地图着色器是数据驱动：166 个 `ShaderDef` 在 `Data/Core/Defs/ShaderDefs/Shaders.Map.xml`，源码在 `Data/Core/Shaders/Map/*.glsl`；同名覆盖按叶子文件名匹配、最后加载的模组静默胜出（`AC:43541-43558`）；`ShaderDefRegistry.HotReload` 为 public（`AC:43514`）。RimLumen 自有着色器独立命名，不覆盖原版文件。
- RimLumen 自有管线直接调 `new Waylith.Rendering.Shader(...)` + `RuntimeShaderCompiler.Compile`，不受 RimUnity `CustomShaderLoader` 的注解限制；set 2 支持最多 8 个 `StorageBuffer`（Vertex|Fragment）。

### Q4 原版光影与后处理 — 已查清（含更正）

- **后处理**：仅 `PostFxComposite.frag.glsl:14-16` 一行饱和度 `mix` + `clamp(0,1)`；地图场景 RT 默认 `B8G8R8A8UNorm`（8-bit），无地图 HDR、无 tonemap/Bloom/暗角/曝光/AA；swapchain 无 HDR10 候选（`WindowRenderContext.cs:127-154`）。世界视图虽有 `R16G16B16A16SFloat`，但裸 `BlitImage` 回拷无 tonemap（`WorldRenderFeature.cs:96,317`）。
- **原版地图光影**（更正首版「只有 19 个着色器」的遗漏）：原版无逐像素光照，全图靠 17×17 分块顶点色网格 `SectionLayer_LightingOverlay`（`AC:81602`，`LightOverlay` ShaderDef queue 3100，`2·src·dst` 混合）净乘 `天空色×(1−屋顶alpha)+glow`（8-bit 下上限约 1.5×）；阴影为几何乘暗 + stencil 防重（`SunShadow` ref12/mask4、`SunShadowFade` ref4）；加色发光集中在 42 个 Additive 着色器。`Lightshaft` 并非死代码，用于地下洞窟出口 `UndercaveMapExitLightshafts`（`Data/Core/Defs/Effects/Fleck_Visual.xml:1373-1386`）。
- **原版 `FleckSystem`**：体量极小（百级简单贴片）且引擎已合批，拦截上 GPU 属负收益且需打补丁，不做。

### Q5 导出格式与未来去拐杖成本 — 已查清

- 导出产物全 UTF-8 JSON（可选 `.json.gz`）+ 唯一二进制 `.rutex[.gz]`（LE magic `"RUTX"`=1481921874、version=1、format/w/h/mipCount/opaque，仅 BC1/BC3/BC4/BC5/BC7 块压缩，`GpuTexturePayload.cs:61-132`）；无二进制 mesh。
- 独立读取器的真实门槛在命名与语义：JSON 内组件 `type` 是 `RimUnity.ParticleSystem` 等 CLR 全名，而 RimUnity 的 `TypeResolver.Resolve` 要求全 AppDomain 同名类型恰好 1 个，否则抛异常（`RimUnity.Assets/TypeResolver.cs:33-40`）。因此未来 P4 写原生读取器时，反序列化类型必须放 `RimLumen.*` 命名空间并用独立的字符串映射表直接转入 `RimLumen.Core` 原生描述符，绝不声明同名 `RimUnity.*` 类型。

### Q6 复用比例与过渡借道策略 — 已查清

- RimUnity 五块中：e 后处理块（28 文件 / 9 处 Harmony / 8 处反射）是游戏更新后最先崩的脆弱点，**0% 复用、不提核心 PR**；c 着色器编译块 **0% 复用**（直调 Waylith）。
- **与首版方案的关键变更**：不再把 a（粒子运行时）和 d（资源加载）写成永久软依赖。`RimLumen.Core` 自己实现原生资产模型与 L1/L2 粒子引擎；仅在 P3 用 `RimLumen.Adapter.RimUnity.dll` 临时借道其 Prefab/`.rutex` 加载器与 L0 冷门模块兜底，P4 可随时用自研轻量读取器替换。

### Q7 业务模组（WotF）接入契约 — 已修订

WotF 的 `VfxService` 直接面向 `RimLumen.Core` 的原生播放接口（不再把 RimUnity 与 RimLumen 平列为对等目标，RimUnity 降级为开发迁移期的旧路径或 `Adapter` 内部数据源）：

```csharp
public interface ILumenVfxService {
    LumenCapabilities Capabilities { get; }
    ILumenEffectHandle Play(in LumenPlayRequest request); // 支持 Follow/Origin/Target/Scale/States
}
public interface ILumenEffectHandle : IDisposable {
    bool IsAlive { get; }
    void Trigger(string eventOrStateName);
    void SetParam(string name, float value);
    void Stop(bool allowFadeOut = true);
}
```

粒子分级在发射器粒度自动裁决（非全局开关）：
- **L2（默认主力，GPU Compute）**：发射、基础形状、初速/继承速度、重力/阻力、颜色与尺寸随寿命曲线、序列帧，外加 `Core` 原生力场（Curl Noise 湍流、Vortex 旋涡、点/线吸引子）、Stretched / Horizontal / Camera Billboard。
- **L1（Core 原生轻量 CPU + GPU Instancing）**：粒子数极少或需主线程逐粒交互的简单发射器。
- **L0（仅 `Adapter.RimUnity` 在场时的兜底）**：预制体启用了世界/平面碰撞、子发射器、复杂 Ribbon 拖尾等暂未进 L2 的 Shuriken 模块时，单个发射器透明回退。

### Q8 零差判据、生态并存与命令行自动化 — 已查清

1. **生态模组代码级核实结论**（`.scratch/findings-rimiris.md`、`.scratch/findings-lighting-mods.md`）：
   - **RimIris Beta**（`3796735623`）：不占 `LayerPassRouter`，不改 `CompositePass.Source` 与场景深度；其 `GradePass` 在 ImGui 前用 `BlendState.Opaque` 整屏盖写 swapchain。RimLumen 靠动态顺位守卫排在它之后即可正常上屏，且法术亮度不被其黑夜压暗吞没。它无渲染管线级外部扩展口，不做对接 API。
   - **Raycast Lighting**（`3828893328`）：CPU shadowcast，混合为 `DstColor/SrcColor`，不命中加色重画规则；不改 `GlowGrid.accumulatedGlow`；会立刻拾取 `RegisterCustomGlower` 注册的法术随身点光并算出软阴影（默认每帧 8 盏预算）。
   - **Celestial Lighting**（`3771657885`）：昼夜改 `SkyTarget` + 叠加层；极光/雪盲/云底透光用原版 `MoteGlow`（加色），若玩家手动开启 RimLumen 的「原版发光体增强」会被一并加光晕（已知局限，该开关默认关闭）。
2. **命令行与自动化入口（原 T8 已通过读 `AC` 查清）**：
   - `-quicktest` 由 `QuickStarter.CheckQuickStart`（`AC:53153-53162`）直接从 Entry 切入 `"Play"` 场景；`-savedatafolder`（`AC:210728`）可隔离测试配置与存档目录；引擎层支持 `-NoWindow`（Windows 下隐藏窗口但完整渲染，`EngineApplication.cs:511`）、`-ScreenshotAfterBoot <path>`（第 120 帧）、`-QuitAfterBoot`（第 180 帧）。
   - 原版无「读指定存档 + 跑指定 tick + 抓不含 UI 的像素」内建参数，P1 在 `RimLumen.Core` 内部提供一条开发期确定性帧读回自检命令即可。
3. **验证判据**：
   - **判据 1（默认零差）**：同一固定场景帧，安装 `RimLumen.Core`（无活动特效、可选增强默认关）vs 未安装，读回像素 `max|Δ| == 0`，且 `MapMain` draw 计数相等。
   - **判据 2（计算与同步自检）**：启动期 `DispatchNow` 往返自检恒等；开启 `-EnableVulkanValidation` 跑满 L2 粒子发射与深度共享渲染，日志 0 条 `Validation` 错误。

---

## 3. 实现硬约束（代码已证实的坑）

1. **重画原版加色项必须传 `snapshot.PropertyArena`**：`MeshDrawSubmission.TryMakeDrawItem`（`MeshDrawSubmission.cs:252-259`）内部传的是 capture 后已被清空的 `CurrentArena`，会静默丢失逐特效颜色与透明度；必须仿照 `CameraCaptureFeature.cs:58-76` 直接调 `DrawItemMaker.TryMake(..., snapshot.PropertyArena, ...)`。
2. **禁止跨 `MeshBatchRenderer` 复用 `FinalDrawItem`**：`FinalDrawItem.cachedPipeline` 只按所属 renderer 的 `pipelineVersion` 判缓存（`MeshBatchRenderer.cs:717-721`），复用会串用对方 render pass 的 Vulkan pipeline；重生成的 item 放入 `PersistentDraws` 管理归还（`MeshDrawFrame.cs:71-87`）。
3. **只读共享原版深度的 RenderPass 声明**：必须自建 `LoadOp.Load`（depth & stencil）且 `InitialLayout = DepthStencilAttachmentOptimal`；禁止调用引擎的 `PostFxPassHelpers.CreateColorRenderPass`（它硬编码 `Clear` 深度，`:48-53`）。
4. **合成步骤顺位守卫**：每帧 `CaptureFrame` 检查 `FrameRenderer.Features` 中自身是否紧邻 `ImGuiRenderFeature` 之前；若被延后挂载的模组（如 RimIris 在首次 `CameraDriver.Update` 时插入）挤到前面，立即 `RemoveFeature` + `InsertFeature` 归位。
5. **着色器识别键**：一律读 `Binding.Shader.Key`（即 `ShaderDef.defName`），禁止读 `MaterialBinding.DebugName`（`MatLoader.LoadMat` 会将其统一覆写为 `"MatLoader.LoadMat"`，`AC:214231`）。

---

## 4. 必须真机实测清单（源码不可判定项）

| # | 实测项 | 源码读不出的原因 | 所在阶段 |
| --- | --- | --- | --- |
| T1 | 自录 compute→vertex/indirect `VkBufferMemoryBarrier` 是否彻底消除驱动层读写冒险 | GPU 驱动调度行为；需 `-EnableVulkanValidation` 红绿对照 | P0（必过） |
| T2 | 自有 `RGBA16F` pass 只读挂载原版 `D32SFloatS8UInt` 深度缓冲（及与 RimUnity 地图 HDR 同开时）验证层是否零报错 | 运行期跨 pass 附件与 layout 组合受驱动校验 | P0（必过） |
| T3 | 确定性像素读回路线（`B8G8R8A8` 场景/交换链 → copy 转 `RGBA8` → `TryReadPixels`）在当前驱动上是否畅通 | `CameraTextureBaker.cs:178` 拒收 BGRA 是代码事实，中转 copy 兼容性需实机跑一次 | P1 |
| T4 | 自有 GLSL/HLSL 着色器（含 SSBO 顶点拉取、软粒子线性深度、`#include`）经 Slang 编译的实际通过情况 | Slang 编译器方言细节 | P0 / P1 |
| T5 | 全分辨率 `RGBA16F` + Bloom 金字塔的显存占用与帧耗，以及 L2 10k~100k 粒子的帧耗基线 | 真实硬件性能数值（量性能时关闭验证层） | P1 / P2 |
| T6 | （可选增强开启时）重画加色 draw 的逐实例属性还原度、双 renderer 首帧编译卡顿、2 个带 stencil 发光着色器的遮罩一致性，以及与 RimIris 同开的双重泛光观感 | 运行期实际资产路径与主观观感 | P3 之后按需 |

*(原 T7 `.rutex` 样本留待 P4 自研原生读取器时验证；原 T8 无窗口自动化已由 `AC` 源码查清并移出本表。)*

---

## 5. 已拍板决策汇总（2026-11-13 定稿）

| # | 决策项 | 定稿结论 |
| --- | --- | --- |
| D1 | 与 RimUnity 的架构关系 | **原生独立内核 + 过渡期可拔除借道**：`RimLumen.Core` 零 RimUnity 依赖；`RimLumen.Adapter.RimUnity` 仅作过渡导入桥接，随时可由 P4 原生读取器替换 |
| D2 | 绘制分流机制 | **`LayerPassRouter.RegisterLayerMask` 路由到自有 `passId`** + L2 自有 Instanced 绘制，0 Harmony |
| D3 | HDR 画布格式 | **`R16G16B16A16SFloat`**（保留 Alpha 供混合合成） |
| D4 | 上屏合成位置 | **Swapchain RenderPass 内、`ImGui` 前最后一位 `RecordInline`**（配顺位守卫，不写回 Scene RT） |
| D5 | 默认画面影响与范围边界 | **纯基础框架层**：默认装上 0 像素改动；全局调色/雾/黑夜/逐像素全局光不做（让渡给 RimIris / Raycast / Celestial）；「原版加色发光体增强」做成默认关闭的可选开关 |
| D6 | 零差验收线 | **默认空载状态逐像素 `max|Δ| == 0`**（对比未安装时） |
| D7 | 外部 PR 与对外 C# API | **均不做**：核心 100% 走引擎公开接口，不向 RimUnity 提阻塞性 PR，也不为外部画质模组开设 C# 渲染插槽 |
| D8 | GPU 粒子范围与分级 | **本阶段一步到位落地 L2（GPU Compute 常用子集 + 旋涡/湍流/吸引子力场）+ L1（Core 轻量 CPU）**；冷门 Shuriken 模块在过渡期回退 L0 |
| D9 | Vulkan Validation Layer | **日常自测必开 `-EnableVulkanValidation`**；测帧耗基线时关闭 |
| D10 | WotF 接入时机 | **等 P1（HDR 主链 + 默认零差）与 P2（原生粒子引擎）验收通过后，在 P3 挂载 Adapter 并接入 WotF** |

---

## 6. 里程碑切分与验收标准

| 阶段 | 交付物（哪些程序集） | 核心内容 | 验收标准 |
| --- | --- | --- | --- |
| **P0 真机探针关卡** | 最小探针（仅 `RimLumen.Core` 原型） | 验证 T1（Compute→Vertex Barrier）、T2（原版深度只读共享）、T4（Slang 编译 SSBO + 软粒子着色器） | `-EnableVulkanValidation` 日志 0 报错；去掉 barrier 能观察到预期告警或对照证据 |
| **P1 原生 HDR 渲染主链** | `RimLumen.Core.dll`（不加载 RimUnity） | 帧图挂载 + 顺位守卫、`RGBA16F` 目标、深度遮挡与软粒子、多级 HDR Bloom、局部 Tonemap、热浪扭曲采样、空载短路与像素读回自检 | ① 空载默认 `max|Δ| == 0` 且 draw 数不变；② 原生测试 Mesh/贴图呈现 >1.0 高光层次、Bloom 与墙根软粒子羽化；③ 与 RimUnity / RimIris 同开不被覆盖 |
| **P2 原生粒子与点光引擎** | `RimLumen.Core.dll`（仍不依赖 RimUnity） | 原生 `EffectGraph` 节点树（含 2.5D 地面层/高度层）、L2 GPU Compute 粒子与力场（Curl/Vortex/Attractor）、L1 轻量 CPU 粒子、高频 `RegisterCustomGlower` 随身点光 | 同屏 10k~50k 粒子下主线程耗时与帧耗达标、验证层 0 报错；运动火球丝滑照亮黑夜地面纹理 |
| **P3 过渡桥接与 WotF 交付** | `RimLumen.Adapter.RimUnity.dll` +（经授权后）WotF 接入 | 解析 Unity 导出的法术预制体映射至 `Core`（L2/L1/L0 自动分流）；可选补上默认关闭的「原版加色发光体增强」开关；接入 WotF 落雷与火龙卷 | WotF 现有导出预制体直接在 RimLumen 管线播放出 HDR 光晕、软粒子与 GPU 微粒；拔掉 Adapter 时 `Core` 自身不报错 |
| **P4 去拐杖：原生轻量直读器（后续可选）** | `RimLumen.Core` 内置或独立 Reader + 极简 Unity 导出脚本 | 直接读取 JSON 层级/粒子参数与 `.rutex` 压缩贴图，彻底移除对 RimUnity 运行时的借道 | 不安装 RimUnity 的情况下独立加载并播放 Unity 导出的特效资产 |

---

## 7. 证据文件索引

- `.scratch/findings-engine-api.md`：Waylith public API、storage buffer 约定、§8 自录 barrier 可行性与时序。
- `.scratch/findings-shader.md`：Slang 编译路径、布局约束与着色器能力矩阵。
- `.scratch/findings-export-format.md`：`catalog.json` / Prefab JSON / `.rutex` 二进制头格式与 `TypeResolver` 约束。
- `.scratch/findings-rimunity-surface.md`：RimUnity 公开面、18 处 Harmony / 51 处反射清单与五块体积量化。
- `.scratch/findings-vanilla.md`、`findings-vanilla-lighting.md`：原版帧结构、后处理、166 个地图着色器、光照网格与 CLI 开关。
- `.scratch/findings-frame-order.md`：Swapchain 三段录制顺序、RimUnity 四分支丢弃 Scene RT 证明（§7）。
- `.scratch/findings-emissive-redraw.md`、`findings-auto-compat-data.md`：加色混合重画、`PropertyArena` 陷阱、着色器覆盖优先级与 `GlowGrid` 接口。
- `.scratch/findings-similar-projects.md`、`findings-rimiris.md`、`findings-lighting-mods.md`：1.7 同类模组全景，以及 RimIris、Raycast Lighting、Celestial Lighting 反编译核实结论。
- `.scratch/decompiled/graphify-out/`：反编译源码本地调用图（22,018 节点）。
