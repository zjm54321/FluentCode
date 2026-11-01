# TaskbarStatusService 契约

状态：基础服务及私有 ICO 接线已落盘。资产接线验证仅限静态/隔离编译/无原生调用 mock；**未实例化 COM、未调用真实任务栏、未验证实际显示**。本文末尾保留基础服务初版的历史验证边界。

## 范围与依赖

- 命名空间：`FluentCode.Services.Taskbar`。
- 文件：`Services/Taskbar/TaskbarStatus.cs`、`TaskbarNative.cs`、`TaskbarStatusService.cs`。
- 使用 Windows 原生 `ITaskbarList3` 与既定 Fluent 私有 ICO，没有新增 NuGet 包、DI、网络、通知、系统托盘或 tile/`BadgeUpdateManager` 依赖。适用于已有 HWND 的桌面窗口，不依赖打包应用 tile 身份。
- 资产接线仅扩展已有 `TaskbarWindowHook` 消息边界及 csproj 的 ICO 部署项；不修改 App/MainWindow/Pages/Notifications，不创建计时器。

## 全部显式公开接口

```csharp
namespace FluentCode.Services.Taskbar;

public enum TaskbarStatus { Idle, Working, NeedsAttention, Completed, Failed }
public sealed record TaskbarProgress(ulong Completed, ulong Total);
public sealed record TaskbarSnapshot(TaskbarStatus Status, TaskbarProgress? Progress = null);

public enum TaskbarOperationStatus
{
    NotInitialized, Submitted, Released, UnsupportedPlatform, WrongThread,
    InvalidWindow, InvalidSnapshot, Disposed, NativeError
}

public sealed record TaskbarOperationResult(
    TaskbarOperationStatus Status, string Operation,
    int? HResult = null, int? Win32Error = null)
{
    public bool Succeeded { get; } // Submitted 或 Released；不是用户可见性保证
}

public sealed class TaskbarStatusService : IDisposable
{
    public TaskbarStatusService(IntPtr windowHandle);
    public IntPtr WindowHandle { get; }
    public TaskbarSnapshot CurrentSnapshot { get; } // 初始 Idle，最后接受的有效意图
    public TaskbarOperationResult LastResult { get; } // 初始 NotInitialized / "None"
    public TaskbarOperationResult Apply(TaskbarSnapshot snapshot);
    public TaskbarOperationResult Reapply();
    public TaskbarOperationResult RefreshAssets(); // 系统主题/高对比/DPI 改变时，同 STA 调用
    public string IconThemeSource { get; } // 固定来源/回退标记，非原始注册表值
    public void Dispose();
}
```

位置 record 的参数对应编译器生成的公开 init 属性/构造函数/Deconstruct 等标准 record 成员。返回值不可变，无共享可变集合。

## 状态映射

每次 `Apply` 是完整快照替换，先清旧 progress，再替换/清除 overlay，再设置当前进度。

| 上层状态 | progress | overlay / 无障碍 description |
|---|---|---|
| Idle | `TBPF_NOPROGRESS` | `HICON=NULL`、description=null，清 overlay |
| Working，无真实进度 | `TBPF_INDETERMINATE` | 清 overlay；不发送任何伪造百分比 |
| Working，有真实进度 | `SetProgressValue(completed,total)` 后明确 `TBPF_NORMAL` | 清 overlay |
| NeedsAttention | `TBPF_NOPROGRESS`，**不是 PAUSED** | `taskbar-attention.ico`；“FluentCode：有任务需要处理” |
| Completed | `TBPF_NOPROGRESS`，不发送 100% | `taskbar-completed.ico`；“FluentCode：任务已完成” |
| Failed | `TBPF_ERROR`；有真实进度时先提交其值，否则不调用 SetProgressValue | `taskbar-failed.ico`；“FluentCode：任务失败” |

