# MiGamepad - 小米蓝牙手柄 Windows 11 XInput 虚拟驱动

本项目是专为**小米蓝牙手柄（型号：MDZ-11-AA）**量身打造的 Windows 11 原生 Xbox 360 控制器映射驱动与守护程序。

---

## 🌟 核心特性

1. **虚拟 Xbox 360 控制器（XInput 协议）**：
   - 彻底摆脱老旧 DirectInput 兼容性差的问题，所有现代 PC 游戏（Steam、Xbox Game Pass、EA App、Epic 等）即插即玩。
2. **多手柄稳定共存（支持 4 手柄同台）**：
   - 支持**小米手柄与官方 Xbox 手柄、PS5/PS4 手柄、Switch Pro 手柄同时连接并共用**进行本地多人联机（如《双人成行》、《胡闹厨房》、《FC 24》等）。
   - 独立分配 XInput 槽位（Player 1 ~ 4），各手柄按键与震动互不干扰。
3. **彻底杜绝“双重输入 / 幽灵手柄”（HidHide 深度集成）**：
   - 自动调用 `HidHide` 内核过滤驱动，向系统和所有游戏进程隐匿小米手柄的原生 DirectInput 物理节点，仅放行虚拟的 Xbox 360 设备，彻底解决“按一次跳两下”或“识别出多余玩家”的痛点。
4. **原生双无极马达触觉震动反馈（Rumble）**：
   - 监听 XInput 震动事件，经**30Hz 脏检查限流器**转换为小米专有 HID 特征报文（`0x20`），不丢包、不卡顿、不打爆蓝牙信道。
5. **蓝牙休眠唤醒自愈与热插拔**：
   - 手柄闲置 5 分钟断电休眠后，再次按下 MI 键即可秒级自动重新绑定虚拟 Xbox 设备，无需重启游戏或重新打开软件。
6. **中心 MI 徽标键映射**：
   - 将手柄中央 MI 键映射为 Xbox Guide 键，一键唤起 Windows 11 的 Xbox Game Bar（游戏工具栏）。

---

## 🚀 快速上手与测试流程

### 1. 安装底层驱动
以**管理员身份**打开 PowerShell，运行项目内置的驱动安装脚本：
```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Install-Drivers.ps1
```
> 该脚本会自动检测并下载安装由微软数字签名的 `ViGEmBus`（虚拟手柄总线）和 `HidHide`（设备隐藏过滤驱动）。

### 2. 小米手柄蓝牙配对
1. 按下手柄正中间的 **MI 徽标键** 开机。
2. 若未配对，长按 MI 键直到指示灯**快速双闪**进入蓝牙配对模式。
3. 打开 Win11 设置 -> **蓝牙和其他设备** -> **添加设备** -> **蓝牙**。
4. 选择并连接 **"Xiaomi Bluetooth Wireless Gamepad"**。

### 3. 运行程序
启动服务：
```powershell
dotnet run --project .\src\MiGamepad.App\MiGamepad.App.csproj
```
启动后程序会自动扫描并接管已连接的小米手柄，在终端中实时显示设备状态与电量。
* **按 `V` 键**：触发当前已连接手柄震动测试（1秒）。
* **按 `C` 键**：清屏并重新显示状态看板。
* **按 `Ctrl + C`**：安全注销虚拟手柄并退出。

### 4. 卸载底层驱动
若需要清理或重新测试安装流程，以**管理员身份**运行卸载脚本：
```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Uninstall-Drivers.ps1
```

---

## 🛠️ 技术架构与设计方案

详细技术架构图、21字节 HID 报文逆向说明与协议分析请查阅项目根目录下的设计文档：
👉 **[DESIGN.md](./DESIGN.md)**

---

## 📂 项目结构

```text
MiGamepad/
├── DESIGN.md                        # 系统架构与通信协议逆向设计文档
├── README.md                        # 项目说明与上手指南
├── MiGamepad.sln                    # 解决方案文件
├── src/
│   ├── MiGamepad.Core/              # 核心业务库 (net8.0-windows)
│   │   ├── Native/                  # Win32 HID API (设备发现、句柄管理)
│   │   ├── Protocol/                # 21字节报文解码、死区处理、震动引擎
│   │   ├── Devices/                 # 单手柄管理 (XiaomiGamepad 状态机)
│   │   ├── Cloaking/                # HidHide 白名单与物理设备隐藏服务
│   │   └── GamepadManager.cs        # 全局多设备监控与 XInput 槽位分配
│   └── MiGamepad.App/               # 桌面应用 / 控制台控制面板 (net8.0-windows)
├── tests/
│   └── MiGamepad.Tests/             # 自动化单元测试 (按键、轴死区、报文验证)
└── tools/
    ├── Install-Drivers.ps1          # ViGEmBus 与 HidHide 一键依赖安装脚本
    └── Uninstall-Drivers.ps1        # 底层驱动一键卸载清理脚本
```

---

## 🧪 运行单元测试

```powershell
dotnet test
```
通过 13 项单元测试，覆盖报文头校验、按键解码、D-Pad 8方向切换、摇杆反转与死区非线性标定等核心算法。
