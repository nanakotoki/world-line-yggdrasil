# 世界线·战斗状态图（world-line-yggdrasil）开发进度

> 杀戮尖塔2（StS2）战斗状态空间图探索 + 最优打法模组。
> 目标：把每场战斗记录成"世界线"有向图（结点=状态、边=操作），支持随时跳回任意结点
> 重打（含整局级 SL），并自动搜索以**局外收益（M3）**为定义的"最优打法"，且保证该打法
> 在真实游戏里真的成立。
>
> 配套文档：`docs/initial-plan.md`（愿景）、`docs/api-notes.md`（战斗 API）、
> `docs/sts2-agent-contract.md`（代理契约）。仓库首页见 `README.md`。

---

## 0. 设计目标与总体思路（读之前先看）

这个项目**不是**"把所有怪/卡/事件预先建表"——那样存不下、也给模组添负担。
核心思路是**让系统能现算任意一个，算错了能被发现、被修正**：

1. **现场读取，不囤数据**：敌人招式/伤害、卡牌数值都从**活游戏对象**现读。
2. **语义去重**：结点身份=语义指纹（忽略 RNG），相同局势合并 → 环/自环出现，
   图小且可读。
3. **真实游戏当裁判（对拍 + 兜底）**：离线模拟器只是快速启发；预测是否成立，靠
   "在真实战斗里逐步执行 + 全量对比"来判定；分歧就交给真实游戏束搜索，保证最终
   打法真实可达。

> 参考数据文件：`Power.tabx`（力量/状态语义）、`random_data.wiki`（RNG 队列类型）、
> `information/`（怪物/卡牌/遗物/药水/状态/事件的灰机 wiki 源码转储）——用于补 Known*
> 兜底表与校准模拟器，不是运行期依赖。

---

## 一、架构总览

| 目录 | 职责 | 关键文件 |
|---|---|---|
| `ModEntry.cs` | 入口：订阅 run/combat 钩子、PatchAll、安装 UI | |
| `Capture/` | 录制：战斗小图 + 运行大图 + 快照读取 | `CombatRecorderModel` `CombatStateReader` `RunRecorderModel` |
| `Graph/` | 图模型 + 语义指纹 | `CombatGraph` `RunGraph` `CombatSnapshotData` `Canonicalizer` |
| `Restore/` | 还原：战斗深快照 / 整局 LoadRun / 语义还原 / 视觉刷新 | `RestoreService` `RunRestoreService` `SnapshotRestorer` `CombatSemanticRestore` `CombatSnapshot` `ReflectionCache` |
| `Objective/` | M3 局外收益评分 | `ObjectiveService` |
| `Simulation/` | 离线模拟器 + 束搜索 | `SimEngine` `SimState` `SimSearch` `SimSetup` `RuntimeCardData` `RuntimeEnemyData` |
| `Search/` | 真实游戏搜索 + 最优闭环 + 快进 | `AutoSearchService` `OptimalPlayService` `ActionEnumerator` `SearchSpeed` |
| `Validation/` | sim 计划在真实战斗逐动作对拍 | `PlanValidator` |
| `Ui/` | 图面板 / 画布 / i18n | `GraphPanel` `GraphCanvas` `Loc` |
| `Persistence/` | JSON 图导出 | `GraphExporter` |

数据流（简）：

```
玩家/搜索 打动作
   └─► CombatRecorderModel 捕获 CombatSnapshotData（语义指纹定 id）
          ├─► CombatGraph（当前战斗小图）
          └─► RestoreService 存 CombatSnapshot（深快照，供本场还原）
房间边界 ──► RunRecorderModel 捕获大结点（run save，供整局 SL）

最优闭环：SimSearch(离线束搜索,M3分) ─sim计划→ PlanValidator(真实逐步执行+对拍)
            ├─ 全对 → OPTIMAL VALIDATED
            └─ 分歧 → AutoSearchService(真实束搜索) → 真实可达最优
```

---

## 二、已完成

### M0 脚手架 / 逆向
- [x] 纯代码 DLL 模组（无 pck，`has_dll=true`），Release 构建自动部署 `mods/WorldLineYggdrasil/`
- [x] `docs/api-notes.md`（sts2.dll 战斗 API）、`docs/sts2-agent-contract.md`（STS2-Agent 契约）
- [x] 依赖：仅游戏本体 + BaseLib；运行时**不需要** STS2-Agent / NaturalConsole

