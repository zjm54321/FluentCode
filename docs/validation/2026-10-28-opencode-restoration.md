# OpenCode 遗留需求恢复与验证（2026-10-28）

## 本轮完成

- 智能体、模型和推理档位选择立即通过受保护接口写入；发送等待同一个在途操作，成功后读取服务端权威状态。外部事件和重连刷新选择器，真正结果未知的操作继续保留保护。
- 首页会话管理不再套用回退操作专属的空闲/历史限制；已开始的请求可以在离开首页后确认结果，旧界面回调仍受来源与会话归属限制。
- 浏览器恢复单行工具栏、跨左右栏的内容区展开、小预览、彩色选中图标、滚动箭头，以及元素/区域选取和逐项批注。
- 会话网页通过当前来源的网络隧道访问，工作区代理监听器与连接代次分离；断开停止转发。跨域和嵌套 iframe 保留 renderer/session/document 身份，选取、动作与截图复用坐标投影。
- `inspect:true` 提供只读、动作相关的授权目标；执行前重新比较目标，上传授权检查不要求预先传输文件。Lighthouse 使用真实运行组件生成 JSON/HTML 报告。
- 设置支持内嵌 WebView2 或独立窗口 Chrome，仅对新建工作区生效。外部 Chrome 显示真实截图、更新时间及显式刷新/选取入口；下载保留最初来源，不接管其他标签页的下载。
- Markdown 与浏览器预览支持离线 Mermaid 11.12.3；终端展示统一，侧栏整行可点击。恢复各档位图标、选择器间距与切换反馈，移除多余箭头。
- `/editor` 与工具栏使用同一个受归属保护的全页编辑器；会话内容改用原生 ScrollView/AnnotatedScrollBar。
- 右栏从 `oh-my-opencode-slim.session-preset.v1` 读取当前会话绑定预设，分别显示真实名称、名称不可用、未绑定和读取失败；不从全局设置推断会话预设。

## 整体验证

| 检查 | 结果与证据 |
| --- | --- |
| 全套测试入口 | 首次执行 68 个入口（66 个 PowerShell 调用、2 个 Node 测试），57 个通过、11 个失败。修复离线源文件闭包、生产成员提取及过时断言后，11 个失败入口全部定向复验通过；其余有效结果复用。首次执行期间候选源码未变。 |
| 原始测试记录 | `artifacts/restoration-suite-20261028T124055Z/{plan,candidate,results}.json` 与 `logs/`。 |
| 失败项闭环 | `artifacts/restoration-harness-fix-20261028T130224Z/{diagnosis,results}.json` 保存逐项原因、实际日志和复用来源。即时选择、发送等待同一任务、权威重读、编辑器归属和回退/恢复限制均有行为断言。 |
| 原生编译 | 修正 ScrollView 的滚动条枚举值后，真实 XAML 各编译阶段及应用 CoreCompile 通过，0 警告、0 错误；见 `artifacts/restoration-native-fix-20261028T125037514Z/compile.log`。 |
| 标准构建与发布 | 原仓库路径下 Debug/x64 solution Build 和 application Publish 均 exit 0，0 警告、0 错误；见 `artifacts/restoration-packaging-fix-20261028T125941Z/{results.json,build.log,publish.log}`。 |
| 完整运行组件 | 构建和发布均保留 12,989 个文件、236,446,127 字节；最长构建路径 283 字符。关键文件及 36 个深层 JS/source-map 的 SHA-256 一致，发布目录自己的 Node 成功导入 Lighthouse 13.4.1。复制工具另有 4 个通过的专项测试。 |

### 受控真实浏览器检查

- `artifacts/browser-native/run-005/results.json`：真实 WebView2 代理认证、服务端 localhost 路由、零直接来源请求、来源站点 401 不获得代理凭据，以及嵌套 OOPIF 的命名/通配 CDP session 事件。
- `artifacts/browser-native/audit-001/audit-results.json`：生产 Lighthouse 在故意缺少可访问性信息的页面上返回 accessibility **0.52** 及真实报告；URL、历史、Cookie、存储、滚动位置保持不变，取消后代理仍可用。
- `artifacts/browser-frames-handoff/README.md`：42 项真实后端分发断言，以及包含真实 Chrome 嵌套跨域坐标、点击、PNG 裁剪、inspect/binding 的 82 项专项检查。
- `artifacts/browser-download-routing/report.md`：修复 renderer `swap` 被误当作逻辑 frame 删除的问题；同进程、嵌套跨域、UI 队列忙碌、子树移除及无关标签页隔离通过，原始失败下载用例复验通过。
- `artifacts/browser-settings-sol/capture-lifecycle/`：15 项生产截图方法生命周期断言，覆盖切页、导航、迟到失败、解码和解绑。

会话控制、删除、预设和编辑器的本轮依据是生产方法/传输的受控夹具与上述原生编译；浏览器真实检查覆盖受控原生宿主和真实 Chrome。完整桌面会话的人手交互验收仍独立记录。

## 构建与部署

先显式运行 `tools/browser-runtime/prepare.ps1`，再执行标准构建。准备固定 Node 24.22.1、Lighthouse 13.4.1 和 108 个锁定生产依赖；生成目录 `artifacts/browser-runtime` 不提交 Git。

MSBuild 的传统文件复制任务不能处理本轮实际出现的深层依赖路径，因此完整运行组件改由已准备的 Node 在 Build/Publish 后复制。复制前验证输入与目标，只替换生成的 `BrowserRuntime`；不裁剪依赖、不改系统长路径设置、不自动下载。部署须保留整个输出目录，包含 `BrowserRuntime` 和离线 Mermaid 资源。

本轮恢复修复提交后，下一阶段另行对照 opencode-qol 与已修改插件，整合对应的前端协议与界面。
