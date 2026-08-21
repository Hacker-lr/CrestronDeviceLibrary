# CrestronDeviceLibrary

Crestron 3 系列 / 4 系列中控通用 SIMPL# 设备库：C# 类库 + SIMPL+ 模块 + SIMPL Windows 主程序。

面向的设备控制场景：音频矩阵（StageCraft / IPS Libra）、摄像机（Sony VISCA）、电源时序器、视频矩阵、LED 控制器、门禁、继电器等。

支持**双机热备冗余**（主/备音频处理器、自动切换、双向状态同步）。

---

## 仓库结构

```
CrestronDeviceLibrary-repo/
├── 4-Series/                          # ★ 4 系列工程（MC4，VTP 主用）
│   ├── CrestronDeviceLibrary.sln      # 4 代解决方案（唯一）
│   └── CrestronDeviceLibrary/         # C# SIMPL# 类库（编译产出 .clz）
│       ├── Devices/
│       │   ├── StageCraftMatrix.cs    # 16×16 音频矩阵（TCP 直连，核心模块）
│       │   ├── RedundantAudioMatrix.cs# 双机热备冗余矩阵（主备+同步+切换）
│       │   ├── BiampTesiraMatrix.cs   # Biamp Tesira 音频处理器
│       │   └── SonyViscaCamera.cs     # Sony VISCA 摄像机
│       ├── Common/                    # PacketBuilder / ResponseParser
│       ├── DeviceManager.cs           # 设备管理器
│       ├── CrestronDeviceLibrary.csproj  # VS .NET 4.7.2 + SDK 2.21.274
│       └── Samples/                   # SIMPL+ 薄壳 .usp（单台/冗余）+ 有效 .clz
│           ├── AudioMatrix_StageCraft.usp / .ush
│           ├── Biamp_Tesira.usp / .ush
│           ├── Redundant_AudioMatrix_StageCraft.usp
│           ├── Redundant_Biamp_Tesira.usp
│           ├── CAM_SONY_VISCA.usp
│           └── CrestronDeviceLibrary.clz  # 4 代有效库（SPlsWork/ 编译工作区）
├── 3-Series/                          # ★ 3 系列工程（MC3 运行库）
│   ├── CrestronDeviceLibrary.sln      # 3 代解决方案
│   └── CrestronDeviceLibrary/         # 通过 <link> 共享 4-Series 源码编译
├── scripts-tools/                     # 调试工具脚本（mc3 console/errlog、抓日志等）
├── packages/                          # 根/各工程 NuGet 还原包
└── CrestronDeviceLibrary.sln          # （已删除，避免与 4/3 代混淆）
```

> `Samples/` 目录在 `4-Series/CrestronDeviceLibrary/Samples/`（不在仓库根）。三处同步指：SIMPL 工程目录、桌面测试目录、此 Samples 目录。

## 双机热备冗余（RedundantAudioMatrix）

`SIMPL+ 薄壳 + C# 泛型<主,备>` 架构，支持任意"主从两类设备"配对（目前用于 StageCraft 与 Biamp）。

- **主备双连接**：各自的 TCP 连接 + 登录/轮询，状态独立互不阻塞。
- **角色与切换**：设备在线即为主；主掉线自动切备用，恢复在线自动切回（回切无瞬断）。
- **双向同步（Mirror）**：静音/电平/路由的变更自动镜像到对端，中控始终只显示"主用"状态源（`_syncingToPeer` 防回环）。
- **状态同步**：`SyncToPeer` 将设备端手动改动（含**路由**）同步给对端，配合镜像保证两端状态一致、VTP 显示及时刷新。
- **折叠防护**：SIMPL+ 反馈引脚用真数组占位（如 `_reserved3[1]`）防止被编译器折叠成 `[#]`；折叠会导致 C# `RaiseStatus()` 抛 `KeyNotFoundException` → 模块崩溃 → 显示离线。`.ush` 里见 `xxx[1]` 即展开成功。

> 曾修复：路由从机端改动不同步到主机/中控（`SyncToPeer` 跳过路由）；同步后出现主备路由"激活/取消交替闪烁"（`ToggleRoute` 沿用设备回读取反而非镜像绝对 `SetRoute`，现已改为镜像绝对状态）。