- 真实进度只允许用于 Working/Failed，`Total > 0` 且 `Completed <= Total`；其他组合、未知枚举、null 快照返回 InvalidSnapshot，不改变当前意图、不调用任务栏。
- Failed 无数值时仅请求 ERROR 状态；具体红色条形的默认长度由 Shell 决定，本服务不把它解释成百分比，也不注入虚假数值。
- 图标来自 `LoadImageW(NULL, fixedPath, IMAGE_ICON, size, size, LR_LOADFROMFILE)`，是**本实例私有 HICON**，只在所属 STA 用 `DestroyIcon` 释放；不加载/释放共享系统图标。本服务不自绘图标、不改变既定资产设计。
- Completed/Failed 没有自动消失计时器；上层确认/清理后提交 Idle 或新的实际快照。

## 上层聚合与接线要求

1. HWND 已创建后，在其所属 **STA/UI 线程**上创建一个服务并持有。构造函数仅记录 HWND/创建线程，不做 COM/PInvoke。使用已有窗口 HWND；本服务不获取/创建/激活窗口。
2. Apply/Reapply 必须在同一个托管线程和窗口所属原生线程上调用；服务还检查 STA、IsWindow、HWND 的所属进程及原生线程。不跨 Dispatcher 自动派发；聊天异步回调由上层调度。线程不符返回 WrongThread，不碰任务栏。
3. 上层明确提供一个聚合快照，**待处理/需用户操作优先于仍在工作，工作优先于历史完成**。多个失败、等待确认与历史终态的业务取舍仍由上层明确决定；服务不维护任务表，不自动计数、不推断结果。收到 HTTP/prompt 接纳、idle 或静默不等于 Completed。
4. 可先保存真实状态并调用 Apply；若按钮尚未就绪，结果不保证显示。已有 `TaskbarWindowHook.Initialize(hwnd)` 注册 `TaskbarButtonCreated` 并在按钮创建及 Explorer 重启恢复后调用 **Reapply**；直接使用服务的上层仍需自己转发消息。
5. Reapply 释放旧的私有 COM 实例，重新创建 `CLSID_TaskbarList`、调用 `HrInit`，使图标缓存失效并重送 CurrentSnapshot。旧 HICON 在新 overlay 提交成功后才释放；失败保留至下次显式调用/Dispose。每次调用只尝试一次，不重试、不循环；消息回调内按 UI 线程规则调用。
6. HWND 变化需释放旧服务并为新 HWND 新建服务；服务不支持重绑定。窗口销毁前若需清状态，显式 Apply(Idle)，然后 Dispose。Dispose **不清任务栏**、不销毁 HWND，只释放自己创建且未对外共享的 RCW 与私有 HICON。

示例（仅接线说明，未执行）：

```csharp
using FluentCode.Services.Taskbar;

// hwnd 由父窗口提供；以下全部在该窗口的 STA/UI 线程。
var taskbar = new TaskbarStatusService(hwnd);
var submitted = taskbar.Apply(new TaskbarSnapshot(TaskbarStatus.Working));
// 仅在业务确实有可测量总量时：
submitted = taskbar.Apply(new TaskbarSnapshot(
    TaskbarStatus.Working, new TaskbarProgress(completedUnits, totalUnits)));

// 聚合器明确发现待处理事项：
submitted = taskbar.Apply(new TaskbarSnapshot(TaskbarStatus.NeedsAttention));
// 父窗口收到 TaskbarButtonCreated / Explorer 恢复后的消息：
var reapplied = taskbar.Reapply();
// 退出前（是否先清状态由父窗口决定）：
taskbar.Apply(new TaskbarSnapshot(TaskbarStatus.Idle));
taskbar.Dispose();
var disposal = taskbar.LastResult;
```

## 失败、释放与可见性语义

