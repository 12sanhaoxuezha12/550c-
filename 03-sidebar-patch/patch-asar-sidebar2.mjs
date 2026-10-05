#!/usr/bin/env node
/**
 * patch-asar-sidebar2.mjs —— 把 DSH 侧边栏默认改成折叠，并且**同时更新 asar 的完整性 hash**。
 *
 * 为什么必须连 hash 一起改：
 *   本机 app.asar 里 11470 个文件**全部**带 integrity（逐文件 SHA256）。只改内容不改 hash，
 *   一旦 Electron 启用了 EnableEmbeddedAsarIntegrityValidation，DSH 会直接启动失败。
 *   两边一起改，则不管 fuse 开没开都能正常启动。
 *
 * 改什么：
 *   目标文件 dsh/node_modules/@deepseek-ai/dsh-client-ui-layout/lib/client.js
 *   里面的布局 store 初始值 "sidebar: 280," → "sidebar: 0,  "（等长 13 字节）
 *   280 是展开宽度，0 就是折叠；界面里随时可以按 Ctrl+B 展回来。
 *
 * 用法：
 *   node patch-asar-sidebar2.mjs --check  <app.asar>   只检查
 *   node patch-asar-sidebar2.mjs --apply  <app.asar>   备份 + 改内容 + 改 hash + 全面自检
 *   node patch-asar-sidebar2.mjs --revert <app.asar>   从备份还原
 */
import { readFileSync, writeFileSync, copyFileSync, existsSync, statSync } from 'node:fs'
import { createHash } from 'node:crypto'

const NEEDLE = 'sidebar: 280,'
const REPLACEMENT = 'sidebar: 0,  '
const BACKUP_SUFFIX = '.bak-before-sidebar-fold'
const TARGET_PATH = 'dsh/node_modules/@deepseek-ai/dsh-client-ui-layout/lib/client.js'

if (NEEDLE.length !== REPLACEMENT.length) {
  console.error('内部错误：替换串不等长'); process.exit(2)
}

const mode = process.argv[2]
const file = process.argv[3]
if (!mode || !file) {
  console.error('用法: node patch-asar-sidebar2.mjs --check|--apply|--revert <app.asar>')
  process.exit(2)
}
if (!existsSync(file)) { console.error('文件不存在: ' + file); process.exit(2) }
const backup = file + BACKUP_SUFFIX
const sha = (b) => createHash('sha256').update(b).digest('hex')

// ------------------------------------------------------------------ revert
if (mode === '--revert') {
  if (!existsSync(backup)) { console.error('没有备份：' + backup); process.exit(1) }
  copyFileSync(backup, file)
  console.log(`已还原 ${file}（${statSync(file).size} 字节）`)
  process.exit(0)
}

// ------------------------------------------------------------------ 解析 asar
const buf = readFileSync(file)
const size = buf.length

const probe = buf.slice(0, 128)
let jsonStart = -1
for (const m of ['{"files"', '{ "files"']) {
  const i = probe.indexOf(Buffer.from(m, 'latin1'))
  if (i >= 0) { jsonStart = i; break }
}
if (jsonStart < 0) { console.error('定位不到 asar header JSON'); process.exit(1) }
const jsonLen = buf.readUInt32LE(jsonStart - 4)
const jsonText = buf.slice(jsonStart, jsonStart + jsonLen).toString('utf8')
const header = JSON.parse(jsonText)
const dataStart = jsonStart + jsonLen + ((4 - ((jsonStart + jsonLen) % 4)) % 4)

// 找目标文件节点
let node = null
let prefix = ''
function walk(files, p) {
  for (const name of Object.keys(files)) {
    const n = files[name]
    const path = p ? p + '/' + name : name
    if (n.files) walk(n.files, path)
    else if (path === TARGET_PATH) node = n
  }
}
walk(header.files, '')
if (!node) { console.error('在 asar 里找不到目标文件：' + TARGET_PATH); process.exit(1) }

