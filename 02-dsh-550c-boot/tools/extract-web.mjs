#!/usr/bin/env node
/**
 * extract-web.mjs —— 从 GameHud 的 web/monitor.html 中抽取 550C 开机动画「三件套」。
 *
 * 为什么要用脚本切、不手抄：
 *   logo 是 4 个字形、7 条 <path>，光 d 属性就 31 KB。手抄必错，脚本可复现。
 *
 * 用法：
 *   node tools/extract-web.mjs <monitor.html 路径> <输出目录>
 *
 * 产出（写进输出目录）：
 *   boot-550c.css    动画样式（含注释块）
 *   boot-550c.html   DOM 片段：动画层 #c550Boot + 背光层 #c550Bg
 *   boot-550c.js     播放/收尾逻辑：playBootAnim / finishBootAnim / 保险丝
 *   demo.html        单文件演示页（把上面三段内联进去，双击即看）
 */
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

const [srcPath, outDirRaw] = process.argv.slice(2)
if (!srcPath || !outDirRaw) {
  console.error('用法: node tools/extract-web.mjs <monitor.html> <输出目录>')
  process.exit(2)
}
const outDir = resolve(outDirRaw)
const html = readFileSync(srcPath, 'utf8')

/** 字节偏移 → 行号（1 起），只用于报告溯源位置 */
const lineOf = (idx) => html.slice(0, idx).split('\n').length

function cut(startAnchor, endAnchor, label) {
  const s = html.indexOf(startAnchor)
  if (s < 0) throw new Error(`[${label}] 找不到起始锚点：${startAnchor.slice(0, 40)}…`)
  const e = html.indexOf(endAnchor, s + startAnchor.length)
  if (e < 0) throw new Error(`[${label}] 找不到结束锚点：${endAnchor.slice(0, 40)}…`)
  return {
    text: html.slice(s, e).replace(/[ \t]+$/, '').replace(/\s*$/, '\n'),
    lines: `${lineOf(s)}-${lineOf(e) - 1}`,
  }
}

const css = cut(
  '  /* ---------- 开机动画：550C logo 逐笔书写 ----------',
  '  /* ---------- 底部“进程”卡 ----------',
  'CSS',
)
const markup = cut(
  '<!-- ============ 开机动画：550C logo 逐笔书写 ============',
  '<!-- ============ 帧率报告视图',
  'markup',
)
const js = cut(
  '/* ============================================================ 开机动画',
  '/* 任何未捕获的脚本错误都直接写在顶部状态标记上',
  'js',
)

/* ---------------- 抽取结果的硬性断言：切歪了就停下，不要产出坏包 ---------------- */
const pathCount = (markup.text.match(/<path class="c550-[wr]"/g) || []).length
const glyphs = [...markup.text.matchAll(/<g id="(c550[A-Za-z0-9]+)"/g)]
  .map((m) => m[1])
  .filter((id) => id !== 'c550Logo')