- `Submitted` 仅表示本次所需 HRESULT 调用均未返回失败；Shell 设置、按钮尚未创建、应用分组/多个窗口、Explorer 状态等均会影响实际展示。无“用户已看到”的保证。
- `CurrentSnapshot` 是最后接受的有效**意图**，并非上次已显示的状态。窗口无效、COM/Win32 提交失败仍保留该意图，以便之后 Reapply；错线程/非法快照不更新意图。
- 原生错误返回 NativeError，提供固定操作名与可用的 HRESULT/Win32Error；不传播平台异常到聊天，不记录日志、不返回原始异常文本。原生多步调用不是事务，失败前可能已清除/改变一部分状态；调用方应记录结果，需要时显式 Reapply，不把部分成功当整体成功。
- COM 使用 `[PreserveSig] int`，负 HRESULT 才表示失败；非负成功码被接受。HrInit 失败不会当初始化成功继续使用；抛异常时尽力释放该实例，下一次可重新创建。
- Dispose 使用 `Marshal.FinalReleaseComObject` 及 `DestroyIcon`，只释放本实例私有资源。幂等，无 finalizer，无后台清理线程。持有 COM 或 HICON 时若错线程 Dispose：不释放、不标为已释放，LastResult=WrongThread；父窗口必须回所属 STA 再次调用。没有原生资源时 Dispose 无原生副作用。图标释放失败返回 NativeError 并保留未释放句柄，允许显式重试；hook 不丢弃该服务或根引用、不伪报 Released。
- 窗口已失效时 Apply/Reapply 返回 InvalidWindow；Dispose 不要求窗口仍存活。上层必须正确管理窗口寿命，避免 HWND 销毁/复用期间发送旧快照。
- 操作是同步、固定有限次数的 COM/Win32 调用；无内部等待/轮询/自动重试，但不承诺 Windows COM/RPC 的硬实时超时上界。

## 官方签名静态依据

本次使用本机安装的 **Windows SDK 10.0.26100.0 官方头文件**，未联网查询、未调用 Windows 任务栏：

- `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\ShObjIdl_core.h`
  - 15660–15667：TBPFLAG（0/1/2/4/8）。
  - 15676：IID `EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF`。
  - 15737–15863：完整 `ITaskbarList3Vtbl`。IUnknown 的 QueryInterface/AddRef/Release 由 InterfaceIsIUnknown 提供，其后声明的顺序为 HrInit、AddTab、DeleteTab、ActivateTab、SetActiveAlt、MarkFullscreenWindow、SetProgressValue、SetProgressState、RegisterTab、UnregisterTab、SetTabOrder、SetTabActive、ThumbBarAddButtons、ThumbBarUpdateButtons、ThumbBarSetImageList、SetOverlayIcon、SetThumbnailTooltip、SetThumbnailClip，共 18 个方法。没有漏掉继承槽位。
  - 29310：CLSID `56FDF344-FD6D-11D0-958A-006097C9A090`。
- 同 SDK `um\WinUser.h`：GetWindowThreadProcessId（10273）、LoadIconW（10749）、系统图标资源与别名（11149–11165）。
- ABI 映射：HWND/HICON/HIMAGELIST/原生指针→IntPtr；ULONGLONG→ulong；DWORD/UINT/TBPFLAG→uint；BOOL→MarshalAs(UnmanagedType.Bool)；LPCWSTR description→LPWStr；HRESULT→PreserveSig int。未调用的 THUMBBUTTON 数组和 RECT 指针槽位也保留为 IntPtr；本服务不使用这些方法。

## 私有 ICO 部署、主题与生命周期（2026-10-07 资产接线）

