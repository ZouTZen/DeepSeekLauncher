# setup-deps.ps1 — install runtime dependencies for the DeepSeek Harness launcher.
# Called by the Inno Setup installer AFTER files are extracted, in two stages:
#   -Stage node    : detect node, install LTS (MSI) if missing, ensure WebView2
#                    Runtime. Runs ELEVATED (needs admin for the MSI).
#   -Stage harness : npm install -g @deepseek-ai/dsh. Runs as the ORIGINAL USER
#                    (runasoriginaluser) so the global prefix stays in the real
#                    user's %APPDATA%\npm, where the launcher can find it.
param(
    [ValidateSet('node', 'harness')]
    [string]$Stage = 'node'
)

$ErrorActionPreference = 'Continue'
$NodeLtsMajor = 24  # Krypton LTS line

function Step([string]$msg) { Write-Host "[DeepSeekHarness] $msg" }

function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Test-Node {
    try {
        $v = & node --version 2>$null
        if ($v) { return $v.Trim() }
    } catch { }
    foreach ($root in 'HKLM:\SOFTWARE\Node.js', 'HKCU:\SOFTWARE\Node.js') {
        try {
            $p = (Get-ItemProperty $root -ErrorAction Stop).InstallPath
            if ($p -and (Test-Path (Join-Path $p 'node.exe'))) { return "registry:$p" }
        } catch { }
    }
    return $null
}

function Install-NodeLts {
    Step "node 未检测到,下载并安装 Node.js LTS v$NodeLtsMajor ..."
    try {
        $idx = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json' -TimeoutSec 30
        $lts = $idx | Where-Object { $_.version -like "v$NodeLtsMajor.*" -and $_.lts -ne $false } | Select-Object -First 1
        if (-not $lts) { $lts = $idx | Where-Object { $_.version -like "v$NodeLtsMajor.*" } | Select-Object -First 1 }
        if (-not $lts) { throw "无法解析 Node.js v$NodeLtsMajor 版本" }
        $v = $lts.version
        $msi = Join-Path $env:TEMP "node-$v-x64.msi"
        $url = "https://nodejs.org/dist/$v/node-$v-x64.msi"
        Step "下载 $url"
        Invoke-WebRequest -Uri $url -OutFile $msi -TimeoutSec 300
        Step "静默安装 node (msiexec /qn) ..."
        $proc = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart" -Wait -PassThru
        if ($proc.ExitCode -ne 0) { throw "msiexec 退出码 $($proc.ExitCode)" }
        Refresh-Path
        Step "node 已安装: $((& node --version).Trim())"
    } catch {
        Write-Host "[DeepSeekHarness] 安装 node 失败: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

function Install-WebView2 {
    $bootstrapper = Join-Path $PSScriptRoot 'MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path $bootstrapper)) { Step "WebView2 bootstrapper 缺失,跳过"; return }
    $clients = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($key in $clients) {
        try {
            $pv = (Get-ItemProperty $key -ErrorAction Stop).pv
            if ($pv) { Step "WebView2 Runtime 已安装: $pv"; return }
        } catch { }
    }
    Step "安装 WebView2 Runtime ..."
    $proc = Start-Process $bootstrapper -ArgumentList '/silent /install' -Wait -PassThru
    Step "WebView2 安装器退出码: $($proc.ExitCode)"
}

function Install-Harness {
    Step "npm 全局安装 @deepseek-ai/dsh ..."
    Refresh-Path
    npm install -g @deepseek-ai/dsh
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[DeepSeekHarness] npm install -g 失败(退出码 $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    Step "harness 已安装"
}

# ---- main ----
switch ($Stage) {
    'node' {
        Step "检测 node ..."
        $nodeVersion = Test-Node
        if ($nodeVersion) { Step "node 已存在: $nodeVersion" } else { Install-NodeLts }
        Install-WebView2
    }
    'harness' {
        Install-Harness
    }
}
Step "阶段 $Stage 完成"
exit 0
