# 🎮 MiGamepad — 小米蓝牙手柄 Windows XInput 驱动服务

让 **小米蓝牙手柄（MDZ-11-AA）** 在 Windows 10 / 11 上变身**原生 Xbox 360 控制器**：所有现代 PC 游戏（Steam、Xbox Game Pass、EA App、Epic……）即插即玩，支持双线性马达震动，且不与 Xbox / PS / Switch 手柄冲突。

一个 exe，双击即完成全部安装——内嵌官方驱动安装器，**无需安装 .NET、无需联网**。

---

## ✨ 功能特性

| | 功能 | 说明 |
|---|---|---|
| 🕹️ | **原生 XInput 映射** | 彻底摆脱 DirectInput 兼容性差的问题，Steam / Game Pass / EA / Epic 游戏直接识别为 Xbox 360 手柄 |
| 📳 | **双线性马达震动** | 监听 XInput 震动事件，30Hz 脏检查限流转换为小米专有 HID 特征报文，不丢包、不卡顿、不虚耗电量 |
| 👥 | **4 手柄同台共存** | 与官方 Xbox、PS5/PS4、Switch Pro 手柄同时使用，本地多人游戏（《双人成行》《胡闹厨房》等）互不干扰 |
| 👻 | **杜绝"幽灵手柄"** | 集成 HidHide 内核过滤驱动，向游戏隐匿小米手柄原生 DirectInput 节点，按键绝不双发 |
| 😴 | **休眠秒回连** | 手柄闲置断电休眠后，按下 MI 键即刻自动重新绑定虚拟手柄，无需重启游戏或软件 |
| 🔧 | **蓝牙回连自愈** | 部分老款蓝牙适配器驱动断连后无法重建 HID 通道（表现为"已连接"但没输入），程序自动检测并修复配对 |
| 🪟 | **LTSC / 精简版兼容** | 自动抑制 Win11 未装 Xbox Game Bar 时的 `ms-gamebar` 弹窗；MI 徽标键智能映射为 Xbox Guide 键 |

---

## 🚀 快速开始

### ① 下载

前往 [**Releases**](../../releases) 页面，下载 `MiGamepad.App.exe`（或完整压缩包 `MiGamepad-Portable.zip`）。

> 系统要求：Windows 10（1809+）/ Windows 11，x64，自带蓝牙或 USB 蓝牙适配器。

### ② 小米手柄进入蓝牙配对

1. 按手柄正中间的 **MI 徽标键** 开机；
2. 若首次使用，**长按 MI 键**直到指示灯**快速双闪**，进入配对模式；
3. 打开 Windows 设置 → **蓝牙和其他设备** → **添加设备** → 选择 **"Xiaomi Bluetooth Wireless Gamepad"**。

### ③ 双击 `MiGamepad.App.exe`

就这一步。程序会自动：

