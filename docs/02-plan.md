# RimLumen 阶段策划（02-plan）

日期 2026-11-12。证据锚点相对 `.scratch/decompiled/`（ilspycmd 9.1.0 反编译 1.7.5005 与 RimUnity 0.1.23），`WS/` = workshop 3819570122，`SH/` = 游戏 `Rendering/Shaders/`。逐条明细在 `.scratch/findings-*.md`（engine-api / shader / export-format / rimunity-surface / vanilla）与 `.scratch/notes-il.md`，本文件只放结论、判据与决策。

## 0. 一页结论

1. 不改 RimUnity、不 patch Waylith 私有结构，内容模组就能插入自定义渲染 feature / pass / 渲染目标 / compute：`EngineApplication.Instance`、`FrameRenderer.InsertFeature`、`IRenderFeature`、`IRenderPass`、`PassAttachment`、`MeshBatchRenderer`、`PostEffectPass`、`RenderTexture`、`ComputeDispatchQueue` 全是 public。RimUnity 自己也只有 public API + Harmony 可用（`Waylith` 的 `InternalsVisibleTo` 不含 RimUnity）。
2. 把地图 draw 导到自己的 HDR 目标，有一条零 patch 路线：`LayerPassRouter.RegisterLayerMask(mask, 自有 passId)`（public，`Waylith/Waylith.Rendering/LayerPassRouter.cs:11`）→ 自有 pass 里 `MeshBatchRenderer.RecordPass(ctx, frame, 自有 passId)`。RimUnity 用的是 Harmony Prefix 替换 `MapMainPass.RecordSlice`（`RimUnity/RimUnity.Rendering/HdrMapHooks.cs:59-91`），是「游戏更新后最先坏」的那种做法。
3. `ComputeShader`/`CommandBuffer` 在 UnityStubs 全是桩；真计算路径 `ComputeKernelCache.Load → ComputeDispatch.Rent → DispatchNow → GpuComputeBuffer.Read` 全 public。引擎**没有** compute→vertex/indirect 的同步 barrier；mod 可在自己的 pre-backbuffer pass 用 public `PipelineBarrier` 自录（现成样板 `GpuComputeBuffer.RecordUploadBarrier`）。驱动层正确性必须实测。
4. 原版后处理只有一行饱和度混合（`SH/PostFX/PostFxComposite.frag.glsl:14-16`）；地图无 HDR 中间目标，swapchain `B8G8R8A8UNorm` 无 HDR10 候选。HDR/色调映射/暗角全部要自建。
5. 导出产物可独立读：全 UTF-8 JSON（可选 gz）+ 仅纹理 `.rutex` 二进制（头格式已拿到）；但 prefab 的组件/成员名就是 RimUnity 的 CLR 反射名，不复用 RimUnity 粒子运行时就无法解释。
6. 推荐：**RimLumen 独立实现渲染基建，RimUnity 作为软依赖只复用粒子运行时与资源加载**；不向 RimUnity 后处理块提 PR。
7. 可验证判据：无特效时场景 RT 逐像素零差；启动期 compute 自检 kernel 验证 encode/decode 往返；`-EnableVulkanValidation` 零报错。

## 1. 八个问题

### Q1 扩展点 — 已查清

