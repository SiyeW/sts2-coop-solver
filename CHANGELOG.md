# 更新记录

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
