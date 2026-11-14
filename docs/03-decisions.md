# D1–D10 决策定稿记录（2026-11-13 已拍板）

本文档记录 `docs/02-plan.md` 最终拍板结果，所有未决项均已关闭。详细架构与约束以 `docs/02-plan.md` 第 1–6 节为准。

| # | 决策 | 定稿结果 | 核心依据 |
| --- | --- | --- | --- |
| D1 | 与 RimUnity 的架构关系 | **原生独立内核 + 过渡期可拔除借道** | `RimLumen.Core.dll` 零外部模组依赖、自持原生资产模型与 L1/L2 粒子引擎；`RimLumen.Adapter.RimUnity.dll` 仅在 P3 临时借道读取 Unity 导出产物，后续可由 P4 原生轻量读取器无感替换 |
| D2 | 绘制路由机制 | **`LayerPassRouter.RegisterLayerMask` + L2 SSBO 直绘** | 全程走 Waylith 公开接口，0 处 Harmony 补丁 |
| D3 | HDR 画布格式 | **`R16G16B16A16SFloat`** | 保留 Alpha 通道供尾端混合合成 |
| D4 | 上屏合成位置 | **Swapchain RenderPass 内、`ImGui` 前最后一位 `RecordInline`** | 规避 RimUnity 开启地图 HDR 时丢弃 Scene RT 写回内容，并配动态顺位守卫防止被 RimIris 整屏不透明覆盖吞没 |
| D5 | 默认画面影响与范围边界 | **装上即生效框架（原版发光增强默认开启）** | 全局调色、雾感、黑夜压暗与逐像素全局光照让渡给 RimIris / Raycast / Celestial；原版加色发光体增强默认开启（可在 `ModSettings` 关闭回退纯静默框架态）；预留独立 3D 骨骼蒙皮实体扩展，不碰原版 `PawnRenderTree` |
| D6 | 零差验收线 | **空载静默态逐像素 `max\|Δ\| == 0`** | 关闭原版发光增强且无活动特效时，与未安装对比像素完全一致，且 `MapMain` draw 数不变 |
| D7 | 外部 PR 与对外 C# API | **均不做** | 核心能力 100% 走引擎公开接口，外部画质模组无渲染管线插槽对接面 |
| D8 | GPU 粒子范围与分级 | **一步到位落地 L2（GPU Compute + 力场）与 L1（Core 轻量 CPU）** | 常用子集与旋涡/湍流/吸引子力场上 GPU；过渡期冷门 Shuriken 模块单发射器回退 L0 |
| D9 | 显卡驱动体检 | **日常自测必开 `-EnableVulkanValidation`** | 抓捕跨 pass 深度共享与 compute→vertex 同步冒险；量帧耗基线时关闭 |
| D10 | WotF 接入时机 | **P1 + P2 独立验收通过后在 P3 接入** | 先建稳原生渲染与粒子底座，再挂过渡桥接交付法术 |