| 需要 | 公开入口 | 锚点 |
| --- | --- | --- |
| 拿到引擎 | `EngineApplication.Instance`（ctor 赋值永不清空）、`.FrameRenderer`、`.GraphicsDevice`、`.RenderContext` | `Waylith/Waylith/EngineApplication.cs:130,142,158,160` |
| 插 feature | `FrameRenderer.AddFeature / InsertFeature(index) / RemoveFeature / Features` | `Waylith/Waylith.Rendering/FrameRenderer.cs:56,64,96,40` |
| 录制顺序 | 全部 `PreBackbuffer==true` pass 按 feature 下标 → swapchain pass 内非 post pass → `IPostBackbufferPass` | `FrameRenderer.cs:282-368` |
| 原版 feature 下标 | 0 Compute → 1 SceneTargets(非 feature) → World → Map → CameraCapture → PostFx(Composite) → ImGui → ScreenCapture | `RimWorld/RimWorldWaylith/Bootstrap.cs:284-321` |
| 自定义 pass id | `RenderPassRegistry.Register(name) → byte`；`LayerPassRouter.RegisterLayerMask(mask, id)`，8 槽、首匹配，原版占 3 槽 | `RenderPassRegistry.cs:13`；`LayerPassRouter.cs:5,11-34`；`Bootstrap.cs:359-362` |
| draw 过滤 | `RecordPass` 只画 `PassId==0 || PassId==passId` | `Waylith/Waylith.Rendering/MeshBatchRenderer.cs:606,632` |
| 拿本帧 draw | `MeshDrawSubmission.CaptureFlushedSnapshot`（帧内缓存，World/Map 共用）+ `DrawItemMaker.TryMake` + `MeshDrawFrame` 全 public | `MeshDrawSubmission.cs:430,140`；`DrawItemFactory.cs:39`；`MeshDrawFrame.cs:43` |
| 自有渲染目标 | `new RenderTexture(w,h,VkFormat color, VkFormat depth, …)` 任意格式；`PassAttachment` + `RenderTextureMaker.Attachment` | `RenderTexture.cs:105`；`PassAttachment.cs`；`RenderTextureMaker.cs:61` |
| 自有 MeshBatchRenderer | ctor public | `MeshBatchRenderer.cs:478` |
| 全屏 pass | `PostEffectPass`（非 sealed）`Record`/`RecordInline`；uniform ≤256B、push ≤128B、绑定全 CombinedImageSampler | `Waylith/Waylith.Rendering.PostFx/PostEffectPass.cs:96,141,152,18,16` |
| 原版场景 RT | `CompositePass.Source`（public get/set, `Func<int,RenderTexture?>`） | `CompositePass.cs:11` |
| 深度共享 | 同一 `RenderTexture.DepthView` 放进多个 attachment | `SceneColorTargets.cs:190-211` |

RimUnity 侧公开面（`findings-rimunity-surface.md` §2）：全屏链 `EfxClip`+`EfxPlayer.Play`+`EfxFrameBuilder.AddPass`（Bloom 之后、UI 之前）；Bloom 只有 `SetLayerEnabled/ReleaseLayer/EffectiveLayerMask`，无按层参数；无 render feature 注册入口、无 raw GLSL。`ShaderDirectory` 是 internal（`RimUnityMod.cs:18`）。

并存可行性：RimUnity transpiler 包裹所有 feature 的 `CaptureFrame`/`Passes`，但对非 Map/PostFx feature 是透传（`RimUnity/RimUnity.Rendering/BloomHooks.cs:209-222,258-267`）；RimUnity 地图 HDR 只捕获 `PassId==0||==MapMain` 的 draw（`HdrDrawFrame.cs:238-245`），自有 passId 的 draw 不会被它的 Bloom/HDR 吃掉。

### Q2 计算着色器 — 已查清（同步语义需实测）

桩：`UnityStubs/UnityEngine/ComputeShader.cs:7-16`（`Dispatch` 只 log，`FindKernel` 返回 -1）、`CommandBuffer.cs:49`、`GraphicsBuffer.cs` 整类。

真路径（全 public）：

| 步 | API | 锚点 |
| --- | --- | --- |
| 载入 | `ComputeKernelCache.Load(path, entry, ReadOnlySpan<ComputeBinding>, pushSize, Glsl/Hlsl)` | `Waylith/Waylith.Rendering.Compute/ComputeKernelCache.cs:27` |
| 绑定 | `ComputeDispatch.Rent` + `SetStorageBuffer/SetStorageImage/SetSampledTexture/SetPushConstants` | `ComputeDispatch.cs:119,70,58,43,75` |
| 缓冲 | `GpuComputeBuffer.Create / Upload<T> / Read(Span<byte>)` | `GpuComputeBuffer.cs:78,129,154` |
| 立即 | `ComputeDispatchQueue.DispatchNow`（fence 等待） | `ComputeDispatchQueue.cs:148-194` |
| 帧内 | `ComputeDispatchQueue.Enqueue(dispatch, owner, key)` → index 0 的 `ComputeRenderPass` 录制 | `:98`；`ComputeRenderFeature.cs:26-30` |

