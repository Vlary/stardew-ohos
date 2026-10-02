# Mods 目录（无 SMAPI 的轻量 Mod 机制）

鸿蒙移植为 NativeAOT 构建，**不支持 SMAPI/Harmony**（沙箱禁止运行时代码生成）。
本目录提供编译期/资产级的 Mod 支持——三类形态：

```
mods/<mod名>/
  manifest.md            说明（可选）
  out/<Content 相对路径>  最终产物：直接覆盖进 Content（xnb/松散文件均可）
  src/                   生成脚本与源素材（可选）
```

打包 xnb 用 `tools/xnbtool`（解包/纹理打包/字符串替换，详见其 README）：

```bash
# 纹理替换（png → 未压缩 xnb）
dotnet tools/xnbtool/bin/Release/net9.0/xnbtool.dll packtex \
  <模板.xnb> <rgba.bin> <宽> <高> out/<路径>.xnb
# 字符串替换（对话/名称/描述等）
dotnet tools/xnbtool/bin/Release/net9.0/xnbtool.dll replace \
  <原.xnb> out/<路径>.xnb "旧文本" "新文本"
```

应用全部 mod 并构建：

```bash
tools/apply-mods.sh && ./build-all.sh hap   # 覆盖 rawfile → 打包
```

> 原理：应用启动时对 hap 内 Content 与沙箱做「按大小差异同步」，
> 因此只需重新打包 hap（~25s）并覆盖安装（保数据），**无需重跑 5 分钟首次提取**。
