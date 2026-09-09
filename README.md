# world-line-yggdrasil（世界线·战斗状态图）

> [!INFO]
> 本项目之后不太会更新了。新项目在 [nanakotoki/new-world-line-yggdrasil](https://github.com/nanakotoki/new-world-line-yggdrasil) 。

杀戮尖塔2（Slay the Spire 2）的战斗状态空间图探索模组。

每场战斗被记录成一张**有向图**：结点 = 游戏状态，边 = 玩家操作（打牌 / 用药水 / 弃药水 /
结束回合）。语义相同的局势合并成一个结点，因此环与自环会出现。运行层面还有一张**大结点树**
（房间/事件/战斗边界），可以像"随时随地 SL"一样跳回任意记录点重打，并支持离线模拟器自动搜索
**最优打法**（以战损等局外收益 M3 评分为准，并在真实战斗里验证成立）。

> 详细开发进度见 [`docs/progress.md`](docs/progress.md)；设计愿景见
> [`docs/initial-plan.md`](docs/initial-plan.md)；战斗 API 笔记 / STS2-Agent 契约见
> `docs/api-notes.md` / `docs/sts2-agent-contract.md`。

## 功能一览

- **战斗图**：实时录制本场状态图，结点按玩家血量着色；点结点 = 还原到该步。
- **运行图 / SL**：整局大结点树，点行 = 整局跳回；战斗结点展开可跳到该战斗任一步。
- **标注**：给结点起名；自由"圈点勾画"批注；清除批注。
- **局外收益评分（M3）**：战损 / 金币 / 药水 / 生命上限 / 遗物计数 / 卡牌奖励，权重可调，
  让"更优"有定义（任何胜利 > 任何失败）。
- **离线模拟器**：现场读取活卡牌/敌人的动态数值，无需囤积全部怪物数据；用模拟器快速搜索
  候选最优线。
- **最优打法闭环**：`wly optimal` = sim 找最优 → 真实战斗快进逐动作验证 → 分歧自动改走
  真实束搜索兜底，保证报告的打法是真实可达的。

## 安装（普通玩家）

解压发布包，把 `WorldLineYggdrasil` 文件夹放入：

```
<游戏安装目录>/mods/
```

需要：游戏版本 >= 0.111.0，且已装杀戮尖塔2 模组框架 **BaseLib**。
运行时**不需要** STS2-Agent / NaturalConsole。

## 使用

- 游戏内 **右键屏幕边缘** 出现「图」标签 → 点开面板（战斗图 / 运行图）。
- 调试控制台（可选）：`wly nodes | restore | run | runrestore | step | simsearch |
  validate | optimal | weight` 等，见 `docs/progress.md` 或控制台内 `wly` 帮助。

## 构建

```powershell
dotnet build WorldLineYggdrasil\WorldLineYggdrasil.csproj -c Release
# 自动部署到 <game>/mods/WorldLineYggdrasil/
```

需要游戏的 `sts2.dll` / `0Harmony.dll` / `GodotSharp.dll`（net9.0），
路径在 `.csproj` 的 `Sts2DataDir` / `Sts2ModsDir` 中配置。

## 发布包

`dist/WorldLineYggdrasil_v0.0.1.zip`（dll + json + 安装说明）。

## 无头测试

```powershell
powershell -ExecutionPolicy Bypass -File tools\test-combat-headless.ps1 -CombatSteps 60
```

需要 [STS2-Agent](https://github.com/CharTyr/STS2-Agent) 在本机 `mods/STS2AIAgent/`
（仅开发用）。

## 数据 / 日志

- 图导出：`%APPDATA%\SlayTheSpire2\WorldLineYggdrasil\graphs\`
- 日志：`%APPDATA%\SlayTheSpire2\logs\godot.log`（`[WorldLineYggdrasil.*]` 标签）

## License

AGPL-3.0。
