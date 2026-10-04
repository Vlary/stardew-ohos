# 数据 Mod 示例：防风草种子售价 8 → 888

演示**数据 Mod 写端**（xnbtool patchdata）：按 MonoGame 反射读取器规范
切分 Data/Objects.xnb（Dictionary<string, ObjectData>），定点替换字段，重打包为未压缩 xnb。

- 目标：`Data/Objects.xnb` 键 `472`（防风草种子）的 `Price` 字段
- 可见性：开新（多人）农场 → 背包开局即有 15 个防风草种子 → 悬停查看售价
