param(
  [switch]$SkipInstaller,
  [switch]$RunAfterBuild
)

# build-native.ps1 -- one-click build for the native app + installer
#
# NOTE: this script is pure ASCII on purpose.
#   Windows PowerShell 5.1 reads a BOM-less .ps1 as the system ANSI codepage (GBK on
#   this machine). Any non-ASCII character in the script body (even inside a comment)
#   can be mis-decoded and corrupt following tokens. We hit exactly this: a comment
#   containing Chinese turned the string 'error|Error' into garbage and broke the
#   build. Keep this file ASCII-only; put localized strings in the C# app instead.
#
# Pitfalls encoded here (all previously hit):
#   1. makensis needs the .nsi to be UTF-8 WITH BOM, else "Bad text encoding".
#      Editors drop the BOM, so we re-add it every build.
#   2. csc.exe only supports C# 5; /out and /target must precede source files.
#   3. NSIS "File /r" flattens directories; the install layout uses explicit SetOutPath.
#   4. The install directory is read-only for users; data goes to %APPDATA% (C# side).
#   5. PowerShell reserves $args; param() must be the first statement.

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# Locate makensis. The electron-builder cache path is the one that exists on the
# machine this was developed on, but a fresh clone elsewhere will not have it.
# Try that first, then PATH, then the usual install roots.
function Find-MakeNsis {
  $cands = @(
    "$env:LOCALAPPDATA\electron-builder\Cache\nsis-3.0.4.1\nsis-3.0.4.1-1mpw4\makensis.exe"
  )
  $onPath = Get-Command makensis.exe -ErrorAction SilentlyContinue
  if ($onPath) { $cands += $onPath.Source }
  $cands += @(
    "$env:ProgramFiles\NSIS\makensis.exe",
    "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
  )
  foreach ($c in $cands) {
    if ($c -and (Test-Path $c)) { return $c }
  }
  return $null
}
$MakeNsis = Find-MakeNsis

function Step($n, $msg) { Write-Host ""; Write-Host "[$n] $msg" -ForegroundColor Cyan }

# ---------- 1. compile C# ----------
Step 1 'Compile native C# UI'
$native = Join-Path $Root 'native'
$exe = Join-Path $native 'ThunderPanSync.exe'
# Source list must include every .cs that participates in the product build.
# Note: UiPreview.cs is a dev-only harness and is deliberately NOT listed here
# (it has its own Main(), which would clash with Program.Main).
$srcs = @(
  'Program.cs',
  'MainForm.cs',
  'EngineHost.cs',
  'BrowserManager.cs',
  'Theme.cs',
  'Drawing.cs',
  'Widgets.cs',
  'DarkForm.cs'
) | ForEach-Object { Join-Path $native $_ }
foreach ($s in $srcs) { if (-not (Test-Path $s)) { throw "missing source: $s" } }

# app.manifest: declares DPI awareness and supportedOS.
# Without supportedOS, .NET reports Environment.OSVersion as 6.2 instead of 10.0
# (measured on this machine), which silently disables OS-capability checks.
$manifest = Join-Path $native 'app.manifest'
$manifestArg = @()
if (Test-Path $manifest) { $manifestArg = @("/win32manifest:$manifest") }

$cscArgs = @('/nologo', '/target:winexe', "/out:$exe") + $manifestArg + @(
  '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
  '/r:System.Net.Http.dll',
  '/optimize+') + $srcs
$out = & $Csc @cscArgs 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
  foreach ($ln in ($out -split "`n")) {
    if ($ln -match 'error') { Write-Host ('  ' + $ln.Trim()) -ForegroundColor Red }
  }
  throw 'C# compile failed'
}
Write-Host ('  OK  {0:N0} B' -f (Get-Item $exe).Length) -ForegroundColor Green

# ---------- 1b. smoke test: the exe must actually start ----------
#
# Why this exists (a real regression that cost a debugging round):
#   csc happily embeds a MALFORMED win32 manifest and still exits 0.
#   The exe then fails at process creation with
#   "The application has failed to start because its side-by-side
#    configuration is incorrect."
#   The trigger was as trivial as a double-hyphen inside an XML comment
#   (XML forbids two consecutive '-' inside comments).
#
#   Compile success proves nothing about loadability. So we start the exe,
#   wait briefly, and fail the build if it is already gone. A working app
#   stays alive (it has a window and a tray icon).
if (Test-Path $manifest) {
  Step '1b' 'Smoke test: exe can start'
  $probe = $null
  $err = $null
  try {
    $probe = Start-Process -FilePath $exe -PassThru -ErrorAction Stop
  } catch {
    $err = $_.Exception.Message
  }
  if ($err -ne $null) {
    Write-Host ('  FAILED to start: ' + $err) -ForegroundColor Red
    Write-Host '  Hint: the embedded manifest is invalid. Run this to see why:' -ForegroundColor Yellow
    Write-Host '    try { [xml](Get-Content native\app.manifest -Raw) } catch { $_.Exception.Message }' -ForegroundColor Yellow
    Write-Host '  XML comments must not contain two consecutive hyphens.' -ForegroundColor Yellow
    throw 'exe failed the load smoke test'
  }
  Start-Sleep -Milliseconds 2500
  $still = Get-Process -Id $probe.Id -ErrorAction SilentlyContinue
  if ($still -eq $null) {
    Write-Host '  FAILED: exe exited immediately after start' -ForegroundColor Red
    throw 'exe failed the load smoke test'
  }
  try { Stop-Process -Id $probe.Id -Force } catch { }
  Write-Host '  OK  exe starts and stays alive' -ForegroundColor Green
}

