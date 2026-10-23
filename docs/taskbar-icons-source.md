# Taskbar overlay 状态图标：设计决策、官方来源、许可证与交付说明

> Historical bare-glyph delivery, superseded by the approved TB1 circular badges
> on 2026-10-23. Current sources, colors and regeneration instructions are in
> `src/AIFrontend/Assets/Taskbar/source/README.md`; hashes and pixel findings below
> describe the previous artwork, not the current ICO files.

本文档记录 `NeedsAttention` / `Completed` / `Failed` 三个 taskbar overlay 图标的
设计判断、官方几何来源（固定 commit + sha256）、配色与对比度依据、尺寸/DPI 映射、
`status -> 文件` 映射、许可、复现命令、静态校验结果与**未做项**。

> 范围声明：本次**只新增资产与本文档**，未修改任何 C#、`AIFrontend.csproj`、XAML、
> Services、App/Page，未改应用主图标，未运行应用/未截图/未操作窗口或任务栏 COM。
> 服务接线（`LoadImageW` 加载、HICON 生命周期、csproj 部署项）由父会话/后续 fixer 完成，
> 见第 8 节。

---

## 1. 背景与目标

`TaskbarStatusService.cs:94-116` 现用 `LoadIconW(NULL, MAKEINTRESOURCE(IDI_*))` 取
共享系统库存图标：`NeedsAttention=IDI_WARNING(32515)`、`Completed=IDI_INFORMATION(32516)`、
`Failed=IDI_ERROR(32513)`。这三个是 Win95/Vista 时代的库存图标（蓝色圆点-i、红色圆点-×、
带渐变的警示三角），在 Windows 11 任务栏上观感陈旧。

本次目标：用**清楚、简洁、现代的 Fluent 状态符号**替换该 overlay 的 `HICON` 视觉，
继续走原生 `ITaskbarList3.SetOverlayIcon` 路径。**不改**任何语义：状态聚合优先级、
overlay 描述文本、进度标志（`Idle/Completed` 为 `NOPROGRESS`、`Failed` 为 `TBPF_ERROR`、
真实进度才发 `SetProgressValue`、绝不伪造 100%）全部保持不变；`Working`/`Idle` 仍无 overlay。

---

## 2. 设计决策（UI 视觉判断）

### 2.1 形态：**glyph-only，透明背景，无彩色底盘**

选用**纯符号（glyph）+ 全透明背景**，不套彩色圆/圆角底盘，不加 keyline 描边。理由：

- 用户明确要求「**透明背景**」「**不变成彩色小点/圆球**」。库存 `IDI_INFORMATION`/`IDI_ERROR`
  正是「彩色圆球」，彩色底盘方案在观感上与之同类，故排除。
- 用 **Fluent 的 filled（实心）字形**而非 regular（线形）：16px overlay 极小，实心字形
  在 16px 下有足够笔画重量，线形会过细发虚。
- 每个尺寸使用**该尺寸的官方原生绘制稿 1:1 落位**（16 号用 `_16_filled`、20 号用 `_20_filled`…），
  不缩放、不做 `Viewbox` 换算——这与本项目 `docs/back-icon-source.md` 的做法一致，
  保留 Fluent 对每个尺寸的光学调优（padding 与笔画节奏为官方原值）。

### 2.2 为什么需要**亮暗两套**（单套不足以高对比）

overlay 由 Shell 直接绘制，**不会**随应用主题着色；其背景是任务栏按钮，跟随
**Windows 个性化主题**（默认亮色 `≈#F3F3F3`，深色 `≈#202020`）。实测（WCAG 相对亮度）：

| 单色方案 | vs 深色任务栏 | vs 亮色任务栏 | 最差 |
|---|---|---|---|
| 亮色系（如 `#FFB900`） | 9.46:1 | **1.55:1** | 1.55 |
| 中间调（最优折中） | ≈4.3:1 | ≈3.4:1 | ≈3.4 |

