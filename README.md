# CrestronDeviceLibrary

Crestron 4-Series 中控项目：SIMPL# C# 设备库 + SIMPL+ 模块 + SIMPL Windows 主程序。

面向的设备控制场景：音频矩阵（StageCraft / IPS Libra）、摄像机（Sony VISCA）、电源时序器、视频矩阵、LED 控制器、门禁、继电器等。

---

## 仓库结构

```
CrestronDeviceLibrary-repo/
├── CrestronDeviceLibrary/          # C# SIMPL# 类库（VS 工程，编译产出 .clz）
│   ├── Devices/
│   │   ├── StageCraftMatrix.cs     # 16×16 音频矩阵（TCP 直连，核心模块）
│   │   └── SonyViscaCamera.cs      # Sony VISCA 摄像机
│   ├── Common/
│   │   ├── PacketBuilder.cs        # SIMPL# ↔ SIMPL+ 数据转换
│   │   └── ResponseParser.cs       # 应答解析
│   ├── DeviceManager.cs            # 设备管理器
│   └── Samples/                    # SIMPL+ 薄壳示例（.usp）
└── SIMPL-cp4/                      # SIMPL Windows 项目（cp4 处理器）
    ├── Demo.smw                    # 主程序
    ├── AudioMatrix_StageCraft.usp  # 音频矩阵模块（与 C# 库联动）
    └── *.usp / *.ush               # 其余设备模块（电源/视频矩阵/LED/门禁等）
```

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

**C# 库**：

1. VS 打开 `CrestronDeviceLibrary/CrestronDeviceLibrary.csproj`（.NET 4.7.2 + Crestron SimplSharp SDK 2.21.274 NuGet）
2. 编译，`bin/Debug/CrestronDeviceLibrary.clz` 即产物

**SIMPL+ 模块**：

1. SIMPL+ 编辑器打开 `.usp`，按 **F12** 编译生成 `.ush`
2. 编译前把 `CrestronDeviceLibrary.clz` 放到 SIMPL 项目目录
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

## 已验证功能（音频矩阵）

- 输入/输出增益加减与实时电平显示（250ms 全 16 路轮询）
- 静音/取消静音（含电平归零联动）
- 混音路由设置与已路由输入高亮反馈
- 预设调用
- 断线自动重连（5s 间隔）
