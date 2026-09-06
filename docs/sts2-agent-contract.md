# STS2-Agent HTTP API 契约（本模组依赖）

参考版本：STS2-Agent v0.9.2（mod_id.json 声明 `min_game_version: 0.111.0`，与本机一致）。
源码：`C:\Users\时嘉庆\AppData\Local\Temp\opencode\sts2-ref\STS2-Agent\`。

## 用途

本模组（WorldLineYggdrasil）运行时依赖 STS2-Agent 的本地 HTTP API，用于：

1. 从真实游戏读取当前状态（构造 `CombatSnapshot` 的种子 / 手动模式结点 / 对拍基准）。
2. 获取真实游戏当前合法动作（`/actions/available`）作为模拟器动作枚举的校验。
3. 在真实游戏里执行动作（`/action`）——"把找到的最优路径真实打完"。

> 注意：自动搜索本身跑在离线模拟器（进程内）上，不经过本 API。本 API 只做桥接/校验/重放。

## HTTP 端点（`Server/Router.cs`）

基址：`http://127.0.0.1:8080`（被占时依次 8081…，可用 `STS2_API_PORT` 指定；`/health` 返回实际 `api_port`）。
端口在 STS2-Agent 作为模组启动时自动监听，无需手动配置。

| 方法/路径 | 说明 |
|---|---|
| `GET /health` | 服务状态；返回 `{ok, request_id, data:{service, mod_version, protocol_version, game_version, status, api_host, api_port, instance_role}}` |
| `GET /state` | 完整游戏状态 JSON（`GameStateService.BuildStatePayload`） |
| `GET /actions/available` | 当前屏幕合法动作列表（`BuildAvailableActionsPayload`） |
| `GET /events/stream` | SSE 事件流（见下） |
| `POST /action` | 执行一个动作，body 为 `ActionRequest` JSON |
| `GET /data/{collection}` | 游戏静态数据导出（cards/relics/monsters/potions/events…） |

统一响应包裹：`{ok:bool, request_id:string, data:..., error?:{code,message,details,retryable}}`。
错误码：400 参数错 / 409 状态不允许该动作（`invalid_action`、`invalid_target`） / 503 状态暂不可用（可重试）。

## /state 顶层字段（`GameStatePayload`）

`{ screen, in_combat, combat?, run?, meta?, crystal_sphere? }`

- `screen`：当前屏幕枚举字符串（COMBAT / MAP / EVENT / SHOP / REST / REWARD / CHEST / BOSS 等）。
- `combat`：`BuildCombatPayload(combatState)` —— 玩家（HP/格挡/能量/星/手牌数组/牌堆统计/能力/遗物/药水/宝珠）、
  敌方（HP/格挡/意图/能力/动作）、回合/轮次。
- `run`：`BuildRunPayload(...)` —— 角色、楼层、金币、遗物、牌组、种子、ascension 等。

精确字段级 schema 见 `STS2AIAgent/Game/GameStateService.cs`（`GameStatePayload`/`CombatPayload`/`RunPayload` 等类定义）。
对拍时以运行时 `/state` 实际 JSON 为准。

## /actions/available（`AvailableActionsPayload`）

`{ screen, actions: [...] }`，每个动作元素描述动作类型、所需参数（`card_index`/`target_index`/`potion_index`/
`choice_index` 等）。**这是"合法动作集"的权威来源**，供模拟器 `ActionReader` 对拍。

## /action（`ActionRequest`）

`{ action:string, ...参数 }`。动作执行在游戏线程，返回 `{ action, status: completed|pending, stable, message, state }`。

## 动作清单（从 MCP README 归纳，profile=guided 的高层动作）

- 状态/流程：`health_check`、`get_game_state`、`get_available_actions`、`act`、`wait_until_actionable`、
  `continue_run`、`save_and_quit`、`proceed`、`select_character`、`embark`…
- 战斗：`play_card`（card_index/target_index）、`end_turn`、`use_potion`（potion_index/target_index）、
  `discard_potion`（potion_index）、选牌弹窗 `select_deck_card`。
- 房间/地图：`choose_map_node`、`choose_event_option`、`choose_rest_option`、`open_chest`、`choose_treasure_relic`、
  `open_shop_inventory`、`buy_card/relic/potion`、`remove_card_at_shop`。
- 奖励：`collect_rewards_and_proceed`、`resolve_rewards`、`choose_reward_card`、`skip_reward_cards`。

> 我们在模拟器动作枚举里只关心**战斗内**子集：`play_card` / `end_turn` / `use_potion` / `discard_potion` /
> 选牌弹窗。其余是跑图阶段，本模组暂不处理。

## SSE 事件类型（`Server/GameEventService.cs`）

`session_started`、`screen_changed`、`combat_started`、`combat_ended`、`combat_turn_changed`、
`route_decision_required`、`reward_decision_required`、`event_state_changed`、`available_actions_changed`。
可用于轻量轮询替代（战斗开始/结束、动作窗口开合）。

## 启动/联调

- 启动游戏后确认 `http://127.0.0.1:8080/health` 可达；不可达则查 `api_port`（8081…）。
- 本机游戏 `mods/` 目前未安装 STS2-Agent —— 联调前需先安装其 release 到 `mods/STS2AIAgent/`。
- 本模组对 STS2-Agent 采用"运行时依赖（HTTP 调用）"，不引用其 DLL；按约定整仓采用 AGPL 许可。