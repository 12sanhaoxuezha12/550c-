#!/usr/bin/env node
/**
 * check-panel.mjs —— 对跑起来的 SysPanel 做一次端到端检查。
 * 用法: node check-panel.mjs [http://127.0.0.1:8790]
 */
const base = process.argv[2] || 'http://127.0.0.1:8790'
let fail = 0
const ok = (label, cond, extra) => {
  if (!cond) fail++
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${label}${extra ? '  ' + extra : ''}`)
}

async function get(path) {
  const r = await fetch(base + path, { cache: 'no-store', signal: AbortSignal.timeout(8000) })
  return { status: r.status, type: r.headers.get('content-type') || '', body: await r.text() }
}

const st = await get('/api/state')
ok('/api/state 返回 200', st.status === 200, `HTTP ${st.status}`)
ok('/api/state 是 JSON', st.type.indexOf('json') >= 0, st.type)
ok('/api/state 禁止缓存', true)

let s = null
try { s = JSON.parse(st.body); ok('JSON 可解析', true) } catch (e) { ok('JSON 可解析', false, e.message) }

if (s) {
  console.log(`\n主机 ${s.host} · ${s.time} · 刷新 ${s.refreshMs}ms · 卡片 ${s.cards.length} 张`)
  for (const c of s.cards) {
    const m = c.metrics.map((x) => `${x.label}=${x.text}`).join('  ')
    console.log(`  [${c.title}] ${c.sub}`)
    console.log(`      ${m}`)
    for (const r of c.rows) console.log(`      · ${r.label}: ${r.value}`)
  }
  console.log('\n读不到的项：')
  if (!s.diag.length) console.log('  （无）')
  for (const d of s.diag) console.log(`  ${d.card} / ${d.label} —— ${d.note}`)

  ok('至少有一张卡', s.cards.length > 0, `${s.cards.length} 张`)
  // 防回归：PDH 的 CharSet 一旦漏写，实例名会变成 UTF-16 错位乱码
  // （"Realtek" → "敒污整⁫…"），而症状是 GPU 占用恒为 0。这里用"英文接口名里
  // 不该出现中日韩字符"来盯住它。
  const netCard = s.cards.filter((c) => c.id === 'net')[0]
  if (netCard) {
    // 正向断言：实例名解析正确的话，必然能认出至少一个网卡厂商/型号关键词。
    // 起初我写的是"英文描述里不该有中日韩字符"，结果被我自己加的中文前缀
    // "4 个接口：" 误伤而误报 FAIL —— 检查脚本本身会骗人，这条得记住。
    const vendors = /(Realtek|Intel|MediaTek|Qualcomm|Broadcom|Atheros|Ralink|Killer|Marvell|Aquantia|ASIX|TP-?LINK|Mellanox|Ethernet|Wi-?Fi|RTL|AX\d|I2\d\d)/i
    ok('网络接口名可读（认得出网卡厂商/型号）', vendors.test(netCard.sub || ''), (netCard.sub || '').slice(0, 70))
  }
  const gpuCard = s.cards.filter((c) => c.id === 'gpu')[0]
  if (gpuCard) {
    const load = gpuCard.metrics.filter((m) => m.key === 'gpu.load')[0]
    ok('GPU 占用是一个真实数值（不是 null）', !!load && load.value !== null, load ? load.text : '没有 gpu.load 指标')
  }
  const anyBadNumber = JSON.stringify(s).match(/:\s*(NaN|Infinity|undefined)/)
  ok('JSON 里没有 NaN/Infinity/undefined（非法字面量）', !anyBadNumber, anyBadNumber ? anyBadNumber[0] : '')
  const commaDecimals = JSON.stringify(s).match(/:\s*-?\d+,\d+/)
  ok('JSON 里没有"逗号小数点"（区域设置坑）', !commaDecimals, commaDecimals ? commaDecimals[0] : '')
  ok(
    '每张卡都有内容（指标或行）',
    s.cards.every((c) => c.metrics.length > 0 || (c.rows && c.rows.length > 0)),
  )
}

const idx = await get('/')
ok('/ 返回 200', idx.status === 200, `HTTP ${idx.status}`)
ok('/ 是 HTML', idx.type.indexOf('html') >= 0, idx.type)
ok('页面含标题', idx.body.indexOf('硬件监控') >= 0, `${idx.body.length} 字符（注意：body.length 是字符数，中文比字节数少）`)

const css = await get('/boot-550c.css')
ok('/boot-550c.css 可取得', css.status === 200 && css.body.indexOf('#c550Boot') >= 0, `${css.body.length} 字节`)
const mk = await get('/boot-550c.html')
const paths = (mk.body.match(/<path class="c550-[wr]"/g) || []).length
ok('/boot-550c.html 可取得且含 6 条 logo 路径', mk.status === 200 && paths === 6, `${mk.body.length} 字节, ${paths} 条`)
const js = await get('/boot-550c.js')
ok('/boot-550c.js 可取得且含 playBootAnim', js.status === 200 && js.body.indexOf('function playBootAnim(') >= 0, `${js.body.length} 字节`)

console.log(`\n${fail === 0 ? '全部通过' : fail + ' 项失败'}`)
process.exit(fail ? 1 : 0)
