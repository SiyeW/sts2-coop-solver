# 更新记录

## 0.10.44

<div lang="zh-CN">

### 简体中文

- CoopSolver 自带所需的组件，RitsuLib 现在为非必须。
- 优化偏离推荐路线后的处理行为。实战续算会保留已核实分支的搜索进度。
- 改进用药后的选牌指示和伤害显示，修复理论最小损失提示的误判。
- 修正部分卡牌、药水、遗物和敌人招式的错误战斗模拟。
- 修复分列图偶尔无法显示的问题，并减少多人路线中不必要的等待提示。
- 修复部分情况下搜索意外中断的问题。
- 修复自动打牌会卡在选牌界面的问题。

</div>

<div lang="en">

### English

- CoopSolver now bundles the components it needs, making RitsuLib optional.
- Improved how the solver handles departures from the recommended plan. Live search keeps its progress when resuming from a verified branch.
- Improved card selection prompts after potion use and damage display. Fixed incorrect confirmations of the theoretical minimum HP loss.
- Fixed combat simulation errors affecting some cards, potions, relics, and enemy moves.
- Fixed cases where the multiplayer action columns failed to appear and reduced unnecessary wait indicators.
- Fixed cases where search could stop unexpectedly.
- Fixed Auto Play getting stuck on card selection.

</div>

## 0.10.43

<div lang="zh-CN">

### 简体中文

- 界面支持 16 种语言。
- 修复单人自动打牌时路线停在旧回合，以及联机跟随时路线更新不及时的问题。
- 修补部分药水的选牌搜索。修复搜索树达到内存上限后，回收内存过于激进的问题。
- 简化面板操作，改进目标标签、玩家配色和图表显示。

</div>

<div lang="en">

### English

- The interface now supports 16 languages.
- Fixed plans lagging behind the current turn during solo Auto Play and staying out of date when following the host.
- Fixed card selection for some potions and excessive pruning when the search tree reached its memory limit.
- Streamlined the combat panel and improved target labels, player colors, and charts.

</div>

## 0.10.37

- 同一个安装包支持游戏 0.111.x 和 0.114.x，需要 RitsuLib 0.6.16。切换 Steam 分支后重启游戏即可。
- 支持公开测试版角色 The Fool，适配 v0.114.0 中改动的卡牌和敌人招式，修复部分遭遇进入战斗后无法搜索的问题。
- 正常换回合时保留实战最优的路线菜单和图表历史，及时移除已完成的动作。
- 单人列表和多人分列都支持两行动作。伤害计入实际打掉的格挡和生命，多个合法顺序下的伤害和余费用 `|` 列出。
- 修复打开下拉菜单时文字清晰度变化，以及等待分列图时面板高度闪动的问题。

安装要求见 [README](README.md)。
