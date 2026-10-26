# run-tests.ps1 -- ThunderPanSync regression test runner
#
# IMPORTANT: THIS FILE MUST STAY PURE ASCII.
#   PowerShell 5.1 reads a BOM-less .ps1 using the system ANSI code page
#   (cp936 on Chinese Windows). Non-ASCII bytes in comments or strings corrupt
#   the token stream and the script fails to parse -- exactly what happened
#   with build-native.ps1. Keep every byte < 128. English comments only.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1 -UnitOnly
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1 -UploadOnly
#
# Exit code: 0 when there is no FAIL. SKIP is NOT a pass: it means the case could
# not be exercised (no browser / no login / timeout) and a human must confirm it.
# The summary prints a loud warning listing every SKIP.
#            1 when at least one case FAILs.
#
# The heavy lifting lives in two Node scripts next to this file:
#   tests/test-unit.js    unit cases, no browser needed
#   tests/test-upload.js  real upload cases, needs a logged-in Edge on :9222
# Both print lines of the form  @@PASS@@|case-name|detail  which this runner
# parses into the human readable [PASS]/[FAIL]/[SKIP] lines.
#
# A case that produces no result line at all is reported as FAIL, never PASS:
# a silent crash must not look like success.

[CmdletBinding()]
param(
    [switch]$UnitOnly,
    [switch]$UploadOnly,
    [int]$UnitTimeoutSec = 120,
    [int]$UploadTimeoutSec = 900
)

$ErrorActionPreference = 'Continue'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$script:nodeExe = $null
$script:results = New-Object System.Collections.ArrayList

# ------------------------------------------------------------------ helpers

function Write-Line([string]$text) {
    [Console]::Out.WriteLine($text)
}

function Add-Result([string]$status, [string]$name, [string]$detail) {
    [void]$script:results.Add([pscustomobject]@{
            Status = $status
            Name   = $name
            Detail = $detail
        })
}

function Find-Node {
    $candidates = @()
    $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $cmd2 = Get-Command node -ErrorAction SilentlyContinue
    if ($cmd2) { $candidates += $cmd2.Source }
    $candidates += (Join-Path $repoRoot 'runtime\node.exe')
    $candidates += (Join-Path $repoRoot 'node.exe')
    $candidates += 'C:\Program Files\nodejs\node.exe'
    $candidates += 'C:\Program Files (x86)\nodejs\node.exe'
    $candidates += 'D:\Nodejs\node.exe'
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return $c }
    }
    return $null
}

function Get-NodeVersion([string]$exe) {
    try {
        $out = & $exe '--version' 2>&1
        if ($LASTEXITCODE -eq 0) { return (($out | Out-String).Trim()) }
    } catch {
    }
    return 'unknown'
}

