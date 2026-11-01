# App 正常运行期 Local 完成通知接线（2026-10-06）

## 已实现范围

本阶段修改仅限 `src/FluentCode/App.xaml.cs`、新增 `src/FluentCode/App.Notifications.cs` 和本文档。不修改 MainWindow、Pages、Services、Models 或主工程文件；不重写通知服务。

`docs/notifications-contract.md` 中“App 尚未接线”的历史状态，现仅在**正常启动/运行中的 Local 完成通知**范围由本文更新；冷启动、单实例和 OpenCode 交互仍未接线。

1. App 创建并激活 MainWindow，先订阅其 Closed，再建立 App 唯一持有的 `CompletionNotificationService`。同一 App 对象再次 OnLaunched 只激活已有窗口，不重复创建或注册；这不是跨进程单实例机制。
2. App 先订阅服务 `NotificationActivated` 和窗口 `LocalTaskCompleted`，再向窗口 DispatcherQueue 投递 Initialize。服务自己保证内部 NotificationInvoked 订阅先于 Register。初始化没有在 App 构造函数中执行，也不从通知回调同步重入窗口。
3. 只消费当前窗口的 `LocalTaskCompleted`，调用 `NotifyCompleted(NotificationSource.LocalApi, args.SourceRequestId)`；不新建 request id、不使用 SessionId 代替请求标识，不根据 HTTP 接受/取消/静默推断成功。
4. 服务识别的 Local completed 激活，且 requestId 非 null 时，只向已捕获的 UI DispatcherQueue 投递“激活当前已有窗口”。不创建新窗口、不切换会话、不读取 RawArguments，不根据 requestId/nonce 打开 URL、导航、提交请求或授权。
5. 当前窗口 Closed 时先清除窗口引用，再退订窗口事件，清除服务引用、退订服务激活事件并 Dispose。已经排队的初始化/激活回调再次检查窗口和服务身份，关闭后不会重新注册或激活旧窗口。

## 已核实的接口及成功来源

- `src/FluentCode/MainWindow.xaml.cs` 实际公开 `event EventHandler<Pages.Local.LocalTaskCompletedEventArgs>? LocalTaskCompleted`，转发时 sender 为 `this`，DTO 原样传递。
- `src/FluentCode/Pages/Local/LocalTaskCompletedEventArgs.cs` 实际提供只读 `SourceRequestId`、`SessionId`；契约为仅 Completed + Stop 触发。
- 当前 `src/FluentCode/Pages/Local/LocalChatView.cs` 的 Completed 分支将 success 设为 `terminal == ChatFinishReason.Stop`，通过既有 TaskCompleted→NormalPage→MainWindow 链转发。App 不重新消费协议流或自行推断其终态。
- 实际服务方法为 `bool Initialize()`、`NotificationDispatchResult NotifyCompleted(NotificationSource source, string requestId)`、`void Dispose()`；激活 DTO 为 `NotificationActivationInfo`。本次 stub 签名按实际声明建立，未添加共享接口。

## 失败、隐私与退出语义

- 通知对象构造、初始化投递、Initialize、NotifyCompleted、窗口激活及 Dispose 的平台异常均在接线边界隔离，不向 Local 事件调用方传播；无 async void、无同步等待后台回调、无新增消息/凭据/激活参数或异常日志。
- Initialize 返回 false 或提交 Failed/Unavailable 不改变聊天结果，不重发消息，也不伪装通知成功。服务仍由 App 持有直到关闭清理；本阶段不自动重试注册。
- 如果初始 DispatcherQueue 拒绝投递，立即退订 Local 完成事件并释放服务。初始化完成前发生的完成事件可能返回 Unavailable；不增加缓存/补发调度。
- `Submitted` 仅表示 Show 未抛异常，不能证明用户看到了横幅。去重仍由原服务按 `(LocalApi, SourceRequestId)` 执行，不跨进程/重启。
- 清除引用先于 Dispose，使已排队回调失效。正常 Closed 负责退出清理；强杀/崩溃无法保证执行清理，本阶段没有添加进程级退出钩子。
- 激活 DTO 是经过解析的数据，**不是授权凭证**。未知/非 Local/非 completed 激活忽略；即使有人伪造合法 completed 参数，App 最多激活已有窗口，不获得任何导航/网络/批准权限。交互 nonce 不在 App 解析或执行。

