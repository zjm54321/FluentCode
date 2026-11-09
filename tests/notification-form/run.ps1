# Headless only: installed .NET 10 SDK/reference packs, no restore, no new packages, no WinRT,
# no native notification registration/show, no UI, no live server. Fake HttpMessageHandler only.
# Production sources are compiled read-only; this script never edits them.
param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$dotnetRoot = Split-Path $dotnet
$sdk = Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$referencePack) { throw '.NET 10 SDK/reference pack required.' }
$output = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path ([IO.Path]::GetTempPath()) ('notification-form-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'question-capture.json') $output
$common = @('/nologo', '/langversion:latest', '/nullable:enable', '/warnaserror+', '/target:exe',
    ('/out:"' + (Join-Path $output 'NotificationFormTests.dll') + '"'))
$common += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net10.0/*.dll') | ForEach-Object { '/reference:"' + $_.FullName + '"' }

# Production sources under test (read-only): the form projection, the interaction coordinator,
# its bindings/gate/codec, and the OpenCode transport + DTOs it re-reads pending state through.
$notifications = @('NotificationFormPolicy.cs', 'NotificationInteractionCoordinator.cs',
    'NotificationInteractions.cs', 'NotificationArgumentCodec.cs', 'NotificationActivationInfo.cs',
    'OpenCodeInteractionGate.cs', 'NotificationSource.cs', 'NotificationText.cs', 'NotificationInteractionXml.cs',
    'OpenCodeExecutionProjection.cs') # Required by wildcard-linked session-control DTOs; real type, not a stub.
foreach ($file in $notifications) { $common += '"' + (Join-Path $repo "src/FluentCode/Services/Notifications/$file") + '"' }
$common += Get-ChildItem (Join-Path $repo 'src/FluentCode/Services/OpenCode/*.cs') |
    Where-Object Name -NE 'OpenCodePairingService.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$common += '"' + (Join-Path $PSScriptRoot 'Program.cs') + '"'

$rsp = Join-Path $output 'compile.rsp'
$common | Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Core compile failed: $LASTEXITCODE" }
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' |
    Set-Content -Encoding UTF8 (Join-Path $output 'NotificationFormTests.runtimeconfig.json')
& $dotnet (Join-Path $output 'NotificationFormTests.dll')
if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $LASTEXITCODE" }
Write-Output "Evidence directory: $output"

# Real question widgets with a controllable dispatcher and inert native controls. No physical IME.
$other = $common | Where-Object { !$_.StartsWith('/out:') -and !$_.EndsWith('Program.cs"') }
$other += '/define:QUESTION_INPUT_TESTS'
$other += '/out:"' + (Join-Path $output 'OtherInputTests.dll') + '"'
$other += '"' + (Join-Path $repo 'tests/opencode-event-sync/InteractionUi.TestHost.cs') + '"'
foreach ($name in @('OpenCodeFormCard.cs', 'OpenCodeQuestionOverlay.cs', 'OpenCodeInteractionPolicy.cs', 'OpenCodeFormFieldPresentation.cs', 'OpenCodeInteractionUi.cs', 'OpenCodeDisplayText.cs', 'OpenCodeOptionLabelPanel.cs')) {
    $other += '"' + (Join-Path $repo "src/FluentCode/Pages/OpenCode/$name") + '"'
}
foreach ($name in @('OtherInput.TestHost.cs', 'OtherInputTests.cs')) { $other += '"' + (Join-Path $PSScriptRoot $name) + '"' }
$page = [IO.File]::ReadAllText((Join-Path $repo 'src/FluentCode/Pages/OpenCodePage.QuestionOverlay.cs'))
$sync = [regex]::Match($page, '(?ms)^    private void SyncQuestionOverlay\(bool permissionHadFocus\).*?^    \}')
if (!$sync.Success) { throw 'Missing production SyncQuestionOverlay member.' }
$linkedPage = Join-Path $output 'QuestionOverlay.PageMember.cs'
"using Microsoft.UI.Xaml; namespace FluentCode.Pages; public sealed partial class OpenCodePage {`n" + $sync.Value + "`n}" | Set-Content -Encoding UTF8 $linkedPage
$other += '"' + $linkedPage + '"'
$otherRsp = Join-Path $output 'other-input.compile.rsp'
$other | Set-Content -Encoding UTF8 $otherRsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$otherRsp" 2>&1 | Tee-Object -FilePath (Join-Path $output 'other-input.compile.log')
if ($LASTEXITCODE -ne 0) { throw "Other input compile failed: $LASTEXITCODE" }
Copy-Item (Join-Path $output 'NotificationFormTests.runtimeconfig.json') (Join-Path $output 'OtherInputTests.runtimeconfig.json')
& $dotnet (Join-Path $output 'OtherInputTests.dll') 2>&1 | Tee-Object -FilePath (Join-Path $output 'other-input.tests.log')
if ($LASTEXITCODE -ne 0) { throw "Other input tests failed: $LASTEXITCODE" }