约束：绑定类型只有 `CombinedImageSampler/StorageImage/StorageBuffer/SampledImage`，无 UBO，push ≤128B，set 0、≤16 绑定；入口固定 `main`（`ComputeKernelCache.cs:163`）；`ComputeDispatchQueue.Configure` 在 `EngineApplication.cs:390` 已调用。引擎自己的用法样板：`Waylith/Waylith.Hardware/GpuProbe.cs:73-126`。

同步缺口：`RecordDispatch` 出口 barrier 只盖 storage image，`src=Compute, dst=Fragment|Compute`；storage buffer 不在任何 barrier 内；`RecordFrame` 全文 0 处 `PipelineBarrier`（`findings-engine-api.md` §1）。自救路线：mod 的 pre-backbuffer pass 插在 index 1（Compute 之后、Map 之前），在 `ctx.CommandBuffer`（同一条帧命令缓冲，`ComputeRenderPass.cs:54-57`）录 `ComputeShader → VertexShader|DrawIndirect` 的 buffer barrier；`PipelineBarrier` 为 public（`Brutal.VulkanApi`，RimUnity 直接调用 `HdrSceneCopy.cs:13`），样板 `GpuComputeBuffer.RecordUploadBarrier`（`GpuComputeBuffer.cs:119-127`）。时序成立：`CaptureFrame` 主线程按 feature 顺序、`Record` 渲染线程同序（`findings-engine-api.md` §8.3）。

**必须实测**：自录 barrier 在真实驱动上是否消除 hazard —— 这是驱动行为，源码读不出；工具是 `-EnableVulkanValidation`（`Waylith/Waylith.Graphics/GraphicsDevice.cs:725`）。

### Q3 着色器语言 — 已查清

- 后端只有 Slang（`slang_createGlobalSession2`），输入只认 `Glsl`/`Hlsl` 两枚举、三阶段（无几何）；`.slang` 源不接受；`TryLoadOverride` 永远 false，不能热替换 SPIR-V（`findings-shader.md` §1）。
- `ShaderSourceLoader.RegisterShaderSourceDirectory / SetOverrideShaderDirectories` public static，`#include "..."` 递归展开 —— mod 可注册自己的着色器目录给 `ComputeKernelCache.Load` 和 `PostEffectPass(fragmentShaderPath)`。
- 自定义材质着色器（经 RimUnity `CustomShaderLoader`）硬约束：set0 帧 UBO、set1 空、set2 材质（b0 std140 ≤512B、纹理 ≤16、storage buffer ≤8）、push 64B 仅顶点。
- 能力矩阵（内容模组 / 引擎层）：MRT 否/是（`OUTLINE_MRT` 仅引擎变体注入，且 `CreateMrtColorRenderPass` 全树无调用者）；Stencil 是/是；Blend 是/是；StructuredBuffer 是/是；storage image 否/是（compute）；GrabPass 运行时有但导出器文档称不支持（`CustomShaderConversion.md:27`，冲突待实测）；Compute 经路径加载 是/是；关键字变体 否/是；几何 否/否；多 pass 是/是。
- RimLumen 自有 pass 不经 `CustomShaderLoader`，直接 `new Waylith.Rendering.Shader(...)` + `RuntimeShaderCompiler.Compile`（`CustomShaderLoader.cs:111-112` 就是这么做的），因此上面「内容模组否」的项对 RimLumen 自有管线不构成限制，只对「Unity 导出的材质」构成限制。

### Q4 原版可复用件 — 已查清

