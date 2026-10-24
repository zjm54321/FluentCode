# Offline A2 UI/selection checks; no WinUI application build, live HTTP, or user preference IO.
param([string]$EvidenceRoot = '', [string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = if ($SourceRoot) { (Resolve-Path $SourceRoot).ProviderPath } else { (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath }
$dotnet = (Get-Command dotnet.exe).Source
$root = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $root 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$pack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('opencode-model-picker-ui-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$statePath = "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogModels.cs"
$state = [IO.File]::ReadAllText($statePath)
$end = $state.IndexOf('internal enum OpenCodeRollbackKind')
if ($end -lt 0) { throw 'Missing model-state extraction boundary.' }
$generated = Join-Path $output 'ModelState.cs'
$state.Substring(0, $end) | Set-Content -Encoding UTF8 $generated
$refPath = "$repo/src/AIFrontend/Services/OpenCode/OpenCodeDtos.Sessions.cs"
$wire = [regex]::Match([IO.File]::ReadAllText($refPath), '(?ms)^public sealed record OpenCodeModelRef\r?\n\{.*?^\}')
if (!$wire.Success) { throw 'Missing wire model reference.' }
$wirePath = Join-Path $output 'ModelRef.cs'
"using System.Text.Json.Serialization; namespace AIFrontend.Services.OpenCode;`n" + $wire.Value | Set-Content -Encoding UTF8 $wirePath
$blankPath = "$repo/src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs"
$method = [regex]::Match([IO.File]::ReadAllText($blankPath), '(?ms)^    private async Task SetBlankVariantAsync\(.*?^    \}')
$entries = [regex]::Match([IO.File]::ReadAllText($blankPath), '(?ms)^    internal const string BlankVariantRadioGroup = .*?^    internal static IReadOnlyList<\(string\? Id, string Label, bool IsChecked\)> BlankVariantMenuEntries\(.*?^    \}')
$homePath = "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeHomeProjection.cs"
$preferences = [regex]::Match([IO.File]::ReadAllText($homePath), '(?ms)^internal sealed record OpenCodeBlankDraftPreferences\(.*?^\}')
$tokenPath = "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeComposerContracts.cs"
$token = [regex]::Match([IO.File]::ReadAllText($tokenPath), '(?ms)^public readonly record struct OpenCodeComposerToken\r?\n\{.*?^\}')
if (!$method.Success -or !$entries.Success -or !$preferences.Success -or !$token.Success) { throw 'Missing variant selection production member/contracts.' }
$variantPath = Join-Path $output 'VariantSelection.cs'
"using System; using System.Collections.Generic; using System.Linq; using System.Threading.Tasks; using AIFrontend.Services.OpenCode; using AIFrontend.Pages.OpenCode; namespace AIFrontend.Pages { public sealed partial class OpenCodePage {`n" + $method.Value + "`n" + $entries.Value + "`n} } namespace AIFrontend.Pages.OpenCode {`n" + $preferences.Value + "`n" + $token.Value + "`n}" | Set-Content -Encoding UTF8 $variantPath
$sources = @($generated, $wirePath, $variantPath, (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'VariantSelection.cs'), "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeModelPresentation.cs", "$repo/src/AIFrontend/Services/OpenCode/OpenCodeRecentModels.cs", "$repo/src/AIFrontend/Services/OpenCode/OpenCodeDtos.Models.cs", "$repo/src/AIFrontend/Services/OpenCode/OpenCodeDtos.Location.cs")
$sources += "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeChildSessionControl.cs"
$sources += "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeModelPickerFavorites.cs"
$sources += @("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeNativeComboBox.cs", (Join-Path $PSScriptRoot 'NativeComboBox.TestHost.cs'))
$card = [IO.File]::ReadAllText("$repo/src/AIFrontend/Pages/OpenCode/OpenCodeSessionControlCard.cs")
$childRender = [regex]::Match($card, '(?ms)^    internal void RenderReadOnly\(.*?^    \}')
$modelRender = [regex]::Match($card, '(?ms)^    internal static void RenderModelButton\(.*?^    \}')
if (!$childRender.Success) { throw 'Missing child ComboBox rendering method.' }
$childPath = Join-Path $output 'ChildCardRender.cs'
"using System; using AIFrontend.Services.OpenCode; using Microsoft.UI.Xaml; using Microsoft.UI.Xaml.Controls; namespace AIFrontend.Pages.OpenCode; internal sealed partial class OpenCodeSessionControlCard {`n" + $childRender.Value + "`n" + $modelRender.Value + "`n}" | Set-Content -Encoding UTF8 $childPath
$sources += $childPath
$assembly = Join-Path $output 'ModelPickerUiTests.dll'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/warnaserror+', ('/out:"' + $assembly + '"'))
$arguments += Get-ChildItem (Join-Path $pack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$arguments += $sources | ForEach-Object { '"' + $_ + '"' }
$rsp = Join-Path $output 'compile.rsp'
$arguments | Set-Content -Encoding UTF8 $rsp
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' | Set-Content -Encoding UTF8 (Join-Path $output 'ModelPickerUiTests.runtimeconfig.json')
New-Item -ItemType File -Path (Join-Path $output 'compile.log') | Out-Null
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Picker UI focused compile failed: $LASTEXITCODE" }
& $dotnet $assembly $repo $output | Tee-Object -FilePath (Join-Path $output 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Picker UI focused tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"
