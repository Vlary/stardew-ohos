# Mod 支持研究（无 SMAPI 方案）

> 结论先行：**完整 SMAPI 生态在鸿蒙沙箱内不可行**（硬约束见下）；本移植提供**资产/数据级 Mod 机制**（今日已打通纹理替换并真机验证）+ 预留编译期代码 Mod 集成路线。

## 一、硬约束（为什么没有 SMAPI）

| 约束 | 证据 | 影响 |
|---|---|---|
| **沙箱禁止匿名可执行内存** | 移植调研期间实测：`mmap` RWX / `PROT_NONE→RWX` / `RW→RX` 三条路径全部被拒 | **JIT 永久不可用** → Harmony 运行时补丁不可用 → SMAPI 核心机制死路 |
| **NativeAOT 静态构建** | 本移植为 AOT 编译产物（`libmain.so`），运行时无法加载新程序集、无动态代码生成 | 任意 dll 插件加载不可能；Mod 必须**编译期**参与构建 |
| 裁剪/反射 | AOT 反射依赖 rd.xml root（本工程已全量 root 游戏程序集） | 代码 Mod 若走反射需同步补 root |

**附注**：Mono(JIT) 换运行时同样死于第一条（无 JIT = Mono 也只能 AOT）。桌面端的 SMAPI 恰好依赖 "运行时生成 IL"，与沙箱模型根本冲突。

## 二、可行方案分级

### L1 资产替换（✅ 已打通并真机验证）

**机制**：
1. Mod 产出目标资产（xnb 或松散文件），放 `mods/<mod>/out/<Content 相对路径>`
2. `tools/apply-mods.sh` 覆盖到 `hap/.../rawfile/Content/`
3. 打包 hap → `bm install -r`（保数据）→ 启动
4. 应用启动时对 hap 内 Content 与沙箱做**按大小差异同步**（host 智能同步）——**秒级**，无需 5 分钟重提取

**xnb 格式规范**（本移植实测 + 读端源码反推，SDV 1.6 / MonoGame 3.8 / XNB v5）：

```
外层：
  'X','N','B', platform('w'), version(5), flags(1B)
    flags: 0x80=LZX 0x40=LZ4（均压缩，后跟 int32 decompressedSize），0x01=HiDef
  int32 totalSize（含 10B 头；压缩分支下 compressedSize = totalSize-14）
  [内容区]
内容区：
  7bit  readersCount
        每 reader: 7bit len + UTF8 全名（如 DictionaryReader`2[[...]]） + int32（版本/附加，SDV 恒 0）
  7bit  sharedResourceCount（SDV 资产恒 0）
  对象：7bit readerIndex(1-based, 0=null) + reader 数据
    引用类型成员 = readerIndex + 数据；值类型成员 = 直读数据（无 index）
    Dictionary: int32 count + 每项 key/value（按上述规则）
    Texture2D: int32 format + uint32 w + uint32 h + int32 mips + [int32 len + RGBA data]（fmt=0 未压缩 RGBA）
    Reflective 类型: 先所有 Property 再所有 Field（声明序），逐成员按上述规则
写端策略：**输出未压缩**（flags 清 0x80/0x40，游戏读端正常分支读取）——绕开 LZX 编码器不存在的问题。
```

**工具**：`tools/xnbtool`（本地 net9.0 JIT，反射复用游戏 MonoGame 的 LzxDecoder 解压）

```bash
xnbtool unpack  <in.xnb> <out.raw>                     # 解出内容区
xnbtool packtex <模板.xnb> <rgba.bin> <w> <h> <out.xnb> # 纹理替换（保留 reader 表与格式）
xnbtool replace <in.xnb> <out.xnb> "旧文本" "新文本"      # 字符串定点替换（长度前缀自动重编码）
```

**已交付示例**：`mods/example-hue-weapons/`——顶栏工具图标色相轮换（纹理替换全链路真机验证）。

**能力边界（L1）**：
- ✅ 贴图替换（任意 Texture2D 资产：人物/NPC/地形/UI/物品图标…）
- ✅ 文本替换（对话/名称/描述——`replace`）
- ⚠️ 结构化数据修改（Data/*.xnb 为 `Dictionary<string, 强类型对象>`）：需按 Reflective 规则写端——可行但需要为每个数据类型复刻字段序（路线图见下）
- ⚠️ 音频（XACT 包：.xwb/.xsb/.xgs）：需 XACT 打包工具，暂未支持
- ⚠️ ContentHashes.json：多人模式会校验资产一致性——联机使用资产 Mod 时需同步更新 hash（单机无碍）

### L2 代码 Mod 编译期集成（🔬 可行，暂未实现）

把 Mod 的 C# 源码（或反编译产物）并入本工程 NativeAOT 构建：
- Harmony patch 点 → **构建期翻译为 Cecil IL patch**（本工程已有成熟范例：mg-patch/sdv-patch/xml-patch 共 100+ 处）
- Mod 反射 → 补 rd.xml root
- 每个 Mod 需人工适配（等价于"移植该 Mod"）

### L2.5 结构化数据 Mod 打包器（📋 路线图）

写一个 JIT 工具（引用 `StardewValley.GameData.dll` + MonoGame）：
- 输入：`Data/<表>.xnb` 模板 + JSON 补丁
- 按 ReflectiveReader 字段序重放写端 → 未压缩 xnb
- 覆盖 Data 数值/物品/任务等大批 mod 场景

### L3 完整 SMAPI（❌ 不可行）

见硬约束表——JIT 禁令是沙箱级设计，非工程可绕。

## 三、给 Mod 作者的建议

1. **优先做资产 Mod**（贴图/文本）——L1 全自动支持
2. 纹理请保持**原始尺寸与 RGBA**（`packtex` 输出 mipmap=1，原资产若有多级 mip 会被简化）
3. 文本替换的"旧文本"串请给足够长的上下文（避免二进制误命中）
4. 面向联机的 Mod 注意 ContentHashes 一致性（建议单机使用）