## 冷启动与仍未实现的功能

- 本次**没有**调用 `AppInstance.GetCurrent().GetActivatedEventArgs()`、检查 ExtendedActivationKind、实现 FindOrRegisterForKey/RedirectActivationToAsync，或修改启动入口/csproj。
- 后续实现激活获取时必须遵守 `Register()` **先于** `GetActivatedEventArgs()` 的契约顺序；当前延后 Initialize 的正常启动方案不能被误认为已经完成这个流程。需在单实例/冷启动整合阶段重新安排启动识别与注册时序。
- 现状仍可能由通知冷启动另一个进程，并在其 OnLaunched 创建额外窗口。App 内复用已有窗口的检查无法阻止跨进程重复窗口；当前不能可靠区分普通启动与通知启动。
- 没有将冷启动 activation 手动投递给交互服务。旧 nonce 的 registry 不跨进程/重启保留，不能据此自动批准或重建 endpoint/request。
- OpenCode 新 idle 观察、权限/Form 通知发现与结果展示、任务栏、单实例重定向均不在本阶段实现范围。

## 实际验证证据

项目外目录：`/mnt/c/Users/Zhang/source/repos/FluentCode-app-notification-glue-validation-20261006`。

- 静态核对订阅→初始化、成功事件→稳定 ID 提交、后台激活→UI queue、Closed→退订/Dispose，以及关闭后的队列身份守卫。
- 仅做**最小 stub 隔离编译**：复制 App 两份源码及四份真实 DTO/结果声明，MainWindow、WinUI 和服务实现用不可执行工程内的最小签名 stub 替代。net10.0 library 编译 **0 警告、0 错误**，不运行程序集。
- `compile-glue.log` 为编译结果；`initial-hashes.json` / `final-hashes.json` 验证六份编译输入与当前源码完全一致且编译期间未变；`Stubs.cs` 明示被替代的边界。
- 现有九份通知源码与 `/mnt/c/Users/Zhang/source/repos/FluentCode-notification-interactions-validation-20261006/final-hashes.json` 的已验证版本一致，复用其原生 projection 编译和纯策略测试证据，不重复执行服务测试。该既有证据也没有 Register/Show 运行证据。
- 使用 `/mnt/c/Program Files/dotnet/dotnet.exe`；清空 NuGet 源、无包依赖、CLI/cache/temp/bin/obj 项目外隔离，禁止首次体验/证书生成/遥测。没有构建主工程、调用真实 Initialize/Register/Show/Unregister、启动关闭 GUI、HTTP、git、安装或读写用户数据。

复现编译（隔离目录内）：

```bash
bash ./run-dotnet.sh build Validation.csproj --configuration Release --no-restore --nologo --verbosity minimal
```

关键 SHA-256：

- `src/FluentCode/App.xaml.cs`：`5e32fb2b4bb4206396d69bcf2b09e44279471134e6c7fa496872eafb9a52d614`
- `src/FluentCode/App.Notifications.cs`：`25f0f876fde5c0dd544050a7b2c6a4ad5586c7446733b5b273b14b970ebaf5a5`

## 父会话最终验收项

1. 并行 UI 合并后统一主工程编译，确认 MainWindow 事件声明、sender 和实际转发路径保持契约；stub 编译不是 WinUI 整体编译。
2. 获得实机运行授权后，验收普通非管理员启动注册、Local 明确成功提交通知，取消/错误/截断不产生成功通知，以及重复稳定 ID 的运行期去重。
3. 验收运行期通知点击只激活现有窗口；窗口关闭与排队激活/初始化竞争时无重开窗口、无关闭后通知或未处理异常，正常关闭执行 Unregister。
4. 在受支持/禁用通知/勿扰等环境分别确认聊天不受通知失败影响，单独记录 Submitted 与实际可见性；不要用其中一项代替另一项。
5. 另开冷启动/单实例整合任务，明确入口、注册和激活获取顺序，测试跨进程重定向及未知/过期 nonce 不授权；不能把本阶段运行期接线当作这项已完成。
# Historical notification integration notes

The Local completion branch described below was removed in the FluentCode
OpenCode-only transition on 2026-11-01. Only OpenCode notifications remain;
the original integration/validation notes are retained as history.
