# sts2.dll 战斗 API 笔记（针对 v0.111.0）

反编译自游戏 `data_sts2_windows_x86_64/sts2.dll`（commit 41cef1ea，release v0.111.0）。
路径：`MegaCrit.Sts2.Core.*`。本文只记录与本模组（战斗状态图/快照/模拟）相关的表面。

## 1. 模组入口与注册

- 装配件中类型标记 `[ModInitializer(nameof(Initialize))]`，静态方法即入口（`MegaCrit.Sts2.Core.Modding`）。
- `ModHelper.SubscribeForCombatStateHooks(string id, CombatHookSubscriptionDelegate del)`：
  `del: (CombatState) => IEnumerable<AbstractModel>`，返回的模型会被当作战斗钩子监听者。
  id 必须唯一（全局按序排序），重复会被拒绝并报错。
- `ModHelper.SubscribeForRunStateHooks(id, (RunState) => IEnumerable<AbstractModel>)` 同理。
- 战斗状态变化时，游戏调用 `AbstractModel` 上的 virtual 钩子方法（见 §3）。

## 2. 战斗状态对象

- `ICombatState`（`Core.Combat`）：`RunState`、`Allies`、`Enemies`、`Creatures`、`Players`、
  `PlayerCreatures`、`RoundNumber`（从 1 起）、`CurrentSide`（`CombatSide`）、`Encounter`、
  `HittableEnemies`、`CreaturesOnCurrentSide`、`GetCreature(uint? combatId)`、`GetCreatureAsync`、
  `GetCreaturesOnSide`、`GetOpponentsOf`、`GetTeammatesOf`、`GetPlayer`、`IterateHookListeners()`、
  `IsLiveCombat()`；事件 `CreaturesChanged`。
- `CombatState : ICombatState`（`Core.Combat`）：实体实现。`CombatManager.Instance` 持有当前战斗，
  `CombatManager.Instance.IsInProgress` 判断是否在战斗。
- `CombatTurnState`、`PlayerTurnPhase`、`EndTurnSignal`、`CombatId`、`CombatSideExtensions` 同命名空间。
- 单机下 `RunManager.Instance.ActionQueueSynchronizer` 是动作队列同步器（见 §5）。

## 3. 语义钩子（AbstractModel virtual 方法，可被模组模型 override）

关键的生命周期钩子（`MegaCrit.Sts2.Core.Models/AbstractModel.cs`）：

| 钩子 | 用途 |
|---|---|
| `BeforeCombatStart()` / `BeforeCombatStartLate()` | 战斗开始 |
| `AfterCombatEnd(CombatRoom)` | 战斗结束（不论胜负） |
| `AfterCombatVictory(CombatRoom)` / `AfterCombatVictoryEarly` | 战斗胜利 |
| `AfterCardPlayed(PlayerChoiceContext, CardPlay)` / `AfterCardPlayedLate` | 卡牌结算完（边完成） |
| `BeforeCardPlayed(CardPlay)` | 卡牌开始结算 |
| `AfterPotionUsed(PotionModel, Creature? target)` | 药水使用 |
| `AfterPotionDiscarded(PotionModel)` | 药水丢弃 |
| `AfterPlayerTurnStart(PlayerChoiceContext, Player)` (+ Early/Late) | 玩家回合开始（决策点） |
| `AfterSideTurnStart(CombatSide, IReadOnlyList<Creature>, ICombatState)` | 某方回合开始 |
| `BeforeSideTurnEnd` / `AfterSideTurnEnd` | 某方回合结束 |
| `AfterCurrentHpChanged(Creature, decimal delta)` | HP 变化（收益/战损） |
| `AfterGoldGained(Player)` | 金币获得（局外收益） |
| `AfterDeath(PlayerChoiceContext, Creature, bool wasRemovalPrevented, float deathAnimLength)` | 死亡 |
| `AfterCardDrawn(PlayerChoiceContext, CardModel, bool fromHandDraw)` / `AfterCardDiscarded` / `AfterCardExhausted` / `AfterCardChangedPiles(CardModel, PileType old, AbstractModel? clonedBy)` | 牌堆流动 |
| `AfterEnergyReset(Player)` / `AfterEnergySpent(CardModel, int)` / `AfterStarsGained/Spent` | 能量/星 |
| `AfterModifyingHandDraw`、`AfterShuffle(PlayerChoiceContext, Player)` | 抽牌/洗牌（含 RNG 消耗点） |
| `AfterPowerAmountChanged(PlayerChoiceContext, PowerModel, decimal, Creature? applier, CardModel?)` | 能力变化 |
| `AfterBlockGained/Cleared/Broken`、`Before/AfterDamageReceived`、`AfterDamageGiven` | 格挡/伤害 |
| `AfterOrbChanneled` / `AfterOrbEvoked` | 宝珠 |
| `AfterModifyingHpLostBeforeOsty` / `AfterHpLostAfterOsty` | 伤害换算的钩子 |
| 修饰类钩子 | `ModifyDamageAdditive/Multiplicative/Cap`、`ModifyBlockAdditive/Multiplicative`、`ModifyEnergyGain`、`ModifyGoldGained`、`ModifyHandDraw`、`ModifyXValue`、`ModifyMaxEnergy` 等（模拟器规则覆盖清单可参考这些名字） |

对拍/记录引擎应当注册一个 `AbstractModel` 子类，override 上述钩子（尤其
`AfterCardPlayedLate`、`AfterPotionUsed`、`AfterPotionDiscarded`、`AfterSideTurnEnd`、`AfterCombatEnd`）。

## 4. 动作模型（GameAction）

