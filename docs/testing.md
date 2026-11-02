# FluentCode 自动化测试指南

本文档全面介绍 FluentCode 的自动化测试体系、测试架构设计、测试套件运行方法以及全部 27 个关键 Runner 的技术覆盖范围。

---

## 1. 测试架构与设计哲学

FluentCode 采用严格的自动化测试策略，确保在快速重构与协议迭代中代码稳健：

- **源码直链编译（Source-Linked Headless Harness）**：
  大多数 Runner 不启动完整的 WinUI 窗口，而是通过 Windows .NET 10 的 `csc` 编译器，将真实的业务源码（`Services/**/*.cs`、`Models/**/*.cs` 等）与专用的轻量测试夹具直接编译，开启 `/warnaserror`，保证核心逻辑的自动化测试覆盖。
- **产物隔离与候选不变性（Immutable Candidate Checks）**：
  测试套件的驱动脚本（`tests/run-suite.py`）要求输出根目录必须为全新创建的路径。在执行测试前后，驱动程序会校验 Git 跟踪及未跟踪的工作区文件哈希，确保测试过程不会产生残留垃圾或篡改源码。
- **环境隔离（Disposable Sandbox）**：
  涉及应用启动与持久化测试的用例（如 `startup-smoke`），通过设置环境变量 `FC_STARTUP_SMOKE_ROOT` 强制将数据与配置重定向至临时沙盒目录，绝不读写用户的生产配置目录 `%LOCALAPPDATA%\FluentCode`。

---

## 2. 运行完整测试套件

所有命令都从客户端仓库根目录执行（独立克隆根目录，或父仓库中的 `clients/windows` 子模块目录）。在 WSL（Linux）环境下，保留原有 Windows PowerShell / `wslpath` 调用方式：

```bash
# 规划并依次执行所有 27 个测试用例（需要指定一个全新的产物输出目录）
python3 tests/run-suite.py --root "artifacts/fluentcode-import-suite-$(date -u +%Y%m%dT%H%M%SZ)" --execute
```

在原生 Windows Python 3.12+ 和 PowerShell 7 环境下，加 `--windows`，无需安装 WSL。`pwsh` 和 .NET 10 SDK 必须在 PATH 中：

```powershell
python tests/run-suite.py --windows --root "artifacts\windows-ci-suite-$(Get-Date -Format yyyyMMddTHHmmss)" --execute
```

- 若省略 `--execute`，驱动脚本仅打印执行计划与各个 Runner 的参数配置。
- 输出目录必须是客户端 `artifacts/` 下的全新目录；旧有 `scripts-move-suite-*` 等命名仍可使用。候选哈希限定为当前客户端仓库的输入文件，不包含父仓库的 Linux 内核文件。
- 测试过程中会输出每个 Runner 的耗时与执行状态，并在指定的证据目录下输出每个用例的日志（`results.json`、编译日志与执行快照）。

### Windows CI

`opencode-qol` 父仓库的 `.github/workflows/windows-client.yml` 递归检出子模块，使用短路径 Windows 克隆、.NET 10 SDK 和原生 `--windows` 模式。父仓库的 gitlink 更新触发 CI；只向客户端独立仓库提交不会自动推进父仓库的锁定版本。先准备 BrowserRuntime，再运行 Debug 构建以恢复 NuGet 依赖。套件用 `--exclude startup-smoke` 运行其余 **26 项**；这些默认入口不启用 `-RealWsl`、`-PanelFeedback` 或现场 OpenCode 后端。浏览器审计使用自建回环夹具；若未安装 Chrome，现有 Chrome 专项用例会明确跳过。

随后单独运行 Release 发布和 `tests/startup-smoke/run.ps1 -PublishDirectory <本次发布目录>`，避免冒烟测试重复发布；再构建自包含 MSIX，解包校验清单与载荷哈希。工作流上传发布目录、未签名 MSIX 及测试证据，仅 `fluentcode-v*` 标签触发 Windows Release ZIP/MSIX。Linux 的 `v*` 发布工作流保持独立；CI 不提供签名证书，MSIX 需签名后才能安装。构建、签名与手动安装步骤见 [MSIX 指南](msix.md)。

原生构建和启动冒烟脚本的相对 `-EvidenceRoot`、`-PublishDirectory` 均以客户端根目录（`clients/windows`）为基准，不依赖 PowerShell 启动时的进程工作目录。

---

## 3. 核心测试清单（27 项关键测试）

自动化套件共包含 **27 个测试项**（25 个 PowerShell 测试 + 2 个 Node.js 运行时测试）：

