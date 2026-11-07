param(
    [Parameter(Mandatory=$true)][string]$FixtureDirectory,
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$RemoteUser
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $FixtureDirectory) { throw 'Refusing to reuse a fixture key directory.' }
New-Item -ItemType Directory -Path $FixtureDirectory | Out-Null
$key = Join-Path $FixtureDirectory 'client-key'
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'client-key') -Destination $key
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'real-opencode') -Destination (Join-Path $FixtureDirectory 'real-opencode')
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $key /inheritance:r /grant:r "${identity}:(F)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to restrict the throwaway private key ACL.' }
$config = @"
Host fixture
    HostName 127.0.0.1
    Port 2222
    User $RemoteUser
    UserKnownHostsFile "$($FixtureDirectory.Replace('\','/'))/custom_hosts"
    GlobalKnownHostsFile none
    IdentityAgent none
    IdentityFile "$($key.Replace('\','/'))"
    IdentitiesOnly yes
    StrictHostKeyChecking yes
    HashKnownHosts yes
"@
[IO.File]::WriteAllText((Join-Path $FixtureDirectory 'config'), $config, [Text.UTF8Encoding]::new($false))
Write-Output 'Prepared isolated Windows fixture config/key/known_hosts; no real .ssh or agent access.'