# Starts a Node script, waits at most TimeoutSec, and converts its output into
# result objects. stdout/stderr are drained asynchronously so a chatty child
# (engine.log() prints every line to stdout) can never fill the pipe and hang.
function Invoke-NodeCases {
    param(
        [string]$ScriptPath,
        [string[]]$ScriptArgs,
        [string[]]$ExpectedNames,
        [int]$TimeoutSec
    )

    $results = New-Object System.Collections.ArrayList

    if (-not $script:nodeExe) {
        foreach ($n in $ExpectedNames) {
            [void]$results.Add([pscustomobject]@{ Status = 'FAIL'; Name = $n; Detail = 'node.exe not found; cannot run this case' })
        }
        return $results
    }
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        foreach ($n in $ExpectedNames) {
            [void]$results.Add([pscustomobject]@{ Status = 'FAIL'; Name = $n; Detail = ('missing test script: ' + $ScriptPath) })
        }
        return $results
    }

    $seen = @{}
    $stdoutText = ''
    $stderrText = ''
    $exitCode = $null
    $timedOut = $false
    $proc = $null

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $script:nodeExe
        $argParts = New-Object System.Collections.ArrayList
        [void]$argParts.Add('"' + $ScriptPath + '"')
        if ($ScriptArgs) {
            foreach ($a in $ScriptArgs) { [void]$argParts.Add('"' + $a + '"') }
        }
        $psi.Arguments = ($argParts -join ' ')
        $psi.WorkingDirectory = $repoRoot
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $psi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)

        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        [void]$proc.Start()

        # Start draining immediately: this is what prevents the classic
        # "child blocked writing to a full pipe while parent waits for exit" hang.
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()

        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while (-not $proc.HasExited) {
            if ((Get-Date) -gt $deadline) { $timedOut = $true; break }
            Start-Sleep -Milliseconds 150
        }

        if ($timedOut) {
            try { $proc.Kill() } catch { }
            try { [void]$proc.WaitForExit(5000) } catch { }
        } else {
            try { $exitCode = $proc.ExitCode } catch { }
        }

        try { $stdoutText = $outTask.Result } catch { $stdoutText = '' }
        try { $stderrText = $errTask.Result } catch { $stderrText = '' }

        if ($timedOut) {
            foreach ($n in $ExpectedNames) {
                [void]$results.Add([pscustomobject]@{
                        Status = 'FAIL'
                        Name   = $n
                        Detail = ('node test process exceeded ' + $TimeoutSec + 's and was killed')
                    })
                $seen[$n] = $true
            }
            if ($stderrText) {
                [Console]::Error.WriteLine('--- stderr from ' + (Split-Path -Leaf $ScriptPath) + ' ---')
                [Console]::Error.WriteLine($stderrText.Trim())
            }
            return $results
        }

        $lines = @()
        if ($stdoutText) { $lines = $stdoutText -split "`r?`n" }

        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if (-not $line) { continue }
            $m = [regex]::Match($line, '^@@(PASS|FAIL|SKIP)@@\|([^|]*)\|(.*)$')
            if (-not $m.Success) { continue }
            $status = $m.Groups[1].Value
            $name = $m.Groups[2].Value.Trim()
            $detail = $m.Groups[3].Value.Trim()
            if (-not $name) { continue }
            if ($seen.ContainsKey($name)) { continue }
            $seen[$name] = $true
            [void]$results.Add([pscustomobject]@{ Status = $status; Name = $name; Detail = $detail })
        }

        foreach ($n in $ExpectedNames) {
            if (-not $seen.ContainsKey($n)) {
                $why = 'test process produced no result for this case'
                if ($null -ne $exitCode) { $why += (' (exit code ' + $exitCode + ')') }
                if ($stderrText) {
                    $firstErr = ($stderrText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
                    if ($firstErr) { $why += ': ' + $firstErr.Trim() }
                }
                [void]$results.Add([pscustomobject]@{ Status = 'FAIL'; Name = $n; Detail = $why })
                $seen[$n] = $true
            }
        }
    } catch {
        foreach ($n in $ExpectedNames) {
            if (-not $seen.ContainsKey($n)) {
                [void]$results.Add([pscustomobject]@{ Status = 'FAIL'; Name = $n; Detail = ('runner error: ' + $_.Exception.Message) })
                $seen[$n] = $true
            }
        }
    } finally {
        if ($proc) { try { $proc.Dispose() } catch { } }
    }

    return $results
}

# ------------------------------------------------------------------ preflight

Write-Line '========================================================='
Write-Line ' ThunderPanSync regression tests (WP5)'
Write-Line '========================================================='

# ASCII self-check on this very file. PowerShell 5.1 parses BOM-less scripts
# with the ANSI code page, so any byte > 127 is a latent parse bomb.
$selfPath = $MyInvocation.MyCommand.Path
try {
    $bytes = [System.IO.File]::ReadAllBytes($selfPath)
    $highBytes = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) { $highBytes++ }
    }
    if ($highBytes -gt 0) {
        Add-Result 'FAIL' 'runner-ascii' ('run-tests.ps1 contains ' + $highBytes + ' byte(s) > 127; must be pure ASCII')
    } else {
        Add-Result 'PASS' 'runner-ascii' ('run-tests.ps1 is pure ASCII: ' + $bytes.Length + ' bytes, 0 above 127')
    }
} catch {
    Add-Result 'FAIL' 'runner-ascii' ('cannot read own file: ' + $_.Exception.Message)
}

