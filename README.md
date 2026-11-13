# RimLumen

面向 RimWorld 1.7（Waylith / Vulkan + Slang）的独立视效资产与 HDR 渲染子引擎。默认作为零侵入基础框架层运行（空载 0 像素改动），0 处 Harmony 补丁，与 RimIris、Raycast Lighting、Celestial Lighting 正交共存。

## 架构与目录归属

- `Source/RimLumen.Core/Contracts/`：跨模块共享契约与注册表（主干 `master` 冻结维护）。
- `Source/RimLumen.Core/Rendering/`、`1.7/Shaders/PostFx/`：P0 探针、HDR `RGBA16F` 渲染与深度共享、多级 Bloom、局部色调映射、扭曲采样、UI 前顺位守卫与像素零差自检（`gpu-foundation`）。
- `Source/RimLumen.Core/{Assets,Scene,Lighting}/`：原生 `EffectGraph` 节点树（2.5D 地面层 + 90° 高度层）、曲线与时间轴、L1 原生轻量 CPU 粒子、高频 `RegisterCustomGlower` 随身点光（`scene-and-lighting`）。
- `Source/RimLumen.Core/ParticlesGpu/`、`1.7/Shaders/Particles/`：L2 GPU Compute 粒子与流体力场（Curl/Vortex/吸引子）及 SSBO 直绘（`gpu-particles`）。
- `Source/RimLumen.Adapter.RimUnity/`、`Source/RimLumen.Core/Compat/`：P3 过渡期可拔除导入桥接与默认关闭的原版加色发光体重画器（`adapter-rimunity`）。

## 构建与离线测试

使用本机便携版 .NET 10 SDK（`Tools/build.ps1` 自动定位）：

```powershell
pwsh -NoProfile -File Tools/build.ps1
pwsh -NoProfile -File Tools/test-offline.ps1
```

策划与定稿决策见 `docs/02-plan.md` 与 `docs/03-decisions.md`。
