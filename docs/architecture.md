# FluentCode 系统架构与技术实现

本文档介绍 FluentCode 客户端的整体架构、组件拓扑、线程模型、安全策略与数据流。

---

## 1. 整体架构与分层

FluentCode 是基于 .NET 10 与 WinUI 3（Windows App SDK 1.8）构建的原生 x64 Windows 桌面应用。其内部主要分为四层：

```
┌─────────────────────────────────────────────────────────────┐
│                       WinUI 3 表现层                        │
│   MainWindow · OpenCodePage · SettingsView · WelcomeView    │
├─────────────────────────────────────────────────────────────┤
│                       领域交互与控制                        │
│   OpenCodePageSource · OpenCodeInteractionGate · Composer   │
├─────────────────────────────────────────────────────────────┤
│                       核心服务层                            │
│   SettingsService · DpapiSecretStore · SafeContentRenderer  │
│   CompletionNotificationService · TaskbarStatusService      │
├─────────────────────────────────────────────────────────────┤
│                       网络与系统基础设施                     │
│   OpenCodeClient (HTTP/SSE) · Persistent PTY (WebSocket)    │
│   Windows DPAPI · Win32 ITaskbarList3 · AppNotification     │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. 关键组件与目录结构

所有核心源代码位于 `src/FluentCode/` 目录下：

### 2.1 应用程序根（`App.*.cs`）
- **单实例激活与管道（`App.Activation.cs` / `App.ActivationPipe.cs`）**：
  在主窗口创建前通过命名互斥体检查单实例。后续实例通过 Windows 本地命名管道 `FluentCode.Activation.v1.<primaryPID>` 向主实例传递命令行参数并激活前台窗口。管道限制仅允许当前 Windows 用户访问，帧大小有界保护。
- **服务中枢（`AppServices.cs`）**：
  持有单例的 `SettingsService` 与 `DpapiSecretStore`。应用所有页面共享同一个设置容器与凭据存储，杜绝各自实例化导致的配置分歧。
- **任务栏与动效策略（`App.Taskbar.cs` / `App.Motion.cs`）**：
  在主 UI 线程统一调度任务栏聚合状态；监听动效偏好并将系统动画与用户设置综合解析为 `FluentMotion` 动效令牌。

### 2.2 宿主窗口（`MainWindow.*.cs`）
- **标题栏与标签集成（`MainWindow.TitleBar.cs`）**：
  调用 `AppWindowTitleBar` API 将内容延伸至系统标题栏区域，实现 Windows 资源管理器风格的单行 TabView 布局。
- **欢迎页宿主（`MainWindow.Welcome.cs`）**：
  在初次运行且未配置有效连接时，以模态宿主覆盖整个工作区，承载 `WelcomeView` 引导页。配置成功后优雅平滑退出。
- **托盘与后台常驻（`MainWindow.Tray.cs`）**：
  维护 Win32 通知区域托盘图标，处理最小化至托盘与后台监听逻辑。

### 2.3 OpenCode 核心工作区（`Pages/OpenCodePage.*.cs`）
- **会话多标签管理（`OpenCodePage.Tabs.cs`）**：
  管理以固定首页（`HomeTab`）为起点的标签序列，支持多会话独立切换与生命周期管理。
- **消息与时间线（`OpenCodeConversationView.cs`）**：
  按逻辑回合增量合并消息。思考过程、工具调用与子代理分别分流至专用呈现器。
- **输入区（`OpenCodePage.Composer.cs`）**：
  支持单行自增高、多模态附件上传、输入法防误触发送、智能体/模型快速选择以及字符快捷触发器（`@`、`/`、`!`）。
- **右侧面板（`OpenCodeRightPanelView.cs`）**：
  承载只读工作目录、WSL 主机快速启动器（Explorer / VS Code / VSCodium / Zed）、上下文圆环（`OpenCodeContextRing`）与实时 To-Do 列表。

### 2.4 原生终端与第三方控件（`src/ThirdParty/WindowsTerminalControl`）
- 采用微软官方开源的 Windows Terminal 核心控件，直接承载为 WinUI 子窗口（Child HWND）。
- 通过 `OpenCodePersistentPtyAirspace.cs` 进行严格的 Airspace 空域遮罩管理：当页面弹出遮罩、对话框或菜单时，原生 HWND 同步隐藏，彻底避免 WinUI 与 Win32 控件层叠撕裂。

---

## 3. 存储路径与凭据安全模型

FluentCode 严格遵循最小权限与隔离存储原则：

### 3.1 本地配置路径
- 配置文件持久化存储在当前用户的本地应用数据目录：
  ```
  %LOCALAPPDATA%\FluentCode\settings.json
  ```
- 应用只使用自身专属的配置目录，首次运行时创建全新配置，绝不扫描、合并或导入其他外部客户端的数据。

### 3.2 凭据操作系统级加密（DPAPI）
- **加密机制**：
  敏感信息（Basic 认证密码、API 令牌、私钥）经由 Windows DPAPI（`DataProtectionScope.CurrentUser`）进行当前用户范围的操作系统加密，保存在本地。
- **配置与密钥分离**：
  `settings.json` 中仅记录抽象的 `CredentialId` 与端点元数据，不以明文形式序列化任何密码或 Token。
- **失败安全（Fail-Closed）**：
  当 DPAPI 凭据解密失败时，系统记录错误状态并拒绝连接，不静默降级为匿名访问。

---

## 4. 线程模型与调度规则

WinUI 3 应用具有严格的线程亲和性（Thread Affinity）：

```
┌────────────────────────────────────────────────────────────────┐
│               WinUI 3 STA / 主 UI 线程                         │
│  - XAML Visual Tree 渲染与布局更新                             │
│  - HWND 窗口消息、标题栏点击与拖拽                             │
│  - ITaskbarList3 COM 接口调用与任务栏角标绘制                  │
│  - AppNotificationManager 原生通知注册                         │
└────────────────┬──────────────────────────────▲────────────────┘
                 │ 派发后台任务                 │ DispatcherQueue
                 ▼                              │ 切换回 UI