`MegaCrit.Sts2.Core.GameActions`（与 STS1 不同，STS2 的 GameAction 只是"玩家输入"的薄包装，
真正的结算逻辑在 `Core.Commands` 的 Command 里）。

- `GameAction` 基类：异步执行；`State`（枚举 `GameActionState`）；`Execute()`；可
  `PauseForPlayerChoice()` 等待玩家选择（如选牌弹窗），`ResumeAfterGatheringPlayerChoice(uint newId)` 恢复；
  事件 `BeforeExecuted/AfterFinished/BeforePausedForPlayerChoice` 等；`CompletionTask` 等待完成；
  `ActionType`（`GameActionType.CombatPlayPhaseOnly` 等）。
- `PlayCardAction(CardModel cardModel, Creature? target)`：出牌。构造时要求目标有 `CombatId`。
  执行时 `_card.CanPlay(out UnplayableReason, out AbstractModel)` 校验、
  `_card.IsValidTarget(target)` 校验、`SpendResources()` 扣费（能量+星，X费/星费），
  然后 `_card.OnPlayWrapper(PlayerChoiceContext, target, isAutoPlay:false, resources)`。
- `EndPlayerTurnAction(Player player, int turnNumber)`：结束回合，内部走 `PlayerCmd.EndTurn(player, canBackOut:true)`。
- `UsePotionAction(PotionModel potion, Creature? target, bool isCombatInProgress)`：用药水。
- `DiscardPotionGameAction`：弃药水。

玩家驱动的动作入队入口：
- 出牌：`CardModel.TryManualPlay(Creature? target)` → `EnqueueManualPlay` →
  `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(this, target))`。
- 药水：`PotionModel.EnqueueManualUse(target)`。
- 弃药水：`RequestEnqueue(new DiscardPotionGameAction(...))`。
- 结束回合：`EndPlayerTurnAction` 入队或直接 `PlayerCmd.EndTurn(player, canBackOut:true)`。

这些正是"真实游戏内动作注入"的原始基元（STS2-Agent 的 `act` 就是这么做的）。

## 5. RNG

`MegaCrit.Sts2.Core.Random`：
- `Rng`：`_counter`(int) + `MegaRandom _random`（私有）。**可序列化**：
  `SerializableRng ToSerializable()`、`void LoadFromSerializable(SerializableRng)`、`Rng(SerializableRng)`。
  方法：`NextBool/NextInt/NextUnsignedInt/NextUnsignedLong/NextFloat/NextDouble/NextGaussian/NextItem/WeightedNextItem/Shuffle`。
- `PlayerRngSet`（`Core.Entities.Rngs`）与 `PlayerRngType`/`RunRngType` 枚举定义各用途 RNG（抽牌、随机目标等）。
- 模拟器/快照必须捕获每类 `Rng` 的 `SerializableRng`（counter+状态），还原用 `LoadFromSerializable`。

## 6. 玩家状态

- `Player`（`Core.Entities.Players`）：`PlayerCombatState`、`Creature`、`RunState`、`NetId`、
  `GetPotionSlotIndex(PotionModel)`、`GetPotionAtSlotIndex(int)`。
- `PlayerCombatState`：`TurnNumber`、`Energy`、`MaxEnergy`、`Hand`（`CardPile`）、
  抽牌/弃牌/消耗堆、`CardPlayCounters` 等。`PlayerCmd`（`Core.Commands`）负责能量重置等。
- `CardModel`：`Owner`、`Pile`（`PileType`：Hand/Draw/Discard/Exhaust…）、`TargetType`、
  `EnergyCost`、`Id`（`ModelId`，`Id.Entry` 为字符串 id）、`CanPlay(out UnplayableReason, out AbstractModel)`、
  `IsValidTarget(Creature?)`、`TryManualPlay`、`CreateClone()`、`OnPlayWrapper(...)`。
- `CardPile`（`Core.Entities.Cards`）：`.Cards`（有序）、`.Type`。
- `Creature`（`Core.Entities.Creatures`）：`CurrentHp/MaxHp/Block`、`IsAlive`、`IsPlayer`、`Player`、
  `CombatId`、`Side`、`Powers`（`PowerModel`）、`GetPowers()` 等。

## 7. 其他值得注意

- `AutoSlay`（`Core.AutoSlay`）：游戏自带自动作战系统，`AutoSlayer.cs`/`AutoSlayConfig.cs` 及
  `Handlers/{Rooms,Screens}`。与我们的离线模拟器无关，但可参考其"合法动作/推进"实现，以及作为
  "高速真实驱动兜底"的现成机制。
- 本模组不需要这些：本模组只依赖 public API + `Krafs.Publicizer` 公开 sts2 私有成员用于深快照
  （参考 `sts2-undo-mod` 的做法），以及 Harmony 做必要的观察/拦截。

## 8. 逆向工具备忘

- `ilspycmd` 8.2 需在 net9 下运行：`dotnet exec --roll-forward LatestMajor <store>/ilspycmd.dll -p -o out sts2.dll`。
- 反编译产物：`C:\Users\时嘉庆\AppData\Local\Temp\opencode\sts2-src\`（3538 文件）。
- 参考实现（本地克隆）：`C:\Users\时嘉庆\AppData\Local\Temp\opencode\sts2-ref\`
  - `sts2-undo-mod`：战斗深快照/还原（M1 直接参考）。
  - `STS2-Agent`：状态读取/动作注入 HTTP 桥（M1/M4 校验参考，见 `docs/sts2-agent-contract.md`）。
  - `vakuu-prime`：纯 Python 战斗模拟器（M4 规则移植参考）。