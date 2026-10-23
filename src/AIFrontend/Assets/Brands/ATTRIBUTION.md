# 品牌图标资源：官方出处、许可证与商标归属

本文件记录 `src/AIFrontend/Assets/Brands/` 下八个模型家族的官方品牌静态资源来源、
抓取时间与 SHA-256，并声明商标归属。

> 抓取时间：2026-10-06（本轮）。抓取方式：对下列官方 HTTPS 地址做静态 GET，仅保存响应字节，
> **不执行**下载内容。**未**使用第三方聚合站素材、**未**自绘、**未**以企业 logo 冒用产品图标。

## 1. ChatGPT

官方原文（逐字节保存，仅作溯源）：

- 文件：`chatgpt.official.svg`
- 官方来源 URL：`https://chatgpt.com/cdn/assets/favicon-l4nq08hd.svg`
- SHA-256：`8f2de046d22cfecfe3917cf87ff1ce49a48d79956f6b18be3b37db29148a0aeb`
- 形态：SVG（180×180 viewBox，根 `fill="none"`；填充色**仅**由内嵌 `<style>`（含
  `@media (prefers-color-scheme: dark)`）定义）

交付渲染（几何逐字、仅把填充由 CSS 改为显式 `fill` 表现属性；**未改绘**几何、**未**重绘品牌）：

- 文件：`chatgpt.svg`（浅色）/ `chatgpt.dark.svg`（深色）
- SHA-256：`chatgpt.svg` = `50df5036e3c15ceabae9384b18844cd99d6e5978d5b9e8b922e2e1f0c8e1d962`；
  `chatgpt.dark.svg` = `ad9bbac7ddc4ac23a115b23d2f15110a2f6a5a833a9122b283201d17f6ae6bde`
- 差异：仅 `<style>` 元素（CSS）替换为 path 上的 `fill="#000000"`（浅）/ `fill="#FFFFFF"`（深）；
  `d` 与官方原文逐字一致（本会话以脚本提取并比对 `d1==d2==d3` 通过）。
- 商标归属：ChatGPT / OpenAI 标识为 OpenAI 的商标与品牌资产。

**为何需要此适配（不假称浏览器 CSS 有效，关键）**：WinUI `SvgImageSource` 底层为 Direct2D SVG 渲染
（Microsoft Learn「SVG Support」：其“支持元素/属性”表中**没有 `<style>` 元素**，仅支持 `fill`、`fill-rule`
等表现属性）。因此该官方 SVG 的内嵌 CSS 与 `@media` 媒体查询**不保证**被执行——若原样加载，`path`
将继承根元素的 `fill="none"`，很可能**渲染为空白**。故本仓库不依赖 CSS：改用几何逐字、显式 `fill`
的浅/深两版，并按控件 `ActualTheme` 选择。此为**配色适配**（与 `docs/back-icon-source.md` 的既定先例
同类），**不构成**“CSS 媒体查询在 SvgImageSource 中有效”的声明；几何本身未改绘、未自绘。

## 2. Gemini

- 文件：`gemini.png`
- 官方来源 URL：`https://www.gstatic.com/lamda/images/gemini_sparkle_4g_512_lt_f94943af3be039176192d.png`
- SHA-256：`5e7cfecaa53f4f65a313fe89b0f389548126544a78fad8489510c70ae641a4a1`
- 格式：PNG（512×512 RGBA，光栅，无 CSS 依赖；浅/深主题下同一文件）
- 商标归属：Gemini / Google 标识为 Google LLC 的商标与品牌资产。

## 3. Claude

- 文件：`claude.svg`
- 官方来源 URL：`https://claude.ai/favicon.svg`（claude.ai 官方公开 icon metadata）
- SHA-256：`b150888bc7257af83e3b85d3c2be4294f88986026f8168f6c12fc1fde6697350`
- 格式：SVG（248×248 viewBox，路径显式 `fill="#D97757"`，无 CSS 依赖；浅/深主题下同一文件）
- 商标归属：Claude / Anthropic 标识为 Anthropic PBC 的商标与品牌资产。
- 说明：该资源由 Claude 官方产品域 `claude.ai` 直接提供；**不**采用 Anthropic 企业 logo、
  **不**自绘、**不**使用第三方聚合站素材。

## 4. 工程接入（报告，非本文件职责）

- 这些资源需由 `AIFrontend.csproj` 以 `Content` 复制到输出目录，使其出现在
  `AppContext.BaseDirectory/Assets/Brands/`（`WindowsPackageType=None` 下非 `ms-appx`）。
  例如：`<Content Include="Assets\Brands\**" CopyToOutputDirectory="PreserveNewest" />`。
- `chatgpt.official.svg` 仅供溯源，若父会话希望减小输出体积可将其排除在 `Content` 之外（或
  一并复制，体积约 4 KB，无功能影响）。
- **本 lane 不修改 `*.csproj`**；该复制需求仅报告给父会话统一处理。
- 代码接入点：`src/AIFrontend/Pages/Web/ProviderBrandIcons.cs`（16 DIP；SVG/PNG 自适应；
  ChatGPT 按 `ActualTheme` 选浅/深变体）。