- `PostFxComposite.frag.glsl`：`outColor = clamp(mix(vec3(lum), rgb, saturation), 0, 1)`，`lum = dot(rgb, (.22,.707,.071))`；文件头注明无 AA（`:3,14-16`）。`CompositePass.Saturation` 每帧来自 `ColorCorrectionCurves.saturation`，该类是只剩 `saturation` 的壳（`Bootstrap.cs:135-138`；`UnityStubs/UnityStandardAssets.ImageEffects/ColorCorrectionCurves.cs:37`）。
- `SceneDefault.frag.glsl`：`tex * _Color * vColor`，alpha≤0.001 discard，无光照无 gamma（`:23-28`）。
- 其余 16 个全是 BC 编码 / mipmap / NV12 / GPU 探针，与成像链无关。
- 格式：`EngineApplicationOptions.cs:19,21,27` 默认 `B8G8R8A8UNorm / D32SFloatS8UInt / 2 帧`；`Program.cs:43-58` 未覆盖；swapchain 只匹配 `ColorFormat + SRGBNonlinearKHR`，无 HDR10（`WindowRenderContext.cs:127-154`）。
- 地图无 HDR 中间目标；世界视图有 `R16G16B16A16SFloat` 但裸 `BlitImage` 回拷无 tonemap（`WorldRenderFeature.cs:317,96`；`SceneColorTargets.cs:117-135`）。`Camera.allowHDR` 地图路径不读（`MapRenderFeature` 无引用）。
- 结论：HDR 目标、色调映射、暗角、曝光、Bloom 原版一概没有；可复用的只有 `PostEffectPass` 这个全屏 pass 骨架和 `SceneColorTargets` 的 attachment 组织方式。

### Q5 导出格式 — 已查清

- 运行时数据：`catalog.json`（`Manifest/AssetRecord`，`formatVersion` 接受 1/2，`AssetCatalog.cs:39-43`）、prefab JSON（`PrefabData/NodeData/ComponentData/MemberData/ValueData`，`kind ∈ null/object/asset/string/integer/number/specialNumber/bool/vector/array/data`，`ValueReader.cs:24-83`）、资源 JSON（`ResourceData`）、`s_*.json` = `ShaderData`（运行时不读 `shader.json`/`descriptorFile`）。全 `System.Text.Json` + `IncludeFields`，可选 `.json.gz`。
- 二进制只有 `.rutex[.gz]`：`"RUTX"` LE magic、version=1、format/w/h/mipCount/opaque、每 mip `Int32 len + 数据`、必须 EOF；只接受块压缩 10/12/25/26/27（`RimUnity.Core/RimUnity.IO/GpuTexturePayload.cs:61-132`）。无二进制 mesh。
- 独立读取器最小集合 7 项、语义绑定 RimUnity 运行时 8 项，见 `findings-export-format.md` §5。关键：组件 `type` 与成员 `name` 是 `RimUnity.ParticleSystem` 等类型的反射名（`ParticleSystem.cs:2130-2225`），`MinMaxCurve/MinMaxGradient` 只写非默认字段，枚举是整数 —— 不带 RimUnity 粒子运行时就要重写一份语义相同的解释器。
- 哈希（`RimUnity_Custom_<h1>_<h2>`、blobs SHA-256）在 Editor 侧，本地无源码；运行时只要求 `ShaderData.key == AssetRecord.id`。

### Q6 复用比例与 PR / 独立 — 已查清

五块量化（`findings-rimunity-surface.md` §5）：

| 块 | 文件 | 字节 | Harmony 文件 | AccessTools 文件 | 作为依赖复用 | 独立时可搬（MIT） | 对 RimLumen 的用处 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| a 粒子/动画运行时 | 73 | 357,746 | 0 | 0 | 100% | ~85%（去 Verse/HostAssets 耦合） | 直接用 |
| b 材质/颜色空间 | 17 | 59,531 | 3 | 3 | 100% | ~60%（3 处 Harmony 要重做） | 直接用 |
| c 着色器编译 | 7 | 35,451 | 0 | 0 | 0%（直接调 Waylith public） | 概念 | 不需要 |
| d 导出格式/加载 | 27 | 66,381 | 2 | 1 | 100% | ~80% | 直接用 |
| e 后处理 | 28 | 146,233 | 9 | 8 | 0%（目标不同：全局 Bloom vs 分层 HDR 链） | 0% | 只借设计（RGBA16F 乒乓、`HdrTransfer` 编解码） |

