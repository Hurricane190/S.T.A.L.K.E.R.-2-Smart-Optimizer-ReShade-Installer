param(
    [switch]$FrameworkDependent,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host '=== ZoneTune release build ===' -ForegroundColor Cyan
if (-not $SkipTests) {
    Write-Host 'Running self-tests...' -ForegroundColor Cyan
    dotnet run --project src\ZoneTune.Cli -c Release -- --selftest
    if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed. Release build aborted.' }
}

if (Test-Path dist) { Remove-Item dist -Recurse -Force }
New-Item -ItemType Directory -Force dist | Out-Null

$common = @(
    '-c','Release',
    '-r','win-x64',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded'
)
if ($FrameworkDependent) { $common += '--self-contained'; $common += 'false' }
else { $common += '--self-contained'; $common += 'true' }

Write-Host 'Publishing ZoneTune.exe...' -ForegroundColor Cyan
dotnet publish src\ZoneTune\ZoneTune.csproj @common -o dist
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed.' }

Write-Host 'Publishing zonetune-cli.exe...' -ForegroundColor Cyan
dotnet publish src\ZoneTune.Cli\ZoneTune.Cli.csproj @common -o dist
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

Copy-Item README.md, INSTALL.md, ERROR_LOGGING.md, THIRD_PARTY_NOTICES.md, LICENSE, CHANGELOG.md, MAINTENANCE.md dist -Force
if (Test-Path docs) { Copy-Item docs dist -Recurse -Force }

# Remove development-only artefacts that publish may copy.
Get-ChildItem dist -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem dist -Filter '*.xml' -ErrorAction SilentlyContinue | Remove-Item -Force

$hashLines = Get-ChildItem dist -File | Where-Object { $_.Name -ne 'checksums.sha256' } | Sort-Object Name | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $($_.Name)"
}
$hashLines | Set-Content dist\checksums.sha256 -Encoding ASCII

Write-Host ''
Write-Host 'Build complete:' -ForegroundColor Green
Get-ChildItem dist -File | Select-Object Name, @{n='Size';e={ '{0:N1} MB' -f ($_.Length / 1MB) }} | Format-Table -AutoSize
Write-Host 'Checksums: dist\checksums.sha256'
