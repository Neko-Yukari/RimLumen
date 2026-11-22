# RimLumen 特效在线预览与编辑工作台（阶段 1 MVP）

脱离 RimWorld 游戏启动的浏览器端特效工作台：在浏览器内通过 **.NET 10 WebAssembly (AOT + SIMD)** 直接运行真实的 `RimLumen.Core` 解析器、校验器、状态机与 L1 CPU 粒子/飘带模拟器，并使用 `three.js` 移植 RimLumen 原生着色器实时绘制。

## 前置环境

- **Node.js** >= 20（及 `npm`）
- **.NET 10 SDK** + `wasm-tools` 工作负载（与仓库 `Tools/build.ps1` 所用游戏便携 SDK 完全隔离；若安装了独立目录 `C:\Users\81004\AppData\Local\rimlumen-dotnet-wasm`，构建脚本会自动优先选用，否则使用 `DOTNET_ROOT` 或系统 `dotnet`）。

## 构建与本地打开方式

> 说明：浏览器安全策略禁止 `file://` 协议直接加载 `.wasm` 与 ES Module，请始终通过本地 HTTP 静态服务打开。

### 1. 一键编译 WASM 与前端产物

```pwsh
pwsh -NoProfile -File WebEditor/build-editor.ps1
```

该脚本会：
1. 以 AOT + SIMD 模式发布 `WebEditor/RimLumen.WebSim/` 并同步至 `WebEditor/app/public/wasm/`；
2. 安装/校验锁定依赖并在 `WebEditor/app/` 运行 `tsc --noEmit` 与 `vite build`，产出纯静态站点 `WebEditor/app/dist/`。

### 2. 本地启动静态服并打开浏览器

```pwsh
pwsh -NoProfile -File WebEditor/serve-editor.ps1
```

执行后在浏览器访问 `http://127.0.0.1:4173/editor/` 即可使用。

若只改前端 UI 界面并做热更新开发，也可在 `WebEditor/app/` 下运行 `npm run dev`（前提是已跑过一次 `build-editor.ps1` 生成 `public/wasm/`）。

## 离线防分叉测试（独立于 `RimLumen.sln`）

```pwsh
dotnet run --project WebEditor/RimLumen.WebSim.Tests/RimLumen.WebSim.Tests.csproj -c Release
```

校验独立 XML `LumenEffectDef` 解析器（对照 `Verse.DirectXmlToObject` / `ParseHelper`）、内置 6 个 Demo 与 `docs/api.md` 示例的 JSON↔XML 逐字段相等性，以及模拟/顶点导出契约。

## 能力与明确不支持范围

- **支持格式**：`.lumen.json`（保留注释与尾逗号）与**独立 `LumenEffectDef` XML**；打开哪种格式即以对应模式编辑并默认保留原格式导出，同时支持带丢失提醒的跨格式导出。
- **XML 明确限制**：不支持 `ParentName` 继承、`Abstract` 模板与 `PatchOperation`，遇到上述特性或未知/大小写错误的标签会直接给出报错位置与说明。
- **渲染边界**：重点保证特效自身的发射、曲线、渐变、状态机、子发射器与粒子/节点飘带呈现；不还原 RimWorld 地图全局光照接管、墙体阴影/遮挡贴图、场景深度软粒子裁剪与可编程 ShaderPack 后处理。