const fileOffset = Number(node.offset)
const fileSize = node.size
const abs = dataStart + fileOffset
const integrity = node.integrity
const blockSize = integrity ? integrity.blockSize : 4194304

console.log(`asar          ${file}`)
console.log(`文件大小      ${(size / 1048576).toFixed(1)} MB (${size} 字节)`)
console.log(`header JSON   ${jsonStart} + ${jsonLen}   数据区起点 ${dataStart}`)
console.log(`目标文件      ${TARGET_PATH}`)
console.log(`              数据区偏移 ${fileOffset}  大小 ${fileSize} 字节  绝对 ${abs}–${abs + fileSize}`)
console.log(`integrity     ${integrity ? integrity.algorithm + ' 单块? ' + ((integrity.blocks || []).length === 1) : '无'}`)

// 原文里命中几次（必须恰好一次，且落在目标文件内）
const fileBuf = buf.slice(abs, abs + fileSize)
let hitCount = 0
let hitInFile = -1
let i = -1
while ((i = fileBuf.indexOf(Buffer.from(NEEDLE, 'latin1'), i + 1)) >= 0) {
  hitCount++
  if (hitInFile < 0) hitInFile = i
}
const globalCount = (buf.toString('latin1').match(/sidebar: 280,/g) || []).length
console.log(`\n命中         目标文件内 ${hitCount} 处；整个 asar 内 ${globalCount} 处`)

if (hitCount === 0) {
  if (fileBuf.indexOf(Buffer.from(REPLACEMENT, 'latin1')) >= 0) {
    console.log('\n看起来**已经打过补丁**了，无需重复操作。')
    process.exit(0)
  }
  console.error('\n目标文件里找不到 "sidebar: 280,"，版本可能不同，拒绝改动。')
  process.exit(1)
}
if (hitCount !== 1) { console.error(`\n目标文件内命中 ${hitCount} 处，不是唯一，拒绝改动。`); process.exit(1) }

if (mode === '--check') {
  console.log('\n检查通过（未改动任何字节）。')
  process.exit(0)
}
if (mode !== '--apply') { console.error('未知模式 ' + mode); process.exit(2) }

// ------------------------------------------------------------------ 备份
if (!existsSync(backup)) {
  copyFileSync(file, backup)
  console.log(`\n已备份 -> ${backup}`)
} else {
  console.log(`\n备份已存在，沿用 ${backup}`)
}

// ------------------------------------------------------------------ 改内容
const patched = Buffer.from(buf)
Buffer.from(REPLACEMENT, 'latin1').copy(patched, abs + hitInFile)

// ------------------------------------------------------------------ 重算该文件的 integrity
const newFileBuf = patched.slice(abs, abs + fileSize)
const newBlocks = []
for (let o = 0; o < newFileBuf.length; o += blockSize) {
  newBlocks.push(sha(newFileBuf.slice(o, o + blockSize)))
}
// 单块时 hash 就是这一块的 hash（本机 header 里 hash 与 blocks[0] 相同，印证了这点）
const newHash = newBlocks.length === 1 ? newBlocks[0] : sha(newFileBuf)
console.log(`\n新内容 SHA256 ${newHash}`)
console.log(`旧 hash       ${integrity ? integrity.hash : '（无）'}`)

// ------------------------------------------------------------------ 改 header 里的 hash
// 用 offset 字符串做锚点精确定位到这个文件的 integrity 记录（offset 是唯一的）
const anchor = `"offset":"${fileOffset}"`
const anchorAt = jsonText.indexOf(anchor)
if (anchorAt < 0) { console.error('在 header JSON 里定位不到该文件的 offset 锚点，已还原'); copyFileSync(backup, file); process.exit(1) }

let headerFixed = 0
// 在该锚点之后的小窗口里替换 hash 与 blocks[0]
const winStart = anchorAt
const winEnd = Math.min(jsonText.length, anchorAt + 400)
let window = jsonText.slice(winStart, winEnd)

