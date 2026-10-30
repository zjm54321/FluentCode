param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
New-Item -ItemType Directory -Path $EvidenceRoot -Force|Out-Null
$source=Get-Content -Raw -Encoding UTF8 "$repo/src/AIFrontend/Pages/OpenCode/OpenCodeBrowserExternalView.cs"
$bind=$source.Substring($source.IndexOf('    internal void Bind('),$source.IndexOf('    private void UpdateStatus()')-$source.IndexOf('    internal void Bind('))
$capture=$source.Substring($source.IndexOf('    private async Task CaptureAsync()'),$source.IndexOf('    private async Task ShowChromeAsync(')-$source.IndexOf('    private async Task CaptureAsync()'))
$hostSource=(Get-Content -Raw -Encoding UTF8 "$PSScriptRoot/CaptureLifecycleHost.cs").Replace('    /* METHODS */',$bind+$capture)
$generated=Join-Path $EvidenceRoot 'CaptureLifecycle.Generated.cs';$hostSource|Set-Content -Encoding UTF8 $generated
$dotnet=(Get-Command dotnet.exe).Source;$root=Split-Path $dotnet
$sdk=Get-ChildItem "$root/sdk" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$pack=Get-ChildItem "$root/packs/Microsoft.NETCore.App.Ref" -Directory|Where-Object Name -Like '10.*'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1
$dll=Join-Path $EvidenceRoot 'CaptureLifecycle.dll';$rsp=Join-Path $EvidenceRoot 'compile.rsp'
$arguments=@('/nologo','/target:exe','/langversion:latest','/nullable:enable','/warnaserror+',('/out:"'+$dll+'"'))
$arguments+=Get-ChildItem "$($pack.FullName)/ref/net10.0/*.dll"|ForEach-Object{'/reference:"'+$_.FullName+'"'}
$arguments+='"'+$generated+'"';$arguments|Set-Content -Encoding UTF8 $rsp
& $dotnet exec (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') "@$rsp" 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'compile.txt');if($LASTEXITCODE -ne 0){throw "Capture lifecycle compile failed: $LASTEXITCODE"}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'|Set-Content -Encoding UTF8 (Join-Path $EvidenceRoot 'CaptureLifecycle.runtimeconfig.json')
& $dotnet $dll 2>&1|Tee-Object -FilePath (Join-Path $EvidenceRoot 'results.txt');if($LASTEXITCODE -ne 0){throw "Capture lifecycle checks failed: $LASTEXITCODE"}
