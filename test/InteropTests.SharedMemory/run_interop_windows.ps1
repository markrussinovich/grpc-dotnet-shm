# Windows PowerShell runner for cross-language interop tests.
# Verifies Go ↔ .NET SHM RPC interop in all 4 directions.
#
# Prerequisites:
#   - Go ≥ 1.25.0 (or set $env:GOTOOLCHAIN = "go1.25.0")
#   - .NET 10.0 SDK
#   - grpc-go-shmem checked out as a SIBLING of grpc-dotnet-shm (the
#     interop go.mod uses a relative ../../../../grpc-go-shmem replace
#     so contributors do not need a Windows-absolute path)
#
# Usage:
#   pwsh -File run_interop_windows.ps1 [-Build]
#
# -Build: rebuild Go and .NET binaries before running tests.

[CmdletBinding()]
param(
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

$goDir         = Join-Path $here 'go'
$goServerExe   = Join-Path $goDir 'server\server_new.exe'
$goClientExe   = Join-Path $goDir 'client\client_new.exe'
$dotnetSrvExe  = Join-Path $here 'dotnet-server\bin\Release\net10.0\DotNetServer.exe'
$dotnetCliExe  = Join-Path $here 'dotnet-client\bin\Release\net10.0\DotNetClient.exe'

function Kill-Strays {
    Get-Process |
        Where-Object { $_.ProcessName -match '^(server_new|client_new|DotNetClient|DotNetServer)$' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
}

function Run-Case([string]$name, [string]$serverPath, [string[]]$serverArgs,
                  [string]$clientPath, [string[]]$clientArgs,
                  [string]$expectedSubstring) {
    Write-Host ""
    Write-Host "==== $name ===="
    Kill-Strays
    $segName = "interop_{0}_{1}" -f ($name -replace '\W','_'), (Get-Random)
    $serverFullArgs = $serverArgs + @('-segment', $segName)
    # Note: .NET server uses positional segment arg; Go uses -segment.
    if ($serverPath -like '*DotNetServer.exe') { $serverFullArgs = @($segName) }
    $clientFullArgs = $clientArgs + @('-segment', $segName)
    if ($clientPath -like '*DotNetClient.exe') { $clientFullArgs = @($segName) + $clientArgs }

    $sOut = New-TemporaryFile
    $sErr = New-TemporaryFile
    try {
        $server = Start-Process -FilePath $serverPath -ArgumentList $serverFullArgs `
            -PassThru -NoNewWindow `
            -RedirectStandardOutput $sOut.FullName -RedirectStandardError $sErr.FullName
        Start-Sleep 4
        if ($server.HasExited) {
            Write-Host "  FAIL: server exited prematurely (code=$($server.ExitCode))"
            Get-Content $sOut.FullName, $sErr.FullName | ForEach-Object { "    $_" }
            return $false
        }
        $clientOut = & $clientPath @clientFullArgs 2>&1 | Out-String
        $exit = $LASTEXITCODE
        Write-Host "  Client (exit=$exit):"
        $clientOut.Split("`n") | ForEach-Object { "    $_" }
        if ($exit -ne 0) {
            return $false
        }
        if ($expectedSubstring -and ($clientOut -notmatch [regex]::Escape($expectedSubstring))) {
            Write-Host "  FAIL: response did not contain expected substring '$expectedSubstring'"
            return $false
        }
        Write-Host "  PASS"
        return $true
    } finally {
        if ($server -and -not $server.HasExited) {
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        }
        Remove-Item $sOut.FullName, $sErr.FullName -ErrorAction SilentlyContinue
    }
}

if ($Build) {
    Write-Host "==== Building Go binaries ===="
    $env:GOTOOLCHAIN = 'go1.25.0'
    Push-Location (Join-Path $goDir 'server')
    & go build -o server_new.exe .
    if ($LASTEXITCODE -ne 0) { throw "Go server build failed" }
    Pop-Location
    Push-Location (Join-Path $goDir 'client')
    & go build -o client_new.exe .
    if ($LASTEXITCODE -ne 0) { throw "Go client build failed" }
    Pop-Location
    Write-Host "==== Building .NET binaries ===="
    & dotnet build -c Release (Join-Path $here 'dotnet-server\DotNetServer.csproj') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw ".NET server build failed" }
    & dotnet build -c Release (Join-Path $here 'dotnet-client\DotNetClient.csproj') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw ".NET client build failed" }
}

foreach ($exe in @($goServerExe, $goClientExe, $dotnetSrvExe, $dotnetCliExe)) {
    if (-not (Test-Path $exe)) {
        throw "Required binary missing: $exe. Run with -Build."
    }
}

$results = @()
$results += @{ Name = 'Go_to_Go';      Pass = (Run-Case 'Go server  + Go client'  $goServerExe @()  $goClientExe @('-name','GoCli')                'from Go server') }
$results += @{ Name = 'Go_to_DotNet';  Pass = (Run-Case 'Go server  + .NET client' $goServerExe @() $dotnetCliExe @('DotNetCli')                   'from Go server') }
$results += @{ Name = 'DotNet_to_Go';  Pass = (Run-Case '.NET server + Go client'  $dotnetSrvExe @() $goClientExe @('-name','GoCli')               'from .NET server') }
$results += @{ Name = 'DotNet_to_DotNet'; Pass = (Run-Case '.NET server + .NET client' $dotnetSrvExe @() $dotnetCliExe @('DotNetCli')              'from .NET server') }

Write-Host ""
Write-Host "==== Summary ===="
$pass = 0; $fail = 0
foreach ($r in $results) {
    if ($r.Pass) { Write-Host ("  PASS  {0}" -f $r.Name); $pass++ }
    else         { Write-Host ("  FAIL  {0}" -f $r.Name); $fail++ }
}
Write-Host ""
Write-Host "Total: $pass passed, $fail failed"
exit $fail
