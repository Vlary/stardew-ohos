# 星露谷物语 · 鸿蒙 PC 移植（NativeAOT 方案）

把 Steam 正版《星露谷物语》1.6.15（.NET/MonoGame）移植到 HarmonyOS 7 / API 26（aarch64）真机运行。

**已验证功能**：主菜单 → 新的开始/加载存档 → 角色创建（立绘、键盘输入）→ 农场游玩 → 存档/读档（含多人农场）、合作模式（主持开房，UDP 24642 监听）、音频、键盘鼠标滚轮输入、中文字体。

---

## 架构

```
hap(ArkTS XComponent)
  └─ libSDL2.so（OHOS 后端：动态子 XComponent、EGL/GLES、OHAudio、输入桥）
       └─ libmain.so（NativeAOT，C#）
            ├─ stardew-host：SDL_main 入口、Content 提取、GlEsShim（FBO 读回）、SdvPaths
            ├─ MonoGame.Framework 3.8.0（二进制 IL patch：OHOS 适配）
            ├─ Stardew Valley.dll（二进制 IL patch：反射修复/键位/存档）
            ├─ Lidgren.Network（SHA256 → 纯托管哈希替换，NativeAOT 兼容）
            └─ OpenAL shim（OHAudio 直连，软件混音）
```

- **NativeAOT**（net9.0, linux-musl-arm64 经 OHOS clang 交叉编译）：自编 v9.0.20 运行时的 musl `.a`（GC 堆上限、NUMA 桩清空）
- **二进制 IL patch**（Mono.Cecil）：不改游戏源码，对三个 dll 做精确修补（详见 `mg-patch` / `sdv-patch` / `xml-patch`）
- **System.Private.Xml 修补**：NativeAOT 下 .NET 反射式 XmlSerializer 读取器的 6 类缺陷（集合 Add 解析歧义、字典参数形态、只读集合重建等），共 19 处
- **Content 分发**：hap rawfile 打包 → 首次启动应用内解压到沙箱

## 目录

| 目录 | 说明 |
|---|---|
| `stardew-host/` | 宿主入口（NativeAOT，产物 `libmain.so`） |
| `sdv-patch/` | Stardew Valley.dll 的 Cecil 修补工具 |
| `mg-patch/` | MonoGame.Framework.dll 的 Cecil 修补工具 |
| `xml-patch/` | System.Private.Xml.dll 的 Cecil 修补工具 |
| `helper/` | OHOS.Helper.dll（反射修复、克隆、哈希等运行期辅助） |
| `openal-shim/` | OpenAL → OHAudio 直连实现 |
| `sdl2-ohos/` | SDL2 for OHOS（含移植修改） |
| `hap/` | 鸿蒙工程（ArkTS + native） |
| `toolwrap/` | clang/lld 交叉编译包装器 |
| `build-game.sh` / `build-all.sh` | 构建脚本 |

## 构建

> **本仓库不包含任何游戏文件**。你需要自备 **Steam 正版《星露谷物语》**，并把游戏目录下的
> `MonoGame.Framework.dll`、`Stardew Valley.dll` 等依赖 dll 放到 `game-files/`。

1. **环境**：HarmonyOS Command Line Tools（含 SDK API 26）、.NET 9 SDK、自编 dotnet 运行时 musl `.a`
2. **签名配置**：`cp hap/build-profile.json5.template hap/build-profile.json5`，两种方式二选一：
   - `devecocli signature generate`（推荐：自动注册包名+登记设备，需华为开发者账号登录）
   - DevEco Studio 自动签名
3. **一键构建部署**：`./build-all.sh`（修补 → AOT → 打包 → 安装 → 启动）
4. 首次启动解压 Content（约 5 分钟）

## 已知限制

- 单机联机（局域网）：主机侧就绪；端到端需第二台设备验证
- 触摸板/触屏的合成点击在游戏内会转为鼠标动作（键鼠玩法不受影响）
- 仅支持 aarch64 HarmonyOS PC；未适配手机形态

## 版权

本项目为个人学习性质的移植工程，**不包含也不分发任何星露谷游戏资源/代码**（游戏 dll 与 Content 均需用户自备正版）。
Stardew Valley © ConcernedApe。MonoGame (MS-PL)、SDL2 (zlib)、Lidgren.Network (MIT)。
