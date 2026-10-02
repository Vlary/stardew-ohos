# 示例 Mod：工具图标色相轮换

把顶栏工具图标（斧/锄/壶/镐）的 RGB 通道轮换（R←G←B），形状保留、颜色明显变化。

- 目标资产：`TileSheets/weapons.xnb`（128×144 RGBA）
- 产物：`out/TileSheets/weapons.xnb`（未压缩 xnb，游戏可直接读取）
- 生成：`src/weapons-hue.png` 为改色后贴图；由 `../../tools/xnbtool` 的 packtex 打包
- 应用：`tools/apply-mods.sh` 将 `out/` 覆盖到 hap rawfile，随后构建部署；
  应用启动时"智能同步"会把变更文件写入沙箱 Content（秒级），进游戏即可见。