### M1 录制 / 还原 / SL
- [x] **战斗小图**：`CombatRecorderModel` 在"玩家决策点"捕获快照（首回合根、每次打牌/用药/
      弃药、end_turn、终局）；`Canonicalizer` 语义指纹去重 → 环/自环可见；每个结点同时存
      `CombatSnapshotData`（身份/显示/评分）与深快照（还原）
- [x] **运行大图（随时随地 SL）**：`RunRecorderModel` 在房间/事件边界捕获大结点
      （`RunManager.ToSave`，事件/战斗支持 PreFinishedRoom），选项=世界线分支；
      **存档点首次捕获后不可变**
- [x] **战斗深快照还原**：`RestoreService`+`SnapshotRestorer`（undo-mod 移植）——当前战斗内
      任意小结点可还原，敌方回合中禁止（防抽牌/意图结算冲突）；含整套视觉刷新
      （`Visuals/HandRefresher`、`CreatureVisualRefresher`、`PotionRefresher`、`PowerRefresher`…）
- [x] **整局还原**：`RunRestoreService`（CleanUp + SetUpSavedSingleplayer + LoadRun）
- [x] **旧战斗小结点跳转**：`CombatSemanticRestore` 按 id 重建牌堆到重进战斗上；
      **语义还原成功后从实时 CardModel 重建屏幕手牌**（修复"模型对、界面冻结在开局"）
- [x] 终局/中途还原的录制器计数同步（`SyncCountersTo`），多分支收益互不污染

### M2 图 UI
- [x] 内置 Godot 结点画布（Panel/Label/Line2D/Polygon2D + `GuiInput` 信号）——规避运行时加载
      模组的自定义 Control override 不被引擎挂载的问题
- [x] 战斗图/运行图双视图（右键屏幕边缘「图」呼出）；点结点还原；结点按血量着色
      （绿/黄/红）、金色=胜利、红=失败；当前结点紫环、最优路径金环
- [x] 结点命名（面板输入框 / `GraphNode.Name`）；**圈点勾画**：墨迹层（世界坐标存储、
      随平移缩放、重建后重绘）+「涂画/选择」切换 +「清除批注」
- [x] i18n 骨架：`Ui/Loc.cs`（zh/en 键名取词 + `Loc.Language`），主要文案已接入，其余可增量迁移
- [x] 性能：画布重建同帧合并延迟（搜索期不再每结点全量重建场景树）

### M3 局外收益评分（"更优"的定义）
- [x] `ObjectiveService`：战损 / 金币 / 药水 / 生命上限 / 遗物计数 / 卡牌奖励；
      权重（滑块/`wly weight` 调）；Boss 战损因子（act3 ×0、act1/2 ×0.5、普通 ×1）
- [x] 结算画面奖励（金币/药水/卡牌）折叠进终局 outcome（`AfterRewardTaken`）
- [x] **失败重罚**：任何失败 -1000（修复 act3 Boss `HpLost×0` 时"失败≈胜利"的 bug）
- [x] **sim 惩罚用药水**：`SimState.PotionsUsed/Discarded` 全链路，模拟器不再白用药水换分

### M4 离线模拟器（现场读取）
- [x] `SimEngine`：伤害/格挡/能量/抽牌/洗牌、力量/虚弱/易伤/脆弱/缩小/中毒、回合状态衰减
      （含 `SkipNextDurationTick` 语义：敌方回合刚上的 debuff 存活到下一玩家回合）
- [x] `RuntimeCardData`：从活卡 `DynamicVars`（字典）现读 `Damage/Block/Cards/Energy/HpLoss`，
      力量类键名 `VulnerablePower` 等；修复 `KeyNotFoundException` 与 PowerVar 键名 bug
- [x] `RuntimeEnemyData`：活怪招式机现读（循环 + `RandomBranchState` 分支/连招限制，
      `CanRepeatXTimes`/`CannotRepeat`/`UseOnlyOnce`）+ `AttackIntent.DamageCalc` 基础伤害；
      **MonsterAi RNG 捕获对齐**复刻随机选招
- [x] `SimSearch`：离线束搜索收集终局、M3 打分取最优
- [x] 诚实降级：选择弹窗卡（NEOWS_FURY/PURITY/HEADBUTT）、X 费卡从 sim 排除（不瞎猜）；
      FISTICUFFS 类"格挡=伤害"、OFFERING 类 HP 消耗/耗尽已建模

### M5 自动搜索 / 最优打法闭环
- [x] `AutoSearchService`：真实游戏 BFS/贪心/束搜索（restore→注入真动作→录制器捕获真实
      子结点），终局 M3 打分 + `SetBestPath`