游戏更新后谁先坏（按脆弱度）：
1. RimUnity e 块：两个 transpiler 改 `FrameRenderer.CaptureFrame/RecordFrame` IL（`BloomHooks.cs:69-77`）、`MeshPipelineCache.GetOrCreatePipeline` transpiler（`ShaderDepthComparisons.cs:17`）、`MapMainPass.RecordSlice` Prefix、28 处 `AccessTools` 反射 Waylith 私有成员。IL 形状一变 → `PatchAll` 抛 → **整个 RimUnity 加载失败**，没有降级路径。
2. RimUnity b/d 块的 3+2 处 Harmony。
3. RimLumen 自有管线：只用 public 签名（上表 Q1），签名变化在编译期暴露，运行期有 `try` 降级空间。
4. 导出格式：`formatVersion` 校验，最稳。

推荐：**独立实现 + 软依赖**。理由：(1) 我们要的分层 HDR 链与 RimUnity 全局 Bloom 不是同一目标，PR 会变成重写它的 e 块；(2) e 块是 Harmony 重度，我们用 public feature 插入就能做到同样的事且更稳；(3) a/b/d 块 MIT、零 Harmony（a）或少量，作为依赖直接用，不 fork。程序集切分：`RimLumen.dll` 只引 Waylith/UnityStubs/Assembly-CSharp；`RimLumen.RimUnity.dll` 引 RimUnity.*，用 `LoadFolders.xml` 的 `IfModActive="Chezhou.RimUnity"` 条件加载，RimUnity 坏了 RimLumen 自有管线不跟着死。

PR 候选（可选，不阻塞本阶段）：公开 `RimUnityMod.ShaderDirectory`；`BloomSettings` 按层强度。都不是我们的依赖项。

### Q7 双后端并存 — 设计稿（待拍板）

现状：法术侧唯一入口 `VfxService.Play(VfxRequest)`，调用点 3 处（`Projectile_ArcaneMissiles.cs:191,211`、`RailgunCastService.cs:58`）；`VfxRequest` 只有 map/follow/origin/target/direction/length/width/chargeTicks/vfxDefName；look 参数全在 `VfxDef` 子类；WotF 对 RimUnity 的 API 面是 `AssetLoader.Load / PrefabLoader.Load / Object.Instantiate / Bloom.SetLayerEnabled / ScreenEfx`。

接口（WotF 内，方法签名级）：

```csharp
interface IVfxBackend {
    string Id { get; }                 // "rimunity" | "rimlumen"
    int Priority { get; }              // 大者优先
    bool IsAvailable { get; }          // 运行时探活（引擎实例 + 自有 feature 已注册）
    VfxCapabilities Query();           // 位域，不是版本号
    IVfxHandle Play(VfxRequest req, VfxDef def);   // Dispose 释放
}
[Flags] enum VfxCapabilities { None=0, HdrScene=1, LayeredBloom=2, Tonemap=4,
    ScreenEffect=8, ComputeParticles=16, Volumetric=32 }
interface IVfxHandle : IDisposable { bool Finished { get; } void Tick(); }
```

规则：
1. `VfxService.Play` 内部：按 `Priority` 降序取首个 `IsAvailable`；同优先级按 `Id` 字典序（跨机确定）。
2. `VfxDef` 新增 `requiredCapabilities`（位域，默认 None）；所选后端 `Query()` 不满足 → 降到下一个并 `WotfLog.Warning` 记缺哪一位；不静默。
3. 全部不可用 → 抛（与 `VfxService.cs:34-45` 既有风格一致）。
4. 现有 `VfxWorker` 作为 RimUnity 后端的实现细节保留；`MapComponent_Vfx` 改持 `IVfxHandle` 列表。法术代码、`VfxRequest`、Def XML 零改动。
5. 资源：两后端共读同一份 `1.7/Data/RimUnity` 导出；RimLumen 后端在 D1 选「软依赖」时直接用 `PrefabLoader`，选「独立」时用自有读取器（Q5）。
6. 并存冲突点（已查）：`LayerPassRouter` 8 槽（原版 3 + RimLumen 1）；`RenderPassRegistry` id 由注册分配不冲突；RimUnity Bloom 不吃自有 passId 的 draw（Q1）；两者都开地图 HDR 时的深度共享 **必须实测**。