const cssVars = [...new Set([...css.text.matchAll(/var\((--[\w-]+)/g)].map((m) => m[1]))]
const textLine = (markup.text.match(/id="c550Text"[^>]*>([^<]*)</) || [])[1] || ''

const checks = [
  ['CSS 抓到 #c550Boot 规则', css.text.includes('#c550Boot{')],
  ['CSS 抓到水印/收尾态 .c550-toWater', css.text.includes('.c550-toWater')],
  ['CSS 抓到独立背光层 #c550Bg', css.text.includes('#c550Bg{')],
  ['CSS 抓到逐笔书写依赖的 clip-path 相关类 .c550-w/.c550-r', css.text.includes('.c550-w{') && css.text.includes('.c550-r{')],
  ['markup 是单个 800×230 的 SVG', markup.text.includes('<svg viewBox="0 0 800 230"')],
  ['markup 含 #c550Logo 分组', markup.text.includes('id="c550Logo"')],
  ['markup 含背光层 <use> 复用', markup.text.includes('<use href="#c550Logo"')],
  ['markup 含底部状态行', textLine.length > 0],
  ['markup 的 deshake 滤镜在（filter=url(#c550Despike)）', markup.text.includes('filter="url(#c550Despike)"')],
  ['JS 抓到 playBootAnim', js.text.includes('function playBootAnim(')],
  ['JS 抓到 finishBootAnim', js.text.includes('function finishBootAnim(')],
  ['JS 抓到 6 秒保险丝', js.text.includes('}, 6000)')],
  ['logo 路径数 ≥ 6', pathCount >= 6],
  ['字形分组为 four groups', glyphs.length === 4],
]
const failed = checks.filter(([, ok]) => !ok)

console.log(`源文件: ${resolve(srcPath)}`)
console.log(`CSS    : 第 ${css.lines} 行`)
console.log(`markup : 第 ${markup.lines} 行`)
console.log(`JS     : 第 ${js.lines} 行`)
console.log(`logo   : ${pathCount} 条路径，分组 ${glyphs.join(' / ')}`)
console.log(`状态行 : "${textLine}"`)
console.log(`CSS 依赖的自定义属性: ${cssVars.length ? cssVars.join(', ') : '（无）'}`)
console.log('--- 断言 ---')
for (const [label, ok] of checks) console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}`)
if (failed.length) {
  console.error(`\n抽取失败：${failed.length} 条断言未通过，未写出任何文件。`)
  process.exit(1)
}

/* ---------------- 写文件 ---------------- */
mkdirSync(outDir, { recursive: true })

const header = (what, lines) =>
  `/* 550C 开机动画 · ${what}\n` +
  ` * 摘自 GameHud 的 web/monitor.html 第 ${lines} 行（原样摘出，未改写）。\n` +
  ` * 动画与 SVG 原稿：Voidpoket；插件工程与移植：Ziyang Song (dsh-550c-boot, MIT)。\n` +
  ` * 本文件由 tools/extract-web.mjs 生成，请勿手工编辑；改动画请改源文件后重跑脚本。\n` +
  ` */\n`

writeFileSync(join(outDir, 'boot-550c.css'), header('样式', css.lines) + css.text, 'utf8')
writeFileSync(join(outDir, 'boot-550c.html'), `<!-- 550C 开机动画 · DOM 片段\n     摘自 GameHud 的 web/monitor.html 第 ${markup.lines} 行（原样摘出，未改写）。\n     依赖 boot-550c.css 与 boot-550c.js；本文件由 tools/extract-web.mjs 生成。 -->\n` + markup.text, 'utf8')
writeFileSync(join(outDir, 'boot-550c.js'), header('播放逻辑', js.lines) + js.text, 'utf8')

/* ---------------- demo.html：单文件、零外部请求 ---------------- */
const varDefs = cssVars.map((v) => `      ${v}:ui-monospace,SFMono-Regular,Menlo,Consolas,"Courier New",monospace;`).join('\n')
const cards = [
  ['CPU', '占用 38 % · 功耗 122 W'],
  ['GPU', '占用 71 % · 显存 6.4 GB'],
  ['内存', '31.2 / 64 GB'],
  ['帧率', '187 fps · 1% low 142'],
  ['磁盘', '读 412 MB/s · 写 88 MB/s'],
  ['网络', '下行 3.1 MB/s · 上行 0.4 MB/s'],
]
  .map(([t, v]) => `    <div class="fake-card"><b>${t}</b><div>${v}</div></div>`)
  .join('\n')

const demo = `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>550C 开机动画 · 独立演示（移植自 dsh-550c-boot）</title>
<style>
  /* 演示页自己的骨架：只补动画 CSS 依赖的自定义属性，再铺一层"假面板"。
     这一段与 550C 动画无关，正式移植时请删掉。 */
  :root{
${varDefs}
  }
  html,body{margin:0;height:100%;background:#0b0b0c;color:#d8d8d8;
    font:13px/1.6 system-ui,"Segoe UI","Microsoft YaHei",sans-serif}
  .fake-app{position:absolute;inset:0;z-index:1;display:grid;
    grid-template-columns:repeat(3,1fr);grid-auto-rows:1fr;gap:14px;padding:76px 22px 22px}
  .fake-card{background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.08);
    border-radius:14px;padding:14px 16px;backdrop-filter:blur(6px)}
  .fake-card b{display:block;font-size:12px;color:#9a9a9a;font-weight:500;margin-bottom:6px}
  .toolbar{position:fixed;left:0;right:0;top:0;z-index:500;display:flex;align-items:center;gap:10px;
    padding:9px 14px;background:rgba(12,12,13,.82);border-bottom:1px solid rgba(255,255,255,.08);
    backdrop-filter:blur(8px)}
  .toolbar b{color:#fff;font-weight:600}
  .toolbar span{color:#8a8a8a;font-size:12px}
  .toolbar button{margin-left:auto;background:transparent;border:1px solid rgba(255,255,255,.18);
    color:#e6e6e6;border-radius:8px;padding:5px 14px;font:inherit;font-size:12.5px;cursor:pointer}
  .toolbar button:hover{background:rgba(255,255,255,.08)}
</style>
<style>
  /* ===== 以下整段来自 boot-550c.css（monitor.html 第 ${css.lines} 行原样摘出） ===== */
${css.text}</style>
</head>
<body>

<div class="toolbar">
  <b>550C 开机动画 · 独立演示</b>
  <span>点画面或按 Esc 跳过；卡片用了毛玻璃，可看出背光水印被柔化的效果</span>
  <button id="replay" title="重新加载本页，再播一遍">重播</button>
</div>

<div class="fake-app">
${cards}
</div>

<!-- ===== 以下整段来自 boot-550c.html（monitor.html 第 ${markup.lines} 行原样摘出） ===== -->
${markup.text}
<script>
/* 片段依赖一个取元素的帮手：GameHud 主体里它叫 $，这里补上。 */
function $(id) { return document.getElementById(id) }
</script>
<script>
/* ===== 以下整段来自 boot-550c.js（monitor.html 第 ${js.lines} 行原样摘出） ===== */
${js.text}</script>
<script>
/* 演示引导：面板加载后自动播一次（等价于 GameHud config.txt 里 bootanim=1）。
   重播走整页刷新 —— 片段里那个 6 秒保险丝是一次性的加载期定时器，
   原地重播有可能被它打断，刷新最稳，也最不容易骗人。 */
window.addEventListener('load', function () { playBootAnim(1) })
document.getElementById('replay').addEventListener('click', function () { location.reload() })
</script>
</body>
</html>
`
writeFileSync(join(outDir, 'demo.html'), demo, 'utf8')

console.log('\n已写出：')
for (const f of ['boot-550c.css', 'boot-550c.html', 'boot-550c.js', 'demo.html']) {
  const size = readFileSync(join(outDir, f)).length
  console.log(`  ${f.padEnd(16)} ${size} 字节`)
}
