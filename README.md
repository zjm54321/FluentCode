# FluentCode

原生 C# / WinUI 3 OpenCode 桌面客户端（Windows 11 x64），提供 Fluent 风格桌面外壳。OpenCode 是默认且唯一的工作页面；保留设置、账户/连接与任务状态入口。

> 自动化入口见 [关键测试清单](docs/test-suite.md)；后端协议见 [OpenCode 客户端契约](docs/opencode-client-contract.md)。

---

## 核心功能概览

> 说明：以下为源码已包含的功能概览，源码接线存在不代表端到端实机验收已完成。

### 1. 原生窗口外壳
- 提供 OpenCode 会话、设置、账户/连接与任务状态入口，使用应用级共享设置和凭据服务。

### 2. OpenCode 客户端
- **V2 后端配置与凭据安全**：支持配置后端根地址及 Basic 认证，凭据由 Windows DPAPI 加密持久化；同页支持一次性 V2 配对链接兑换（须显式保存）。
- **交互与连通**：源码接入 native chat、原生权限审批与 Form 表单交互；已验证只读位置查询、会话列表读取及单轮 Prompt 往返完成（idle=succeeded）；工具卡片采用组合原生 Expander 架构，隔离渲染验证通过，原生端到端全量交互待验。
- **QOL 插件整合**：会话预设应用、确认后的加密内容清理、Billion 上下文与缓存账本、独立持久终端已接入。终端默认只读，输入和接管须显式启用；关闭查看不会结束进程。详见 [原生终端集成](docs/native-terminal-integration.md)。

### 3. 系统通知与任务栏
- **原生状态与通知**：源码对接 Windows 任务栏状态与 Fail-Closed 原生通知服务（跨进程实测待验）。

---

## 快速使用指南

### 先决条件
1. **操作系统**：Windows 11 (x64)（已实际验证，其他版本未声称测试）。
2. **运行时环境**：依赖 .NET 10 Runtime (x64) 与 WebView2 Runtime。Windows App SDK 原生核心组件已随完整输出目录打包自带（`WindowsAppSDKSelfContained=true`），无需单独安装 App SDK。
   - OpenCode 会话浏览器的代理与 Lighthouse 使用随输出目录提供的独立 Node 运行组件，不依赖系统 Node/npm。选择外部 Chrome 时，需另有已安装的 Chrome；应用不会安装浏览器。
3. **外部服务依赖**：**本程序不会自动拉起外部服务**。使用前请确保自备的后端服务已启动：
   - OpenCode 协作：需自行启动 OpenCode 兼容后端服务（如本机测试服务）；

### 配置与操作步骤

1. **配置 OpenCode 服务**：
   - 从应用的设置/连接入口打开 OpenCode 服务配置；
   - 选择手动配置或配对链接：
     - **手动配置**：在服务地址栏填写服务根地址（示例通用格式：`http://localhost:port`，路径不追加 `/api`），填入 Basic 认证用户名与服务密码，点击 **保存 (Save)**。保存后凭据由 DPAPI 加密持久化；可点击 **测试连接** 校验后端响应；
     - **配对链接**：在同页下方的配对区域粘贴后端生成的一次性 V2 配对 URL 并点击兑换；**兑换后须显式点击保存**，粘贴或兑换不会自动连接；
     - *注意*：**保存配置不等于已建立连接**，需测试连接或实际发起请求确认连通。

2. **选择 OpenCode 会话浏览器**：
   - 在设置的浏览器区域选择内嵌 WebView2 或外部 Chrome。外部 Chrome 使用独立窗口，应用内显示实际截图预览；文件和 Mermaid 仍在应用内预览。
   - 保存仅影响新建的浏览器工作区。已有会话需关闭会话标签后重新打开；只关闭网页不会重建工作区。
   - 会话网页经当前 OpenCode 来源的网络隧道访问，网页中的 `localhost` 指服务端。连接中断时停止转发，不切回桌面本地网络。高级附加模式的本机调试端点与必需启动参数见设置页。

---

## 运行与构建

### 常用命令（PowerShell，工程根目录下执行）
```powershell
# 首次构建或更新运行组件后显式准备依赖（下载固定版本并校验；不修改系统 Node/npm）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\browser-runtime\prepare.ps1

# 编译 Debug 配置
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Debug

# 运行 Debug 输出（启动脚本包含 20 秒窗口等待，无自动防重复杀进程，启动前请确保无残留的 FluentCode.exe 进程）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Configuration Debug
```

> **重要部署说明**：
> 本项目为依赖部署模式（`SelfContained=false`），构建输出位于：
> `src\FluentCode\bin\x64\<Configuration>\net10.0-windows10.0.26100.0\win-x64\`
> **必须在完整输出目录中启动**，或完整拷贝整个 `win-x64` 目录，严禁单独拷贝单个 `FluentCode.exe` 运行。
> `BrowserRuntime` 子目录也必须完整保留。构建与发布在缺少已准备组件时会报错，不会自动下载；应用启动时也不会安装依赖。准备脚本的输出位于忽略跟踪的 `artifacts\browser-runtime`。

---

## 凭据安全说明

- 配置文件持久化保存在用户本地目录：`%LOCALAPPDATA%\FluentCode\settings.json`。
- 敏感凭据（Basic 密码、Token、API Key 等）由 Windows DPAPI 加密保护，配置文件中仅存元数据与 CredentialId，绝不以明文形式保存密码或密钥。
- FluentCode 只使用自己的配置目录；首次使用时建立新配置，不导入其他应用的数据或凭据。

---

## 开发文档

- [OpenCode 页面](docs/opencode-ui-contract.md)与[富文本渲染](docs/opencode-rich-content-contract.md)
- [窗口外壳](docs/shell-ui-contract.md)与[设置](docs/settings-ui-contract.md)
- [应用集成](docs/app-integration-contract.md)与[通知](docs/notifications-contract.md)
- [原生终端](docs/native-terminal-integration.md)与[运行期主题刷新](docs/theme-refresh.md)
- [任务栏状态](docs/taskbar-contract.md)与[关键测试清单](docs/test-suite.md)
