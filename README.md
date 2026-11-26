# CoopSolver

### [中文](#中文) | [English](#english)

---

<div lang="zh-CN">

## 中文

**《杀戮尖塔 2》多人战斗路线求解器，同时支持单人模式。**



CoopSolver 基于 [CombatSolver](https://github.com/Torch1230/CombatSolver) 的战斗模拟环境开发，旨在实现其尚不支持的多人战斗求解。项目采用独立设计的搜索算法，根据当前战况搜索后续各回合的出牌顺序、攻击目标、选牌及药水使用方案，并预测战斗中的生命值损失。

### 功能特性

#### 多人协同求解

CoopSolver 将所有玩家纳入同一战斗环境进行求解，综合考虑不同玩家的手牌、能量、遗物、药水及卡牌之间的相互作用，搜索全队的战斗路线。



在多人战斗中，并非所有玩家的行动都需要严格遵循同一出牌顺序。CoopSolver 会分析行动之间的依赖关系，将求解结果拆分为各玩家的独立行动序列：

- 互不影响的行动可以由各玩家自行执行。
- 存在先后依赖关系的行动会标注等待提示，并通过箭头指向队友需要完成的对应操作。
- 支持仅查看自己的行动及相关的队友行动。

例如，当一名玩家需要等待队友先为敌人施加易伤时，界面会明确标出需要等待的那张牌；其余互不影响的出牌操作则可以同时进行。

#### 战斗路线可视化

在游戏内提供紧凑的战斗求解面板，以回合为单位展示完整的战斗路线。

- 单人列表与多人分栏布局。
- 显示出牌顺序、目标、伤害、剩余能量、选牌及药水使用方式。
- 预计战损及战损变化图表。
- 不同候选战斗路线的查看与切换。
- 随实际战斗操作更新求解结果。

#### 搜索算法

CoopSolver 在现有战斗模拟环境的基础上实现了独立的搜索算法，并引入了一组可通过机器学习优化的局面评估参数，用于辅助搜索过程中对不同战斗局面的评估与筛选。

#### 自动打牌

单人模式支持自动执行求解路线，包括出牌、选牌及使用药水。玩家可以随时停止自动打牌，恢复手动操作。

#### 联机同步

房主可以将求解结果实时同步给队友，无需每位玩家分别进行搜索。



CoopSolver 不要求联机房间内的所有玩家均安装本 Mod：

- 房主与队友均已安装时，队友可以直接查看房主的求解结果。
- 房主未安装时，队友仍可在本机独立求解。
- 未安装 Mod 的玩家可以正常参与战斗。

### 下载与安装

当前支持游戏版本：**0.111.x / 0.114.x**

1. 从 [Releases](https://github.com/SiyeW/sts2-coop-solver/releases/latest) 下载最新版本的 CoopSolver。
2. 下载前置 Mod [RitsuLib 0.6.16](https://github.com/BAKAOLC/STS2-RitsuLib/releases/download/v0.6.16/STS2-RitsuLib.0.6.16.variant-pack.zip)。
3. 在 Steam 库中右键点击《杀戮尖塔 2》，选择「管理 → 浏览本地文件」。
4. 将两个 Mod 分别解压至游戏安装目录下的 `mods/` 文件夹中。
5. 启动游戏，在模组列表中启用 CoopSolver 和 RitsuLib。

安装完成后，应确保以下文件位于对应目录中：

- `mods/CoopSolver/CoopSolver.json`
- `mods/STS2-RitsuLib/mod_manifest.json`

### 使用方法

进入战斗后，将鼠标悬停在界面顶部的 CoopSolver 信息栏上，即可展开求解面板。点击左上角的 ▶ 开始搜索。



**单人模式**



可以手动按照推荐路线出牌，也可以使用「自动打牌」执行当前路线。通过「用药」旁的下拉菜单可以查看其他已搜索到的方案。



**多人模式**



房主开始搜索后，开启「同步到全队」。队友选择「跟随主机」，即可查看同一条战斗路线。



每位玩家按照自己所在列的行动顺序操作即可，遇到等待提示时，待队友完成箭头所指向的操作后再继续出牌。

### 更新与反馈

- 版本更新记录
- [问题反馈与功能建议](https://github.com/SiyeW/sts2-coop-solver/issues)

### 致谢

本项目使用基于 [Co-op Bots](https://github.com/imbalilang/STS2-Co-op_Bots) 修改的战斗模拟内核。感谢以下项目及其开发者对《杀戮尖塔 2》开源 Mod 生态的贡献：

- [CombatSolver](https://github.com/Torch1230/CombatSolver)
- Co-op Bots
- Random Foreseer
- [RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)

### 开源许可

本项目原创代码采用 Apache License 2.0 开源，第三方组件遵循各自的开源许可，详见 THIRD_PARTY_NOTICES.md。

</div>

---

<div lang="en">

## English

**A multiplayer combat solver for Slay the Spire 2, with full support for single-player combat.**

CoopSolver builds upon the combat simulation environment of [CombatSolver](https://github.com/Torch1230/CombatSolver), extending combat solving to multiplayer games. It features an independently developed search algorithm that explores combat plans across multiple turns, determining card play order, targeting, card selections, and potion usage while predicting HP loss.

### Features

#### Cooperative Combat Solving

CoopSolver searches for combat plans for the entire party within a shared simulation environment, taking into account each player's hand, energy, relics, potions, and interactions between cards played by different players.

In multiplayer combat, not every action needs to follow a strict global sequence. CoopSolver analyzes the dependencies between actions and breaks down the combat plan into individual action sequences for each player:

- Independent actions can be performed without waiting for other players.
- Actions that depend on another player's move are marked with waiting indicators, with arrows pointing to the specific prerequisite actions.
- Players can filter the display to show only their own actions and relevant actions from teammates.

For example, if a player needs a teammate to apply Vulnerable before playing an attack, the interface indicates exactly which card they need to wait for. Other independent actions can proceed without unnecessary coordination.

#### Combat Plan Visualization

A compact in-game interface presents complete combat plans on a turn-by-turn basis.

- Single-player list and multiplayer column layouts.
- Card play order, targets, damage, remaining energy, card selections, and potion usage.
- Predicted HP loss and HP loss charts.
- Browsing and switching between alternative combat plans.
- Updated combat plans based on actual player actions.

#### Search Algorithm

CoopSolver implements its own search algorithm on top of the existing combat simulation environment. It incorporates a set of state evaluation parameters designed to be optimized through machine learning, guiding the search toward more promising combat states.

#### Auto Play

In single-player mode, CoopSolver can automatically execute the selected combat plan, including playing cards, making card selections, and using potions. Players can stop auto play and resume manual control at any time.

#### Multiplayer Synchronization

The host can synchronize combat plans with other players in real time, eliminating the need for each player to run a separate search.

**CoopSolver does not require everyone in the lobby to have the mod installed.**

- When both the host and other players have CoopSolver installed, players can view the host's combat plans directly.
- If the host does not have CoopSolver installed, other players can still run their own local searches.
- Players without the mod can participate in combat as usual.

### Download & Installation

Currently supported game versions: **0.111.x / 0.114.x**

1. Download the latest version of CoopSolver from [Releases](https://github.com/SiyeW/sts2-coop-solver/releases/latest).
2. Download the required dependency, [RitsuLib 0.6.16](https://github.com/BAKAOLC/STS2-RitsuLib/releases/download/v0.6.16/STS2-RitsuLib.0.6.16.variant-pack.zip).
3. Right-click Slay the Spire 2 in your Steam library and select **Manage → Browse local files**.
4. Extract both mods into the `mods/` directory inside the game installation folder.
5. Launch the game and enable both CoopSolver and RitsuLib in the mod list.

After installation, make sure the following files are in their respective locations:

- `mods/CoopSolver/CoopSolver.json`
- `mods/STS2-RitsuLib/mod_manifest.json`

### Usage

During combat, hover over the CoopSolver status bar at the top of the screen to expand the solver panel. Click the ▶ button in the upper-left corner to start searching.

#### Single-player

Follow the suggested combat plan manually, or enable Auto Play to execute it automatically. Use the dropdown next to the potion usage option to browse alternative combat plans found by the solver.

#### Multiplayer

Once the host starts searching, enable combat plan synchronization. Other players can select **Follow Host** to view the same combat plan.

Each player follows the action sequence in their own column. When a waiting indicator appears, wait for the teammate to complete the action indicated by the arrow before proceeding.

### Updates & Feedback

- [Changelog](CHANGELOG.md)
- [Bug Reports & Feature Requests](https://github.com/SiyeW/sts2-coop-solver/issues)

### Acknowledgements

CoopSolver uses a modified version of the combat simulation core from [Co-op Bots](https://github.com/imbalilang/STS2-Co-op_Bots).

Special thanks to the following projects and their developers for their contributions to the Slay the Spire 2 modding community:

- [CombatSolver](https://github.com/Torch1230/CombatSolver)
- [Co-op Bots](https://github.com/imbalilang/STS2-Co-op_Bots)
- Random Foreseer
- [RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)

### License

Original code in this project is licensed under the [Apache License 2.0](LICENSE). Third-party components are subject to their respective licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for details.

</div>
