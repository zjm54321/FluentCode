# 品牌图标资源：官方出处、许可证与商标归属

本文件记录 `src/AIFrontend/Assets/Brands/` 下三个 provider 的官方品牌静态资源来源、
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
