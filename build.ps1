# build.ps1 — compile the WebView2 desktop launcher to a 64-bit exe
# using only the Windows built-in .NET Framework compiler (csc.exe).
#
# Produces: dist\<OutputName> plus the WebView2
# assemblies and the win-x64 native loader that must sit beside it.
#
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-OutputName DshDesktop.exe]

param(
    [string]$OutputName = 'DshDesktop.exe'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\DshDesktopLauncher.cs'
$dist = Join-Path $root 'dist'
$lib  = Join-Path $root 'lib\core-extract\out'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

if (-not (Test-Path $csc))  { throw "csc.exe not found at $csc" }
if (-not (Test-Path $src))  { throw "source not found at $src" }
if (-not (Test-Path $lib))  { throw "extracted WebView2 package not found at $lib" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$refs = @(
    (Join-Path $fw 'System.dll'),
    (Join-Path $fw 'System.Core.dll'),
    (Join-Path $fw 'System.Windows.Forms.dll'),
    (Join-Path $fw 'System.Drawing.dll'),
    (Join-Path $lib 'lib\net462\Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $lib 'lib\net462\Microsoft.Web.WebView2.WinForms.dll')
)

$refArgs = ($refs | ForEach-Object { "/r:`"$_`"" }) -join ' '

$out = Join-Path $dist $OutputName

$manifest = Join-Path $root 'src\DshDesktop.manifest'
$icon = Join-Path $root 'src\assets\deepseek-whale.ico'

$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', "/out:$out")
$cscArgs += $refs | ForEach-Object { "/r:$_" }
$cscArgs += "/win32manifest:$manifest"
# Give the exe FILE its icon (what Explorer/desktop shows).
$cscArgs += "/win32icon:$icon"
# Embed the whale icon as a managed resource under a stable name; the launcher
# reads it at runtime for the title-bar and taskbar icons.
$cscArgs += "/resource:$icon,DshDesktop.deepseek-whale.ico"
$cscArgs += $src

Write-Host "Compiling launcher -> $OutputName ..."
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $out)) { throw 'csc produced no exe' }

Write-Host "Assembling runtime files..."
# A running launcher locks the destination DLLs, so skip a file that already
# exists (its content comes from the same SDK version and cannot change);
# only a MISSING file is a real error. This keeps `build.ps1` idempotent when
# rebuilt while the launcher is open.
$runtimeFiles = @(
    @{ src = (Join-Path $lib 'lib\net462\Microsoft.Web.WebView2.Core.dll');     dst = (Join-Path $dist 'Microsoft.Web.WebView2.Core.dll') },
    @{ src = (Join-Path $lib 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'); dst = (Join-Path $dist 'Microsoft.Web.WebView2.WinForms.dll') },
    @{ src = (Join-Path $lib 'runtimes\win-x64\native\WebView2Loader.dll');     dst = (Join-Path $dist 'WebView2Loader.dll') }
)
foreach ($f in $runtimeFiles) {
    if (Test-Path $f.dst) {
        Write-Host "  (skip) $([IO.Path]::GetFileName($f.dst)) already present"
        continue
    }
    Copy-Item $f.src $f.dst -Force
}

Write-Host ""
Write-Host "Built: $out"
Get-ChildItem $dist | Select-Object Name, Length
