# CoopSolver

《杀戮尖塔 2》的战斗辅助模组。路线按回合列出出牌、目标、选牌和用药，界面显示预计扣血。

## 战斗中使用

鼠标悬停在战斗界面顶部的信息栏上时，路线面板会展开。左上角的 ▶ 用于开始搜索。“实战最优”随实际操作更新，“用药”旁的菜单可以切换已经找到的方案。

单人可以用“自动打牌”执行当前路线，包括用药和选牌。“停止打牌”可以随时结束自动操作。

## 多人联机

房主开始搜索并开启“同步到全队”后，装了模组的队友可以通过“跟随主机”查看同一条路线。房主没装模组，队友也可以用“本机搜索”。没装模组的玩家照常参战。

多人路线按玩家分列，各人从上往下看自己那一列。“等 P2 相信着你”表示 P2 打出这张牌之后，下一步才可以执行。“只看自己”会保留自己的动作和需要等待的队友动作。

## 安装

CoopSolver 的安装包在 [Releases](https://github.com/SiyeW/sts2-coop-solver/releases/latest) 页面，依赖 RitsuLib 0.6.16。同一个安装包支持游戏 0.111.x 和 0.114.x。[RitsuLib 通用包](https://github.com/BAKAOLC/STS2-RitsuLib/releases/download/v0.6.16/STS2-RitsuLib.0.6.16.variant-pack.zip)

Steam 库中的“管理 → 浏览本地文件”可以打开游戏目录。两个模组各自解压到 `mods/` 下。

```text
mods/CoopSolver/CoopSolver.json
mods/STS2-RitsuLib/mod_manifest.json
```

重新启动游戏后，两个模组都可以在模组列表中启用。

- [更新记录](CHANGELOG.md)
- [第三方许可和来源声明](THIRD_PARTY_NOTICES.md)
