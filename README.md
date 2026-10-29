# AIFrontend

原生 C# / WinUI 3 桌面端框架（Windows 11 x64），提供 Fluent 风格桌面外壳，整合网页端大模型、本地/直接协议模型（Local）、OpenCode 编程助手协作与任务看板。

> **当前验证状态（2026-10-29）**：OpenCode 遗留需求及 QOL 插件前端整合已完成，72 个测试入口经失败项定向修复后全部通过；Debug/x64 构建与发布均为 0 警告、0 错误。真实验证覆盖会话浏览器、离线终端渲染及原生持久 PTY 协议。功能及证据见 [当前验证记录](docs/current-validation-status.md)。

---

## 核心功能概览

> 说明：以下为源码已包含的功能概览，源码接线存在不代表端到端实机验收已完成。

### 1. 窗口外壳与自适应导航
- **系统导航与自适应**：原生 WinUI NavigationView 控件（`PaneDisplayMode="Auto"`，默认 320/48 宽度）与页面返回历史机制。
- **底端入口**：侧栏底端集成任务状态、账户头像与设置入口（含旋转动画）。

### 2. OpenCode 客户端
- **V2 后端配置与凭据安全**：支持配置后端根地址及 Basic 认证，凭据由 Windows DPAPI 加密持久化；同页支持一次性 V2 配对链接兑换（须显式保存）。
- **交互与连通**：源码接入 native chat、原生权限审批与 Form 表单交互；已验证只读位置查询、会话列表读取及单轮 Prompt 往返完成（idle=succeeded）；工具卡片采用组合原生 Expander 架构，隔离渲染验证通过，原生端到端全量交互待验。
- **QOL 插件整合**：会话预设应用、确认后的加密内容清理、Billion 上下文与缓存账本、独立持久终端已接入。终端默认只读，输入和接管须显式启用；关闭查看不会结束进程。[专项验证](docs/validation/2026-10-29-qol-plugin-frontend.md)列明协议、页面接线和渲染的实际证据。

### 3. Local 本地与直连协议
- **协议适配**：支持 OpenAI Responses、ChatCompletions 及 Anthropic Messages 协议（离线通过 281 项协议断言）；严格按能力声明控制推理参数。
- **离线渲染与存储**：内置安全 HTML 本地渲染器（198 项离线断言）与会话持久化；在线真实调用待验证。

### 4. 网页端 AI (WebView2)
- **多平台切换**：SelectorBar 支持 ChatGPT、Gemini 与 Claude 独立切换。
- **独立环境**：为各官方网页提供独立的 WebView2 Profile 并隔离 Cookie（未做真实网页登录与独立 Cookie 清除端到端验收）。

### 5. 系统通知与任务栏
- **原生状态与通知**：源码对接 Windows 任务栏状态与 Fail-Closed 原生通知服务（跨进程实测待验）。

### 6. 看板 (Board)
- **主界面骨架**：提供 Scaffold 基础导航骨架；任务编排与 MCP 协议集成明确延期。

---

## 快速使用指南

### 先决条件
1. **操作系统**：Windows 11 (x64)（已实际验证，其他版本未声称测试）。
2. **运行时环境**：依赖 .NET 10 Runtime (x64) 与 WebView2 Runtime。Windows App SDK 原生核心组件已随完整输出目录打包自带（`WindowsAppSDKSelfContained=true`），无需单独安装 App SDK。
   - OpenCode 会话浏览器的代理与 Lighthouse 使用随输出目录提供的独立 Node 运行组件，不依赖系统 Node/npm。选择外部 Chrome 时，需另有已安装的 Chrome；应用不会安装浏览器。
3. **外部服务依赖**：**本程序不会自动拉起外部服务**。使用前请确保自备的后端服务已启动：
   - OpenCode 协作：需自行启动 OpenCode 兼容后端服务（如本机测试服务）；
   - Local 模型：需自备并运行兼容 OpenAI / Anthropic 协议的本地网关或第三方 API 服务。

### 配置与操作步骤