| 序号 | 测试 Runner 路径 | 核心验证范围 |
|:---:|---|---|
| 1 | `tests/opencode-auth/run.ps1` | BaseUrl 格式校验、DPAPI 凭据加密与解密、Basic 与 Bearer 认证请求头组装、禁止自动重定向与凭据脱敏 |
| 2 | `tests/opencode-connection/run.ps1` | HTTP 连接生命周期、端点拼接规范、请求超时强制熔断、取消令牌链与异常信息脱敏 |
| 3 | `tests/opencode-pairing/run.ps1` | V2 一次性配对链接解析、合法性校验、显式 GET 兑换流程与非自动持久化约定 |
| 4 | `tests/opencode-event-sync/run.ps1` | SSE 事件流解析、50ms 批量合并、断线恢复；真实页面处理函数/权限卡片/遮罩的子会话请求归属、回复 HTTP/Gate、初始 pending/重连/回退、父子导航、忙碌/IME 与未知拒绝恢复（原生控件为替身） |
| 5 | `tests/message-pagination/run.ps1` | 会话消息历史双向分页拉取、不透明 Cursor 游标维护、增量更新与分页边界对齐 |
| 6 | `tests/opencode-view-ack/run.ps1` | 界面确认机制、未处理提示同步、会话未读计数与已观测执行状态 |
| 7 | `tests/opencode-observations/run.ps1` | 权威会话状态、active 快照与 idle 终态；未打开子会话/孙会话权限和表单的通知跟踪、无关根排除、重连与跟踪退役，保留 root-only 完成通知规则 |
| 8 | `tests/notification-form/run.ps1` | 原生 Toast 通知组装、交互按钮与输入表单 Nonce 映射、`OpenCodeInteractionGate` 仲裁与未知结果锁定 |
| 9 | `tests/opencode-composer/run.ps1` | 输入区状态管理、字符快捷触发器（`@` 提及、`/` 命令、`!` Shell 直通）、多模式草稿切换与持久化 |
| 10 | `tests/opencode-composer-hookup/run.ps1` | 输入区按键管线：隧道阶段 Enter 拦截、Shift+Enter 换行、IME 中文输入法 80ms 选词保护、发送与停止按钮动态切换 |
| 11 | `tests/opencode-commands-attachments/run.ps1` | 斜杠命令目录读取与 204 回调确认；附件 Base64 编码、MIME 白名单、单文件与总容量大小硬上限截断 |
| 12 | `tests/opencode-queued-withdraw/run.ps1` | 服务端收件箱（inbox）排队条目展示、本地归属校验与消息安全撤回 |
| 13 | `tests/opencode-plugin-mutations/run.ps1` | MCP 服务连接/断开控制、插件运行状态查询、动态功能声明与连接详情浮层模型 |
| 14 | `tests/opencode-session-controls/run.ps1` | 会话控制流：运行期中断（Interrupt）、Agent 角色热切换、大模型切换与会话元数据修改 |
| 15 | `tests/opencode-session-ui-state/run.ps1` | 会话前端运行态投影、正在执行动效短语（Working Phrases）上下文匹配与状态同步 |
| 16 | `tests/fluent-motion/run.ps1` | Fluent 物理动画令牌、贝塞尔曲线参数计算、系统动画设置与用户“减弱动态效果”偏好仲裁 |
| 17 | `tests/startup-smoke/run.ps1` | **端到端发布冒烟**：编译并发布 Release 产物，拉起独立沙盒进程运行至少 15 秒，验证 HWND 句柄可用性与 UI 调度器就绪标记 |
| 18 | `tests/opencode-session-return/run.ps1` | 子代理会话层级导航、祖先链（Ancestry）拓扑合法性复核与一键安全返回原点 |
| 19 | `tests/opencode-capabilities/run.ps1` | 服务端特性嗅探、目录级扩展指令发现与多工作区环境配置适配 |
| 20 | `tests/opencode-browser-engine/run.ps1` | 会话级浏览器工作区生命周期、WebView2 与外部 Chrome 引擎切换、未保存设置脏状态导航拦截 |
| 21 | `tests/browser-runtime/run.ps1` | 离线浏览器运行时 C# 宿主集成、反向隧道回调（`tunnel.open/read/write`）、CDP 代理与 Lighthouse 审计 |
| 22 | `tests/opencode-browser-settings/run.ps1` | 浏览器配置持久化、用户数据目录隔离、回环代理白名单与禁止自代理循环 |
| 23 | `tests/desktop-bridge-qol/run.ps1` | 桌面增强 QoL 功能：紧凑 Token 格式化（如 `82.3K` / `1M`）、Billion 上下文遥测与压缩块列表投影 |
| 24 | `tests/opencode-persistent-pty/run.ps1` | 原生终端卡片布局、WinUI 空域（Airspace）同步遮挡与恢复、Windows Terminal 子窗口生命周期 |
| 25 | `tests/opencode-persistent-pty-transport/run-backend.ps1` | 终端底层 WebSocket 传输协议、VT100 字节流解析、服务端权威网格尺寸协商（Resize）与控制权接管 |
| 26 | `tests/browser-runtime/runtime.test.mjs` | Node.js 侧运行时单元测试：本地 HTTP/CONNECT/WebSocket 隧道代理、TCP 半关闭与背压控制 |
| 27 | `tests/browser-runtime/audit.test.mjs` | Node.js 侧审计单元测试：真机 Lighthouse 快照审计、CDP 会话扁平化与审计取消清理 |

---

## 4. 独立桌面回归测试（Theme Refresh Regression）

除了无人值守的 27 项自动化套件外，仓库中还包含一项针对 Windows 桌面环境的真机主题刷新测试：

- **脚本路径**：`tests/theme-refresh/run.ps1`
- **执行方式**：
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\theme-refresh\run.ps1 `
      -PublishDirectory "<Release发布目录>" `
      -EvidenceRoot "<全新证据目录>"
  ```
- **测试机理**：
  该用例在同一进程中测试动态深浅主题刷新。测试脚本会通过修改 Windows 注册表 `AppsUseLightTheme` 键值并在系统广播 `WM_SETTINGCHANGE` 消息，模拟真实的 Windows 系统在浅色与深色之间往复切换（Light → Dark → Light）。
- **校验内容**：
  启动内置测试桩，捕获 XAML 视觉树并比对像素：
  - 窗口 Mica 底色与实色回退更新。
  - 品牌文字标（Wordmark）与第三方模型提供商图标（OpenAI、DeepSeek 等）的 SVG 矢量图是否即时反色。
  - 原生终端着色器与快速访问编辑器按钮的前景继承色是否正确更新。
  - 测试结束前在 `finally` 块中**严格还原用户原有的 Windows 系统主题设置**。
> **注意**：由于该测试会直接改变宿主 Windows 桌面的外观，因此故意排除在全自动套件之外，需开发者手动触发。
