# D1–D10 决策表（待用户答复）

由 `docs/02-plan.md` 归并而成，做减法。回复「全按推荐」，或只列例外（例：`D3 B，D8 B，其余 A`）。

| # | 决策 | 选项 | 推荐 | 理由 | 选错的代价 |
| --- | --- | --- | --- | --- | --- |
| D1 | 与 RimUnity 的关系 | A 软依赖（粒子/加载走 RimUnity，条件程序集）／B fork 粒子与加载两块／C 自有资源格式 | **A** | 粒子运行时 73 个文件零 Harmony，直接依赖比 fork 424 KB 便宜 | A 错：RimUnity 因游戏更新加载失败时，Unity 导出的粒子在 RimLumen 后端也播不了，要转 B（补 fork + P5 读取器 + T7）。B 错：长期维护 424 KB 并追 RimUnity 语义。C 错：现有导出资源与制作流程作废 |
| D2 | 地图 draw 接管 | A `LayerPassRouter` 路由到自有 passId／B Harmony Prefix `RecordSlice` | **A** | 是 public、零 patch，也是「无特效零差」结构保证的地基 | A 错：路由按整层生效，表只有 8 槽且无注销接口（原版占 3）；行不通转 B，P1 骨架重写、零差改靠实测证明。B 错：和 RimUnity 抢同一方法（它的 Prefix 返回 false），游戏更新签名一变即坏 |
| D3 | HDR 目标格式 | A `R16G16B16A16SFloat`／B `B10G11R11UFloat` | **A** | 与世界视图、RimUnity 同格式，带 alpha | A 错：显存是 B 的 2 倍，改一个常量回退。B 错：没有 alpha，自有层只能加色合成，D4-A 的 alpha 写回做不了 |
| D4 | 合成位置 | A 以 Load 打开原版场景 RT 后写回／B 替换 `CompositePass` | **A** | 不碰原版 pass，原版像素不经过我们的着色器 | A 错：场景 RT 是 8bit，自有层要先 tonemap 再写回；将来做全局 tonemap/暗角要另加一趟整屏 pass。B 错：每个像素都过我们的 shader，零差只能实测；RimUnity 用 `passes[0] is CompositePass` 识别（`BloomHooks.cs:261`），替换后并存行为未知 |
| D5 | 全局效果默认 | A 只作用自有层／B 全局默认开 | **A** | 默认配置下零差判据才是硬门槛 | A 错：整屏观感默认看不到，改默认值即可。B 错：装上就改变所有玩家的原版画面，判据 1 失效 |
| D6 | 像素判据路线 | A swapchain smoke + 场景 RT 零差／B 只 swapchain | **A** | swapchain 含 UI，做不了逐像素零差 | A 错：T2 可能白做，两条读回都不通时退 B。B 错：UI 一动就有差，只能设阈值，判据 1 名存实亡 |
| D7 | 对上游提 PR | A 不提／B 提 2 个小 PR（公开 `ShaderDirectory`、按层 Bloom 参数） | **A** | 本阶段没有任何东西依赖它 | A 错：无代价，随时可补。B 错：对外可见动作加等上游的时间 |
| D8 | GPU 粒子纳入本阶段 | A 下一阶段／B 本阶段 | **A** | 同步正确性（T1）没证实前不在上面盖楼 | A 错：晚一个阶段。B 错：T1 不过则已写的粒子代码作废 |
| D9 | 验证层 | A 手测/CI 都带 `-EnableVulkanValidation`／B 仅调试时 | **A** | T1、T4 的同步错误不带验证层是静默的 | A 错：测试机要装 Vulkan validation layer（本机未核），T6 性能要另跑一次。B 错：错误换个驱动才炸，判据 3 取消 |
| D10 | WotF `VfxService` 改造时机 | A P1 零差通过后／B 同步进行 | **A** | 跨仓改动应建在已验证的后端上 | A 错：WotF 接入晚一个阶段。B 错：P1 失败则 WotF 侧要回滚，且 L2 只有 WotF 主会话能跑 |

## 耦合

- D4-A 依赖 D2-A：D2 选 B，D4-A 的结构保证不成立。
- D3-B 与 D4-A 冲突（无 alpha）。
- D8-B 要求 D9-A。

## 实测项由哪条决策解锁

| 实测 | 解锁条件 | 说明 |
| --- | --- | --- |
| T1 自录 barrier | D8、D9 | D8=B 时升为 P0 阻塞项；D8=A 时在 P2 做，不挡 P1 |
| T2 场景 RT 读回 | D6=A | D6=B 则不做 |
| T3 GrabPass 冲突 | 不由 D1–D10 解锁 | 只在有特效要用屏幕扭曲时需要 |
| T4 两个 mod 同开 HDR 的深度共享 | D1=A | 并存成常态，必测 |
| T5 Slang 兼容性 | 无条件 | 任何选项下自有 pass 都要过 Slang |
| T6 显存与帧耗 | D3 | 结果决定是否降到 B |
| T7 `.rutex` 真实样本 | D1=B | 其余不测 |
| T8 无窗口自动化 | D6=A 或 D9=A 的 CI 部分 | **归类有误：它是读 Assembly-CSharp 的代码题，不是实测** |

## 需要跨会话的两件

- **T3、T7 要跑 Unity 导出**：超出 RimLumen 会话边界，由 WotF 侧导样本。
- **T8** 需要反编译 `Assembly-CSharp.dll` 找 `-quicktest` 之类的开关（目前只反编译了 Waylith 与 RimUnity）。