if (integrity) {
  const oldHash = integrity.hash
  const before = window
  window = window.split(`"hash":"${oldHash}"`).join(`"hash":"${newHash}"`)
  ;(integrity.blocks || []).forEach((ob, bi) => {
    window = window.split(`"${ob}"`).join(`"${newBlocks[bi] || newHash}"`)
  })
  if (window !== before) {
    // 写回 Buffer（等长替换，长度必然不变）
    const wb = Buffer.from(window, 'utf8')
    const orig = Buffer.from(jsonText.slice(winStart, winEnd), 'utf8')
    if (wb.length !== orig.length) {
      console.error('替换后 header 片段长度变化，已还原'); copyFileSync(backup, file); process.exit(1)
    }
    wb.copy(patched, jsonStart + winStart)
    headerFixed = 1
  }
}
if (!headerFixed) { console.error('没能更新 header 里的 hash，已还原'); copyFileSync(backup, file); process.exit(1) }

writeFileSync(file, patched)

// ------------------------------------------------------------------ 自检
const after = readFileSync(file)
const problems = []
if (after.length !== size) problems.push(`大小变了：${size} -> ${after.length}`)

// 1) 内容确实改了
const afterFileBuf = after.slice(abs, abs + fileSize)
if (afterFileBuf.indexOf(Buffer.from(NEEDLE, 'latin1')) >= 0) problems.push('目标文件里仍能搜到原串')
if (afterFileBuf.indexOf(Buffer.from(REPLACEMENT, 'latin1')) < 0) problems.push('目标文件里搜不到新串')

// 2) 重新解析 header，核对新 hash 与实际内容一致（这一步最关键）
const j2Start = (() => {
  const p2 = after.slice(0, 128)
  for (const m of ['{"files"', '{ "files"']) {
    const k = p2.indexOf(Buffer.from(m, 'latin1'))
    if (k >= 0) return k
  }
  return -1
})()
let ok2 = false
if (j2Start > 0) {
  const j2Len = after.readUInt32LE(j2Start - 4)
  const j2 = JSON.parse(after.slice(j2Start, j2Start + j2Len).toString('utf8'))
  let n2 = null
  ;(function walk2(files, p) {
    for (const name of Object.keys(files)) {
      const n = files[name]
      const path = p ? p + '/' + name : name
      if (n.files) walk2(n.files, path)
      else if (path === TARGET_PATH) n2 = n
    }
  })(j2.files, '')
  if (!n2 || !n2.integrity) problems.push('自检时找不到目标文件的 integrity')
  else {
    const realHash = sha(afterFileBuf)
    if (n2.integrity.hash !== realHash) problems.push(`hash 不一致：header ${n2.integrity.hash} vs 实际 ${realHash}`)
    else ok2 = true
  }
} else problems.push('自检时无法解析 header')

// 3) 除目标文件与 header 外，其余字节不应变化
let diffOutside = 0
for (let k = 0; k < size; k++) {
  if (buf[k] === after[k]) continue
  const inTarget = k >= abs && k < abs + fileSize
  const inHeader = k >= jsonStart && k < jsonStart + jsonLen
  if (!inTarget && !inHeader) diffOutside++
}

console.log(`\n=== 自检 ===`)
console.log(`  文件大小不变        ${after.length === size ? '✓' : '✗'}`)
console.log(`  内容已替换          ${afterFileBuf.indexOf(Buffer.from(REPLACEMENT, 'latin1')) >= 0 ? '✓' : '✗'}`)
console.log(`  header hash 与实际一致 ${ok2 ? '✓（这是能否正常启动的关键）' : '✗'}`)
console.log(`  目标与 header 之外变更的字节 ${diffOutside} ${diffOutside === 0 ? '✓' : '✗'}`)

if (problems.length) {
  console.error('\n自检失败，正在还原：')
  problems.forEach((p) => console.error('  · ' + p))
  copyFileSync(backup, file)
  process.exit(1)
}

console.log(`\n补丁完成。备份：${backup}`)
console.log('启动 DSH 后侧边栏应默认折叠；需要时按 Ctrl+B 展开。')
console.log(`还原命令：node "${process.argv[1]}" --revert "${file}"`)