- 检测并静默安装缺失的 **ViGEmBus / HidHide** 官方驱动（已内嵌于 exe）；
- 部署到 `C:\Program Files\MiGamepad\`；
- 注册并启动 Windows 常驻服务，**开机自动运行**。

完成后打开任意游戏即可使用。🎉

> **已安装过？** 再次双击 exe 会打开管理菜单：`[S]` 停止服务进入手动测试模式 · `[U]` 卸载服务。

---

## 🎮 日常使用

装好后**无需任何日常操作**——服务随系统自启（早于用户登录），手柄关机休眠后按下 MI 键即自动回连，热插拔随意。

**日常可用的小工具：**

- **测震动**：双击 exe → 按 `S` 停止服务进入手动测试模式 → 按 `V` 键触发 1 秒震动测试（`Ctrl+C` 退出并恢复服务托管）。
- **配置文件** `%ProgramData%\MiGamepad\settings.ini`（一般保持默认即可）：

  | 配置项 | 可选值 | 默认 | 说明 |
  |---|---|---|---|
  | `MapGuideButton` | `auto` / `on` / `off` | `auto` | MI 徽标键映射为 Xbox Guide 键（唤起 Xbox Game Bar）。`auto` 仅在系统存在 Game Bar 时启用，LTSC 等系统自动关闭 |
  | `SuppressGameBarPopup` | `auto` / `on` / `off` | `auto` | 抑制虚拟手柄接入时的 `ms-gamebar` "需要新应用" 弹窗。`auto` 仅在未安装 Game Bar 的系统生效 |
  | `AutoRepairBluetooth` | `on` / `off` | `on` | 蓝牙回连通道损坏时自动删配对并重配（必要时弹出系统"添加设备"页，点一下手柄即可） |

  修改后重启服务生效：`sc stop MiGamepad && sc start MiGamepad`（或直接双击 exe 在菜单中操作）。
- **运行日志**：`%ProgramData%\MiGamepad\service.log`。

---

## ⌨️ 命令行参考（管理员 PowerShell）

```powershell
.\MiGamepad.App.exe --install          # 一条命令完成全部安装（等价于双击 exe 首次运行）
.\MiGamepad.App.exe --uninstall        # 卸载服务（驱动与安装目录保留）
.\MiGamepad.App.exe --install-drivers  # 仅安装底层驱动，不注册服务
.\MiGamepad.App.exe --set-guide auto   # 设置 MI 键映射: auto / on / off
```

- **升级版本**：下载新版 exe 后重新执行一次 `--install` 即可（自动停旧服务 → 覆盖文件 → 重启）。
- **彻底清理**：先 `--uninstall`，再以管理员运行 [`tools/Uninstall-Drivers.ps1`](./tools/Uninstall-Drivers.ps1) 卸载 ViGEmBus / HidHide 驱动。
- 服务自带崩溃自动重启（5s → 10s → 60s 退避）与断手柄自愈重连，常驻后台无需看管。

---

## 🧯 常见问题

### 手柄呼吸灯常闪、显示"已连接"但没有任何输入

老款蓝牙适配器驱动的一个已知故障：实测 某老款蓝牙适配器配套 2013 年版驱动在 Win11 24H2 上，手柄断电重开后系统秒显示"已连接"，但 HID 数据通道永远建立不起来，HID 节点以约 20 秒周期闪断。

- **根治**：升级适配器蓝牙驱动到该系列最终版（如 老款适配器系列 系列的 **最终版**，2016 WHQL），升级后实测完全恢复。Intel 已下架产品页，可从 [驱动存档站](驱动存档站) 获取。
- **彻底**：更换一个 BT 5.0+ USB 蓝牙适配器（约 30~50 元）。
- **兜底**：程序内置蓝牙自愈会自动检测此状态并尝试重配（`AutoRepairBluetooth` 默认开启）。

### 接入手柄时弹出"需要新应用打开 ms-gamebar"

Windows 11 已知问题：未安装 Xbox Game Bar 的系统上，任何 XInput 设备接入都会触发该弹窗。程序启动时已自动写入注册表抑制，无需手动处理（如需关闭：`SuppressGameBarPopup=off`）。

### 按键双重输入 / 游戏识别出两个手柄

物理手柄未被执行 HidHide 隐藏（程序异常退出可能遗留规则）。重启服务即可——启动时会自动清理遗留规则并按当前连接重新隐藏；同时确认 HidHide 已安装（可用 `--install-drivers` 补装）。

---

<details>
<summary><b>🛠️ 从源码构建（开发者）</b></summary>

```powershell
dotnet build                        # 构建
dotnet test                         # 运行 13 项单元测试（报文校验 / 按键解码 / 摇杆死区标定等）
powershell -ExecutionPolicy Bypass -File .\tools\Publish-Portable.ps1
                                    # 打包自包含单文件 exe + 内嵌驱动安装器，产物在 dist\
dotnet run --project .\src\MiGamepad.App\MiGamepad.App.csproj   # 交互模式直接跑
```

技术架构图、21 字节 HID 输入报文与 7 字节震动特征报文的协议逆向分析，见 👉 **[DESIGN.md](./DESIGN.md)**。

</details>

---

## 🙏 致谢

本项目站在以下优秀开源项目的肩膀上，特此致谢：

- **[irungentoo/Xiaomi_gamepad](https://github.com/irungentoo/Xiaomi_gamepad)** —— 小米手柄 HID 协议逆向的开创性工作。本项目输入报文解析与震动特征报文（`0x20`）的实现均以此为参考起点。
- **[Nefarius / ViGEmBus](https://github.com/nefarius/ViGEmBus)** —— 虚拟游戏手柄总线驱动（BSD-3-Clause），本项目虚拟 Xbox 360 控制器的基石。驱动安装器 `v1.22.0` 内嵌于发行文件中。
- **[Nefarius / HidHide](https://github.com/nefarius/HidHide)** —— 游戏输入隐藏过滤驱动（MIT），解决"幽灵手柄"双发问题的核心。驱动安装器 `v1.5.230` 内嵌于发行文件中。
- **[Nefarius.Drivers.HidHide](https://www.nuget.org/packages/Nefarius.Drivers.HidHide) / [Nefarius.ViGEm.Client](https://www.nuget.org/packages/Nefarius.ViGEm.Client)**（NuGet）—— 上述两个驱动的 .NET 客户端库。
- **[Microsoft .NET 8](https://dotnet.microsoft.com/)** —— 运行时与 Windows 服务托管框架。

> 内嵌驱动安装器均从官方 GitHub Release 原样分发、未做修改，条款详见 [`src/MiGamepad.App/Drivers/LICENSES-THIRD-PARTY.txt`](./src/MiGamepad.App/Drivers/LICENSES-THIRD-PARTY.txt)。

---

## 📄 许可证

本项目基于 [MIT License](./LICENSE) 开源发布。
内嵌的 ViGEmBus / HidHide 驱动安装器遵循其各自上游许可证，见上方第三方许可说明。
