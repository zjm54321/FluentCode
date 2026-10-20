# Local 会话存储与安全渲染离线验证（2026-10-06）

## 结论与范围

- 依 `docs/implementation-contracts.md:296-315,504` 和 `docs/rendering-contract.md` 核对；**没有复现需要修复的生产代码缺陷，生产源码未修改**。仓库内仅新增本文档。
- 三类核心行为：会话 ID/消息快照与切换所需数据、失败/取消保留有效旧文件、Markdown/HTML 输出策略。前两类补齐旧证据缺口；渲染复用源码一致的既有结果，不重复运行。
- 没有读写真实用户会话或凭据；每个测试显式注入项目外合成数据根目录。没有构建主工程、运行 GUI/WebView、真实 API、通知、git、软件安装或其他进程控制。

## 既有证据复用

### 会话已有基础往返与损坏保护

目录：`/mnt/c/Users/Zhang/source/repos/AIFrontend-core-validation-20261006T145839Z`。

- 只读核对 `Program.cs:69-96`、`persistence-tests.utf8.log`、`fixed-post-validation-hashes.json`：原多轮消息/工具/思考/模型持久化、List/Load/Delete、损坏文件拒绝覆盖/删除已有通过结果。
- 本次使用的 9 份 Models 和 5 份存储相关 Configuration 文件，逐文件与该旧快照及记录 SHA-256 一致。没有重新运行原 Settings/DPAPI/OpenCode 测试，也未触碰其合成或真实凭据。

### Markdown/HTML 输出策略

目录：`/mnt/c/Users/Zhang/source/repos/AIFrontend-render-validation-20261006T153633Z`。

- 当前 `src/AIFrontend/Services/Rendering/SafeContentRenderer.cs` 与该目录源码快照及 `final-hashes.json` 完全一致；只读核对 `Program.cs`、`test-results.json`，复用原三组 **198 项、0 失败**的证据，不新跑渲染矩阵。
- 原证据实际覆盖标题/强调/列表/引用/围栏代码/表格，普通 HTML 结构与文本保留，Markdown 原始 HTML 惰性文字、javascript 链接、直接 HTML script/事件属性/实体编码 javascript URL/资源标签移除，以及输出 DOM 的标签白名单和**零属性**。
- 继续使用 Markdig **1.4.0**、HtmlSanitizer **9.2.1039**，未改依赖版本或净化策略。服务只返回 fragment；所有属性（包括 href）移除，保留链接文字而不提供链接跳转。非允许容器的子文字可以留下，不将这些可见文字误判为可执行脚本。
- 原验证使用 AngleSharp 解析输出 DOM，并非浏览器执行测试；不据此宣称 WebView 宿主安全已验收。

## 本次补充的两组存储验证

隔离目录：`/mnt/c/Users/Zhang/source/repos/AIFrontend-local-storage-validation-20261006`。

1. **identity_switch_snapshot_and_filename_guard：通过。** 两个 CreateNew 产生不同稳定 ID；保存后由新 store 实例按 A→B→A 加载，比较全部序列化字段（包括 Unicode/换行/Markdown 原文、消息顺序、思考及模型）。验证默认时间补齐、List 排序、等待目录锁前已分离调用方消息集合、更新 A 不影响 B。另用合成 filename/Id 不一致文件确认 Load/List 报错且原始字节保留。
2. **valid_file_survives_failure_and_cancellation：通过。** 对既有有效文件分别执行非法消息 Save、预取消 Save、等待共享目录锁时取消，以及读取旧文件成功但替换提交失败。每次均比较旧文件字节不变、新 store 仍可读原会话、无遗留临时文件；故障解除后可再次成功保存。

提交失败由 Windows `FileShare.Read` 打开合成目标文件以允许旧文件校验读取、拒绝替换实现；实际栈定位到 `src/AIFrontend/Services/Configuration/AtomicFile.cs:46` 的 File.Move，再到 `src/AIFrontend/Services/Configuration/LocalSessionStore.cs:125`，不是在初始读取阶段就失败。

### 测试过程中的修正（仅 harness）

初次运行第一组已通过。第二组最初只接受 IOException，实际 Windows 抛 `System.UnauthorizedAccessException: Access to the path is denied.`；这是测试对文件系统异常类别的假设错误，不是存储契约违例。只修改项目外 harness 接受这两种文件系统异常，并要求栈确实经过 AtomicFile 写入，再补验旧文件及清理结果。之后**仅重跑第二组**，第一组沿用原通过证据；没有改生产源码来迎合测试。

## 证据与复现

上述本次隔离目录中：

- `initial-hashes.json`：14 份存储依赖与旧证据相同、渲染快照相同。
- `initial-test-run.log` / `initial-test-results.json`：第一组通过及第二组初始 harness 假设失败的原始记录。
- `failure-test-run.log` / `test-results.json`：修正 harness 后第二组通过。
- `commit-failure.txt`：预期替换失败异常和原始源码栈。
- `build.log` / `final-build.log`：隔离编译均 **0 警告、0 错误**。
- `final-hashes.json`：14 份源码与实际测试副本相同且测试期间未变；渲染源码仍与原测试副本相同。
- `validation-summary.json`：组合两组存储有效结果及复用渲染结果，明确生产代码零改动。
- `Program.cs`、`Validation.csproj`、`NuGet.Config`、`run-dotnet.sh`：可复现输入。无包依赖、清空包源、无主工程引用；SDK/cache/temp/bin/obj 均隔离。

工具为 `/mnt/c/Program Files/dotnet/dotnet.exe`，SDK **10.0.401** / runtime **.NET 10.0.12**。脚本向 Windows 显式传递禁止首次体验、证书生成和遥测的环境设置。

在本次隔离目录完整复现两组补充测试：

```bash
bash ./run-dotnet.sh build Validation.csproj --configuration Release --no-restore --nologo --verbosity minimal
bash ./run-dotnet.sh run --project Validation.csproj --configuration Release --no-build --no-restore
```

本次实际修正后运行只加 `-- --failure-only` 重跑第二组；没有把组合历史证据描述为一次全量重跑。

当前关键源码 SHA-256：

- `src/AIFrontend/Services/Configuration/LocalSessionStore.cs`：`6ca51ace7dc4d370263dd25b76be357464f8ec76756e150fa0b91c65eef5a520`
- `src/AIFrontend/Services/Rendering/SafeContentRenderer.cs`：`50dc5581d4c09d69bc96a59bd7bd69d567a7060553e40d75afac7f4f8b59052c`

## 边界与主应用后续验收

- 未模拟磁盘耗尽、断电/进程终止、跨进程写竞争或写入中精确取消时序；本次取消实测为预取消和锁等待中取消。源码在 File.Move 前最后检查取消，提交后取消不撤销写入，与既有契约一致，不承诺所有取消竞态下事务回滚。
- 同目录原子替换依赖文件系统；本次仅证明测试所用 Windows 文件系统上的受控提交失败保护，不推广为断电持久性保证。
- UI owner 仍需验收真实界面会话创建/切换/卸载重载、模型引用失效提示、保存错误显示，以及已保存正文/思考的渲染接线；服务级 A/B/A 读取不等于 UI 切换已实测。
- 不可信片段宿主还必须在加载前禁脚本、设置契约 CSP、拦截导航/新窗口及全部资源请求，不拼入未净化原文、不启用宿主桥接。本 lane 未运行或验收这些 WebView 机制；它们不应被应用到正常运行的官方网站 WebView。
- 没有发现必须越界修改共享接口或 UI 的问题，也没有新增 UI 接线要求；以上是既有契约仍需主应用环境完成的验收项。
