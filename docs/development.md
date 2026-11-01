# FluentCode 开发与构建指南

本文档介绍 FluentCode 的开发环境搭建、日常构建、测试运行、发布流程与常见问题排查。

---

## 1. 环境准备与先决条件

FluentCode 是使用 C# 13、.NET 10 与 WinUI 3 开发的 Windows 桌面应用。

### 1.1 硬件与操作系统
- **操作系统**：Windows 10 21H2（Build 19044）或更高版本（x64 架构）；推荐使用 Windows 11 24H2。
- **内存与存储**：推荐至少 16 GB 内存与 10 GB 可用磁盘空间（用于 Visual Studio 构建工具与依赖缓存）。

### 1.2 必备软件与 SDK
1. **.NET 10 SDK (x64)**：从微软官方下载并安装 .NET 10 SDK。
2. **Visual Studio 2022 / 2026 或 Visual Studio Build Tools**：
   必须勾选以下工作负载与组件：
   - **.NET 桌面开发**（.NET Desktop Development）
   - **Windows 应用开发**（Windows Application Development，包含 Windows App SDK / WinUI 3 模板）
   - **C++ 核心桌面工具**（包含 MSBuild 与 C++ 运行时依赖）
3. **Microsoft Edge WebView2 Runtime**：大多数 Windows 10/11 已内置；如缺失请安装 Evergreen 运行时。
4. **Git**：用于版本控制与源码管理。
5. **Python 3**（可选）：若在 WSL 环境下运行自动化测试套件或重新生成应用图标，需要安装 Python 3。

---

## 2. 代码仓库结构

```
AIFrontend-history-20261020/
├── FluentCode.sln             # 顶级 Visual Studio 解决方案文件
├── src/
│   ├── FluentCode/            # FluentCode 主程序工程 (C# / WinUI 3)
│   │   ├── Assets/            # 应用程序图标、品牌矢量图、任务栏角标
│   │   ├── Models/            # 应用配置、实体模型与状态定义
│   │   ├── Motion/            # FluentMotion 动效系统与策略
│   │   ├── Pages/             # 主界面、会话工作区、设置页与欢迎页
│   │   └── Services/          # 配置存储、OpenCode 客户端、通知、任务栏与渲染
│   └── ThirdParty/            # 源码级第三方依赖 (WindowsTerminalControl)
├── scripts/                   # 构建、发布、运行与图标生成脚本
├── tools/
│   └── browser-runtime/       # 离线 Node.js 浏览器运行时准备脚本与清单
├── tests/                     # 27 组自动化测试 Runner 与测试驱动
└── docs/                      # 架构设计、协议与用户文档
```

---

## 3. 标准开发流程

所有构建与发布入口均已通过 Windows PowerShell 脚本封装在 `scripts/` 目录下。

### 步骤 1：准备离线浏览器运行时
FluentCode 将独立的 Node.js 运行时打包在发布目录中，以支持会话隔离浏览器与网络隧道。在首次编译或更新前端依赖后，必须先显式执行准备脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\browser-runtime\prepare.ps1
```
> **说明**：该脚本会从官方源下载固定版本的 Node.js（Node 24.22.1 x64），严格比对 SHA-256 校验和，并在隔离缓存中运行 `npm ci` 安装锁定的生产依赖，最终生成 `artifacts/browser-runtime/`。此步骤需要网络连接；后续编译与启动均不再联网。

### 步骤 2：编译 Debug 版本
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-debug.ps1
```
- 该脚本会调用 MSBuild 还原并编译 `FluentCode.sln`，生成完整的 Debug 输出，并核对关键依赖项哈希。
- 如需重定向构建日志与证据，可附加 `-EvidenceRoot <新目录路径>`。
- 也可以使用轻量编译脚本：
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Debug
  ```

### 步骤 3：启动应用
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Configuration Debug
```
- 脚本会启动 Debug 目录下的 `FluentCode.exe`，并等待窗口句柄就绪。
- **注意**：启动前请确保没有残留的 `FluentCode.exe` 进程在后台运行。

### 步骤 4：编译并发布 Release 版本
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1
```
- 该脚本使用 Release 配置执行编译和独立发布（`dotnet publish`），将所有依赖项（包括 .NET 程序集、Windows App SDK 运行时、Windows Terminal DLL 与 BrowserRuntime）聚合至发布目录，并校验关键文件完整性。

---

## 4. 依赖部署规则与输出路径

FluentCode 采用依赖框架的部署模式（`SelfContained=false`），构建输出目录为：
```
src\FluentCode\bin\x64\<Configuration>\net10.0-windows10.0.26100.0\win-x64\
```

> ⚠️ **重要部署注意事项**：
> - 必须在完整的输出目录下运行应用，或者完整复制整个 `win-x64` 文件夹。
> - **严禁仅复制单个 `FluentCode.exe` 到其他目录执行**，否则会导致缺少 Windows App SDK 原生组件或 `BrowserRuntime` 失败退出。

---

## 5. 打包与发布计划（MSIX）

当前阶段 FluentCode 采用免打包的可执行文件输出目录分发。未来规划如下：

- **子树合并**：FluentCode 计划整体以 `clients/windows` 子树的形式合并入 `opencode-qol` 官方仓库，保持完整的提交历史。
- **MSIX 打包**：规划的应用包标识符为：
  ```
  mingz.SST.OpenCodeDesktop.Fluent
  ```
- **安装与更新渠道**：采用开发者证书自签名侧载（Sideloading），并配置 `.appinstaller` 清单文件，实现应用启动时的无感自动差量检查与更新。

---

## 6. 常见故障排查（Troubleshooting）

### 6.1 编译报错：`Missing published artifact: BrowserRuntime\node.exe`
- **原因**：尚未准备离线浏览器运行时。
- **解决**：在仓库根目录执行：
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\browser-runtime\prepare.ps1
  ```

### 6.2 编译报错：`Visual Studio MSBuild is required`
- **原因**：系统中未安装 Visual Studio 或缺少 MSBuild 组件。
- **解决**：安装 Visual Studio 2022 Community 或 Build Tools，并在 Visual Studio Installer 中确保勾选了“.NET 桌面开发”与“Windows 应用开发”工作负载。

### 6.3 编译时文件被锁定：`MSB3027: 无法将文件 FluentCode.exe 复制到...`
- **原因**：之前的 FluentCode 实例仍在后台运行，或者已最小化到系统托盘。
- **解决**：打开任务管理器结束所有名为 `FluentCode.exe` 的进程，或在 PowerShell 中运行：
  ```powershell
  Stop-Process -Name FluentCode -ErrorAction SilentlyContinue
  ```

### 6.4 运行闪退或 DPAPI 凭据读取异常
- **原因**：DPAPI 加密与当前 Windows 用户账户绑定。如果将数据目录跨机器或跨用户复制，会导致旧凭据无法解密。
- **解决**：删除 `%LOCALAPPDATA%\FluentCode\settings.json` 文件以重置配置，应用下次启动时将进入欢迎引导页重新建立连接。
