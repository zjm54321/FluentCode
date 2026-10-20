# 返回按钮图标：官方资源出处、许可证与几何（16 号原生方案）

本文档记录 `MainWindow.xaml` 中 `BackButton` 返回箭头图标的官方来源、许可证、
几何落位方式与构建项结论。**本文档不构成“与 Microsoft Store 或原字体图标视觉精确一致”的声明**：
主题着色为适配改动，最终视觉一致性待用户验收（见第 6、7 节）。

> 变更记录：2026-10-06，将上一版 `arrow_left_20_regular`（本机 vendor 副本 + `Viewbox` 0.8
> 等比缩放）替换为上游官方仓库固定 commit 的 `arrow_left_16_regular` 原生几何 1:1 落位；
> LICENSE 全文同步落盘至 `licenses/fluentui-system-icons-LICENSE.txt`。

## 1. 采用的资源

- 图标：**arrow_left_16_regular**（Fluent UI System Icons，regular 线型，16px 原生绘制稿）
- 固定 commit：**a563cf9166f4f91aa617557ed272612b7f0a2f72**
- 官方文件（固定 commit raw）：

  ```
  https://raw.githubusercontent.com/microsoft/fluentui-system-icons/a563cf9166f4f91aa617557ed272612b7f0a2f72/assets/Arrow%20Left/SVG/ic_fluent_arrow_left_16_regular.svg
  ```

- 本次会话从上述固定 commit 重新抓取的官方 SVG 原文（逐字，未改一字）：

  ```xml
  <svg width="16" height="16" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg">
  <path d="M13.5 8.5C13.7761 8.5 14 8.27614 14 8C14 7.72386 13.7761 7.5 13.5 7.5L3.80298 7.5L7.83448 3.87165C8.03974 3.68692 8.05638 3.37078 7.87165 3.16552C7.68692 2.96027 7.37077 2.94363 7.16552 3.12836L2.16552 7.62835C2.06016 7.72317 2 7.85826 2 8C2 8.14174 2.06016 8.27683 2.16552 8.37165L7.16552 12.8716C7.37077 13.0564 7.68692 13.0397 7.87165 12.8345C8.05638 12.6292 8.03974 12.3131 7.83448 12.1283L3.80298 8.5L13.5 8.5Z" fill="#212121"/>
  </svg>
  ```

- 取用路径 `d`（原样搬入 XAML，未编辑坐标）：

  ```
  M13.5 8.5C13.7761 8.5 14 8.27614 14 8C14 7.72386 13.7761 7.5 13.5 7.5L3.80298 7.5L7.83448 3.87165C8.03974 3.68692 8.05638 3.37078 7.87165 3.16552C7.68692 2.96027 7.37077 2.94363 7.16552 3.12836L2.16552 7.62835C2.06016 7.72317 2 7.85826 2 8C2 8.14174 2.06016 8.27683 2.16552 8.37165L7.16552 12.8716C7.37077 13.0564 7.68692 13.0397 7.87165 12.8345C8.05638 12.6292 8.03974 12.3131 7.83448 12.1283L3.80298 8.5L13.5 8.5Z
  ```

- 静态校验值：上述 path 字符串 sha256 =
  `2a3be55136fccfe77185c3e63343c681a0c6b6624d6ed4a434ad7ecafcc778a6`，
  与 `MainWindow.xaml` 内嵌 `Data` 值一致（本会话逐字比对通过）。

## 2. 许可证

- 许可证：**MIT License**，**Copyright (c) 2020 Microsoft Corporation**
- 官方 LICENSE（固定 commit raw）：

  ```
  https://raw.githubusercontent.com/microsoft/fluentui-system-icons/a563cf9166f4f91aa617557ed272612b7f0a2f72/LICENSE
  ```

- 完整文本已落盘于 `licenses/fluentui-system-icons-LICENSE.txt`：
  本次以 curl 从上述固定 commit 抓取，并以“二次抓取 + 逐字节比对（cmp）”确认与官方原文一致
  （sha256 `69bc45dc42b9acb96a69823adbc6ae538374e3c0bde169b855b32c48eaaef52f`）。
  上一版文档中“本环境未能逐字抓取 LICENSE 全文”的限制已解除。
