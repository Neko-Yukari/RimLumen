# RimLumen

RimWorld 1.7 跑在 **Waylith** 引擎上（Ludeon 自研，基于 RocketWerkz/Ahwoo 的 Brutal 框架：.NET + Vulkan + Slang + GLFW），不是 Unity。Unity 的 API 由游戏根目录的 `UnityStubs.dll` / `Waylith.UnityCompat.dll` 模拟。

现有的 Unity 兼容层是 **RimUnity**（`Chezhou.RimUnity`，MIT，作者 chezhou9413）：它把一部分 Unity 组件在 Waylith 上重新实现，模组才能「在 Unity 里制作、在游戏里播放」。

**RimLumen** 是这一层的现代渲染基础设施：

- HDR 渲染目标、局部色调映射、曝光、分层泛光、热扭曲、计算着色器粒子
- 一套能承载这些能力的资源格式与加载器
- 一套能力查询接口，让模组按「后端支持什么」降级，而不是按「装了哪个模组」写分支

## 与 RimUnity 的关系（待定）

调研后决定：给上游提 PR，还是独立实现并复用其导出格式。判据是复用比例——如果 RimUnity 的导出格式、粒子运行时、材质转换能直接吃下，就做独立实现并兼容其格式，避免两份 RimUnity 冲突；如果核心渲染通道必须改在它内部，就走 PR。

## 不以修改游戏本体为前提

不 patch Waylith 内部私有结构，优先走 `UnityStubs` 暴露的公开 API 与 RimUnity 已有的扩展点。

## 状态

规划阶段，尚无实现。任务书见 `docs/00-brief.md`，事实与证据见 `docs/01-research.md`。

## 使用方

`Whisper-of-the-Forest`（`NekoYukari.WhisperOfTheForest`）是第一个使用者，但本项目按通用库设计。模组开源，素材可以用 CC-BY-SA / GPL 这类宽松或传染许可，只需在引用处标注来源与许可。