- 固定映射与原始资产详见 `taskbar-icons-source.md`；两套六个 ICO 未重画、未改色。主项目仅增加 `Assets\Taskbar\**\*.ico` 的 Content ItemGroup，输出/Publish 均 `PreserveNewest`，不部署转换源 SVG。
- 路径仅由 `AppContext.BaseDirectory`、`Assets/Taskbar`、内部固定套名及文件名组合；不接受 caller 路径。缺文件或无法加载返回 `NativeError / LoadTaskbarAsset / Win32Error`，不静默回退库存图标，不返回文件路径或异常文本。
- 普通主题只读 HKCU `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`（0 深、1 亮）；**不是 AppsUseLightTheme，也不是应用 RequestedTheme**。这纠正来源文档第 5 节的候选 API 提示，未修改该设计文档。缺值/非法值/已知读取异常回退原设计默认 dark-taskbar，公开 `IconThemeSource=RegistryFallbackDark`。
- 先只读 `SPI_GETHIGHCONTRAST`；启用高对比时以 `GetSysColor(COLOR_WINDOW)` 的背景亮度 best-effort 选原样 light/dark 资产，来源标记 `HighContrastWindowLight` / `HighContrastWindowDark`。查询失败显式 `HighContrastQueryFailedFallbackDark`。没有生成专用高对比图标、没有改系统配置；自定义高对比任务栏背景可能不同于 COLOR_WINDOW，**不保证其对比度**。
- 普通注册表成功来源为 `SystemLight` / `SystemDark`；未查询为 `NotRead`。服务及 hook 均公开 `IconThemeSource`，这些都是固定诊断字符串，不含用户数据。
- 采用 HWND 的 `GetDpiForWindow`：≤96/120/144 DPI 分别取 16/20/24，以上取最大 32 档。混合 DPI、任务栏与窗口不同屏或 >200% 时 Shell 仍可能缩放；不是任务栏实际物理 DPI/像素效果的保证。
- `TaskbarIconCache` 为内部 helper，无公开注入接口：`GetIcon(TaskbarStatus, IntPtr, out IntPtr)` 获取/复用；`Commit(IntPtr)` 仅在 SetOverlayIcon 成功后替换并释放旧图标；`Invalidate()` 只失效 key；`ReleaseAll()` 释放自有图标。后三个原生操作相关方法均验证创建线程与 STA。缓存通常一个已应用图标，失败候选最多再一个；后续调用先清失败候选，清理失败不再加载。
- COM 失败不退休旧图标；成功清除 Idle/Working overlay 后释放缓存；Reapply/主题变化成功后清旧缓存。DestroyIcon 成功立即移除记录，失败保留所有权并返回 `NativeError / DestroyTaskbarAsset`，无后台重试、无非 owner 释放、无 double free 成功句柄。
- 已有 hook 在 `DefSubclassProc` 之后同步响应 `WM_SETTINGCHANGE`、`WM_THEMECHANGED`、`WM_SYSCOLORCHANGE`、`WM_DPICHANGED`，调用 `RefreshAssets()`；不解析 lParam 指针或消息字符串、不跨线程、不改真实快照。`TaskbarButtonCreated` 继续 Reapply/重建 COM。
- 父若已经使用 hook 的 Initialize/Apply/Dispose，无新增 App/Page 接线；直接使用服务则在相同 HWND STA 上转发上述消息到 `RefreshAssets()`，Explorer 恢复到 `Reapply()`，关闭到 `Dispose()`。清理失败时保留 owner 引用，检查 LastResult 并在 owner STA 显式重试。
- 本轮证据目录：`artifacts/validation/taskbar-assets-20261007/`。只允许静态、headless 隔离编译及替身 mock；**真实 COM/LoadImage/DestroyIcon/任务栏外观/细对勾/主题切换/Explorer 恢复均未运行验收**，由父统一执行。

## 基础服务初版验证状态与后续（历史记录，不代表本次资产接线）

- 已静态核对上述官方头文件、完整 vtable/指针宽度/BOOL/字符串编码、状态映射、真实进度校验、共享图标不释放、STA/HWND 所属进程线程限制及无重试策略。
- 未创建编译工程或调用 dotnet：本轮白名单只有 Taskbar 源码目录和本文档，不在源码目录混入验证工程/产物。因此没有首次运行/证书副作用。
- 未实例化 COM、未调用 LoadIcon/真实任务栏、未运行应用、未修改 App/Pages/csproj/Notifications、未执行 Git。
- 父会话待统一编译与运行集成：COM 激活和 HrInit、各状态实际提交、真实进度、系统 overlay 及无障碍描述、窗口生命周期、消息接线和 Explorer 恢复、Shell 设置/分组下可见性。若另建隔离 compile-only 工程，所有 dotnet 调用应显式设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`、`DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`，只编译、不实例化服务/COM。
