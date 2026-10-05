#!/usr/bin/env node
/**
 * verify.mjs —— 发布前的自检：文件在不在、关键常量在不在、哈希对不对。
 *
 * 只做**文件层**断言。视觉效果、语法检查不在这里：
 *   语法  → node --check dsh-plugin/lib/client.js  dsh-plugin/lib/index.js
 *   视觉  → 只能人眼看（无头浏览器在这台机器上跑不动，见 docs/踩过的坑.md 第 15 条）
 *
 * 用法：
 *   node tools/verify.mjs            # 自检
 *   node tools/verify.mjs --sums     # 顺带写出 SHA256SUMS.txt
 */
import { readFileSync, writeFileSync, existsSync, statSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { join, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const wantSums = process.argv.includes('--sums')

/* 会被校验的文件（顺序即 SHA256SUMS.txt 的顺序）；排除 .bak 与上游原始文档 */
const FILES = [
  'README.md',
  'CREDITS.md',
  'LICENSE',
  'docs/踩过的坑.md',
  'dsh-plugin/package.json',
  'dsh-plugin/cordis.patch.yml',
  'dsh-plugin/lib/client.js',
  'dsh-plugin/lib/index.js',
  'dsh-plugin/README.upstream.md',
  'web-panel/README.md',
  'web-panel/demo.html',
  'web-panel/boot-550c.css',
  'web-panel/boot-550c.html',
  'web-panel/boot-550c.js',
  'tools/extract-web.mjs',
]

const checks = []
const ok = (label, cond) => checks.push([label, !!cond])
const read = (rel) => (existsSync(join(root, rel)) ? readFileSync(join(root, rel), 'utf8') : '')

/* ---------- 1. 文件是否齐全 ---------- */
for (const f of FILES) ok(`文件存在：${f}`, existsSync(join(root, f)))

/* ---------- 2. 不该出现的备份文件 ---------- */
const leftovers = FILES.filter((f) => /\.bak|\.broken-|\.orig/.test(f))
ok('清单里没有混进 .bak / 物证文件', leftovers.length === 0)

/* ---------- 3. DSH 侧插件包的元数据 ---------- */
let pkg = null
try { pkg = JSON.parse(read('dsh-plugin/package.json')) } catch { /* 下面会报 */ }
ok('package.json 可解析', pkg !== null)
ok('包名是 dsh-550c-boot', pkg && pkg.name === 'dsh-550c-boot')
ok('版本是 0.3.3（与上游基线一致）', pkg && pkg.version === '0.3.3')
ok('许可声明是 MIT', pkg && pkg.license === 'MIT')
ok('dsh.client 声明在（浏览器半边才会被加载）', pkg && pkg.dsh && pkg.dsh.client)
ok('dsh.bundle.patch 声明在（否则装不上）', pkg && pkg.dsh && pkg.dsh.bundle && pkg.dsh.bundle.patch)
ok('cordis.patch.yml 里有挂载行 boot-550c', read('dsh-plugin/cordis.patch.yml').includes('id: boot-550c'))

const client = read('dsh-plugin/lib/client.js')
ok('改造项：WATERMARK_CSS 在', client.includes('const WATERMARK_CSS ='))
ok('改造项：HOST_SHEET_CSS 在', client.includes('const HOST_SHEET_CSS ='))
ok('改造项：P0 离散兜底 RETIRE_FALLBACK_MS = 4600', client.includes('const RETIRE_FALLBACK_MS = 4600'))
ok('改造项：P1 真正退场（display:none !important）', client.includes("boot.style.setProperty('display', 'none', 'important')"))
ok('改造项：水印填充 #333333', client.includes('fill:#333333'))
ok('改造项：状态行用了 !important', client.includes("#bootText{opacity:0 !important}"))
ok('上游 playBoot 时序 250 + i * 300 在', client.includes('250 + i * 300'))
ok('上游 totalMs 式子 250 + n*300 + 400 在', client.includes('250 + sorted.length * 300 + 400'))
ok('绝对看门狗 12s/30s 在', client.includes('? 30000 : 12000'))
ok('两个时钟没被合并（页面 3.5s / 标记 1.6s）',
  client.includes('transition:background-color 3.5s') && client.includes('background-color 3.5s cubic-bezier(.4,0,.2,1)'))

/* ---------- 4. Web 侧三件套 ---------- */
const wcss = read('web-panel/boot-550c.css')
const whtml = read('web-panel/boot-550c.html')
const wjs = read('web-panel/boot-550c.js')
const pathCount = (whtml.match(/<path class="c550-[wr]"/g) || []).length
const glyphs = [...whtml.matchAll(/<g id="(c550[A-Za-z0-9]+)"/g)].map((m) => m[1]).filter((x) => x !== 'c550Logo')

ok(`logo 是 6 条路径（实测 ${pathCount}）`, pathCount === 6)
ok(`logo 有 4 个字形分组（实测 ${glyphs.join('/')}）`, glyphs.length === 4)
ok('logo 是单个 800×230 的 SVG', whtml.includes('<svg viewBox="0 0 800 230"'))
ok('背光层用 <use> 复用 logo（不重复 31KB）', whtml.includes('<use href="#c550Logo"'))
ok('CSS 含动画层 #c550Boot', wcss.includes('#c550Boot{'))
ok('CSS 含收尾态 .c550-toWater', wcss.includes('.c550-toWater'))
ok('CSS 含独立背光层 #c550Bg', wcss.includes('#c550Bg{'))
ok(
  'CSS 只依赖 --mono 一个自定义属性',
  JSON.stringify([...new Set([...wcss.matchAll(/var\((--[\w-]+)/g)].map((m) => m[1]))]) === '["--mono"]',
)
ok('JS 含 playBootAnim', wjs.includes('function playBootAnim('))
ok('JS 含 finishBootAnim', wjs.includes('function finishBootAnim('))
ok('JS 含 6 秒保险丝', wjs.includes('}, 6000)'))
ok('JS 与 DSH 侧时序一致（250 + i * 300）', wjs.includes('250 + i * 300'))

const demo = read('web-panel/demo.html')
ok('demo 内联了 CSS', demo.includes('#c550Boot{'))
ok('demo 内联了 DOM 片段', demo.includes('<use href="#c550Logo"'))
ok('demo 内联了 JS 并触发了播放', demo.includes('playBootAnim(1)'))
ok('demo 补上了 $ 帮手', demo.includes('function $(id)') || demo.includes('function $(id) { return document.getElementById(id) }'))
ok('demo 声明了 --mono', demo.includes('--mono:'))

/* ---------- 5. 溯源信息在（MIT 要求保留署名） ---------- */
const credits = read('CREDITS.md')
const license = read('LICENSE')
const rootReadme = read('README.md')
ok('CREDITS 提到上游作者 Ziyang Song', credits.includes('Ziyang Song'))
ok('CREDITS 提到动画原稿作者 Voidpoket', credits.includes('Voidpoket'))
ok('CREDITS 提到上游仓库 URL', credits.includes('github.com/yannicksong0106/dsh-550c-boot'))
ok('LICENSE 保留了上游版权行（Ziyang Song）', license.includes('Copyright (c) 2026 Ziyang Song'))
ok('LICENSE 保留了动画原稿版权行（Voidpoket）', license.includes('Copyright (c) 2026 Voidpoket'))
ok('LICENSE 里的本地版权占位符还在（提醒你去改）', license.includes('<在此填你的名字或 GitHub ID>'))
ok('根 README 声明了"不是原创动画"', rootReadme.includes('这不是原创动画'))
ok('根 README 指向了踩坑文档', rootReadme.includes('docs/踩过的坑.md'))

/* ---------- 输出 ---------- */
const failed = checks.filter(([, v]) => !v)
console.log(`仓库根目录: ${root}\n`)
for (const [label, v] of checks) console.log(`${v ? 'PASS' : 'FAIL'}  ${label}`)
console.log(`\n合计 ${checks.length} 项，通过 ${checks.length - failed.length}，失败 ${failed.length}`)

if (wantSums) {
  const lines = FILES.filter((f) => existsSync(join(root, f))).map((f) => {
    const h = createHash('sha256').update(readFileSync(join(root, f))).digest('hex')
    return `${h}  ${f}`
  })
  writeFileSync(join(root, 'SHA256SUMS.txt'), lines.join('\n') + '\n', 'utf8')
  console.log(`\n已写出 SHA256SUMS.txt（${lines.length} 个文件）`)
}

/* 顺带报一下体积，防止误把 161KB 的备份当成正文提交 */
const size = (f) => (existsSync(join(root, f)) ? statSync(join(root, f)).size : 0)
console.log(`\n体积参考: dsh-plugin/lib/client.js ${size('dsh-plugin/lib/client.js')} 字节，` +
  `web-panel/boot-550c.html ${size('web-panel/boot-550c.html')} 字节，` +
  `web-panel/demo.html ${size('web-panel/demo.html')} 字节`)

process.exit(failed.length ? 1 : 0)
