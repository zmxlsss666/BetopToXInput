# BetopToXInput

> 把没有任何数字签名的 Betop C031 (KingChuang 代工) 手柄桥接为 XInput / Xbox 360 控制器。
> 摆脱"未签名驱动 + 64位禁用测试签名"困境。

---

## 这个项目做什么

Betop C031 的原始驱动 (`GAJOYPS.dll`, `GaJoyFF.dll`) 没有 WHQL 数字签名,在新版 Windows
上要么装不上,要么装上之后摇杆/振动不可靠。

本项目**完全绕过** Betop 的内核驱动,改用:

1. **Windows 自带的 HID 类驱动** (`hidclass.sys`,系统签名) 直接读手柄的输入报文
2. **ViGEmBus** 社区签名驱动(已 WHQL 认证)创建一个虚拟 Xbox 360 控制器
3. **共享内存** 在游戏进程和本服务之间同步手柄状态

振动从虚拟控制器传回 `BetopRumbleWriter` → HID 输出报告 → 真实马达。

---

## 项目结构

```
BetopToXInput/
├── BetopToXInput.sln
├── README.md
└── src/
    ├── BetopToXInput.Core/        # HID 读/写 + 状态映射
    │   ├── BetopHidReader.cs       # 通过 HidLibrary 读 Betop HID 输入
    │   ├── BetopRumbleWriter.cs    # 写 5 字节振动报文 [ReportID,0,0,强,弱]
    │   ├── BetopInputState.cs      # 解析后的按键/摇杆/扳机状态
    │   ├── XInputStateMapper.cs    # Betop state → Xbox 360 state
    │   ├── XInputSharedMemory.cs   # 与代理 DLL 共享内存的 Layout
    │   └── XInputProxyBridge.cs    # 服务侧:读 HID → 写共享内存;读共享内存 → 写 HID
    ├── BetopToXInput.ViGEm/       # ViGEmBus 方案
    │   └── ViGEmBridge.cs          # 把 Betop state 推入 ViGEm Xbox360Controller
    ├── BetopToXInput.Proxy/       # XInput.dll 代理方案(纯用户态)
    │   ├── XInputProxy.cs          # xinput1_3.dll 的真身,所有 XInput 函数的实现
    │   ├── XInputNative.cs         # XInput ABI 类型
    │   └── DllExportAttribute.cs   # 桩属性,需替换为 DllExport NuGet
    └── BetopToXInput.App/         # 主程序(服务)
        ├── Program.cs              # 入口,选择 vigem / proxy / both
        ├── appsettings.json
        └── app.manifest
```

---

## 两种使用模式

### 模式 A: ViGEmBus (推荐,稳定)

游戏会看到一个 *真实插入* 的 Xbox 360 控制器。振动自动双向转发。

**前置**:安装 [ViGEmBus](https://github.com/ViGEm/ViGEmBus/releases) (1.21+)
**步骤**:
1. 安装 ViGEmBus 驱动(`ViGEmBus_Setup_x64.msi`,一次即可,签名驱动)
2. 拔掉 Betop 自带的未签名驱动(可选,推荐)
3. 把 Betop 插上,运行:
   ```cmd
   BetopToXInput.App.exe
   ```
4. 启动游戏,手柄自动识别为 Xbox 360

### 模式 B: XInput.dll 代理(纯用户态,无需任何虚拟驱动)

把编译出的 `BetopToXInput.Proxy.dll` 重命名成 `xinput1_3.dll`,
放入游戏 exe 同目录(游戏的搜索路径优先于 `System32`)。
本服务跑着,游戏就以为在用真的 XInput。

**步骤**:
1. 编译 `BetopToXInput.Proxy.csproj` → 产出 `.dll`
2. 在 `BetopToXInput.Proxy.csproj` 取消注释启用 [DllExport](https://www.nuget.org/packages/DllExport/):
   ```xml
   <PackageReference Include="DllExport" Version="1.7.4" />
   ```
3. 用 3F 的 [DllExport.bat](https://github.com/3F/DllExport) 完成 IL 重写,
   使 `XInputProxy` 类的 `[DllExport]` 方法真的导出
4. 把生成的 `xinput1_3.dll` 复制到游戏目录
5. 启动 `BetopToXInput.App.exe`(模式 `proxy` 或 `both`)
6. 启动游戏

### 模式 C: 同时启用两个

`appsettings.json`:
```json
{ "Mode": "both" }
```

---

## 配置 (appsettings.json)

| 字段 | 含义 | 默认 |
|------|------|------|
| `Mode` | `vigem` / `proxy` / `both` | `vigem` |
| `VigemUserIndex` | 虚拟 Xbox 控制器索引(0-3) | `0` |
| `StickDeadzone` | 摇杆径向死区(0..1) | `0.08` |
| `DumpHidReports` | 启动时打印 HID 报文 | `false` |
| `LogFile` | 日志文件路径 | `betop-to-xinput.log` |

---

## 校准 HID 报文

不同批次的 Betop C031 固件,按键/摇杆/扳机在 HID 报文中的偏移可能略有差异。
如果你发现按 X 出 Y 之类的错位,运行:

```cmd
BetopToXInput.App.exe --dump
```

按每个按键,记录下输出。打开
`src\BetopToXInput.Core\BetopHidReader.cs`,根据实际值调整
`ParseReport` 里的位运算掩码。

---

## 编译

```cmd
git clone <this>
cd BetopToXInput
dotnet restore
dotnet build -c Release
```

产物在 `src\BetopToXInput.App\bin\Release\net6.0-windows\`。

---

## 为什么这套方案能彻底解决"驱动无签名"问题

- Betop 自带的未签名驱动只用于 *DirectInput 摇杆类的注册* 和 *FF 报文*
- 本项目**不依赖**它:
  - 输入走系统自带的 `hidclass.sys`(已签名,一直可用)
  - 振动走同一个 `hidclass.sys`,不需要 DirectInput
- ViGEmBus 是社区 WHQL 认证的签名驱动,**只需安装一次**
- XInput 代理模式连 ViGEmBus 都不需要

---

## 已知限制

- XInput 1.4 之后增加了部分高频特性(GSensor 等),本代理未实现;
  对绝大多数游戏无影响,只对极少数要求 XInput 1.4 完整 ABI 的游戏不工作
- 一个进程里只能虚拟 1 个 Betop(第 2/3/4 个槽位固定返回 not-connected)。
  如需多手柄,扩展 `XInputProxyBridge` 让它同时持有多个 `BetopHidReader`
- Betop 固件差异:少数早期固件把 DPad 用 4 个独立 bit 而不是 hat-switch,
  此时需要修改 `ParseReport` 的 hat 解码段

---

## License

MIT
