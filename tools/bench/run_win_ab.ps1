#!/usr/bin/env pwsh
# Windows no-spin A/B harness. Runs N trials of multiple variants and
# emits an averaged comparison table.
param(
    [int]$Trials = 3,
    [string]$Sizes = "0,1024,4096",
    [string]$OutDir = "C:\Temp\bench_win_ab"
)

$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..\..")
try {
    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $variants = @(
        @{ Name="spin"; Env=@{ "SHM_WIN_ALLOW_SPIN" = "1" } },
        @{ Name="nospin"; Env=@{} },
        @{ Name="nospin_coal"; Env=@{ "SHM_ENABLE_COALESCE" = "1" } }
    )

    foreach ($v in $variants) {
        for ($t = 1; $t -le $Trials; $t++) {
            # Reset env
            Remove-Item Env:\SHM_WIN_ALLOW_SPIN -ErrorAction SilentlyContinue
            Remove-Item Env:\SHM_ENABLE_COALESCE -ErrorAction SilentlyContinue
            foreach ($k in $v.Env.Keys) {
                Set-Item -Path "Env:$k" -Value $v.Env[$k]
            }
            $tag = "$($v.Name)_t$t"
            $outDir = Join-Path $OutDir $tag
            Write-Host "=== Variant=$($v.Name)  Trial=$t ==="
            dotnet run --no-build -c Release --project benchmark-shm/ringbench `
                -- --sizes $Sizes --only shm --output $outDir 2>&1 |
                Select-String -Pattern "^\s+(0B|1KB|4KB|16KB|64KB)\s+\d+" |
                Select-Object -First 30
        }
    }

    # Aggregate
    Write-Host "`n=== AGGREGATE (avg of $Trials trials) ==="
    $rows = @()
    foreach ($v in $variants) {
        $unary = @{}
        $stream = @{}
        for ($t = 1; $t -le $Trials; $t++) {
            $tag = "$($v.Name)_t$t"
            $csvPath = Join-Path $OutDir $tag "windows\results.csv"
            if (-not (Test-Path $csvPath)) { continue }
            $csv = Import-Csv $csvPath
            foreach ($r in $csv) {
                $key = "$($r.size_bytes)"
                $val = [double]$r.avg_latency_us
                if ($r.type -eq "unary") {
                    if (-not $unary.ContainsKey($key)) { $unary[$key] = @() }
                    $unary[$key] += $val
                } else {
                    if (-not $stream.ContainsKey($key)) { $stream[$key] = @() }
                    $stream[$key] += $val
                }
            }
        }

        foreach ($size in ($unary.Keys + $stream.Keys | Select-Object -Unique | Sort-Object { [int]$_ })) {
            $u = if ($unary.ContainsKey($size)) { ($unary[$size] | Measure-Object -Average).Average } else { 0 }
            $s = if ($stream.ContainsKey($size)) { ($stream[$size] | Measure-Object -Average).Average } else { 0 }
            $rows += [PSCustomObject]@{
                Variant = $v.Name
                Size = $size
                UnaryAvgUs = [math]::Round($u, 1)
                StreamAvgUs = [math]::Round($s, 1)
            }
        }
    }
    $rows | Format-Table -AutoSize
    $rows | Export-Csv -Path (Join-Path $OutDir "aggregate.csv") -NoTypeInformation
    Write-Host "Aggregate CSV: $(Join-Path $OutDir 'aggregate.csv')"
}
finally {
    Pop-Location
}