## 文件同步规则（重要，多目录踩坑）

`CrestronDeviceLibrary.clz` 与 `.usp` 需保持**三处同步**：

1. SIMPL 工程目录 `D:\Crestron\projector\Demo\simpl\cp4\`
2. 桌面测试目录 `C:\Users\YSL\Desktop\cp4\`
3. 宏源码库 `4-Series\CrestronDeviceLibrary\Samples\`

`.usp` 文件**必须 CRLF 行尾**——LF 会被 SIMPL+ 编译器静默吞掉声明区（不报错但产出 0 个引脚，`.ush` 中 `MinVariableInputs=0`）。保存时确认编辑器行尾为 "CRLF"。`3-Series/4-Series` C# 修改一次即可，但 `.clz` 需 3 代、4 代各编一份分别部署。

## 架构

```
VTP 触控面板 ── joins ── SIMPL Windows 程序 (Demo.smw)
                              │
                              ├── SIMPL+ 模块 (AudioMatrix_StageCraft.usp)
                              │       #USER_SIMPLSHARP_LIBRARY "CrestronDeviceLibrary"
                              │       RegisterDelegate(...) 绑定回调
                              │
                              └── CrestronDeviceLibrary.clz (C# 库)
                                      │
                                      └── TCPClient 直连设备 (IP:Port)
                                              ▲
                                              └── 音频处理器 / 摄像机 / ...
```

**为什么 C# 直连 TCP，而不是经 SIMPL+ 串口转发**：

- 设备二进制命令含 `0x00` 字节（如 Meter 查询 `82 7d 00 00 03 ...`）。
- SIMPL+ 的 `STRING`/`BUFFER` 是 NULL 结尾的 C 风格字符串，经委托转发含 `0x00` 数据会抛 `StringBuilder.Append` 异常。
- 结论：**所有含 0x00 的二进制协议必须由 C# 库直连设备 TCP**；纯 ASCII 协议（VISCA 等）可走委托。

## 音频矩阵协议要点（StageCraft / IPS Libra，TCP 1698）

同一条 TCP 连接上混用两种帧：

**1. ASCII 文本命令**（以 `#` 结尾，无 0x00，SIMPL+ 可转发）：

| 命令 | 含义 | 应答示例 |
|------|------|----------|
| `L1_add N#` / `L1_sub N#` | 输入 N 增益加减 | `PreLevel N:±X.XdB` |
| `L2_add N#` / `L2_sub N#` | 输出 N 增益加减 | `PostLevel N:±X.XdB` |
| `L1_Mute N#` / `L2_Mute N#` | 输入/输出静音 | `L1Mute:01001010...` |
| `ReadL1 N#` / `ReadL2 N#` | 查询输入/输出增益 | `PreLevel/PostLevel` |
| `LOADP N#` | 调用预设 N | — |

**2. 二进制帧**（`82 7d [payload] 7d 82`，可含 0x00，必须 C# 直连）：

- 帧头固定 **8 字节**：`[type 2B] [cmd] [func] [sub] [vary] [x x]`
- **Meter 数据从 `body[8]` 开始**，16 字节，每字节一路（输入 `func=07 sub=07`，输出 `func=0B sub=01`）
- **路由数据从 `body[9]` 开始**（`body[8]` = 输出通道号 out-1），16 字节每字节对应输入 1..16（01=已路由）
- 电平换算公式：`analog = (byte - 31) × 532`（无信号时字节为 0x9C=156，自然溢出为 ≈0）

> 注意：过时原宏 `AudioMatrx_IPS_Libra.usp` 声称"UDP 50000 + `set:/get:` 命令"，
> 实测设备并不响应。**真实协议是 TCP 1698**（原宏仅作参考）。

## 配置参数（在 `.usp` 文件头部改）

```simpl+
// =====================================================================
//  [设备连接配置] 改这里，改完重新编译（F12）
// =====================================================================
#DEFINE_CONSTANT DEVICE_IP    "192.168.0.222"
#DEFINE_CONSTANT DEVICE_PORT  1698
```

`#DEFINE_CONSTANT` 支持字符串字面量（IP 写完整一段即可）；STRING 变量不能声明时初始化，需在 `Main()` 里中转：

```simpl+
device_ip$ = DEVICE_IP;
matrix.Configure(device_ip$, DEVICE_PORT);
matrix.Start();                       // 直连 + 自动登录 + 断线自动重连
matrix.StartLevelPolling(250);        // 电平/音量表/静音 轮询（250ms 全 16 路）
```

## 构建与部署

**C# 库**（3 代、4 代各一份）：

1. 4 代：VS 打开 `4-Series/CrestronDeviceLibrary.sln` → 编译 → `4-Series/CrestronDeviceLibrary/bin/Debug/CrestronDeviceLibrary.clz`（约 3.98 MB）
2. 3 代：VS 打开 `3-Series/CrestronDeviceLibrary.sln` → 编译 → `3-Series/CrestronDeviceLibrary/bin/Debug/CrestronDeviceLibrary.clz`（约 1.21 MB）
3. 把对应 `.clz` 放入相应中控的 SIMPL 项目目录（工程 cp4 / 桌面 cp4 / Samples 三处同步）。

**SIMPL+ 模块**：

1. SIMPL+ 编辑器打开 `.usp`，按 **F12** 编译生成 `.ush`
2. 编译前确认 `CrestronDeviceLibrary.clz` 已放 SIMPL 项目目录、`.usp` 为 CRLF 行尾
3. SIMPL Windows 里重新编译程序 → Toolbox 上传 `.lpz`

**部署注意**：

- 若曾用 TCP/IP Client 符号连设备，直连改造后**必须删掉这些符号**（设备限连接数，旧连接会占满名额导致 C# 连不上/设备无响应）。
- 程序运行中直接覆盖 `.dll` 会触发 AppWatchdog 崩溃重启；正式部署一律走 Toolbox 上传 `.lpz`。

## 调试经验速查

| 症状 | 根因 | 修复 |
|------|------|------|
| Meter 电平不跳动/通道错位 | Meter 数据起始偏移错（用了 `body.Length-16`） | 固定 `dataStart = 8` |
| 静音后电平永久卡死 | 孤立 `0x82` 帧头阻塞解析循环 | 处理顺序 ASCII 优先 + 防溢出清空 |
| 输出 Meter 不更新（输入正常） | 整段 `Clear()` 缓冲误删粘连的二进制帧 | 二进制/ASCII 分开切帧，不整段 Clear |
| 二进制命令发不出去（err 日志 `StringBuilder.Append`） | SIMPL+ 委托遇 0x00 崩溃 | C# 直连设备 TCP |
| 直连后设备无响应 | 旧 TCP/IP Client 符号占着设备连接 | 删 .smw 里的 TCP/IP Client 符号 |
| 路由高亮错乱 | 路由数据读错偏移 | `body[8]`=输出号，`body[9..24]`=路由 |
| `Error 1307 Variables must be declared before array declarations` | SIMPL+ 单变量声明在数组之后 | 单变量移到所有数组声明之前 |

**SSH 直连调试**（推荐，比截图快得多）：中控处理器开 SSH 后可用 paramiko 直连，
`progstop -p:01` / `progstart -p:01` 控制程序，console 里能实时看到 `CrestronConsole.PrintLine` 输出。

## 已验证功能

**音频矩阵（单台 StageCraft）**：

- 输入/输出增益加减与实时电平显示（250ms 全 16 路轮询）
- 静音/取消静音（仅熄灭音量表，**不影响电平推子**设定值）
- 混音路由设置与已路由输入高亮反馈
- 预设调用
- 断线自动重连（5s 间隔）

**双机热备冗余（RedundantAudioMatrix / RedundantBiampTesira）**：

- 主备同时在线，主掉线自动切备、恢复自动回切
- 静音/电平/路由变更双向镜像同步，VTP 实时刷新
- 从机端手动改路由 → 主机跟随 → 中控显示同步更新，无闪烁回环
