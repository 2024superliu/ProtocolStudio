# 🚀 WpfProtocolStudio - 通信与协议双通道调试 Studio

[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue.svg)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-brightgreen.svg)](https://learn.microsoft.com/zh-cn/dotnet/desktop/wpf/)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

`WpfProtocolStudio` 是一款基于 C# .NET Framework 4.8 与 WPF MVVM 架构开发的工业级双通道通信中继与协议调试 Studio。

软件支持 **串口 (SerialPort)**、**TCP Server**、**TCP Client**、**UDP** 以及 **CAN 总线 (ControlCAN / 周立功 ZLG)** 之间的任意双通道自由组合透明中继转发，并提供了 **1ms 高精度连续发包**、**15ms 串口断帧组包**、**多格式切显**、**断线有界缓存补发** 与 **日志/文件全量导出** 等强悍功能。

---

## 🌟 核心特性与技术亮点

### 1. 🔄 强悍的双通道透明中继转发 (A ↔ B)
* 支持 **通道 A** 与 **通道 B** 独立配置与任意组合；
* 支持 **串口 ↔ 串口**、**串口 ↔ TCP/UDP**、**TCP ↔ CAN 总线**、**CAN ↔ 串口** 等自由中继转发；
* 提供了 **一键暂停/恢复中继转发** 功能，且暂停转发时底层接收日志依然正常记录。

### 2. 📡 多通信协议原生全面支持
* **串口 (SerialPort)**：支持高达 **921600** 超高波特率，可自定义数据位、停止位与奇偶校验位；
* **TCP 服务端 (TCP Server)**：多客户端同时连入管理，支持广播与客户端状态实时感知；
* **TCP 客户端 (TCP Client)**：支持指定目标 IP 与端口，带有链路自动断线重连检测；
* **UDP 双向通信**：支持本地端口与远程目标 IP/端口独立映射；
* **CAN 总线 (ZLG ControlCAN)**：完美兼容周立功 ControlCAN.dll 驱动，支持 CAN1 / CAN2 接口选择、波特率设置、滤波 ID 屏蔽与发送帧 ID 设定。

### 3. ⚡ 物理极限 1ms 高精度定时循环发送
* 集成 **Windows 多媒体高精度时钟 API (`winmm.dll timeBeginPeriod(1)`)**，突破系统 15.6ms 时间片限制；
* 基于 `Task.Run` + `System.Diagnostics.Stopwatch` 微秒级高精度时间轮询，真正实现 **1ms 高频连续发包**。

### 4. 📦 15ms 串口断帧超时自动组包 (解决拆包痛点)
* 全新设计 **15ms 帧空闲组包缓冲区 (Frame Assembly Buffer)**；
* 自动合并物理层 FIFO 拆分的数据块（如解决 `W` + `elcome to UartAssist` 分包），确保界面展示与日志记录呈现完整完整的物理数据帧。

### 5. 💾 高性能日志与持久化引擎
* **无锁并发队列**：采用 `BlockingCollection<T>` 生产者-消费者模式，数据收发与磁盘 I/O 完美解耦，高频收发零卡顿；
* **三种日志格式**：支持自动/手动导出为 **TXT (时间轴排版报告)**、**CSV (Excel 表格)** 及 **BIN (纯二进制无损字节流)**；
* **断线应对策略**：支持 **直接丢弃**、**内存有界缓存重连自动补发 (Bounded Queue)** 以及 **自动停止对端** 3 种异常防护模式；
* **日志与界面脱钩**：支持 **“暂停界面刷屏”**，界面列表停止滚动时不影响后台物理转发与日志实时落盘。

### 6. 🔍 历史日志全文搜索与常用模板
* **历史日志全文检索**：内置历史日志搜索服务，支持对 HEX、ASCII、时间戳、通道方向与备注的全文搜索与匹配高亮；
* **常用报文模板**：内置 Modbus 03/06 读写指令与 PING 心跳模板，支持自定义保存与一键加载快捷发送。

---

## 🛠️ 项目技术栈

| 组件 | 技术 / 库 |
| :--- | :--- |
| **开发语言** | C# 7.3 |
| **运行框架** | .NET Framework 4.8 |
| **界面 UI 框架** | WPF (Windows Presentation Foundation) |
| **设计模式** | MVVM (Model-View-ViewModel), Command Pattern |
| **通信驱动** | `System.IO.Ports`, `System.Net.Sockets`, `ControlCAN.dll` |
| **高精度定时** | `winmm.dll (timeBeginPeriod)`, `System.Diagnostics.Stopwatch` |
| **配置序列化** | `JavaScriptSerializer` (JSON) |

---

## 📂 目录结构说明

```
WpfProtocolStudio/
├── Channels/                   # 通信通道实现层 (Serial, Tcp, Udp, Can)
│   ├── SerialPortChannel.cs    # 串口通道 (内置 15ms 断帧组包)
│   ├── TcpServerChannel.cs     # TCP 服务端通道
│   ├── TcpClientChannel.cs     # TCP 客户端通道
│   ├── UdpChannel.cs           # UDP 通信通道
│   └── CanChannel.cs           # CAN 总线通道 (ControlCAN)
├── Engine/                     # 中继转发引擎
│   └── ForwardingEngine.cs     # 双向透明转发、断线策略与有界缓存队列
├── Services/                   # 后台核心服务
│   ├── LogService.cs           # 生产者-消费者高并发日志落盘服务
│   ├── HistoryLogSearchService.cs # 历史日志全文搜索解析器
│   ├── FileTransferService.cs  # 传输文件接收与发送处理器
│   └── ConfigManager.cs        # JSON 配置文件管理器
├── ViewModels/                 # MVVM 视图模型层
│   └── MainViewModel.cs        # 主界面业务逻辑、高精度 Timer 与命令绑定
├── Models/                     # 数据模型 (DataRecord, MessageTemplate)
├── Helpers/                    # UI 转换器与 RelayCommand 辅助类
├── MainWindow.xaml             # 精美 WPF 主界面 View
└── App.xaml                    # 应用入口与样式资源
```

---

## 🚀 编译与构建说明

### 1. 软件环境要求
* Windows 10 / Windows 11 操作系统；
* Visual Studio 2019 / 2022 (安装有 `.NET 桌面开发` 工作负载)；
* `.NET Framework 4.8 Target SDK`。

### 2. 编译步骤
1. 克隆本项目至本地：
   ```bash
   git clone https://github.com/2024superliu/ProtocolStudio.git
   ```
2. 使用 Visual Studio 打开 `WpfProtocolStudio.sln` 解决方案；
3. 将编译配置切换为 **`Release | Any CPU`**；
4. 点击顶部菜单 **`生成` ➔ `重新生成解决方案`**；
5. 编译成功的二进制文件存放在 `d:\VSDemo\WpfProtocolStudio\bin\Release\WpfProtocolStudio.exe`。

---

## 📖 使用操作指南

1. **配置通道**：分别在左侧【通道 A】与【通道 B】WrapPanel 中选择物理类型（如串口 COM1、TCP 服务端 8080 等），点击 **`打开通道`**；
2. **双向转发**：确保顶部 **`开启 A-B 转发`** 勾选框处于开启状态，此时 A/B 两端数据将自动实时中继；
3. **高频发送**：在底部发送框输入报文，勾选 **`循环发送`**，设置周期为 **`1` ms**，点击 **`发送数据`** 即可启动微秒级高频脉冲发送；
4. **日志导出**：点击顶部菜单 **`文件(F)` ➔ `保存日志文件`**，即可自主选择保存为 TXT、CSV 表格或纯 BIN 二进制数据；
5. **历史搜索**：点击顶部菜单 **`文件(F)` ➔ `历史日志检索与搜索`**，选择本地历史日志文件进行关键字精确定位与解析。

---

## 📄 开源许可证

本项目采用 [MIT License](LICENSE) 开源许可证。

---

> 💡 **作者与维护**：[2024superliu](https://github.com/2024superliu)