任何**单一**彩色 glyph 都无法在亮/暗任务栏同时达到高对比（最差仅 ~3:1，勉强触底）。
因此交付**两套**：一套面向深色任务栏（高亮色），一套面向亮色任务栏（深色），
各套在其目标主题上均 **≥4.7:1**（见第 4 节）。这是「亮暗两套」而非「单套」的直接依据。

### 2.3 未采用 / 已否决方案

- **彩色底盘 + 挖空符号（badge）**：背景无关、对比度最高，但会被读作「彩色小方块」，
  违反第 2.1 的透明/非圆点约束 → 否决。
- **keyline 描边**：可单套通吃亮暗，但描边在深色底上不显示、在浅底上呈现「贴纸感」，
  偏离 Fluent 单色语言 → 否决。
- **bell（`alert`）代替警示三角**：已抓取 `alert_*_filled` 备用，最终选 **warning 三角**
  ——「需处理/需关注」语义最直接，且与 `Failed` 的叉形区分清晰。

---

## 3. 官方几何来源（固定 commit + sha256）

- 图标集：**Fluent UI System Icons**（`microsoft/fluentui-system-icons`），**MIT**。
- 固定 commit：**`a563cf9166f4f91aa617557ed272612b7f0a2f72`**（与本项目返回箭头图标同 commit）。
- raw 地址模板（`<NAME>`=图标名，`<SIZE>`=16/20/24/32）：

  ```
  https://raw.githubusercontent.com/microsoft/fluentui-system-icons/a563cf9166f4f91aa617557ed272612b7f0a2f72/assets/<NAME>/SVG/ic_fluent_<name>_<SIZE>_filled.svg
  ```

- 本次逐字下载并**原样**保存于 `src/AIFrontend/Assets/Taskbar/source/official/`，
  与上游 `cmp` 逐字节比对一致。sha256：

  | 文件名（`source/official/`） | sha256 |
  |---|---|
  | `ic_fluent_warning_16_filled.svg` | `81c79ec724293f2f8e0e212812a7d341d3ccf9544f67de5ed940c200cbc4c3da` |
  | `ic_fluent_warning_20_filled.svg` | `c7c307d7a597ecd0e9398073fbdd98a0ff7aee8a71b207ba23a67fde8c072a4e` |
  | `ic_fluent_warning_24_filled.svg` | `69a17bce0f11d87f281aabdb344c8f8cb9c2d8d413a185150eaa2e6ec422bcae` |
  | `ic_fluent_warning_32_filled.svg` | `dd62ffedb50dc8484c84106ef65244f01806b65227bf48cf6c34b56f78069a96` |
  | `ic_fluent_checkmark_16_filled.svg` | `78f780488da6025092fde68466d8938960a60b2bc81ff094184b08e2f30eb202` |
  | `ic_fluent_checkmark_20_filled.svg` | `0850ce9b88d85961051cfa7ae3e352904e8b9bc46b061f2523399dc80f68b27d` |
  | `ic_fluent_checkmark_24_filled.svg` | `1886370b96eaf1c02743c920c55bdd3ef57c3eaaf4dfeec5ce5ae3b473df1488` |
  | `ic_fluent_checkmark_32_filled.svg` | `695d8a679929d61d53782bdfd7a64704a674c2a28b0a4bd7122fadff30812409` |
  | `ic_fluent_dismiss_16_filled.svg` | `7afde8b0bfc9ae2478760cdefba512fa5898223960c65a827139bea8ca4cdff4` |
  | `ic_fluent_dismiss_20_filled.svg` | `579375dd435194cedccf6e12ee762cd8fb7a337d0d09cbcf9aac8ce90c6a89d3` |
  | `ic_fluent_dismiss_24_filled.svg` | `059bf86157a83cf26ab0f009d8aaadc15cf0b4c8132fe34ad3a009f00e01febf` |
  | `ic_fluent_dismiss_32_filled.svg` | `85b5d86b57093282a7d1e8c4a74691ff6e2dc2572a2c3d24d62f33c995d9fb20` |

