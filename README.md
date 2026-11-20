# StS2 Co-op Solver

[中文](#中文) · [English](#english)

<div lang="zh-CN">

## 中文

《杀戮尖塔 2》单人及多人战斗求解器。查看每位玩家的出牌顺序、目标、选牌和用药，自行操作。多人局由房主计算，装了同版模组的队友可以查看共享路线，其他队友也可以正常进房。

### 安装

1. 在 Steam 创意工坊订阅 [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295)（0.6.14 或更高版本）。
2. 从 [Releases](https://github.com/SiyeW/sts2-coop-solver/releases/latest) 下载 `CoopSolver-版本号.zip`。在 Steam 库中右键游戏 →「管理」→「浏览本地文件」，将压缩包里的整个 `CoopSolver` 文件夹解压到 `mods`。安装后应能看到 `mods/CoopSolver/CoopSolver.json`。
3. 重启游戏，启用 RitsuLib 和「多人战斗求解器」。已在 Windows、游戏 0.111.1 上验证。

### 使用

将鼠标移到战斗信息栏，点击开始搜索。单击长条固定展开，再点收起。拖动可以移位，滚轮缩放整个面板。设置中可以打开副屏窗口并调整 CPU 上限。

- **只搜开局**把全部算力用于战斗起点。**搜实战**随出牌推进，默认给开局预留 20%。两行分别显示各自找到的最好结果，切换操作列表可以对照查看。
- **扣2/36 打8**表示这名玩家本回合净扣 2 血、回合末余 36 血、造成 8 点伤害。点击「只看自己」可折叠队友操作。
- **隐藏结果**让全队只看计算量，搜索继续。**暂停搜索**同时暂停两路。重载同一场战斗后，可以恢复已保存的开局路线。自定义角色或第三方战斗机制的结果可能需要实战核对。

</div>

<div lang="en-US">

## English

A combat solver for solo and co-op runs in Slay the Spire 2. Follow the suggested card plays, targets, card selections and potion uses manually. In co-op, the host runs the search. Teammates with the same mod version can see the host’s route, and teammates without the mod can still join.

### Install

1. Subscribe to [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) on Steam Workshop (0.6.14 or later).
2. Download `CoopSolver-<version>.zip` from [Releases](https://github.com/SiyeW/sts2-coop-solver/releases/latest). Right-click the game in your Steam library and choose **Manage → Browse local files**. Extract the entire `CoopSolver` folder into `mods`. Check that `mods/CoopSolver/CoopSolver.json` exists.
3. Restart the game and enable RitsuLib and CoopSolver. Tested on Windows with game version 0.111.1.

### Use

Hover over the combat bar to start the search. Click the bar to pin or unpin the panel, drag to reposition it, or use the wheel to zoom. Settings include a separate window for a second monitor and a CPU core limit.

- **只搜开局** spends the full search budget on the opening position. **搜实战** follows the current position and reserves 20% of search time for the opening by default. The bar shows the best result found for each position. Switch between their action lists to inspect either route.
- **扣2/36 打8** means the player loses a net 2 HP, ends the turn at 36 HP and deals 8 damage that turn. Select **只看自己** to fold teammates’ actions.
- **隐藏结果** hides recommendations for everyone while the search counter keeps updating. **暂停搜索** pauses both searches. Reloading the same fight can restore a saved opening route. Predictions for custom characters and third-party combat mechanics may differ from play.

</div>

## 构建 / Build

Windows PowerShell · Visual Studio 2022 Build Tools (.NET desktop build tools) · Slay the Spire 2 · RitsuLib

```powershell
$library = 'D:\SteamLibrary'
.\scripts\build.ps1 -GameDir "$library\steamapps\common\Slay the Spire 2" -RitsuRuntime "$library\steamapps\workshop\content\2868840\3747602295\compat\0.111.0\STS2-RitsuLib.Runtime.dll"
```

输出 / Output: `build/CoopSolver-<version>.zip`

## 许可 / License

CoopSolver · [Apache-2.0](LICENSE)

Co-op Bots Kernel · CombatSolver · Random Foreseer · [第三方许可 / Third-party notices](THIRD_PARTY_NOTICES.md)
