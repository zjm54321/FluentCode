# 原生终端集成指南

本文档介绍 FluentCode 中内嵌的 Windows Terminal 终端控制系统的技术实现、空域（Airspace）隔离方案、持久 PTY 交互与外观定制。

---

## 1. 概述与结构

FluentCode 内嵌了 Windows Terminal 的核心渲染控件（由 `src/ThirdParty/WindowsTerminalControl` 提供），以 Win32 子窗口（Child HWND）形式嵌入界面，支持 DirectX 文本渲染与控制台控制序列。

---

## 2. 终端类型与模式

应用内的终端分为两种场景：

| 终端类型 | 数据源与协议 | 交互能力 | 几何尺寸协商 |
|---|---|---|---|
| **持久交互终端（Persistent PTY）** | 后端独立 WebSocket 长连接，双向二进制 VT 流 | 支持双向键盘输入与全屏交互应用（vim、htop、REPL） | 服务端权威；客户端通过“适应窗口”发起提议，服务端确认后重放 |
| **Shell 工具命令输出（Shell Output）** | `/api/shell/{id}/output` REST 接口 | 严格只读；用于查看智能体执行单条 Shell 脚本的标准输出 | 本地视口自适应，不向服务端发送终端缩放请求 |

---

## 3. 用户交互与生命周期

### 3.1 双击接管输入（Takeover Protocol）
智能体启动的持久终端可能正在后台运行命令。为防止误触按键打断任务，终端在打开时处于只读状态：
- 键盘输入被就地拦截。
- **双击终端文本区域**：向服务端发送控制权接管信号，终端转入交互模式（Controller），激活光标并开始响应键盘输入。

### 3.2 重命名（F2）与生命周期管理
- **重命名**：选中终端后按 <kbd>F2</kbd> 或在菜单中点击“重命名…”，可为终端指定自定义名称。
- **关闭视图 vs 结束进程**：
  - 点击终端卡片右上角的 ✕ 仅销毁前端 HWND 并取消数据读取，**不会终止服务端的 Shell 或 PTY 进程**。
  - 需要终止进程时，必须点击明确的“结束”操作。

### 3.3 窗口自适应（Window Fit）
终端字符矩阵受服务端进程控制。点击工具栏中的“适应窗口”按钮：
1. 客户端测量当前卡片可用物理像素与字符矩阵，计算目标行数（Rows）与列数（Cols）。
2. 向服务端发起尺寸变更提议。
3. 服务端确认并调整虚拟终端尺寸后，客户端重置解析器并在新尺寸下重放缓冲区。

### 3.4 文本选取与触控
支持鼠标拖拽与触控手势选取文本。选取复制采用 Windows UI 自动化（UIA）的 `TextPattern` 只读接口，不向控制台注入伪造按键。滚动条采用细窄样式适配桌面与触控。

---

## 4. WinUI 3 子窗口空域管理（Airspace Management）

在 Windows 桌面应用中，将 Win32 子窗口（HWND）嵌入到 DirectComposition / WinUI 3 视觉树中会面临经典的 **Airspace（空域冲突）** 问题：原生 HWND 无法被上层 XAML 元素（如弹窗、下拉菜单、飞行浮层）按图层顺序正常遮挡，容易产生视觉穿透。

FluentCode 通过 `OpenCodePersistentPtyAirspace.cs` 实现了严密的同步空域管理：

```
 触发操作（打开设置 / 弹出权限确认 / 呼出斜杠菜单 / 窗口移动）
                          │
                          ▼
            OpenCodePersistentPtyAirspace
                          │
  ┌───────────────────────┴───────────────────────┐
  │                                               │
  ▼                                               ▼
同步将 HWND 隐藏 (ShowWindow SW_HIDE)      记录当前卡片与焦点状态
  │                                               │
  └───────────────────────┬───────────────────────┘
                          │ 
                          ▼ 用户关闭上层遮罩 / 窗口移动完成
                          │
            重新校准物理坐标与 DPI 缩放
                          │
                          ▼
            恢复显示 HWND (ShowWindow SW_SHOW)
```

- **同步隐藏**：当页面弹出权限审批遮罩、表单问题卡片、全屏浏览器浮层或打开应用设置时，终端 HWND 立即同步隐藏，XAML 遮罩层完整呈现。
- **物理内边距**：HWND 控件在终端卡片内四周各内缩 12 DIP，外圈的圆角边框、阴影与工具栏完全由 XAML 原生绘制，视觉浑然一体。
- **窗口移动与缩放阻尼**：在拖动窗口或调整尺寸过程中，暂时抑制终端 HWND 频繁重排，待布局稳定后再刷新边界。

---

## 5. 外观与主题定制

终端外观配置通过 `AppSettings.TerminalAppearance` 持久化，修改后实时生效：

- **等宽字体**：默认使用 Cascadia Mono，支持自定义字体名称。
- **字号调整**：支持 6 至 72 pt 的字号范围（默认 12 pt）。
- **配色方案（Color Schemes）**：
  - 跟随系统主题：深色下自动选用 Campbell，浅色下自动选用 One Half Light。
  - 内置经典配色：Campbell、Campbell Powershell、One Half Dark、One Half Light、Solarized Dark、Solarized Light、Tango Dark、Tango Light、Vintage。
- **光标形态**：支持条形（Bar）、方块（Block）与下划线（Underline），并支持开关光标闪烁。

在“设置 → 终端”或终端卡片右上角的“外观”菜单中调整上述配置时，更改立即写入本地配置并通过原生 API 直接更新着色器画刷，**无需销毁重建 HWND 句柄或重置终端缓冲区内容**。
