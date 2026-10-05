# patch-sidebar.ps1 —— 把 DSH 的侧边栏改成"启动时默认折叠"
#
# 请双击同目录的「修补.bat」来运行，不要直接右键运行本文件
# （PowerShell 默认执行策略会挡住 .ps1）。
#
# 它会做这些事：
#   1. 检查 DSH 是否在运行；在运行就提示并（经你确认后）自动关闭它
#   2. 把 app.asar 里那个布局初始值 sidebar: 280 改成 0，让侧边栏默认折叠
#   3. 同时重算并回写该文件的 SHA256（asar 有完整性记录，不改 hash 可能起不来）
#   4. 备份原文件、逐项自检，任何一步不对就自动还原
#
# 想还原：跑一次同目录的「还原.bat」，或者手工执行
#   node patch-asar-sidebar2.mjs --revert "D:\DeepSeekHarness-0.2.0-rc.2\resources\app.asar"

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$HERE   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ASAR   = 'D:\DeepSeekHarness-0.2.0-rc.2\resources\app.asar'
$NODE   = 'D:\dsh-home\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe'
$PATCH  = Join-Path $HERE 'patch-asar-sidebar2.mjs'

Write-Host ''
Write-Host '============================================' -ForegroundColor Cyan
Write-Host '  把 DSH 侧边栏改成默认折叠' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host ''

# ---------- 0. 前置检查 ----------
if (-not (Test-Path $ASAR)) {
  Write-Host "[错误] 找不到 app.asar：" -ForegroundColor Red
  Write-Host "       $ASAR"
  Write-Host '       DSH 装在别的位置的话，请把这个路径改成实际位置。'
  return
}
if (-not (Test-Path $NODE)) {
  Write-Host "[错误] 找不到 Node（DSH 自带的那份）：" -ForegroundColor Red
  Write-Host "       $NODE"
  return
}
if (-not (Test-Path $PATCH)) {
  Write-Host "[错误] 找不到补丁脚本 patch-asar-sidebar2.mjs（应和本文件在同一目录）" -ForegroundColor Red
  return
}

# ---------- 1. 处理正在运行的 DSH ----------
$procs = @(Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
  Write-Host "[注意] DSH 正在运行（$($procs.Count) 个进程），文件被占用，无法替换。" -ForegroundColor Yellow
  Write-Host ''
  Write-Host '       修补必须先把 DSH 完全关掉。'
  Write-Host '       你的会话是自动保存的，重开 DSH 后可以继续。'
  Write-Host ''
  $ans = Read-Host '       现在就自动关闭 DSH 并继续吗？(Y/N)'
  if ($ans -notmatch '^[Yy]') {
    Write-Host ''
    Write-Host '       已取消。请手动退出 DSH 后重新运行本脚本。' -ForegroundColor Yellow
    return
  }
  Write-Host ''
  Write-Host '       正在关闭 DSH …' -ForegroundColor Yellow
  foreach ($p in $procs) { try { $p.CloseMainWindow() | Out-Null } catch { } }
  Start-Sleep -Seconds 3
  # 还没退出就强杀
  $left = @(Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue)
  if ($left.Count -gt 0) {
    Write-Host '       仍在运行，强制结束 …' -ForegroundColor Yellow
    foreach ($p in $left) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }
  }
  for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 500
  }
  Start-Sleep -Seconds 2
  if (Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue) {
    Write-Host '[错误] DSH 没能关闭，请手动退出后重试。' -ForegroundColor Red
    return
  }
  Write-Host '       DSH 已关闭。' -ForegroundColor Green
  Write-Host ''
}

# ---------- 2. 先只检查，不动手 ----------
Write-Host '--- 检查（不改任何字节） ---' -ForegroundColor Gray
& $NODE $PATCH --check $ASAR
if ($LASTEXITCODE -ne 0) {
  Write-Host ''
  Write-Host '[中止] 检查未通过，为避免改坏文件，本次不做任何修改。' -ForegroundColor Red
  return
}

# ---------- 3. 正式打补丁 ----------
Write-Host ''
Write-Host '--- 打补丁（会先备份） ---' -ForegroundColor Gray
& $NODE $PATCH --apply $ASAR
$code = $LASTEXITCODE

Write-Host ''
if ($code -eq 0) {
  Write-Host '============================================' -ForegroundColor Green
  Write-Host '  修补完成' -ForegroundColor Green
  Write-Host '============================================' -ForegroundColor Green
  Write-Host ''
  Write-Host '  现在可以重新启动 DSH 了 —— 侧边栏会默认收起。'
  Write-Host '  需要时按 Ctrl+B 随时展开/收起。'
  Write-Host ''
  Write-Host "  备份在：$ASAR.bak-before-sidebar-fold" -ForegroundColor DarkGray
  Write-Host '  想还原就跑「还原.bat」。' -ForegroundColor DarkGray
} else {
  Write-Host '============================================' -ForegroundColor Red
  Write-Host '  修补失败（脚本已自动还原原文件）' -ForegroundColor Red
  Write-Host '============================================' -ForegroundColor Red
  Write-Host ''
  Write-Host '  上面的报错信息发我，我来判断原因。'
}