- 部署/打包阶段是否需要将该许可文件复制到输出目录等构建项，**由父会话处理**；
  本次未修改 `AIFrontend.csproj`。

## 3. 几何落位（16 画板 → 16 图标位，1:1，无缩放）

XAML 结构（`BackButton` 的内容）：

```xml
<Canvas Width="16" Height="16">
    <Path Data="…官方 path d…"
          Fill="{ThemeResource TextFillColorPrimaryBrush}" />
</Canvas>
```

- **画板 1:1**：`Canvas` 固定为官方 `viewBox` 尺寸 16×16；`Path` 默认 `Stretch="None"`，
  几何按官方原始坐标落位；无 `Viewbox`、无任何缩放变换，缩放系数 = **1.0**，显示尺寸 16×16。
- **内边距与线宽为官方 16 号原值**：几何（含贝塞尔控制点凸包界）全部落在
  x∈[2, 14]、y∈[2.94363, 13.0564] 内（实际曲线略小于该界）；箭头主干厚度 1 单位 = **1.0 DIP**。
- **为何不用 `PathIcon`**：`PathIcon` 无 `viewBox` 概念，其模板会把**几何自身包围盒**拉伸填满图标位，
  丢失官方内边距并把线宽拉粗；故用固定 16 画板 `Canvas` + `Path` 保真。
- **与上一版的差异**：上一版为 `arrow_left_20_regular` 经 `Viewbox` 等比（0.8）缩入 16×16，
  线宽随之变细至约 0.8 DIP；本版为 16 号原生绘制稿 1:1 落位，笔画节奏、内边距与线宽
  均为官方 16 号设计原值，不再存在缩放换算。

## 4. 主题与高对比（适配改动声明）

- 官方 SVG 原始着色为硬编码 `fill="#212121"`（单色深灰，面向浅色背景）。
- XAML 中改用 `Fill="{ThemeResource TextFillColorPrimaryBrush}"`——这是**相对官方原始着色的适配改动**：
  浅色近黑、深色近白，随浅/深主题与高对比自动换色；不使用任何硬编码颜色。
- 高对比：`TextFillColorPrimaryBrush` 在 WinUI 高对比主题字典中映射到系统窗口文字色画刷，
  随高对比设置换色。
- 未新增 `MainWindow.xaml.cs` 主题处理代码；按钮其余行为（折叠/让位/点击区/可用态）保持不变。

## 5. 构建项结论

- **无需任何资产构建项**：几何以官方 `path d` 内联在 XAML 中，不新增 `.svg` 资源文件，
  故不需要修改 `AIFrontend.csproj`。
- 未采用 `SvgImageSource` 方案；若日后改用，需增加部署项并处理非打包资源定位，
  超出本次范围，留给父会话。

## 6. 视觉一致性声明（如实）

- 本几何即官方 `arrow_left_16_regular` 原生绘制稿，与上游 16 号变体同源同稿、1:1 落位。
- 但**不声称**与 Microsoft Store 或既有字体图标（Segoe Fluent Icons 字形）视觉完全一致、
  尺寸完全等同：主题画刷着色为适配改动，跨产品渲染亦可能存在差异。
- 最终视觉一致性**待用户验收**（编译验收由父会话统一执行）。

## 7. 本次未做（验证边界）

- **未构建**（未执行 `dotnet build`/`msbuild`）、**未运行应用**、**未启停任何进程**。
- **未做任何 UI 操作**：未激活/点击/截图，未跑 UI 探针；未触碰当前预览副本。
- **未改 `AIFrontend.csproj`**，未改 `MainWindow.xaml.cs`，未改其他任何文件。
- 未做 git 操作。
- 本会话已完成的静态核对：path 逐字一致（sha256 比对）、16 坐标 / 无 `Viewbox` / 无缩放、
  `Button` 36×32 / 折叠行为 / 68 DIP 导航 / 主题画刷保留、LICENSE 全文完整性（逐字节比对）。
- 编译与最终视觉验收由父会话/用户完成。
