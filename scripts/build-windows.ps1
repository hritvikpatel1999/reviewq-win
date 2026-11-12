[CmdletBinding()]
param(
    [ValidateSet('win-x86', 'win-x64', 'win-arm64')][string[]]$Runtime = @('win-x86', 'win-x64', 'win-arm64'),
    [string]$Version = '3.0.0',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$InstallerCompiler
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'windows\ReviewQ.sln'
$dist = Join-Path $root 'dist\windows'
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit $LASTEXITCODE" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric components.' }
& (Join-Path $PSScriptRoot 'generate-windows-icon.ps1')
if (-not $SkipTests) { Invoke-Dotnet @('test', $solution, '-c', 'Release', '--nologo') }
if (-not $SkipInstaller) {
    if (-not $InstallerCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InstallerCompiler = $command.Source }
        else {
            foreach ($candidate in @(
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
            )) {
                if (Test-Path -LiteralPath $candidate) { $InstallerCompiler = $candidate; break }
            }
        }
    }
    if (-not $InstallerCompiler -or -not (Test-Path -LiteralPath $InstallerCompiler)) {
        throw 'Inno Setup 6.3+ is required for installers. Install it or explicitly request -SkipInstaller for ZIP-only output.'
    }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$artifacts = [System.Collections.Generic.List[string]]::new()
foreach ($rid in $Runtime) {
    $publish = Join-Path $dist "publish\$rid"
    Invoke-Dotnet @('publish', (Join-Path $root 'windows\src\ReviewQ.Windows\ReviewQ.Windows.csproj'),
        '-c', 'Release', '-r', $rid, '--self-contained', 'true', '-p:PublishSingleFile=false',
        "-p:Version=$Version", '-o', $publish, '--nologo')
    $executable = Join-Path $publish 'ReviewQ.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw "Missing $rid executable." }
    $bytes = [System.IO.File]::ReadAllBytes($executable)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $expected = switch ($rid) { 'win-x86' { 0x014c }; 'win-x64' { 0x8664 }; 'win-arm64' { 0xaa64 } }
    if ($machine -ne $expected) { throw "Wrong executable architecture for $rid." }
    $archive = Join-Path $dist "ReviewQ-$Version-$rid.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
    $artifacts.Add($archive)
    if (-not $SkipInstaller) {
        $arch = $rid.Substring(4)
        & $InstallerCompiler /Q "/DAppVersion=$Version" "/DArchitecture=$arch" "/DPublishDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'windows\packaging\ReviewQ.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed for $rid." }
        $installer = Join-Path $dist "ReviewQ-$Version-$rid-setup.exe"
        if (-not (Test-Path -LiteralPath $installer)) { throw "Installer output missing for $rid." }
        $artifacts.Add($installer)
    }
}
$lines = foreach ($artifact in $artifacts) { "$((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($artifact))" }
$lines | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Validated packages: $($artifacts -join ', ')"