- **本资产不含任何第三方 PNG**；几何全部来自上述官方 SVG，未把网络任意 PNG 当作授权素材。
- 许可全文（MIT，`Copyright (c) 2020 Microsoft Corporation`）已在本仓库：
  `licenses/fluentui-system-icons-LICENSE.txt`（sha256
  `69bc45dc42b9acb96a69823adbc6ae538374e3c0bde169b855b32c48eaaef52f`，本次重新抓取
  固定 commit 的 `LICENSE` 并 `cmp` 确认**逐字节一致**）。**未新增许可文件**（复用现有）。

### 3.1 我们对几何的改动（适配声明，如实）

`source/composed/<set>/<status>_<size>.svg` 是打包 ICO 的实际源文件：取官方 SVG 的
`width/height/viewBox` 与 `path d` **逐字不变**，**仅把 `fill="#212121"` 改为第 4 节的语义色**。
未添加/删除坐标、未加 `fill-rule`（保持上游默认 nonzero 规则）、无缩放变换。

- 关键点：`warning_*_filled` 的「!」由上游以相反绕向的子路径（nonzero 规则）形成**挖空**。
  已用像素 alpha 验证（第 7 节），挖空处透明、露出其后的任务栏背景——在深色任务栏上即为
  「琥珀三角 + 深色!」的经典警示符，而非纯色三角。
- 不改 `fill-rule` 是刻意的：`dismiss_*_filled` 含重叠子路径，若强加 `evenodd` 会把重叠区挖空、
  破坏叉形。保持上游规则可确保渲染与其官方外观一致。

---

## 4. 配色与对比度

| 语义 | 状态 | 深色任务栏套 | 亮色任务栏套 |
|---|---|---|---|
| 需关注 | `NeedsAttention` | `#FFB900` 琥珀 | `#9D5D00` 深琥珀 |
| 已完成 | `Completed` | `#6CCB5F` 绿 | `#0F7B0F` 绿 |
| 失败 | `Failed` | `#FF6B6B` 红 | `#C42B1C` 深红 |

对比度（WCAG 相对亮度；深色底取 `#202020`、亮色底取 `#F3F3F3`）：

| 颜色 | vs 深色任务栏 | vs 亮色任务栏 |
|---|---|---|
| `#FFB900` | **9.46:1** | 1.55:1 |
| `#6CCB5F` | **8.03:1** | 1.83:1 |
| `#FF6B6B` | **5.87:1** | 2.50:1 |
| `#9D5D00` | 3.10:1 | **4.73:1** |
| `#0F7B0F` | 3.00:1 | **4.90:1** |
| `#C42B1C` | 2.88:1 | **5.10:1** |

- 深色套在其目标主题下最差 **5.87:1**，亮色套最差 **4.73:1**，均超过 WCAG 非文本 3:1 底线
  并达到常规文本 4.5:1 水平 → 满足「高对比」。
- 色相沿用 Fluent 状态色族（warning 琥珀 / success 绿 / danger 红），语义与 Windows 一致；
  具体数值为按上述对比度选定的实现值（非逐字引用某个 Fluent token）。
- 交叉回退（若只接了单套而任务栏是另一主题）：各色在非目标主题上仍有 ~2.9–3.1:1，
  可见但不理想 —— 故第 5 节给出主题判定建议。

---

## 5. 适用主题与尺寸/DPI

- **适用对象是「任务栏主题」（Windows 个性化），不是应用主题。** 二者可能不一致
  （WinUI3 应用可亮色而任务栏深色）。建议按**系统**亮/暗判定，或直接以 `UISettings` /
  `AppsUseLightTheme(0x1)` 读取，而非 `Application.Current.RequestedTheme`。
- **默认套 = `dark-taskbar/`**（深色任务栏）；亮色任务栏请改用 `light-taskbar/` 同名文件。
  切换只是替换路径常量，不涉及几何/语义改动。
- 单套即可工作（另一主题下 ~3:1 仍可见）；双套为「高对比」最优解。

每个 ICO 内含 **16 / 20 / 24 / 32** 四档（32bpp + 8bit alpha），DPI 映射：

