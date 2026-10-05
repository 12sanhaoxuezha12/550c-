# shot.ps1 —— 用无头 Edge 给面板截图（自己核对界面用）
#
# 两个必须知道的坑（本机实测踩过）：
#   1. --user-data-dir / --screenshot 的路径**必须自己加引号**。
#      Start-Process -ArgumentList 不会替含空格的路径加引号，
#      "D:\AI WORK\..." 会被拆成两个参数，浏览器于是报
#      "Multiple targets are not supported in headless mode"。
#   2. DSH 的 workspace-write 沙箱会拒绝 Edge 给自身缓存目录做 ACL 授权
#      （stderr 里是 "Failed to grant sandbox access ... (0x5) 拒绝访问"），
#      浏览器能起但渲染不出来。需要在放宽文件权限的情况下运行本脚本。
#
# 用法：
#   Set-ExecutionPolicy -Scope Process Bypass -Force      # PowerShell 默认会挡 .ps1
#   .\shot.ps1 -Url 'http://127.0.0.1:8766/?boot=0' -Out 'D:\AI WORK\shot.png'
#
# 提示：加 ?boot=0 可跳过 550C 片头，直接截数据面板。

param(
  [string]$Url = 'http://127.0.0.1:8766/?boot=0',
  [string]$Out = "$env:TEMP\syspanel-shot.png",
  [int]$Budget = 15000,
  [int]$Width = 1600,
  [int]$Height = 900,
  [int]$TimeoutSec = 90
)

$ErrorActionPreference = 'Stop'

$browser = $null
foreach ($c in @(
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
    "$env:LOCALAPPDATA\Microsoft\Edge\Application\msedge.exe",
    'C:\Program Files\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe')) {
  if (Test-Path $c) { $browser = $c; break }
}
if (-not $browser) { Write-Error '没找到 Edge/Chrome'; exit 1 }

$outFull = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force -Path (Split-Path $outFull) | Out-Null
$udd = Join-Path ([IO.Path]::GetTempPath()) ('sp-shot-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Remove-Item $outFull -ErrorAction SilentlyContinue

Write-Host "浏览器 : $browser"
Write-Host "目标   : $Url"
Write-Host "输出   : $outFull  (${Width}x${Height}, virtual-time-budget=${Budget}ms)"

# 引号是关键，见文件头说明
$argList = @(
  '--headless=new', '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage',
  '--hide-scrollbars', '--force-device-scale-factor=1',
  "--user-data-dir=`"$udd`"",
  "--screenshot=`"$outFull`"",
  "--window-size=$Width,$Height",
  "--virtual-time-budget=$Budget",
  "`"$Url`""
)

$p = Start-Process -FilePath $browser -ArgumentList $argList -PassThru -WindowStyle Hidden `
     -RedirectStandardOutput (Join-Path $udd 'out.txt') -RedirectStandardError (Join-Path $udd 'err.txt')

# 一定要有超时：没有超时保护的话，卡住的 headless 会空转烧 CPU（这台机器上烧过 1002 秒）
$done = $p.WaitForExit($TimeoutSec * 1000)
if (-not $done) {
  Write-Warning "$TimeoutSec 秒未退出，强制结束"
  try { $p.Kill() } catch { }
  Start-Sleep -Seconds 2
}

Remove-Item $udd -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $outFull) {
  $f = Get-Item $outFull
  Write-Host "OK  $($f.Length) 字节  $($f.FullName)" -ForegroundColor Green
} else {
  Write-Warning '没有生成截图。常见原因：①沙箱拒绝了 Edge 的目录授权（放宽权限再试）②--headless=new 不被支持（换 --headless）'
  exit 1
}
