# RimLumen 代理规则

## 验证

日常验证只跑两条秒级离线命令，两条都必须以退出码 0 结束：

- `pwsh -NoProfile -File Tools/build.ps1`：构建整个解决方案，0 警告 0 错误（警告即错误）。
- `pwsh -NoProfile -File Tools/test-offline.ps1`：运行全部离线单元测试；`-Filter <名称片段>` 只跑匹配的用例。

改动打包内容或构建配置时，加跑 `pwsh -NoProfile -File Tools/pack-release.ps1`。

## 实机

- 代理不得私自启动或关闭 RimWorld 进程，包括运行 `Tools/launch-showcase.ps1`。
- 不编写游戏内探针宿主、Vulkan 日志正则门禁或跨进程自动化测试，不把真机运行结果设为合并或发布的前置条件。
- 渲染画面与实机表现由用户本人查看：按需运行 `Tools/launch-showcase.ps1`，或直接在游戏内看。
- `Tools/launch-showcase.ps1 -Benchmark <jsonPath>` 跑一次无窗口的 GPU 粒子阶梯压测并自动退出。它只在用户明确要求压测时手动运行，不进任何自动化门禁，结果不作为合并或发布的前置条件。

## 离线测试

- `Tests/` 只测不依赖游戏宿主的纯逻辑契约，例如 `std140` 内存偏移对齐、DAG 环与拓扑校验、数学曲线、解析器契约、状态转换。
- 不写 DTO getter 对抄测试，不断言默认值或常量，不测框架。

## 生产代码

- 零测试污染。不加只给测试或探针读取的字段、计数器、日志钩子、旁路注册表，不加仅测试用的开关或注入点。
- 不为迁就无头离线测试写裸 `catch {}`。捕获异常后必须记录或重新抛出。
- 离线环境到不了的宿主依赖收在一个明确的边界上，测试直接调用边界内侧的纯逻辑，例如 `ShaderPackCatalog.ScanRoots`。
- 边界校验一次并归一化。外部输入（`pack.json`、`.lumen.json`、XML Def、Mod 设置）在解析器和 `ShaderPackGraphValidator` 处一次性完成校验与默认值归一化，转成强类型后再往里传。
- 内部信任上游的不可变契约。渲染热路径不重复判空、不重复 `Clamp`、不重复越界检查、不静默回退。