1. **配置 OpenCode 服务**：
   - 点击主界面左下角侧栏底部的 **设置** 图标；
   - 进入 **OpenCode 后端** 分类：
     - **手动配置**：在服务地址栏填写服务根地址（示例通用格式：`http://localhost:port`，路径不追加 `/api`），填入 Basic 认证用户名与服务密码，点击 **保存 (Save)**。保存后凭据由 DPAPI 加密持久化；可点击 **测试连接** 校验后端响应；
     - **配对链接**：在同页下方的配对区域粘贴后端生成的一次性 V2 配对 URL 并点击兑换；**兑换后须显式点击保存**，粘贴或兑换不会自动连接；
     - *注意*：**保存配置不等于已建立连接**，需测试连接或实际发起请求确认连通。

2. **配置 Local 本地与协议模型**：
   - 在 **设置 -> 聊天 provider** 中选择协议类型，填写自备的 API Endpoint 与 API Key；
   - 在 **设置 -> 模型** 中配置并选择具体的 Model ID 并保存；
   - 返回主界面，在 Normal / AI 页面顶部的 SelectorBar 中切换至 **Local** 即可使用对话（窄窗测量溢出已完成源码修正，真机运行待验）。

3. **使用网页端 AI**：
    - 在主界面普通模式下，通过顶部 SelectorBar 自由切换 ChatGPT、Gemini 或 Claude 视图浏览（登录受服务商内嵌策略限制）。

4. **选择 OpenCode 会话浏览器**：
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

# 运行 Debug 输出（启动脚本包含 20 秒窗口等待，无自动防重复杀进程，启动前请确保无残留的 AIFrontend.exe 进程）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Configuration Debug
```

> **重要部署说明**：
> 本项目为依赖部署模式（`SelfContained=false`），构建输出位于：
> `src\AIFrontend\bin\x64\<Configuration>\net10.0-windows10.0.26100.0\win-x64\`
> **必须在完整输出目录中启动**，或完整拷贝整个 `win-x64` 目录，严禁单独拷贝单个 `AIFrontend.exe` 运行。
> `BrowserRuntime` 子目录也必须完整保留。构建与发布在缺少已准备组件时会报错，不会自动下载；应用启动时也不会安装依赖。准备脚本的输出位于忽略跟踪的 `artifacts\browser-runtime`。

---

## 凭据安全说明

- 配置文件持久化保存在用户本地目录：`%LOCALAPPDATA%\AIFrontend\settings.json`。
- 敏感凭据（Basic 密码、Token、API Key 等）由 Windows DPAPI 加密保护，配置文件中仅存元数据与 CredentialId，绝不以明文形式保存密码或密钥。

---

## 详细验证状态

### Billion 只读会话遥测契约与离线证据

- 会话 detail 的 HTTP 200 响应必须含唯一的根级字符串 `id`，与请求的会话 ID 按 Ordinal 精确匹配；缺失、null、非字符串、非法、不匹配或重复 ID 均返回 `Malformed`，所有遥测清空为 Unknown，不回显错误 ID。请求 ID 仍支持 `ses` 前缀、4–256 个 ASCII 字母/数字/`_`/`-`；JSON 转义不改变身份比较结果。
- 匹配 ID 但无遥测字段仍为 `Available` + Unknown；404 仍为 `NoRecord`，其他非 200、取消、超时与体积限制语义不变。只投影允许的遥测字段，不将忽略的 handoff/title/summary 内容解码成字符串，也不公开响应 ID。
- 运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\billion-context\run.ps1`：Windows .NET 10 离线编译（nullable、warnings as errors）及 **944 项断言通过**，覆盖身份、流式读取、白名单投影和缓存账本边界；使用假传输。原生界面分别保留最佳上下文、原始参考及未知值，官方本机控制台须显式确认后打开。
- 本次证据：`artifacts/phase2-billion-20261029T014205Z-7ed612a7a4b54f1cb81cf07d5dbfcbfa/results.txt`；页面接线、构建发布与其余验证见 [QOL 专项记录](docs/validation/2026-10-29-qol-plugin-frontend.md)。

关于自动化断言数据、真机实测结果、已知待修事项与待验收范围，请查看完整记录：  
👉 [当前验证与验收状态文档](docs/current-validation-status.md)