## 5. OpenCode 模型图标（2026-10-23 更新）

- `src/AIFrontend/Pages/OpenCode/OpenCodeModelLogo.cs` 按模型 ID / 显示名识别八个家族，包括网关后的家族名；不把某 provider 下所有未知模型都当成其品牌。
- 所有图像按 20 DIP 排版，无背景底片。SVG 保持官方几何，仅提取独立图标及显式颜色以兼容 WinUI `SvgImageSource`；PNG 保持官方原始像素。
- 单色标志按 `ActualTheme` 使用浅/深版本；未知模型及加载失败共用原生 Fluent Robot 字形（`SymbolThemeFontFamily`，U+E99A），前景随主题继承。
- 资源由 `AIFrontend.csproj` 同时复制到 build 和 publish 输出；所有商标归属仍如上。

以下新增资源均于 **2026-10-23** 用 `curl -L --fail` 从官方站点静态下载；内嵌 SVG / PNG 仅从响应中提取，不执行网页脚本。不采用第三方聚合站或重绘图标。

## 6. Grok / xAI

- 官方来源：`https://grok.com/images/favicon.svg`（由 `https://grok.com` 的 icon link 指向）。
- `grok.official.svg` 保留完整官方响应，SHA-256：`c3db0dfaf760b702b8490c6cbefe07fd8bfe00db43cae6a0acccf768f44d6179`。
- `grok.svg` / `grok.dark.svg`：提取官方两条主图标 path，`d` 逐字不变；去掉背景圆角底片、滤镜、HTML foreignObject 和依赖 mask 的内描边叠层；viewBox 收至 `56 56 400 400`。仅将填色设为黑 / 白。
- 商标归属：Grok / xAI 标识属于 xAI。

## 7. DeepSeek

- 官方来源：`https://www.deepseek.com/` 页首品牌链接内第一枚 inline SVG。
- `deepseek.official.svg` 保留该完整 SVG（含字标），SHA-256：`1a1d76b9af2773338c981b63fa930b1e04acda91cb03bc971e6e85cae3b1099b`。
- `deepseek.svg` / `deepseek.dark.svg`：只提取鲸鱼图标所在 `<g>` 及其 clipPath，不取旁边字标，所有路径 `d` 逐字不变。viewBox 收至官方 clip rect 的 `0.163086 1.75 26.634 19.6`。
- 官方样式变量 `--ds-color-brand-medium-reverse` 浅色为 `#4176e6`，深色为 `#fff`；渲染文件把 `currentColor` 替换为这些显式填色，无 CSS 依赖。
- 商标归属：DeepSeek 标识属于杭州深度求索人工智能基础技术研究有限公司。

## 8. GLM / 智谱 Z.ai

- 官方来源：`https://z-cdn.chatglm.cn/z-ai/static/logo.svg`（由 `https://z.ai` 的 icon link 指向）。
- `glm.official.svg` 保留完整官方响应，SHA-256：`07a45e8e35b0b631ed2c68cd1cb041f9721b1ceeb0bd0e34f1459b0304a741c7`。
- `glm.svg` / `glm.dark.svg`：提取官方 `st23` 的两条 path 和一条 polygon，`d` / `points` 逐字不变；去掉背景底片和未使用 CSS。viewBox 收至 `5.7 7.09 18.6 15.82`，显式黑 / 白填色。
- 商标归属：GLM / Z.ai / 智谱标识属于智谱。

## 9. Kimi / Moonshot

- 官方入口：`https://www.kimi.com/`，其官方样式表 `https://statics.moonshot.cn/kimi-web-seo/assets/BXFB7fl8.css`。
- 资源为该 CSS 中 `.sidebar-header[data-v-65e8e23f]` 的 `--sidebar-logo` 与 `:root.dark .sidebar-header[data-v-65e8e23f]` 的同名变量，均为官方内嵌 `data:image/png;base64,...`；只做 Base64 解码，PNG 字节未修改。
- `kimi.png`：112×112 RGBA，浅色主题黑色 K / 蓝点；SHA-256：`4ba96a6bd822227312f89ef65c383a5c77b699c18f36108926b55c5777369ae8`。
- `kimi.dark.png`：112×112 RGBA，深色主题白色 K / 蓝点；SHA-256：`ed7d02590057505b3e10b548ec542bdd23aa65c33698a62d7b8d462ae000e77a`。
- 两张均透明背景，无底片；原生官方浅/深色文件直接使用，不自行调色或重绘。
- 商标归属：Kimi / Moonshot 标识属于月之暗面。

## 10. Qwen / 通义千问

- 官方来源：`https://img.alicdn.com/imgextra/i4/O1CN01OXv3EM1FN8t9W4P79_!!6000000000474-2-tps-80-80.png`（由 `https://qwen.ai` 的 icon link 指向）。
- `qwen.png`：80×80 RGBA，透明背景，浅/深主题同一官方原始 PNG；SHA-256：`cd9390bc4209c319121d111f3a1f535b3c7dd909b8f860d41acc9744138d58df`。
- 商标归属：Qwen / 通义千问标识属于阿里巴巴。
