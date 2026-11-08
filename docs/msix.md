# Windows MSIX 安装包

FluentCode 提供 x64 MSIX，最低 Windows 10 21H2（19044），使用 Windows SDK 26100 构建。包标识为 `mingz.SST.OpenCodeDesktop.Fluent`，发布者为 `CN=mingz`，显示名称为 FluentCode，版本为 `1.2611.13.0`，采用 GPL-3.0-or-later。

MSIX 自带 .NET 10、Windows App SDK、原生 Terminal 控件及完整离线 BrowserRuntime；目标机不需要 Visual Studio、Node.js 或单独的 .NET Runtime。仍需安装 [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。原有 `build-debug.ps1`、`publish-release.ps1` 继续生成依赖 .NET Desktop Runtime 的非打包版本。

## 构建

从客户端仓库根目录，在 Windows PowerShell 7 中执行。需要 .NET 10 SDK、Visual Studio 的 MSBuild / MSIX Packaging Tools / C++ 工具与 Windows SDK 26100。首次准备运行时：

```powershell
.\tools\browser-runtime\prepare.ps1
# CI 或用于后续签名的未签名包：
.\scripts\package-msix.ps1 -Unsigned -EvidenceRoot artifacts\msix-unsigned
# 本机开发侧载包：显式创建或复用 CN=mingz 开发签名证书：
.\scripts\package-msix.ps1 -DevelopmentCertificate -EvidenceRoot artifacts\msix-development
# 或使用调用者已准备好的 CurrentUser\My 代码签名证书：
.\scripts\package-msix.ps1 -CertificateThumbprint $thumbprint -EvidenceRoot artifacts\msix-signed
```

输出在本次目录的 `package/` 下：`.msix`、`SHA256SUMS.txt`；签名时另有仅含公钥的 `.cer`、`Install.cmd`、`Install.ps1`。安装脚本自动绑定本次包的 SHA256、版本和证书指纹；未签名 CI 包不生成安装脚本。打包过程本身不会信任证书、安装包、提权或启动应用。

开发私钥以不可导出形式保存在当前用户的 Windows 证书/密钥存储区，位于仓库及证据目录之外；按 `CN=mingz` 和 `FluentCode MSIX development` 友好名称复用。无需 PFX 或密码，不应将任何私钥提交、上传或放进构建证据。没有自动时间戳服务；开发包需要在证书有效期内签名。正式分发应使用调用者持有的适当代码签名证书。

脚本使用全新 `C:\fcmsix\<随机目录>` 暂存，以避开 npm 深层文件路径限制；也可用 `-StagingRoot` 指定可写的短本地目录。打包编译使用独立 scratch bin/obj，不覆盖非打包构建。`-PublishedPayloadDirectory` 可显式复用本候选已完成的、以 `FluentCodePackaged=true` 发布的目录；最终校验仍比较所有载荷文件和预备 BrowserRuntime 的哈希。证据目录必须全新。

WAP 项目是 `packaging/FluentCode.Package/FluentCode.Package.wapproj`。它打包完整 self-contained publish，而非只收集单个 EXE 的默认输出。应用已合并的 `FluentCode.pri` 必须参与生成包主索引 `resources.pri`，其中包含应用和 WinUI 的内嵌 XAML；仅携带旁置 PRI 文件会导致已安装包在启动时找不到 WinUI 主题资源。其余 SDK PRI 和 npm 文件保持普通载荷，避免重复索引。打包会补齐应用 PRI 索引到的 AppIcon/Taskbar 资源文件。图标仅机械复用现有 AppIcon PNG；重新生成可运行 `scripts/generate-msix-assets.ps1`。没有创建新图标设计。

## 由用户决定信任和安装

将 `Install.cmd`、`Install.ps1`、`.msix` 和 `.cer` 保留在同一文件夹（可整体复制到含空格或中文的目录），从普通桌面**双击 `Install.cmd`**。无需 PowerShell 7，使用系统自带 Windows PowerShell 5.1，不修改执行策略。窗口会显示中文包身份、SHA256、发布者、证书指纹和当前安装用户；校验全部通过后，输入 `INSTALL` 才继续。

首次需要信任证书时会出现 UAC 提示：管理员助手仅校验证书并将该公钥加入 **本地计算机 / 受信任的人（LocalMachine\TrustedPeople）**，不加入根证书库、不安装应用。助手退出后，原普通用户进程调用 `Add-AppxPackage` 安装；不要右键“以管理员身份运行”整个脚本，否则会被拒绝。已经信任同一证书时不再提权。只有在你确认来源并愿意信任 `CN=mingz` 发布者时才授权。

脚本不下载任何内容、不调整执行策略或开发者模式、不自动关闭应用、不迁移或删除数据，也不会安装后自动启动。若取消 UAC，安装停止；若 Windows 提示包被占用（`0x80073D02`），请自行退出 FluentCode（包括托盘）后重试。窗口会显示结果并暂停。`Install.cmd -WhatIf` 只校验和预览，不确认、不提权、不导入证书、不安装。

给已经完成的签名包补上安装脚本，无需重新发布或修改包：

```powershell
.\scripts\write-install-scripts.ps1 -PackagePath '<包目录>\FluentCode_1.2611.13.0_x64.msix' -CertificatePath '<包目录>\FluentCode-signing.cer'
```

自签名包在没有显式信任时不能安装。以下手动步骤仅供故障排查；先核对 SHA256、主题 `CN=mingz` 和证书指纹，并确认来源：

```powershell
# 只读检查：
Get-FileHash .\FluentCode_1.2611.13.0_x64.msix -Algorithm SHA256
certutil -dump .\FluentCode-signing.cer
# 在管理员 PowerShell 中，显式允许此发布者（不要导入 Trusted Root）：
Import-Certificate -FilePath .\FluentCode-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
# 然后在普通用户 PowerShell 中安装：
Add-AppxPackage -Path .\FluentCode_1.2611.13.0_x64.msix
```

也可以在完成证书信任后双击 MSIX，使用 Windows App Installer。CI 未配置签名证书，PR 和标签产物均未签名，不能直接安装；需要用匹配发布者的证书签名，而不能仅对未签名包导入一个无关证书。

## 数据与运行约定

- MSIX 使用其包专属的 `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState`，从全新 FluentCode 配置开始，不读取或迁移非打包版本的设置/密钥。显式测试覆盖目录仍保持隔离。
- WebView2 的用户目录、浏览器下载、Mermaid/预览缓存使用该数据根；Node 的 CWD、临时目录、用户目录和编译缓存位于 LocalState，而非只读 WindowsApps 安装目录。应用资源与 BrowserRuntime 只读使用。
- 应用以 medium integrity/full trust 运行，不提权；保持已有 AppInstance 单实例和关闭到托盘行为。通知通过固定 COM CLSID `77E0A389-0925-45DF-A8D1-7B7552DAB83D` 和 `----AppNotificationActivated:` 参数进入现有激活处理，包版使用清单身份注册。
- 未添加登录启动任务、自动启动服务或 `.appinstaller` 更新地址。需要用户先提供真实 HTTPS 更新端点，才能配置自动更新。
- `package-msix.ps1` 记录 Terminal/Node 的原生导入，并带上已安装 Visual Studio 的 x64 CRT 可再分发 DLL，保留现有许可证、第三方通知和 BrowserRuntime 内的许可证。

## 校验范围

`pwsh -NoProfile -File tests/msix/run.ps1` 可独立运行打包专项静态校验；它不加入现有 28 项运行时套件，不安装或启动应用。

`tests/msix/run.ps1` 可离线校验源清单，打包脚本还会解包并校验身份、版本、最低系统、COM 激活、运行时/PRI/原生 DLL/图标/通知及每个载荷文件哈希，并通过 SDK `makepri dump` 确认包主资源索引包含应用和 WinUI 主题的内嵌 XAML（`resource-index-validation.json`）。签名校验分别记录密码学完整性与 Windows 信任链结果；开发证书未被信任是预期状态，不通过自动导入证书消除该结果。

解包校验不等于安装/运行验证。真正的已安装启动、单实例、托盘、通知冷/热激活、Terminal、BrowserRuntime 和只读安装目录检查，必须在用户批准证书信任及安装后进行。父仓库 `opencode-qol` 继续拥有 Windows CI；发布子仓库后还需更新父仓库 gitlink。

父仓库 CI 保留 Debug、26 项离线测试和独立 Release/启动冒烟，再以 `package-msix.ps1 -Unsigned` 构建并解包校验自包含 MSIX，上传安装包和证据。仅 `fluentcode-v*` 标签发布 Windows ZIP/MSIX；Linux 的 `v*` 工作流不变。PR 与标签构建都不提供签名证书。