- [x] `PlanValidator`：sim 计划在真实战斗逐动作执行，全量对拍
      （hp/格挡/能量/hand/draw/discard∪play/exhaust/敌方hp/意图）
- [x] `OptimalPlayService` + `wly optimal`：sim 候选 → 真实快进验证执行 → 分歧自动改走
      真实束搜索；over-run（sim 计划偏长、真实提前获胜）优雅收尾；分歧若战斗已结束则直接
      报真实结局；输出一句可读结论"最优打法: [Victory] 战损-X … + 打法: <动作线>"

### M6 打磨
- [x] 圈点勾画、画布合并重建、深快照 FIFO 上限 1500（`RestoreService.MaxSnapshots`）、
      i18n 骨架（见上）

### 打包
- [x] `dist/WorldLineYggdrasil_v0.0.1.zip`（dll + json + 安装说明.txt，UTF-8 BOM）
      —— 接收方解压 → 放入 `<游戏>/mods/` 即用

---

## 三、关键设计决策（为什么这么写）

1. **语义指纹（`Canonicalizer`） vs 深快照（`CombatSnapshot`）分开**
   - 结点身份/合并/显示/评分只用语义快照（忽略 RNG）→ 图紧凑、可合并、环/自环可见；
   - 还原必须用深快照（含 RNG 与对象引用）→ 只在"本场已记录的结点"上可靠。
