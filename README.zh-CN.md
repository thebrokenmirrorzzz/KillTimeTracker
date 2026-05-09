# Kill Time Tracker

基于 SwiftlyS2 开发的 CS2 插件，实时记录并输出玩家击杀敌人所用的时间（从视野内锁定到完成击杀的耗时）。

## 功能

- **击杀耗时追踪** — 自动检测玩家视野角度内的敌人，记录从可见到击杀的精确耗时
- **可见性检测** — 向对方发射 9 条 TraceRay 判定敌人可见性
- **智能计时回退** — 优先使用可见时间，若敌人未进入视野则回退到首次造成伤害的时间
- **全局平均击杀耗时** — 显示玩家自连接以来的平均击杀耗时（跨回合累积）
- **回合击杀计数** — 显示玩家在本回合内的击杀总数（每回合自动重置）
- **耗时分级** — 根据耗时长短显示不同颜色：
  - <span color="#5eff3e">绿色</span> — 500ms 以内（快速反应）
  - <span color="#ffee00">黄色</span> — 500ms ~ 1000ms
  - <span color="#fa3b3b">红色</span> — 1000ms 以上
- **观战者显示转发** — 观战者也能看到被观战玩家的击杀耗时信息
- **多语言支持** — 内置简体中文和英文翻译
- **数据自动清理** — 玩家断开连接时自动清除其所有记录

## 显示效果

击杀后在 centerhtml 中显示两行信息，示例：

```
击杀耗时  ——  用时 352.1ms
平均击杀耗时 284.7ms - 3 回合击败
```

第一行为本次击杀的反应耗时，第二行为该玩家的全局平均值和回合击杀数。

## 安装

1. 从 [Releases](https://github.com/thebrokenmirror/KillTimeTracker/releases) 页面下载最新版本的 `KillTimeTracker.zip`
2. 将压缩包内文件夹解压到 CS2 服务器的 `game/csgo/addons/swiftlys2/plugins/` 目录
3. 重启服务器或使用 `sw plugins reload` 热重载生效

目录结构应为：

```
game/csgo/addons/swiftlys2/plugins/
└── KillTimeTracker/
    ├── KillTimeTracker.dll
    ├── KillTimeTracker.deps.json
    └── resources/
        └── translations/
            ├── en.jsonc
            └── zh-CN.jsonc
```

## 构建

### 前置要求

- .NET 10.0 SDK 或更高版本

### 构建步骤

```bash
git clone <repo-url>
cd KillTimeTracker
dotnet publish -c Release
```

构建产物：
- `build/publish/KillTimeTracker/` — 发布的插件文件目录
- `Release/KillTimeTracker-1.0.0.zip` — 即开即用的部署压缩包（直接解压到 `plugins/` 目录即可）

## 技术细节

### 工作流程

1. **检测阶段**（每 100ms）— 遍历所有存活玩家，通过视线追踪判断视野内是否有敌人，记录锁定起始时间
2. **伤害阶段**（`player_hurt` 事件）— 记录首次造成伤害的时间
3. **击杀阶段**（`player_death` 事件）— 计算耗时并输出结果
4. **回合重置**（`round_start` 事件）— 清除临时追踪数据，重置回合击杀计数
5. **断开清理**（客户端断开）— 清除该玩家的所有记录

### 可见性判定

插件使用以下策略进行敌人可见性判断：

- 视场角（FOV）限制为约 20 度（`cos ≈ 0.939`）
- 对目标发射 9 条不同偏移的 TraceRay 判定敌人可见性
- 任一条 TraceRay 未命中障碍物即视为可见
- 检测在异步线程中执行，避免阻塞主线程

### 数据结构

| 字典 | 键 | 值 | 说明 |
|---|---|---|---|
| `playerTargets` | `int` (Slot) | `TargetState` | 当前追踪的视线锁定状态 |
| `playerDamageTrack` | `int` (Slot) | `ConcurrentDictionary<int, DamageEntry>` | 伤害记录，用于计时回退 |
| `playerPermanentStats` | `ulong` (SteamID) | `PlayerPermanentStats` | 跨回合持久统计（平均耗时、击杀数） |

## 许可证

本项目基于 GNU GPLv3 许可证开源。