### Q8 后处理对原版画面的影响边界 — 判据已定，读回路线需实测

设计原则：RimLumen 只在**自有 passId** 上绘制到**自有 RGBA16F 目标**，合成时以 Load 方式打开原版场景 RT（`CompositePass.Source(frame)`，`CreateColorRenderPass(..., loadOp: Load)` + `rt.GetFramebuffer(renderPass)` 全 public），用加色/alpha 混合写回。因此「无特效 ⇒ 原版像素位级不变」是结构保证，不是调参结果。

判据：
1. **零差**：同一存档同一帧，RimLumen 开（无活动特效）vs 关，场景 RT 逐像素 `max|Δ| == 0`。读回路线：(a) `ScreenCaptureBackend.Current.TryReadScreen`（swapchain，含 UI，alpha 强制 255，`ScreenCaptureFeature.cs:171-186`）；(b) `CameraTextureRenderer.Current.TryReadPixels(rt)` 只接受 `R8G8B8A8UNorm/SRGB/Undefined`（`CameraTextureBaker.cs:178`），而场景 RT 是 `B8G8R8A8UNorm` —— 需先经公开 copy API 转到自有 RGBA8 RT 再读。**哪条可行必须实测**。
2. **编解码往返**：启动期用 `DispatchNow` 跑一个 compute kernel，对 0..255 全部 8bit 值做 `encode(decode(x))`，`GpuComputeBuffer.Read` 回读断言恒等（RimUnity 的 `SceneColorTests` 做同类事但不随 workshop 分发，`findings-vanilla.md` §4.3）。
3. **验证层零报错**：`-EnableVulkanValidation` 下运行 N 帧，日志无 `Validation` 条目。
4. **draw 计数不变**：自有 feature 的 `CaptureFrame` 里统计 `PassId∈{0,MapMain}` 的 draw 数，RimLumen 开关前后相等。
5. 全局效果（tonemap/暗角/曝光）默认只作用于自有层；若用户开启全局版，判据 1 允许差异但必须由用户显式开启（D5）。

## 2. 必须实测清单（源码读不出的原因）

| # | 项 | 为什么读不出 | 工具/方法 |
| --- | --- | --- | --- |
| T1 | 自录 compute→vertex/indirect barrier 是否消除 hazard | 驱动调度行为 | `-EnableVulkanValidation` + 故意去掉 barrier 做红绿对照 |
| T2 | 场景 RT 像素读回路线（swapchain vs copy→RGBA8） | `B8G8R8A8` 被拒是代码事实，但 copy API 在该格式上的驱动支持未定 | 两条路线各试一次 |
| T3 | GrabPass：运行时有 `grabTexture` 支持，导出器文档说不支持 | 导出器源码不在本地 | 导一个带 GrabPass 的材质看 `s_*.json` 是否填 `grabTexture` |
| T4 | 两个 mod 同时开地图 HDR 的深度共享 | RimUnity `HdrDrawTarget.BindDepth` 是 internal，行为取决于运行期 attachment 组合 | 并存启动 + 验证层 |
| T5 | 自有 GLSL 经 Slang 编译的实际兼容性（include、std140、storage buffer 声明） | 编译器行为 | 启动期编译 + 日志 |
| T6 | RGBA16F 全分辨率 ×2 帧的 VRAM/帧耗 | 性能 | `GpuMemoryTracker` 分类 + 帧耗 |
| T7 | `.rutex` 真实二进制（仅 D1 选独立时） | WotF 样本没有 rutex | 导一张 ≥4KiB 纹理 |
| T8 | 无窗口自动化：`-NoWindow` 仍渲染（`EngineApplication.cs:511`），但无「指定存档/tick 截图」入口 | Assembly-CSharp 未反编译，`-quicktest` 类开关未核 | 反编译 Assembly-CSharp 一次 |

不实测、已排除：HDR10 输出（无候选格式）；`ComputeShader` 桩（已读）；原版 tonemap（不存在）。

