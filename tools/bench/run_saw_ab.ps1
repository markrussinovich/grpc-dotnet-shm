#!/usr/bin/env pwsh
# 3-trial A/B for SAW WriterLoop opt-in.
param([int]$Trials=3, [string]$Sizes="0,1024,4096")
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..\..")
try {
    $variants = @(
        @{ Name="saw_off"; Env=@{} },
        @{ Name="saw_on";  Env=@{ "SHM_SAW_WRITERLOOP" = "1" } }
    )
    foreach ($v in $variants) {
        for ($t = 1; $t -le $Trials; $t++) {
            Remove-Item Env:\SHM_WIN_ALLOW_SPIN -ErrorAction SilentlyContinue
            Remove-Item Env:\SHM_SAW_WRITERLOOP -ErrorAction SilentlyContinue
            foreach ($k in $v.Env.Keys) { Set-Item -Path "Env:$k" -Value $v.Env[$k] }
            Write-Host "=== Variant=$($v.Name) Trial=$t ==="
            dotnet run --no-build -c Release --project benchmark-shm/ringbench `
                -- --sizes $Sizes --only shm --output "C:\Temp\bench_saw_$($v.Name)_t$t" 2>&1 |
                Select-String -Pattern "^\s+(0B|1KB|4KB|16KB|64KB)\s+\d+" |
                Select-Object -First 6
        }
    }
}
finally { Pop-Location }