# ---------- 2. assemble publish dir ----------
Step 2 'Assemble publish directory'
$pub = Join-Path $native 'publish'

Get-Process -Name 'ThunderPanSync' -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Host ('  stopping running instance pid={0}' -f $_.Id) -ForegroundColor Yellow
  try { $_.Kill(); $_.WaitForExit(3000) } catch { }
}
Start-Sleep -Milliseconds 500

if (Test-Path $pub) {
  try { Remove-Item $pub -Recurse -Force }
  catch {
    $stale = "$pub.stale-$(Get-Date -Format 'HHmmss')"
    Write-Host ('  locked, renaming to ' + (Split-Path $stale -Leaf)) -ForegroundColor Yellow
    Move-Item $pub $stale -ErrorAction SilentlyContinue
  }
}
New-Item -ItemType Directory $pub -Force | Out-Null
New-Item -ItemType Directory (Join-Path $pub 'runtime') -Force | Out-Null
New-Item -ItemType Directory (Join-Path $pub 'engine\engine') -Force | Out-Null

Copy-Item $exe $pub
$nodeExe = (Get-Command node).Source
if (-not (Test-Path $nodeExe)) { throw 'node.exe not found' }
Copy-Item $nodeExe (Join-Path $pub 'runtime\node.exe')
Copy-Item (Join-Path $Root 'host.js') (Join-Path $pub 'engine')
Copy-Item (Join-Path $Root 'paths.js') (Join-Path $pub 'engine')
Copy-Item (Join-Path $Root 'engine\engine.js') (Join-Path $pub 'engine\engine')
Copy-Item (Join-Path $Root 'engine\cdp.js') (Join-Path $pub 'engine\engine')

$sum = (Get-ChildItem $pub -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ('  OK  {0:N1} MB' -f ($sum / 1MB)) -ForegroundColor Green

# ---------- 3. build installer ----------
if (-not $SkipInstaller) {
  Step 3 'Build NSIS installer'
  if (-not $MakeNsis) {
    throw ("makensis not found. Install NSIS 3.x and either add it to PATH " +
      "or place it at $env:ProgramFiles\NSIS\makensis.exe. " +
      "Alternatively run: .\build-native.ps1 -SkipInstaller (builds the exe and publish dir only).")
  }
  Write-Host "  makensis: $MakeNsis"

  # makensis refuses BOM-less non-ASCII files with "Bad text encoding".
  # Two files need this: installer.nsi itself, and EULA.txt, which is pulled in
  # by "!insertmacro MUI_PAGE_LICENSE" and also shipped via "File EULA.txt".
  # Editors silently drop the BOM, so both are checked (and fixed) every build.
  foreach ($textFile in @('installer.nsi', 'EULA.txt')) {
    $p = Join-Path $Root $textFile
    if (-not (Test-Path $p)) { throw "missing text file: $p" }
    $bytes = [IO.File]::ReadAllBytes($p)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if (-not $hasBom) {
      Write-Host ('  adding UTF-8 BOM to ' + $textFile) -ForegroundColor Yellow
      $text = [IO.File]::ReadAllText($p, (New-Object Text.UTF8Encoding $false))
      [IO.File]::WriteAllText($p, $text, (New-Object Text.UTF8Encoding $true))
    }
  }

  New-Item -ItemType Directory (Join-Path $Root 'release') -Force | Out-Null
  Push-Location $Root
  try {
    $nout = & $MakeNsis /V2 installer.nsi 2>&1 | Out-String
    $code = $LASTEXITCODE
  } finally { Pop-Location }

  if ($code -ne 0) {
    foreach ($ln in ($nout -split "`n")) {
      if ($ln -match 'rror') { Write-Host ('  ' + $ln.Trim()) -ForegroundColor Red }
    }
    throw 'NSIS compile failed'
  }

  $setup = Join-Path $Root 'release\ThunderPanSync-Setup-1.0.0.exe'
  Write-Host ('  OK  {0:N1} MB' -f ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
}

# ---------- 4. summary ----------
Step 4 'Artifacts'
Write-Host ('  native exe    {0,12:N0} B' -f (Get-Item $exe).Length)
$setup = Join-Path $Root 'release\ThunderPanSync-Setup-1.0.0.exe'
if (Test-Path $setup) {
  Write-Host ('  installer     {0,12:N1} MB' -f ((Get-Item $setup).Length / 1MB))
}
Write-Host '  (electron build was 239 MB exe / 118.9 MB installer)'

if ($RunAfterBuild) {
  Step 5 'Launch'
  Start-Process (Join-Path $pub 'ThunderPanSync.exe')
  Write-Host '  started'
}