┌───────────────────────────────────────────────┴────────────────┐
│                     后台工作线程池                              │
│  - OpenCodeClient HTTP REST 请求与 JSON 反序列化               │
│  - GET /api/event SSE 长连接与行缓冲读取                      │
│  - WebSocket 终端 VT 二进制字节流传输                          │
│  - BrowserRuntime Node 进程间管道通信                          │
└────────────────────────────────────────────────────────────────┘
```

- **UI 线程亲和**：
  所有与控件生命周期、动画、HWND 句柄和任务栏相关的操作必须在主 STA 线程上执行。
- **异步调度规范**：
  网络请求与数据流在后台线程池执行。数据就绪后，通过 `DispatcherQueue.TryEnqueue()` 切换回 UI 线程发布。对于高频 SSE 事件，后台管道在 50 毫秒窗口内进行节流批量合并，降低界面重排损耗。

---

## 5. 核心数据流与生命周期控制

### 5.1 数据源所有权（`OpenCodePageSource`）
为了防止跨会话串线与迟到的网络响应污染界面，FluentCode 引入了代际绑定的数据源机制：
- 每次服务配置或会话切换时，发布全新的 `OpenCodePageSource`（包含递增单调的 `Revision`）。
- 页面发起的每一个网络请求在启动时捕获当前的数据源引用与代际。
- 当数据返回时，若当前页面的数据源已经更新或撤销，迟到的响应将被静默丢弃，绝不会写入新会话。

### 5.2 状态仲裁与保护门（`OpenCodeInteractionGate`）
在权限审批与动态交互表单提交时，应用通过进程内互斥的 `OpenCodeInteractionGate` 协调：

```
 用户点击审批/提交 ──► EnterAsync(endpoint, session)
                          │
                          ├─► 重新读取服务端最新 Pending 状态（核对指纹）
                          │
                          ├─► 捕获有效数据源并在私锁内 BeginMutation
                          │
                          ├─► 执行单一 HTTP POST/DELETE
                          │     ├─ 204 Succeeded ──► CompleteMutation
                          │     ├─ 404/409       ──► Expired
                          │     └─ 断网/超时     ──► OutcomeUnknown (保持保护)
                          ▼
                       释放 Lease
```

- **排他执行**：同一会话下的并发操作被串行化，防止界面与通知双入口重复提交。
- **未知状态保护**：如果请求在网络传输中途断开或超时，操作被标记为 `OutcomeUnknown` 并保持会话保护，绝不自动重试，直至用户显式确认并重新核对服务端权威状态。