## 3. 待拍板决策（每条附推荐）

| # | 决策 | 选项 | 推荐 |
| --- | --- | --- | --- |
| D1 | RimLumen 与 RimUnity 关系 | A 软依赖（RimLumen 自有渲染基建，粒子/加载走 RimUnity，条件程序集）／B 完全独立（fork a+d 块 ≈424 KB C#）／C 独立+自有资源格式 | **A**。B 多 30% 代码且语义要跟 RimUnity 同步；C 丢掉 Unity 制作流水线 |
| D2 | 地图 draw 接管机制 | A `LayerPassRouter` 路由到自有 passId（零 patch）／B Harmony Prefix `RecordSlice`（RimUnity 方式） | **A** |
| D3 | HDR 目标格式 | A `R16G16B16A16SFloat`／B `B10G11R11UFloat` | **A**（与 World/RimUnity 一致，T6 后可降） |
| D4 | 合成位置 | A pre-backbuffer pass 以 Load 打开原版场景 RT 加色写回／B 替换 `CompositePass` | **A**（判据 1 结构保证） |
| D5 | 全局效果默认 | A 只作用自有层，全局版用户显式开／B 全局默认开 | **A** |
| D6 | 像素判据路线 | A swapchain 截图 smoke + 场景 RT 零差硬判据／B 仅 swapchain | **A**（T2 定路线） |
| D7 | PR 策略 | A 不提／B 提 2 个小 PR（公开 ShaderDirectory、按层 Bloom 参数） | **A**，本阶段不依赖 |
| D8 | GPU 粒子纳入本阶段 | A 只建基建 + compute 自检 kernel，GPU 粒子下一阶段／B 本阶段做 | **A**，T1 通过后再开 |
| D9 | 验证层作为标配 | A 手测/CI 都带 `-EnableVulkanValidation`／B 仅调试时 | **A** |
| D10 | WotF 侧 `VfxService` 改造时机 | A RimLumen P1 零差通过后／B 同步进行 | **A**（跨仓，需单独授权） |

## 4. 阶段分解与验收

| 阶段 | 内容 | 验收证据 |
| --- | --- | --- |
| P0 实测 | T1–T6（T7/T8 按 D1/需要） | 每项红→绿命令输出与日志 |
| P1 骨架 | mod + `RimLumen.dll` 注册 feature（index 1）+ 自有 passId + RGBA16F RT + 透传合成 | 判据 1 零差、判据 3 零报错、判据 4 计数相等 |
| P2 计算 | compute 自检 kernel + 自录 barrier + `GpuComputeBuffer` 往返 | 判据 2 恒等；T1 红绿对照 |
| P3 后处理 | 自有层：分层 Bloom（阈值/强度按层）、tonemap、暗角、曝光；全屏 `PostEffectPass` 链 | 每个效果开/关的截图对 + 判据 1 在效果关时仍零差 |
| P4 能力查询 | `IVfxBackend` 落地（RimLumen 侧实现 + 条件程序集）；WotF 侧改造另行授权 | 日志打印选中后端与能力位 |
| P5 读取器 | 仅 D1=B 时 | `.rutex`/prefab/material 解析单测 |

## 5. 证据文件索引

- `.scratch/findings-engine-api.md`：Q1/Q2 的 Waylith public 面、barrier、storage buffer 约定、MRT、PostEffectPass、§8 自录 barrier。
- `.scratch/findings-shader.md`：Q3 编译路径、注解、布局约束、能力矩阵。
- `.scratch/findings-export-format.md`：Q5 全 schema、`.rutex` 头、两份清单。
- `.scratch/findings-rimunity-surface.md`：Q1 RimUnity 公开面、Q6 Harmony/反射全清单与五块量化。
- `.scratch/findings-vanilla.md`：Q4 原版帧结构与着色器、Q8 读回工具与开关。
- `.scratch/notes-il.md`、`.scratch/plan-outline.md`：主会话自持笔记。
- `.scratch/decompiled/graphify-out/`：反编译源码调用图（22,018 节点），`graphify_hybrid__local__*` 可查。