2. **现场读取三层**
   - 读得到 → 现读活对象（卡 `DynamicVars`、怪招式机/`DamageCalc`）；
   - 读不到（格挡/增益/减益值写在 C# 委托里）→ 小 `KnownBlock/KnownBuff/KnownDebuff/
     KnownStatus` 表，**对拍发现一个补一个**（查反编译 / `Power.tabx` / wiki），不预生成；
   - 补不上（选择弹窗/随机/X 费/职业机制）→ sim 排除（诚实降级）或由真实搜索接管。
3. **真实游戏当裁判（A+B）**：离线 sim 快、可能乐观/不完整；`PlanValidator` 在真实战斗里
   逐步执行候选线并全量对拍；分歧 → `AutoSearchService` 真实束搜索给"真实可达最优"。
4. **深还原 vs 语义还原**：当前战斗内小结点 = 深快照还原（可靠）；**旧**战斗小结点 =
   整局 LoadRun + 语义还原（按 id 重建牌堆）。语义还原必须补视觉手牌重建，否则"模型对、
   界面冻结"。
5. **存档点不可变**（关键 bug 根因）：大结点 run save 若被重进覆盖，会烙进 RNG 漂移 →
   重进遭遇被重掷成别的战斗 → 旧快照被还原到错误战场 → "乱打卡死/宿命论"。
   首次捕获后不再覆盖。
6. **RNG 对齐**：洗牌用 `Shuffle` 队列、怪物选招用 `MonsterAi` 队列（`random_data.wiki`），
   经 `SerializableRng→MegaRandom` 捕获到 sim 状态，逐分支克隆，模拟与真实走同一随机序列。

---

## 四、使用手册

### 图面板（无需控制台）
游戏内 **右键屏幕边缘** → 「图」标签 → 面板。

- **战斗图**：本场状态图。左键点结点=还原到该步（敌方回合禁止）；右键拖=平移；滚轮=缩放；
  「涂画」=左键自由圈点；左下可给选中结点起名；「清除批注」擦墨迹。
  权重滑块：战损/金币/药水/生命上限/遗物计数/卡牌 的相对重视度。
- **运行图**：整局大结点树（事件/战斗/商店…）。点行=整局跳回；战斗结点可展开其小图，
  点小结点=跳到该战斗对应步。

### 调试控制台（可选；`wly`）
| 命令 | 作用 |
|---|---|
| `wly nodes` | 列出当前战斗小结点（编号） |
| `wly restore <n\|id>` | 战斗内跳回第 n 个结点 |
| `wly run` / `wly runrestore <n>` | 列出 / 整局跳回大结点 |
| `wly step <大结点> <小结点>` | 整局跳回并进入旧战斗的指定步 |
| `wly outcome` | 列出本场各终局 outcome 对比 |
| `wly weight <hp|gold|potion|maxhp|relic|card> <0-5>` | 调 M3 权重 |
| `wly simsearch [预算] [beam]` | 离线模拟器找最优线 |
| `wly validate` | 在真实战斗逐步验证 `LastPlan` |
| `wly optimal [预算] [兜底预算]` | 一键最优：找→验证执行→分歧兜底 |
| `wly search [预算] [root] [greedy|bfs|beam]` / `wly searchstop` | 真实游戏搜索 |
| `wly dump` | 导出当前图 JSON |

---

## 五、未完成 / 需校准 / 已知风险

### sim 精度长尾（对拍驱动，非一次性）
- [ ] **Known\* 表未穷尽**：部分稀有敌人的 格挡/增益/减益 值未映射（`KnownBlock/KnownBuff/
      KnownDebuff/KnownStatus`），验证分歧即补
- [ ] **每回合被动**：覆甲回格挡、再生回血、仪式加力等"回合触发型"敌人被动未建模
- [ ] **药水效果**：sim 用药=仅消耗（不模拟火/血/力药等效果）；真实验证会用真效果，
      有分歧则走兜底
- [ ] **职业机制**：充能球/激发（Silent 之外）、X 费、复杂随机目标等卡牌效果不建模
- [ ] 升级/附魔对卡面数值的读取是否完全一致（个别 `ValueProp.Move` 动态值）待长期对拍

### 已知风险 / 待定
- [ ] **大结点 id = `幕-层-类型`**：同层同类型的两个房间会**合并成同一大结点**，存档与分支
      可能互相串（风险高，尚未改；若实测出现再给房间 id 加唯一性区分）
- [ ] **真实搜索注入竞态**：连续快速注入时个别动作被真实端拒绝 → 验证分歧（已靠兜底缓解，
      非阻塞）
- [ ] **语义还原依赖重进遭遇不变**：已靠不可变存档点修复主路径；极端场景（同层多战、跨
      保存）仍待更多实测
- [ ] **内存/性能**：深快照 FIFO 上限已加，但大图（数千结点）的还原与画布仍缺压力测试
- [ ] **UI 视觉**：语义还原对 敌方血条/能量/弃牌堆计数 等是否全部刷新，未逐项核对

### 规范 / 运维
- [ ] git 仓库尚未提交（工作区几乎全为未跟踪文件）
- [ ] 版本 0.0.1，无 CHANGELOG；发布包需手动重打（`dist/`）
- [ ] 控制台命令 `DebugOnly=true`：普通玩家仅用图面板；如需开放再议
- [ ] i18n：`Loc` 骨架已建，`GraphPanel/GraphCanvas` 大量标签仍是中文字面量，按需增量迁移
- [ ] `install.txt`/`安装说明.txt` 与 README 内容需随版本同步维护

### 建议下一步（按价值排序）
1. 用户实机复测"大结点 SL → 旧战斗小结点 → 乱打"主流程（本会话已 headless 修复，需实机确认）
2. 若复现"同层两场战斗串在一起" → 修大结点 id 唯一性
3. 对拍继续撞新怪，补 `Known*`（尤其 act2+ 敌人与精英/Boss）
4. 把 sim 的"每回合被动"补一个通用钩子（覆甲/再生/仪式）
5. git 提交 + 版本/CHANGELOG 规范

---

## 六、复现 / 测试入口

- **无头探针**（`%TEMP%\opencode\probe-*.ps1`，需 STS2-Agent 在本地）：
  - `probe-sim.ps1`：simsearch + validate（对拍收敛信号）
  - `probe-optimal.ps1`：`wly optimal` 最优闭环（含 over-run/兜底路径）
  - `probe-archived.ps1` / `probe-archived2.ps1`：大结点 SL → 旧战斗小结点跳转回归
    （验证不可变存档点修复：重进遭遇必须与原来一致）
- **构建**：`dotnet build WorldLineYggdrasil\WorldLineYggdrasil.csproj -c Release`
  （自动部署到游戏 mods 目录；改路径在 `.csproj` 的 `Sts2DataDir/Sts2ModsDir`）
- **日志**：`%APPDATA%\SlayTheSpire2\logs\godot.log`（`[WorldLineYggdrasil.*]` 标签）
- **图导出**：`%APPDATA%\SlayTheSpire2\WorldLineYggdrasil\graphs\`

---

## 七、相关数据/参考文件

| 文件 | 角色 |
|---|---|
| `Power.tabx` | 力量/状态 id→语义 词典（补 Known*/写状态规则的参考） |
| `random_data.wiki` | 各 RNG 队列（CombatCardSelection/Shuffle/MonsterAi…）用途 |
| `information/*.html` | 怪物/卡牌/遗物/药水/状态/事件 灰机 wiki 源码转储（人类参考） |
| `%TEMP%\opencode\sts2-src` | 反编译游戏源码（权威数值/机制，本地逆向用） |
