#!/usr/bin/env node
/**
 * check-page-js.mjs —— 对 web/index.html 做"没有浏览器也能做"的静态检查。
 *
 * 能做：抽出内联 <script>，用 vm 编译（只编译不执行）验证语法；检查关键 DOM id 是否齐、
 *       标签是否大致平衡、有没有把 </script> 写进字符串这类低级事故。
 * 不能做：**渲染效果**。布局好不好看、550C 片头动起来什么样，只能人眼看。
 *        这台机器上没有可用的无头浏览器（见 550c-boot-ports/docs/踩过的坑.md 第 15 条）。
 *
 * 用法: node check-page-js.mjs <index.html 路径>
 */
import { readFileSync } from 'node:fs'
import vm from 'node:vm'

const file = process.argv[2]
if (!file) {
  console.error('用法: node check-page-js.mjs <index.html>')
  process.exit(2)
}
const html = readFileSync(file, 'utf8')
let fail = 0
const ok = (label, cond, extra) => {
  if (!cond) fail++
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${label}${extra ? '  ' + extra : ''}`)
}

console.log(`文件 ${file}（${html.length} 字节）\n`)

// ---- 1. 内联脚本语法 ----
const scripts = [...html.matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi)].map((m) => m[1])
ok('抽到内联 <script> 块', scripts.length > 0, `${scripts.length} 块`)
scripts.forEach((code, i) => {
  try {
    new vm.Script(code, { filename: `inline-${i + 1}.js` })   // 只编译，不执行
    ok(`第 ${i + 1} 块内联 JS 语法正确`, true, `${code.length} 字节`)
  } catch (e) {
    ok(`第 ${i + 1} 块内联 JS 语法正确`, false, e.message)
  }
})

// ---- 2. 关键 DOM 节点 ----
const ids = [
  // 顶栏
  'liveDot', 'srcTag', 'hostTag', 'clock', 'btnDiag', 'diag', 'diagList',
  // 顶部 4 个 KPI
  'kpiCpu', 'kpiGpu', 'kpiMem', 'kpiVram',
  // 两个大折线
  'chartCpu', 'chartGpu', 'cpuCur', 'gpuCur',
  // 底部横排
  'bottomRow', 'cardProc', 'procList', 'cardDiskNet', 'dnGrid', 'cardSys', 'sysList',
  // 右列详情
  'cardCpuDetail', 'cpuDetailNum', 'cpuDetailMetrics', 'cpuDetailRows',
  'cardGpuDetail', 'gpuDetailNum', 'gpuDetailMetrics', 'gpuDetailRows',
  'cardMemDetail', 'memDetailNum', 'memBar', 'memDetailRows',
]
for (const id of ids) ok(`页面里有 #${id}`, html.includes(`id="${id}"`))

// ---- 3. 脚本引用的 id 必须在页面里存在（防拼写错导致运行时报 null） ----
const referenced = new Set()
for (const m of html.matchAll(/\$\('([^']+)'\)/g)) referenced.add(m[1])
for (const m of html.matchAll(/setText\('([^']+)'/g)) referenced.add(m[1])
for (const m of html.matchAll(/getElementById\('([^']+)'\)/g)) referenced.add(m[1])
const missing = [...referenced].filter((id) => !html.includes(`id="${id}"`))
ok('脚本引用的 id 都在页面里', missing.length === 0, missing.length ? missing.join(', ') : `${referenced.size} 个引用`)

// ---- 4. 结构 ----
ok('声明 UTF-8', /<meta\s+charset="utf-8"/i.test(html))
ok('有 CSP 友好的内联写法（无外部 CDN 请求）', !/src="https?:\/\//i.test(html))
ok('div 开合数量平衡',
  (html.match(/<div\b/gi) || []).length === (html.match(/<\/div>/gi) || []).length,
  `${(html.match(/<div\b/gi) || []).length} 开 / ${(html.match(/<\/div>/gi) || []).length} 闭`)
ok('style 开合数量平衡',
  (html.match(/<style\b/gi) || []).length === (html.match(/<\/style>/gi) || []).length)
ok('script 开合数量平衡',
  (html.match(/<script\b/gi) || []).length === (html.match(/<\/script>/gi) || []).length)

// ---- 5. 轮询地址与片头地址 ----
ok('轮询 /api/state', html.includes("fetch('/api/state'"))
ok('片头三件套按 /boot-550c.* 取', html.includes("'/boot-550c.css'") && html.includes("'/boot-550c.js'"))
ok('片头可关闭（?boot=0）', html.includes('boot=0'))
ok('片头加载失败有兜底（catch 里什么都不做）', /\.catch\(function \(\) \{ \/\* 片头加载失败/.test(html))

// ---- 6. 主题与历史折线 ----
ok('只保留一套皮肤（页面上没有皮肤切换入口）',
  !html.includes('themeSel') && !html.includes('data-theme'),
  '按用户要求：只要面板这一套，终端/浅色已剔除')
ok('有历史折线绘制函数', html.includes('function drawChart('))

console.log(`\n${fail === 0 ? '全部通过' : fail + ' 项失败'}`)
console.log('注意：以上只证明"语法与结构没问题"，**不证明渲染效果**。页面好不好看需要人眼看。')
process.exit(fail ? 1 : 0)
