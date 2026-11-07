# 小米蓝牙手柄 Windows 11 XInput 虚拟驱动技术设计方案

> **归档说明 (2026-11)**：这是项目最初的设计稿原文存档。文中"输入报告"的字节偏移表为早期推测，
> 与真机实测不符（正确布局见根目录 [DESIGN.md](../DESIGN.md)，已通过真机报文验证）。
> 震动通路同样有重大修订：设备特征报文为 7 字节（而非 3 字节），且无输出报文通路，
> 详见 DESIGN.md 第三节。本文仅作历史参考。

本方案旨在为**小米蓝牙手柄（型号：MDZ-11-AA）**提供现代 Windows 11 环境下的原生 Xbox 360 控制器（XInput）接入支持，彻底解决原生 DirectInput 游戏兼容差、缺少震动反馈、蓝牙休眠断连，以及与其他手柄共存时的“幽灵手柄/按键双发”问题。

---

## 一、系统整体架构

```mermaid
flowchart TD
    subgraph HW["物理层 (Hardware)"]
        XiaomiPad["小米蓝牙手柄 (MDZ-11-AA)"]
        OtherPad["其它手柄 (Xbox / PS5 / Switch)"]
    end

    subgraph OSKernel["Windows 11 内核层 (Kernel Space)"]
        BthHID["bthhid.sys (系统经典蓝牙 HID 驱动)"]
        HidHideFilter["HidHide.sys 过滤驱动 (物理设备隐藏)"]
        ViGEmBus["ViGEmBus.sys (虚拟游戏手柄总线驱动)"]
    end

    subgraph UserSpace["用户态驱动核心 (MiGamepad Core Service)"]
        DevWatcher["设备热插拔与状态机 (DeviceWatcher)"]
        HidStream["HID 原始数据流读写器 (HidApi / FileStream)"]
        ReportParser["21字节报文解码器 (Button / Stick / Trigger)"]
        RumbleSync["震动节流反馈器 (30Hz 脏检测 + SetFeature 0x20)"]
        HidHideAPI["HidHide 自动配置客户端"]
        ViGEmClient["ViGEmClient (虚拟 X360 控制器管理)"]
    end

    subgraph Games["游戏与应用层"]
        XInputGames["游戏 (Steam / Xbox App / 3A 大作)"]
    end

    XiaomiPad --> BthHID
    OtherPad --> Games
    BthHID --> HidHideFilter
    HidHideFilter -.->|屏蔽DirectInput| Games
    HidHideFilter -->|白名单放行| HidStream

    DevWatcher -->|热插拔通知| ViGEmClient
    HidStream --> ReportParser
    ReportParser --> ViGEmClient
    ViGEmClient --> ViGEmBus
    ViGEmBus --> Games

    Games -.->|XInput 震动事件| ViGEmBus
    ViGEmBus -.->|Rumble 回调| ViGEmClient
    ViGEmClient --> RumbleSync
    RumbleSync -->|SetFeature 0x20| HidStream
    HidStream --> XiaomiPad
```

---

## 二、通信协议逆向与报文格式

### 1. 设备基本参数
* **Vendor ID (VID)**: `0x2717` (Xiaomi)
* **Product ID (PID)**: `0x3144`
* **蓝牙芯片**: Broadcom BCM20730 (Bluetooth 3.0 HID Profile)

### 2. 输入报告 (Input Report, 长度 21 字节)
每当手柄产生按键动作、扳机按压或摇杆位移，系统会收到 21 字节的原始数据包：

| 字节偏移 (Offset) | 字段名称 | 取值范围 | 说明 |
| :--- | :--- | :--- | :--- |
| **Byte 0** | Report ID | 固定 `0x04` 或 `0x00` | 输入报文标识 |
| **Byte 1** | Left Stick X | `0x00 ~ 0xFF` | 左摇杆水平轴（中心约 `0x80`） |
| **Byte 2** | Left Stick Y | `0x00 ~ 0xFF` | 左摇杆垂直轴（中心约 `0x80`） |
| **Byte 3** | Right Stick X (Z) | `0x00 ~ 0xFF` | 右摇杆水平轴（中心约 `0x80`） |
| **Byte 4** | Right Stick Y (Rz)| `0x00 ~ 0xFF` | 右摇杆垂直轴（中心约 `0x80`） |
| **Byte 5** | Left Trigger (LT) | `0x00 ~ 0xFF` | 线性左扳机（松开 `0`，压到底 `255`） |
| **Byte 6** | Right Trigger (RT)| `0x00 ~ 0xFF` | 线性右扳机（松开 `0`，压到底 `255`） |
| **Byte 7** | D-Pad (POV/Hat) | `0x00 ~ 0x08` | 十字键（0=无按下, 1=上, 2=右上, 3=右...） |
| **Byte 8** | Buttons 1 | 位掩码 | Bit 0: A, Bit 1: B, Bit 3: X, Bit 4: Y, Bit 6: LB, Bit 7: RB |
| **Byte 9** | Buttons 2 | 位掩码 | Bit 0: LS(L3), Bit 1: RS(R3), Bit 2: Back, Bit 3: Start |
| **Byte 10** | Special Buttons | 位掩码 | Bit 0: MI 键 (中心小米徽标键，映射 Xbox Guide) |
| **Byte 11~12**| Reserved / Battery| 状态数据 | 电量百分比与连接标志 |
| **Byte 13~18**| Accelerometer | 3 × Int16 LE | 内置 3 轴重力传感器原始数据 |