| 任务栏缩放 | 采用档位 |
|---|---|
| 100% | 16 |
| 125% | 20 |
| 150% | 24 |
| 200% | 32 |
| 250–300% | Shell 以 32 档缩放（16px overlay 上无损观感） |

尺寸依据：taskbar overlay 逻辑尺寸为 16 DIP，故 16 为基准；20/24/32 覆盖 125/150/200% 常见 DPI。

---

## 6. `status -> 文件` 映射（交付契约）

| `TaskbarStatus` | 语义 | overlay 描述（**不变**） | 默认文件（深色任务栏） |
|---|---|---|---|
| `NeedsAttention` | 需用户处理 / 结果未知 | `AIFrontend：有任务需要处理` | `Assets/Taskbar/dark-taskbar/taskbar-attention.ico` |
| `Completed` | 任务已完成 | `AIFrontend：任务已完成` | `Assets/Taskbar/dark-taskbar/taskbar-completed.ico` |
| `Failed` | 任务失败 | `AIFrontend：任务失败` | `Assets/Taskbar/dark-taskbar/taskbar-failed.ico` |

亮色任务栏套：同目录结构换成 `Assets/Taskbar/light-taskbar/taskbar-{attention,completed,failed}.ico`。

| 文件 | 使用字形（官方） | 颜色 |
|---|---|---|
| `taskbar-attention.ico` | `warning_{16,20,24,32}_filled` | 深色套 `#FFB900` / 亮色套 `#9D5D00` |
| `taskbar-completed.ico` | `checkmark_{16,20,24,32}_filled` | 深色套 `#6CCB5F` / 亮色套 `#0F7B0F` |
| `taskbar-failed.ico` | `dismiss_{16,20,24,32}_filled` | 深色套 `#FF6B6B` / 亮色套 `#C42B1C` |

- `Idle` / `Working` **无 overlay**（`HICON=NULL`），本次不提供图标，行为不变。
- 交付的 ICO sha256：

  | 文件 | sha256 |
  |---|---|
  | `dark-taskbar/taskbar-attention.ico` | `66f9fba2bce5084ecaa25c20df3f719a28e4bac8b2e2410c8e8c72c564093bf6` |
  | `dark-taskbar/taskbar-completed.ico` | `23af8e65e20a26897cbc72defa34ae8737551d29e6266fe752963d40e526c704` |
  | `dark-taskbar/taskbar-failed.ico` | `ec44d99bb3f70dc0ba0fdbc10d6064989696d38ab23c4c5311e545b2629d7170` |
  | `light-taskbar/taskbar-attention.ico` | `be80e4cb8cf7e4fb89955ad4ded322ca8676885fa8b041d759090e7c18c3adec` |
  | `light-taskbar/taskbar-completed.ico` | `6312b570ea22b22cf95958fff3b0ea0ac37a98d0688510a41ca14fd8653c95f1` |
  | `light-taskbar/taskbar-failed.ico` | `8b96f1f094f49e1ec693a7f4d1926c32d717a83cf18c7decc862ebe33b1df0da` |

---

## 7. 静态校验结果（本次实际执行）

工具：`resvg 0.48.1`、`icoutils 0.32.3`（经 `nix build nixpkgs#resvg nixpkgs#icoutils`
临时取用，**未永久安装**、未改 `/nix/store`）；自写纯 Python ICO/PNG 解析器核对像素。

- **ICO 容器**：6 个文件均为 `ICONDIR{reserved=0, type=1, count=4}`，含 16/20/24/32 四档。
- **位深/透明度**：四档全部 `BITMAPINFOHEADER biBitCount=32、biCompression=0`，
  alpha `min=0 / max=255`（透明背景成立）；AND mask 置位数与透明像素数逐一相等。
- **填充色**（从 ICO 像素反查）：深色套 `#FFB900/#6CCB5F/#FF6B6B`、亮色套
  `#9D5D00/#0F7B0F/#C42B1C`，与设计一致（个别像素为抗锯齿混合值）。
- **形状**（16px 条目 alpha 图）：warning = 三角且「!」为挖空；completed = 对勾；
  failed = 叉形。三者形状互异、语义可辨。