$script:nodeExe = Find-Node
if ($script:nodeExe) {
    $nodeVer = Get-NodeVersion $script:nodeExe
    Add-Result 'PASS' 'node-available' ('node=' + $script:nodeExe + ' version=' + $nodeVer)
} else {
    Add-Result 'FAIL' 'node-available' 'node.exe not found on PATH or in known locations; every case will fail'
}

# ------------------------------------------------------------------ unit cases

$unitCases = @(
    'require-construct',
    'scan-empty',
    'scan-zero-byte',
    'scan-future-mtime',
    'scan-system-dir',
    'remote-name',
    'state-corrupt'
)

$unitNames = @(
    'engine-require-construct',
    'scan-empty-dir',
    'scan-zero-byte-skip',
    'scan-future-mtime-not-pending',
    'scan-system-dir-blocked',
    'remote-name-basename',
    'state-corrupt-recovery'
)

if (-not $UploadOnly) {
    Write-Line ''
    Write-Line '--- unit cases (no browser required) ---'
    $unitScript = Join-Path $scriptDir 'test-unit.js'
    for ($i = 0; $i -lt $unitCases.Count; $i++) {
        $caseId = $unitCases[$i]
        $caseName = $unitNames[$i]
        # NOTE: wrap in @() -- PowerShell unrolls a single-element ArrayList on
        # return, which would leave $one as a bare PSCustomObject whose .Count
        # is 0 and the loop below would silently add nothing.
        $one = @(Invoke-NodeCases -ScriptPath $unitScript -ScriptArgs @($caseId) -ExpectedNames @($caseName) -TimeoutSec $UnitTimeoutSec)
        for ($j = 0; $j -lt $one.Count; $j++) {
            [void]$script:results.Add($one[$j])
        }
    }
}

# ------------------------------------------------------------------ upload cases

$uploadNames = @('cdp-9222-endpoint', 'upload-runonce', 'upload-idempotent')

if (-not $UnitOnly) {
    Write-Line ''
    Write-Line '--- real upload cases (require a logged-in Edge on port 9222) ---'
    $uploadScript = Join-Path $scriptDir 'test-upload.js'
    $uploadResults = @(Invoke-NodeCases -ScriptPath $uploadScript -ScriptArgs @() -ExpectedNames $uploadNames -TimeoutSec $UploadTimeoutSec)
    for ($j = 0; $j -lt $uploadResults.Count; $j++) {
        [void]$script:results.Add($uploadResults[$j])
    }
}

# ------------------------------------------------------------------ report

Write-Line ''
Write-Line '--- results ---'

$passCount = 0
$failCount = 0
$skipCount = 0

for ($i = 0; $i -lt $script:results.Count; $i++) {
    $r = $script:results[$i]
    $suffix = ''
    if ($r.Detail) { $suffix = ' - ' + $r.Detail }
    if ($r.Status -eq 'PASS') {
        $passCount++
        Write-Line ('[PASS] ' + $r.Name + $suffix)
    } elseif ($r.Status -eq 'SKIP') {
        $skipCount++
        Write-Line ('[SKIP] ' + $r.Name + $suffix)
    } else {
        $failCount++
        Write-Line ('[FAIL] ' + $r.Name + $suffix)
    }
}

$total = $script:results.Count
Write-Line ''
Write-Line ('TOTAL=' + $total + ' PASS=' + $passCount + ' FAIL=' + $failCount + ' SKIP=' + $skipCount)

if ($skipCount -gt 0) {
    Write-Line ''
    Write-Line ('WARNING: ' + $skipCount + ' case(s) SKIPPED - SKIP is NOT a pass, confirm manually:')
    for ($i = 0; $i -lt $script:results.Count; $i++) {
        $r = $script:results[$i]
        if ($r.Status -eq 'SKIP') { Write-Line ('  - ' + $r.Name + ' : ' + $r.Detail) }
    }
}

if ($failCount -gt 0) {
    Write-Line 'RESULT: FAILED'
    exit 1
}

if ($skipCount -gt 0) {
    Write-Line ('RESULT: OK-MINUS-SKIPS (no failures, but ' + $skipCount + ' case(s) were skipped and are NOT verified)')
    exit 0
}

Write-Line 'RESULT: OK (no failures)'
exit 0