### 3. 震动特征包 (SetFeature Report, 长度 3 字节)
通过调用 Windows HID 的 `HidD_SetFeature` 或写特征报告向手柄下发马达控制指令：
```text
[0x20] [Small Motor Strength (0x00-0xFF)] [Big Motor Strength (0x00-0xFF)]
```
* **Byte 0**: `0x20`（小米手柄专属震动指令 ID）
* **Byte 1**: 右侧弱震马达（高频细震）强度
* **Byte 2**: 左侧强震马达（低频重震）强度

---

## 三、核心模块与关键机制设计

### 1. 解决双重输入冲突（HidHide 自动化隐藏）
* **原理**：利用 `Nefarius.Drivers.HidHide` 提供的 IOCTL 控制接口。
* **流程**：
  1. 启动驱动服务时，获取自身进程名与可执行文件路径，加入白名单。
  2. 枚举匹配 `HID\VID_2717&PID_3144`，将其加入 HidHide 的阻止列表（Blocked List）。
  3. 此时所有普通应用程序（Steam、各类游戏进程）无法再读取到物理手柄发出的 DirectInput 信号，只有白名单中的本驱动可以正常读写。
  4. 游戏仅能看到虚拟出的 Xbox 360 控制器，从根本上解决按键双发与多手柄混用混乱。

### 2. 多手柄共存与 XInput 动态槽位分配
* 用户同时使用多个小米手柄，或者同时连接原生 Xbox One 手柄时：
  * **唯一设备标识**：通过 Windows 提供的设备实例路径（Device Path 中包含蓝牙物理地址，形如 `_001122334455`）对每个物理手柄单独实例化。
  * **虚拟手柄动态管理**：每个物理连接对应一个独立的 `IXbox360Controller` 实例。手柄物理断开（关机/休眠）时立即调用 `Disconnect()` 释放 XInput 槽位，手柄重新连入时自动申请空闲槽位（Slot 0~3）。

### 3. 震动回传节流与保护机制 (Rumble Rate Limiter)
* 游戏调用 XInput 震动时，频率往往超过 100Hz。
* **节流器算法**：
  * 限制更新频率为 30Hz（即两次下发最小间隔 33ms）。
  * 脏数据检查：如果当前震动数值与上一帧相同，不重复下发，避免蓝牙信道拥塞导致通信卡顿与断连。

### 4. 摇杆非线性校准与死区保护
* 针对老化手柄摇杆物理漂移，引入可调节的死区半径：
  $$\Delta X = X_{raw} - 128, \quad \Delta Y = Y_{raw} - 128$$
  $$\text{Distance} = \sqrt{\Delta X^2 + \Delta Y^2}$$
  当 $\text{Distance} < \text{Deadzone}$ 时输出 0，否则线性平滑映射到 `[-32768, 32767]`。

---

## 四、工程实施步骤

1. **基础设施安装**：
   - 依赖驱动：安装最新的 ViGEmBus 驱动与 HidHide 驱动。
   - 编译环境：配置 .NET 8 SDK。
2. **构建 MiGamepad.Core**：
   - 实现设备枚举与热插拔监听 (`DeviceWatcher`)。
   - 实现 21 字节输入报告到 `Xbox360Report` 的解码映射。
   - 实现带限流机制的震动回发控制。
   - 集成 HidHide 驱动的自动配对白名单。
3. **构建应用程序与托盘服务**：
   - 实现后台托盘无感运行、开机自启、多设备状态展示（电量、当前对应 Player 槽位）。