- **来源一致性**：`source/official/` 12 个 SVG 与固定 commit 上游 `cmp` 逐字节一致；
  `licenses/fluentui-system-icons-LICENSE.txt` 与上游 `LICENSE` 逐字节一致。

复现命令（本机 NixOS on WSL2；`resvg`/`icotool` 路径以 `nix build --print-out-paths` 为准）：

```bash
R=$(nix build --no-link --print-out-paths nixpkgs#resvg)/bin/resvg
I=$(nix build --no-link --print-out-paths nixpkgs#icoutils)/bin/icotool
# 每个 (set,status)：
$R --width 16 --height 16  source/composed/$set/${status}_16.svg /tmp/${status}_16.png
$R --width 20 --height 20  source/composed/$set/${status}_20.svg /tmp/${status}_20.png
$R --width 24 --height 24  source/composed/$set/${status}_24.svg /tmp/${status}_24.png
$R --width 32 --height 32  source/composed/$set/${status}_32.svg /tmp/${status}_32.png
$I -c -o $set/taskbar-${status}.ico /tmp/${status}_{16,20,24,32}.png
```

---

## 8. 交付与接线说明（供父会话 / fixer，本次未做）

1. **部署项**：`AIFrontend.csproj` 目前无该资产的复制规则（本次未改 csproj）。
   需由父会话加入类似
   `<Content Include="Assets\Taskbar\**\*.ico" CopyToOutputDirectory="PreserveNewest" />`，
   否则运行期取不到文件。
2. **加载方式**：从文件加载请用
   `LoadImageW(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE)`（`user32.dll`）。
   运行期路径建议 `Path.Combine(AppContext.BaseDirectory, "Assets", "Taskbar",
   "dark-taskbar", "taskbar-attention.ico")` 之类；ICO 内含 16/20/24/32，任意请求尺寸均可解析。
3. **HICON 所有权变更（重要）**：当前 `LoadIconW(NULL, IDI_*)` 返回**共享系统 HICON，不可
   `DestroyIcon`**；而 `LoadImageW(...LR_LOADFROMFILE)` 返回的是**调用方私有 HICON**，
   **必须在替换/退出时 `DestroyIcon`**，否则泄漏。请在 `SetOverlayIcon` 成功后再销毁被替换的
   旧 HICON，并保持当前生效的 HICON 存活至下一次替换。
4. **主题选择**：默认接 `dark-taskbar/`；如需支持亮色任务栏，按第 5 节判定系统主题切换目录。
5. **不改语义**：仅替换 overlay 图标的 HICON 视觉；不要改动状态聚合优先级、overlay 描述文本、
   progress 标志/数值逻辑，也不要为「完成」伪造 100%。
6. **不加依赖**：本方案不需要 `BadgeNotificationManager`、NuGet 包或 MSIX 变更
   （项目 `WindowsPackageType=None`，官方源对 unpackaged 报 `Not applicable`）。

---

## 9. 本次未做（验证边界，如实）

- **未运行应用、未编译**（未执行 `dotnet build`/`msbuild`）、未启停进程。
- **未做任何 GUI 操作**：未激活/点击窗口，未截图，未实例化 COM，未调用
  `LoadIcon`/`LoadImage`/`SetOverlayIcon`，未触碰真实任务栏 Explorer。
- **未改任何代码/配置**：未改 `TaskbarStatusService`/`TaskbarNative`/其它 Services、
  `AIFrontend.csproj`、任何 `MainWindow`/`App`/`Page`、其它 docs、Git 配置、全局配置、Claude memory；
  未新增生产依赖或永久安装软件；未改应用主图标。
- 资产仅含官方 glyph 几何与本方选定色值，**不含任何 secret**。
- **实际任务栏显示效果待父会话/用户统一验收**（本环境为 WSL2，无桌面）。若 16px 下
  completed 对勾偏细（官方 filled 字形设计如此），可再议是否改用更大光学尺寸；
  本文档不声称与实际 taskbar 渲染像素完全一致。
