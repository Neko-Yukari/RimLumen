# 已核实事实（2026-11-12）

只列有证据的。每条后面是证据锚点。「推断」明确标注。

## 1. 引擎

| 事实 | 证据 |
| --- | --- |
| 1.7 用 Waylith 引擎，不是 Unity | `RimWorld/Licenses.txt:5` 原文 |
| 版本 1.7.5005 rev1082，Steam beta `unstable` | `Version.txt`、`appmanifest_294100.acf` |
| 游戏根目录没有 Unity 播放器或 `*_Data/Managed` | 目录实测；根目录是 `RimWorld.exe` + `coreclr.dll` + `Waylith*.dll` + `UnityStubs.dll` |
| 引擎有计算管线和 MRT 通道 | `Waylith.dll` 字符串：`ComputePipeline`、`ComputeRenderPass`、`ComputeRenderFeature`、`CreateMrtColorRenderPass`、`ComputeDispatchQueue` |
| 19 份引擎着色器源码随游戏分发 | `RimWorld/Rendering/Shaders/`：`SceneDefault.vert.glsl`、`SceneDefault.frag.glsl`、`PostFxComposite.frag.glsl`、`EncodeBC7Fast.comp.hlsl`、`MipmapDownsample.comp.hlsl`、`MipmapKaiser.comp.hlsl`、`GpuProbe.comp.hlsl` 等 |
| Slang 编译器随游戏分发 | 根目录 `slang.dll`、`slang-compiler.dll`、`slang-glslang.dll`、`Brutal.Slang.dll` |
| Unity API 兼容层存在 | `UnityStubs.dll` 字符串：`CommandBuffer`、`ComputeShader`、`DispatchCompute`、`allowHDR`、`AddCommandBuffer`、`ParticleSystemAnimationMode` 等 |

**未验证**：`ComputeShader` / `DispatchCompute` 在 Waylith 上是真实现还是空桩（只有字符串证据）。**这是代码可查项，用 IL 读，不要跑探针。**

## 2. RimUnity

| 事实 | 证据 |
| --- | --- |
| 包名 `Chezhou.RimUnity`，MIT，作者 chezhou9413 | workshop `3819570122` 的 `About/About.xml` 与 `LICENSE` |
| 游戏内版本 0.1.23；制作侧 UPM 包是 0.1.22 | `Docs/zh-CN/Release-0.1.23.md`；`WotF-Vfx/Packages/manifest.json` |
| 有文档体系 | `Docs/en`、`Docs/zh-CN`：`Status.md`、`ColorAndBloom.md`、`RenderDiagnostics.md`、`Workflow.md`、`CustomShaderConversion.md`、`ParticleExpansion.md` |

能力边界（证据在导出器 `Editor/Export/*.cs` 与 `RimUnity.dll` 的 IL）：

- **粒子模块**基本齐全：main / emission / shape / velocity / force / limit velocity / inherit velocity / color·size·rotation over lifetime & by speed / noise / collision（plane+world）/ trigger / sub-emitters / texture sheet animation / trails（含 Ribbon）/ lights / custom data / external forces / lifetime by emitter speed。字段走白名单，白名单外抛 `NotSupportedException`（`ParticleWriter.cs`）。
- **渲染器**只有 6 种模式：Billboard / Stretch / HorizontalBillboard / VerticalBillboard / Mesh / None，四种 billboard 都有真实实现分支（IL）。`sortingFudge`、阴影、GPU instancing 字段不在白名单，**写了会被静默丢弃**（推断，未逐项实测）。
- **点光**只接 Point，无阴影无 Cookie（`ComponentWriter.cs:150` 抛异常），落 GlowGrid，10 Hz、≤40 格（`ParticleExpansion.md:12,24`）。
- **不支持**：Shader Graph（`RimUnityWorkflow.cs:82` 导出期报错）、VFX Graph、Timeline、GPU Instancing、Jobs（IL 无类型）；GrabPass、屏幕深度软粒子、光照/阴影 pass（`CustomShaderConversion.md`）。
- **Animator**：基础状态机 / 1D 混合 / 事件；不支持 Humanoid、IK、嵌套、复杂过渡（`Status.md:27`）。
- **Bloom**：全局一套参数（`threshold` / `intensity` / `softKnee` / `scatter`），默认 threshold 0.8、intensity 0.6；层 0–31，内容模组接管某层（WotF 用 29，引用计数 `VfxBloomLayers.cs:14,38`）；**没有每层不同强度**。地图 HDR 默认关闭（`ColorAndBloom.md:15-21`）。
- **HDR**：贴图与浮点颜色保留 >1；地图 HDR 开启后按线性 HDR 混合。

## 3. 游戏相机与 2.5D

- 正交投影（`Verse/CameraDriver.cs:441` `orthographicSize = RootSize`），高度 15–65。
- 视线 −Y（俯视），屏幕上方 = +Z。证据：`PawnCacheCameraManager.cs:29` 用 `Quaternion.Euler(90,0,0)` 复刻地图视角；地图网格全建在 XZ 平面（`MeshMakerPlanes.cs:50`）。**推断**（未读场景资产里的相机旋转），两条独立源码一致。
- 正交俯视下世界高度不产生屏幕位移，所以「竖起来」只能靠画。原版闪电是教科书：`LightningBoltMeshMaker` 把折线平铺在 XZ 地面，从落点向北 200 格。

## 4. WotF 现有流水线的问题

- 预制体是**产物**：`WotfSpellVfxBuild.cs:18` 读 XML、`:71` `SaveAsPrefabAsset` 覆盖；贴图 `SpellMask()`（`:397` 起）每次重生成。
- 四个入口会覆盖手改：预览菜单（`WotfVfxPreview.cs:107`）、Filmstrip、Workbench 改参数（`WotfVfxWorkbench.cs:129`）、Workbench 导出（`:246-251`）。
- 不重建直接导出已可行但没有菜单：`WotfVfxBuild.ExportTestPrefab`（`WotfVfxBuild.cs:112-117`）。
- 落雷火花朝下：`WotfSpellVfxBuild.cs:125-128` 半球绕 X −90°，轴朝 −Z（屏幕下方），且火花在雷柱根部爆开。
- 素材全是程序生成，没有序列帧。

## 5. 为什么网页原型更好看

`Moddev/FireTornado/vortex.js`：1730 个按速度拉长的加色粒子 + 64 步体积 raymarch + RGBA16F 目标 + 全屏泛光（提取 `max(rgb)>0.62`、合成 `scene+bloom*1.65`）+ 曝光 `1-exp(-c*1.4)` + gamma 0.88 + 暗角 + 颗粒，背景暗。

游戏内只有：单套全局泛光、地图 HDR 默认关、亮地面。**差距在成像链路，不在制作工具。**

## 6. 许可

- 模组开源，素材可用 CC0 / CC-BY / CC-BY-SA / GPL，引用处标注来源与许可。
- Unity Asset Store 的 EULA 不允许把原始资源文件随模组分发，无论模组是否开源。
