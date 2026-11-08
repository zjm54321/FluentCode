# FluentCode

> **FluentCode** is a Windows desktop client for [OpenCode](https://opencode.ai) and opencode-qol, built with C#, WinUI 3 (Windows App SDK 1.8), and .NET 10. It provides multi-session chat, native terminal control, an isolated browser runtime, and Windows desktop integration.

[![License: GPL v3+](https://img.shields.io/badge/License-GPL%20v3%2B-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2021H2%2B%20%7C%20Windows%2011-0078D4.svg)](#系统要求)
[![Framework](https://img.shields.io/badge/.NET-10.0%20(x64)-512BD4.svg)](https://dotnet.microsoft.com/)
[![Windows App SDK](https://img.shields.io/badge/WinAppSDK-1.8-0078D4.svg)](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

---

## 项目简介

FluentCode 为 OpenCode 及 opencode-qol 后端提供原生 Windows 桌面界面。应用基于 WinUI 3 开发，使用资源管理器风格的单行标签栏、Windows Terminal 原生控件、离线 Node.js 浏览器运行时以及系统通知与任务栏状态指示。

本项目是保留完整历史的独立 Git 仓库；`opencode-qol` 通过 `clients/windows` 子模块锁定具体提交，父仓库只保存链接，不合并客户端历史，也不将客户端纳入 Linux Nix 构建。当前版本为 **1.2611.14**，父仓库的 Windows 发布标签使用 `fluentcode-v*`（例如 `fluentcode-v1.2611.14`）。已签名 MSIX 的完整文件夹中可双击 `Install.cmd`：先核验安装包和证书，经你确认后仅为证书信任请求 UAC，再以原普通用户安装。不要以管理员身份运行整个安装脚本。构建、签名及故障排查见 [MSIX 指南](docs/msix.md)；自动更新待提供真实发布端点后配置。

---

## 功能特性

- **窗口与标签结构**
  - 单行标签栏：与窗口标题栏合并，最左侧固定首页（Pinned Home Tab），右侧排列会话标签，支持窗口拖拽与常规窗口控制按钮。
  - 背景材质：在 Windows 11 上透出 Mica 材质，在 Windows 10 上回退为 BaseAlt 实色背景。
  - 动效与触控：支持物理过渡动效，可响应系统与应用设置中的“减弱动态效果”；支持触摸手势与长按操作。
  - 主题切换：跟随系统或在设置中手动切换深浅色主题，界面文本、图标、矢量图与终端调色板即时刷新。

- **连接向导与凭据存储**
  - 欢迎页引导：未配置时显示欢迎页，提供 WSL 发行版自动发现、远程地址认证（用户名 + 密码）及配对链接三种连接方式。
  - 凭据安全：凭据通过 Windows DPAPI（CurrentUser 范围）加密保护，配置文件 `%LOCALAPPDATA%\FluentCode\settings.json` 中仅存储凭据引用，不以明文保存密码或密钥。

- **会话与时间线交互**
  - 步骤分组：单次回合内的连续思考与工具调用折叠为处理过程组，头部标明成功与失败次数。
  - 思考过程：支持 Markdown 渲染，进行中时显示思考进度与环形指示器。
  - 工具调用详情：工具条目悬停可查看参数与输出预览，点击可弹出居中大视图，支持代码自动换行与 Diff 并排/统一视图切换。
  - 子代理查看与导航：工具行支持唤起独立只读子会话小窗口；切换进入子会话后，底部显示返回按钮以便随时切回父会话。
  - 消息上下文菜单：右键或长按呼出原生菜单，提供复制整段、复制代码块、单步撤回与恢复撤回。
  - 输入区（Composer）：
    - 底部工具栏提供智能体、模型与思考程度（Effort）切换菜单。
    - 键入 `@` 筛选并插入 Agent 与 Skill。
    - 键入 `/` 呼出服务端命令与本地动作。
    - 开头键入 `!` 切入当前目录的 Shell 执行模式。
    - 支持剪贴板图片粘贴（Ctrl+V）与文件附件拖拽。
    - 输入法选词保护，避免中文选词时误发送；任务运行中发送按钮切为停止按钮。

- **侧边栏与辅助面板**
  - 左侧面板：展示子代理会话树与终端列表。
  - 右侧面板：展示会话标题与 ID、WSL 快速访问入口（在资源管理器中打开、在 VS Code / VSCodium / Zed 中打开）、浏览器与终端快捷入口、上下文 Token 用量与实时 To-Do 待办清单。

- **原生终端集成**
  - 嵌入 Windows Terminal 原生控件（Child HWND 承载），支持持久 PTY 交互会话与只读 Shell 输出查看。
  - 默认处于只读保护，双击终端区域接管键盘输入；按 F2 或通过菜单重命名终端。
  - 关闭终端视图不会终止后台进程，可通过专门的结束按钮终止会话。
  - 支持调整等宽字体、字号（6–72 pt）、光标形态及预设配色方案。

- **会话浏览器**
  - 支持会话级 WebView2 引擎或连接外部 Chrome。
  - 附带离线 Node.js 运行时，提供反向网络隧道与 Lighthouse 审计功能，不修改系统环境变量。

- **系统通知与任务栏**
  - 原生 Toast 通知：后台任务完成通知、权限审批（单次/始终/拒绝）与交互式输入表单。
  - 任务栏状态指示：通过任务栏进度条与状态角标反映空闲、运行中、需处理、已完成与失败五种状态。
  - 系统托盘后台模式：支持关闭窗口时最小化至通知区域，后台继续维持长连接与任务执行。

---

## 系统要求

| 项目 | 要求 | 说明 |
|---|---|---|
| **操作系统** | Windows 10 21H2（Build 19044）或更高，推荐 Windows 11 | 仅支持 x64 架构 |
| **运行时** | .NET 10 Desktop Runtime (x64)<br>Microsoft Edge WebView2 Runtime | 本项目采用依赖框架部署（`SelfContained=false`），需预先安装 .NET 10 桌面运行时。Windows App SDK 1.8 组件已随程序输出目录自带（`WindowsAppSDKSelfContained=true`），无需单独安装 App SDK。 |
| **外部服务** | OpenCode 兼容后端服务（如 `opencode-qol`） | FluentCode 仅作为前端客户端，不自动启动后端守护进程 |

---

## 构建与运行

在 Windows 的短路径 Git 克隆中，从本仓库根目录执行以下命令；若通过 `opencode-qol` 子模块使用，则先在父仓库运行 `git submodule update --init --recursive`，再进入 `clients/windows`：

```powershell
# 1. 准备离线浏览器运行时（下载固定版本的官方 Node 并安装锁定依赖，仅首次需联网）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\browser-runtime\prepare.ps1

# 2. 编译 Debug 版本
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-debug.ps1

# 3. 运行 Debug 构建输出
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Configuration Debug

# 4. 发布 Release 版本
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1
```

> **运行说明**：
> 构建输出位于 `src\FluentCode\bin\x64\<Configuration>\net10.0-windows10.0.26100.0\win-x64\`。
> 运行应用时必须保留该目录及其下 `BrowserRuntime` 子目录的完整结构，不能单独提取单个 `FluentCode.exe` 运行。

---

## 自动化测试

运行 28 项自动化测试套件：

```bash
# 在 WSL 环境下规划并执行完整套件（需要指定一个全新的产物输出目录）
python3 tests/run-suite.py --root "artifacts/scripts-move-suite-$(date -u +%Y%m%dT%H%M%SZ)" --execute
```

原生 Windows Python 可通过 `--windows` 直接调用 PATH 中的 PowerShell 7，无需 WSL：

```powershell
python tests/run-suite.py --windows --root "artifacts\windows-ci-suite-$(Get-Date -Format yyyyMMddTHHmmss)" --execute
```

测试覆盖范围与独立运行方法参见 [测试文档](docs/testing.md)。

---

## 文档索引

- [用户手册](docs/user-guide.md)：面向最终用户的界面功能说明与操作流程。
- [系统架构](docs/architecture.md)：组件划分、生命周期、线程模型与存储设计。
- [OpenCode 协议集成](docs/opencode-integration.md)：REST 端点、SSE 事件流、状态仲裁门与 PTY 传输协议。
- [界面规范与实现](docs/ui-guidelines.md)：Fluent 设计规范、材质透出、动效策略与富文本渲染管线。
- [原生终端集成](docs/terminal.md)：Windows Terminal 控件承载、空域遮罩处理与终端外观设置。
- [通知、任务栏与托盘](docs/notifications-and-tray.md)：Toast 弹窗、任务栏状态角标与托盘后台模式。
- [开发与构建指南](docs/development.md)：环境搭建、编译打包与故障排除。
- [自动化测试指南](docs/testing.md)：测试架构、28 个 Runner 清单与真机主题回归测试。

---

## 开源许可与版权

- FluentCode 源码遵循 [GNU General Public License v3.0 或更高版本 (GPL-3.0-or-later)](LICENSE)。
- 版权所有 © 2026 mingz。
- 第三方组件与资产许可证见 [tools/browser-runtime/THIRD-PARTY-NOTICES.md](tools/browser-runtime/THIRD-PARTY-NOTICES.md) 与 [ATTRIBUTION.md](src/FluentCode/Assets/Brands/ATTRIBUTION.md)。
