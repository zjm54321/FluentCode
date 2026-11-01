# 安全内容渲染契约

## 公开入口

```csharp
namespace FluentCode.Services.Rendering;

public sealed class SafeContentRenderer
{
    public string RenderMarkdown(string source);
    public string SanitizeHtml(string source);
}
```

使用默认无参构造。两方法仅返回 **HTML fragment**，不包装 document、不插入 CSS、不创建 WebView、不联网。

```csharp
var renderer = new SafeContentRenderer();
string fragment = renderer.RenderMarkdown(markdownText);
string cleaned = renderer.SanitizeHtml(untrustedHtml);
```

## 清洗策略

- `Markdig` **1.4.0**：共享只读管线 `MarkdownPipelineBuilder.DisableHtml().UsePipeTables().Build()`；支持基本 Markdown、围栏代码和管道表格。原始 HTML 不执行，作为文字处理。
- Markdown 先 `Markdown.ToHtml`，结果与直接 HTML 走相同 `Ganss.Xss.HtmlSanitizer` 清洗。
- `HtmlSanitizer` **9.2.1039**：每次调用创建独立实例，不向调用方暴露可变策略；无自创 HTML parser、正则“清洗”或手工字符串替换。
- 唯一允许标签：
  `p div span br hr h1 h2 h3 h4 h5 h6 ul ol li dl dt dd blockquote pre code table caption thead tbody tfoot tr th td a em strong b i u s del sub sup`。
- **所有属性均移除**，包括 href/src/srcset/ping/action/formaction/srcdoc/xlink:href、事件处理器、style/class/id、data-*。同时清空允许 schemes 与 CSS 属性，不依赖 URI scheme 归一化判断。
- `script/style/form/iframe/object/svg/math/meta/base/img` 及媒体、资源、控件等其他标签不在白名单内；不能产生脚本、样式、外部资源请求或可点击目标。
- 保留 `<a>` 的文字，但不保留 href，当前版本不提供点击链接功能；Markdown 图片也被移除。
- `KeepChildNodes=true`：剥离非白名单容器而保留其普通文本/安全后代，避免一个恶意 wrapper 删除整段回答。脚本/CSS 等被剥离标签的内容可能作为**惰性可见文字**留下，不承诺隐藏这些文字；不是可执行标签。畸形 HTML 仍由库按 HTML 解析规则恢复，不承诺源文本逐字符保真。

## 输入与限制

- null：抛 `ArgumentNullException(nameof(source))`。
- 空字符串：返回空字符串。
- 单次输入上限 **262144 个 UTF-16 code units**；超过抛 `ArgumentException`，不静默截断。
- Markdown 生成 HTML 上限 **2097152 个 UTF-16 code units**；超过抛 `InvalidOperationException`，不返回部分结果。此检查发生于转换后，不是分配前硬内存限制。
- 两方法同步，不包含取消/超时 API；长度上限不能证明任意恶意嵌套输入都具有固定 CPU/内存成本。
- 输出只有无属性 fragment，表格对齐、语言 class、列表 start 等属性有意丢弃。本服务不负责视觉样式或布局。

## 必需的宿主安全边界

**清洗库不是全部安全边界。** 后续用于显示模型文本的隔离 WebView 必须另外做到：

1. 在加载不可信内容前禁用脚本（WebView2 的 `CoreWebView2.Settings.IsScriptEnabled=false`），不能为渲染片段重新启用。
2. 宿主可信 document/CSP 设置至少 `default-src 'none'`；建议同时明确 `script-src 'none'; style-src 'none'; img-src 'none'; media-src 'none'; font-src 'none'; connect-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'`。本服务不生成 document，也不替宿主插入 CSP。
3. 拦截导航和新窗口；仅允许宿主自身的初始可信加载，不允许片段触发外部/文件/自定义 scheme 导航。新窗口请求应标记已处理而不打开。
4. 在任何内容加载前注册全资源请求拦截，拒绝不可信网络与文件资源请求；不能仅靠脚本关闭或 CSP 代替导航/资源拦截。
5. 不把未清洗原文拼进可信 wrapper 的其他位置（例如脚本、style、属性、标题），不启用片段调用宿主的桥接权限。

这些约束仅用于**模型回答/用户 HTML 的离线渲染宿主**；OpenCode 原生富文本呈现的接入见 `opencode-rich-content-contract.md`。

## 包与工程边界

主 `FluentCode.csproj` 仅新增 Markdig 1.4.0、HtmlSanitizer 9.2.1039；保留 WindowsAppSDK 1.8.260921001、BuildTools 10.0.26100.9169、ProtectedData 10.0.12 及 `Pages\**\*.xaml`。

依赖版本以主项目与 `packages.lock.json` 为准；当前自动化入口见 [关键测试清单](test-suite.md)。
