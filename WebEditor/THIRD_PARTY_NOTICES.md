# RimLumen Web Editor 第三方开源组件许可证清单

以下清单对应 `WebEditor/app/package-lock.json` 中的锁定版本及 `.NET WebAssembly` 运行时依赖：

## 前端运行与构建依赖（`WebEditor/app/`）

| 组件 | 锁定版本 | 许可证 | 仓库 / 主页 |
| --- | --- | --- | --- |
| `react` | `19.3.1` | MIT | https://github.com/facebook/react |
| `react-dom` | `19.3.1` | MIT | https://github.com/facebook/react |
| `three` | `0.174.0` | MIT | https://github.com/mrdoob/three.js |
| `codemirror` | `6.0.2` | MIT | https://github.com/codemirror/dev |
| `@codemirror/lang-json` | `6.0.2` | MIT | https://github.com/codemirror/lang-json |
| `@codemirror/lang-xml` | `6.1.0` | MIT | https://github.com/codemirror/lang-xml |
| `@codemirror/lint` | `6.9.8` | MIT | https://github.com/codemirror/lint |
| `@codemirror/state` | `6.7.6` | MIT | https://github.com/codemirror/state |
| `@codemirror/view` | `6.43.15` | MIT | https://github.com/codemirror/view |
| `@codemirror/language` | `6.13.1` | MIT | https://github.com/codemirror/language |
| `react-resizable-panels` | `2.1.9` | MIT | https://github.com/bvaughn/react-resizable-panels |
| `tweakpane` | `4.0.5` | MIT | https://github.com/cocopon/tweakpane |
| `lucide-react` | `0.475.0` | ISC | https://github.com/lucide-icons/lucide |
| `vite` | `6.4.6` | MIT | https://github.com/vitejs/vite |
| `@vitejs/plugin-react` | `4.7.0` | MIT | https://github.com/vitejs/vite-plugin-react |
| `typescript` | `5.9.3` | Apache-2.0 | https://github.com/microsoft/TypeScript |
| `@types/react` | `19.3.0` | MIT | https://github.com/DefinitelyTyped/DefinitelyTyped |
| `@types/react-dom` | `19.3.0` | MIT | https://github.com/DefinitelyTyped/DefinitelyTyped |
| `@types/three` | `0.174.0` | MIT | https://github.com/DefinitelyTyped/DefinitelyTyped |

## .NET WebAssembly 模拟运行时（`WebEditor/RimLumen.WebSim/`）

| 组件 | 版本 | 许可证 | 说明 |
| --- | --- | --- | --- |
| .NET 10 WebAssembly Runtime (`Microsoft.NET.Runtime.WebAssembly.Sdk` / `dotnet.js` / `dotnet.native.wasm`) | `10.0.15` (SDK `10.0.404`) | MIT | 在浏览器内以 AOT+SIMD 运行 `RimLumen.Core` 真实解析器、状态机、飘带、扭曲时间轴与 L1 CPU 粒子模拟器 |
