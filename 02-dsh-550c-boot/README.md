# 550C 开机动画 · 两处实现合集

一段 **550C** 片头，两个能直接用的落地形态：

1. **`dsh-plugin/`** —— 装进 [DeepSeek Harness](https://github.com/yannicksong0106/dsh-550c-boot)（DSH）的客户端插件，
   启动客户端时全屏播放，播完**不消失**，压暗加模糊留在界面上当水印。
2. **`web-panel/`** —— 同一段动画移植进自研「游戏副屏监控面板」（GameHud）的网页版，
   拆成 CSS / DOM / JS 三个片段，可整段嵌进任何页面；附一个单文件演示页。

> **这不是原创动画。** 550C 片头与 SVG 原稿由 **Voidpoket** 提供，DSH 插件工程由 **Ziyang Song** 完成，
> 上游以 **MIT** 发布（<https://github.com/yannicksong0106/dsh-550c-boot>，本仓库基于 **0.3.3**）。
> 本仓库新增的是**水印常驻层、退场兜底，以及 Web 面板移植**。详见 [CREDITS.md](CREDITS.md) 与 [LICENSE](LICENSE)。

## 30 秒上手

| 你想干什么 | 走哪条路 |
|---|---|
| **只想看效果** | 双击 [`web-panel/demo.html`](web-panel/demo.html)，零依赖、零联网 |
| **装进 DSH** | 见下方「给 DSH 装上」，`dsh-plugin/` 是完整的 0.3.3 包 + 本地改造 |
| **搬进自己的网页** | 抄 [`web-panel/`](web-panel/) 三个片段，照着 [`web-panel/README.md`](web-panel/README.md) 嵌 |

## 目录结构

```
550c-boot-ports/
├─ README.md                  ← 你正在看的这份
├─ CREDITS.md                 来源与署名（三层来源写清楚了）
├─ LICENSE                    MIT：上游两方 + 本仓库新增方，三行版权
├─ dsh-plugin/                A. DSH 客户端插件（改造版，可直接装）
│  ├─ package.json            包声明；dsh.client 与 dsh.bundle 两块都必须有，否则装不上
│  ├─ cordis.patch.yml        profile 挂载行（这才是"装上了"的关键）
│  ├─ lib/index.js            宿主半边：首帧注入、更新检查的同源路由
│  ├─ lib/client.js           浏览器半边：动画、水印、退场（**本地改造都在这里**）
│  └─ README.upstream.md      上游原始说明，保留以便比对
├─ web-panel/                 B. 网页移植版
│  ├─ demo.html               单文件演示（把下面三段内联进自己）
│  ├─ boot-550c.css           动画样式
│  ├─ boot-550c.html          DOM 片段：#c550Boot 动画层 + #c550Bg 背光层
│  ├─ boot-550c.js            播放逻辑：playBootAnim / finishBootAnim / 保险丝
│  └─ README.md               片段怎么嵌、依赖什么、要改哪几个数
├─ docs/
│  └─ 踩过的坑.md              两处实现踩过的坑（**这一份最值钱**）
└─ tools/
   └─ extract-web.mjs         从 monitor.html 抽取三件套的脚本（可复现，勿手抄）
```

## 动画是怎么做出来的

不是逐帧，也不是 SVG 描边动画，而是**遮罩擦除**：

1. logo 是**一个** `<svg viewBox="0 0 800 230">`，4 个字形分组（`five1` / `five2` / `red0` / `cee`），
   共 **6 条 `<path>`**。"0" 那条是红色（`.red` / `.c550-r`），其余白色。
2. 所有 path 先压到 `opacity: .08`，`clip-path: inset(0 0 0 0)`。
3. 用 `getBBox().x` 给 path **按 x 坐标排序** —— 所以是"从左往右写"，跟 SVG 里的书写顺序无关。
4. 每条 path 到点后：先瞬间切到 `inset(0 100% 0 0)`（整条裁没），再跨两帧
   （`requestAnimationFrame` 套一层）切回 `inset(0 0 0 0)`，配 `.6s cubic-bezier(.4,0,.2,1)` 过渡
   —— **一个字形一个字形地被"擦"出来**。

两个关键细节，改代码前务必知道：

- `clip-path` 的过渡必须靠"先设终点外的值、跨两帧再设终点值"来触发；同一帧里直接设终点，浏览器不会插值。
- `getBBox()` 要求元素**已经渲染且有布局**。把 logo 拆成多个 `<svg>`、或把 path 挪进 `display:none` 的容器，
  会拿不到有效包围盒 → 抛错 → 动画整段被跳过（表现为"动画直接消失了"）。

## 时间轴（两处共用同一套数，已逐行比对）

设 path 数 `n = 6`：

| 时刻 | 事件 |
|---|---|
| `0 ms` | 6 条 path 全部 `opacity: .08`，遮罩全开 |
| `250 + i×300 ms` | 第 `i` 条 path 开始书写（`i = 0…5`，共 6 条，过渡 `.6s`） |
| `2400 ms` | 底部状态行 `550C SYSTEM BOOT` 淡入（`.6s`，末尾带闪烁光标） |
| `2450 ms` | 书写收尾（`totalMs = 250 + n×300 + 400`），logo 进入**发光呼吸**（`2.6s` 循环） |
| 之后 | **两处实现在这里分叉**，见下表 |

`totalMs = 250 + 6×300 + 400 = 2450 ms` 这个式子在 DSH 侧（`lib/client.js` 的 `playBoot()`）
和网页侧（`monitor.html` 的 `playBootAnim()`）**完全一致**，是从上游一路继承下来的。

### 分叉点：播完之后干什么

| | DSH 插件（`simple` 档） | 网页面板（GameHud） |
|---|---|---|
| 起淡出 | 交接即开始，`becomeMark()` | `totalMs + 1800 = 4250 ms` |
| 页面变色时长 | **3.5 s**（`:host` / `#boot` / 注入样式表 / 两处内联 JS，共 5 处必须同步） | `1.6 s` |
| logo 自身形态 | **1.6 s**（**故意与页面变色分成两个时钟**，合并会让 logo 像在融化） | `1.6 s` |
| 收层 | `transitionend` 优先，兜底 `RETIRE_FALLBACK_MS = 4600 ms` 后 `#boot{display:none !important}` | `4250 + 1600 = 5850 ms` 时 `display:none` |
| 绝对看门狗 | 12000 ms（`simple`）/ 30000 ms（`full`），到点无条件收场 | 6000 ms 一次性保险丝 |
| 最终形态 | 字形迁进 `.dsh550c-marklayer`（`position:fixed`、`pointer-events:none`），`fill:#333333` + `blur(8px)`，宽度收到 `min(520px,68vw)` | 动画层收掉，背光交给**独立的** `#c550Bg` 层（`blur(8px)`、`opacity:.55`） |

## 两处实现的差异一览

| 维度 | DSH 插件 | 网页面板 |
|---|---|---|
| 运行环境 | DSH 客户端 Electron 窗口，**Shadow DOM**（`attachShadow`） | 普通页面，**全局 DOM** |
| 样式与 DOM 来源 | JS 字符串常量：`CSS_550C`（上游）+ `WATERMARK_CSS`（本地）+ `HOST_SHEET_CSS`（本地，挂在 document 上） | `monitor.html` 内联的 `<style>` 与内联 SVG |
| 全屏层怎么来的 | 宿主半边在**文档解析阶段**注入首帧（盖住 DSH 自己的 `HARNESS / Loading plugins…` 卡片），客户端半边同帧接管 | 页面自己的 `<div id="c550Boot">` |
| 分层（z-index） | `#boot` 2000，宿主层 2147483000，拖拽条 2147483001 | `#c550Boot` 400，水印态 260，`#c550Bg` 0 |
| 水印实现 | 同层改造 + 把字形迁到独立 marklayer，宿主透明化 | 动画层收干净，`#c550Bg` 用 `<use href="#c550Logo">` 复用那段 SVG（**不重复 31 KB 路径**） |
| 跳过 | 点击画面或 `Esc`（`full` 档尤其需要） | 点击画面或 `Esc` |
| 开关 | 设置 → 通用 → 550C 开机动画（三档：简易 / 完整 / 关闭），存 `localStorage` 键 `dsh-550c-boot:mode` | `config.txt` 里 `bootanim=1/0` |
| 配色 | 四套磷光（琥珀 / 绿 / 青 / 白），存 `dsh-550c-boot:scheme` | 只用了原稿配色 |
| 额外内容 | `full` 档还有 16 秒完整流程（47 节点逐点覆写 → `SYSTEM IS REWRITTEN`）+ 桌面标题栏按钮收编 | 无，只搬了 `simple` 档 |

## 本地相对上游 0.3.3 改了什么

改动的完整取值都在 `dsh-plugin/lib/client.js` 里（本地新增的常量有注释块说明为什么）。

1. **水印常驻（新增 `WATERMARK_CSS`）**：片头播完不收干净，而是把 550C 压暗、加模糊留作界面底纹。
   - `#logo .white{fill:#333333}` —— 必须显式重涂，白底界面上沿用上游的白色会直接看不见；
     `#333` 而不是纯黑，是留一点抬升，免得看着像页面上被抠了个洞。
   - 模糊用的是 **SVG 自己的 `filter`**，不是盖一层的遮罩 —— 这样它**永远不可能挡住正文**。
   - 曾经做过"中间一块磨砂窗"，**已废弃**：那块窗正好落在对话渲染区，挡住正文。
     代码里留了注释明令别再引入居中层。**水印模糊半径要按标记实际尺寸调**：
     对 540 px 宽的横条是"磨砂"，对 96 px 宽的图形就是"抹平"。
2. **P0 离散兜底**：不再只靠 `transition` 完成可见性交接。后台标签页 / 最小化窗口会被 Chromium
   节流甚至丢弃过渡，结果停在"看不见但能点"的中间态（实测 `#boot` 停在 `rgb(0,0,0)`、宿主停在
   `rgb(5,4,3)`）。现在 `transitionend` + `RETIRE_FALLBACK_MS` 超时双保险。
3. **P1 真正退场**：交接完成后把 `#boot` 设成 `display:none`。
   只变透明的全屏层**仍然是一层活着的合成层**（1440×900、`z-index:2000`），
   上一版正是栽在这里：那版给了 `pointer-events:auto`，把所有点击都吃掉了。
4. **状态行必须 `!important`**：`.boot-text.show{animation:fadeIn … forwards}` 会把 `opacity`
   钉在末态，**动画高于一切普通声明，包括内联**。不加 `!important`，
   `550C SYSTEM BOOT` 就永远留在屏幕上 —— 上一版正是如此。
5. **Web 移植**：见 `web-panel/`，额外加了"动画层收干净、背光独立成层"的结构。

## 已知限制与未验证项

如实列出，别把这份包当成熟产品：

- **`dsh-plugin/` 的改造只在本机 DSH `0.2.0-rc.2`（desktop profile）上跑过。**
  上游声明的最低版本是 `>=0.2.0-rc.1`，没验过的版本不作保证。
- **没有视觉回归手段。** 本机用 headless Edge 做截图验证会空转烧 CPU（实测跑掉 1002 秒），
  而 Ollama 只配了非多模态模型、Python 侧也没有 SVG 渲染库（无 `cairosvg` / `svglib`）。
  所以"看起来好不好"**只能人眼看**；本仓库只做了文件层与语法层验证：
  `node --check` 两份 JS 通过，`web-panel/` 三件套由脚本按锚点切出并断言 14 项结构（全部 PASS）。
- **`web-panel/demo.html` 没有在浏览器里实际渲染验证过。** 它内联的三段是原样摘出的可用代码，
  但演示页的骨架（工具栏、假面板卡片）是后加的。第一次打开请自己确认一眼。
- **完整档（16 秒、47 节点覆写）没有纳入本仓库的验证范围**，只随包带着。
- **版式一改，模糊和滤镜都得重算。** 上游原稿的 `despike` 滤镜（`stdDeviation="2.2"` +
  `feFuncA slope5/intercept-2`）是按 **800×230** 调的：2.2 只占 0.27%。
  把 viewBox 切成 26 / 95 / 58 宽之后，同一个 2.2 就变成 8.5% / 2.3% / 3.8% 的模糊，
  alpha 又被 `intercept=-2` 削掉低值部分，**细笔画直接归零、图形整片消失**。

更详细的坑（CSS 写在 JS 字符串里的换行陷阱、`clipPath` 的 `userSpaceOnUse` 坐标系、
`getBBox()` 与拆 SVG 的冲突、检查脚本自己的假阳性……）见 **[docs/踩过的坑.md](docs/踩过的坑.md)**。

## 给 DSH 装上

> `dsh plugin` 硬编码拒绝 `desktop` profile（Electron 应用独占管理它），
> 所以桌面版要**手工装**。`web` 等 profile 才能用命令行。

**桌面版（本机实测路径）** —— 三步，改完**重启客户端**才生效：

1. 把 `dsh-plugin/` 整个目录复制成
   `<DSH_HOME>\profiles\desktop\node_modules\dsh-550c-boot\`。
2. 改 `<DSH_HOME>\profiles\desktop\package.json`：`dependencies` 加一行 `"dsh-550c-boot": "0.3.3"`，
   `dsh.profile.bundles` 加一行 `"dsh-550c-boot"`。
3. 重启客户端。改这两个文件**前先备份**。

**命令行（`web` 等 profile）**：

```sh
dsh plugin --profile web add dsh-550c-boot          # 从 npm 装上游版
dsh plugin --profile web add <本目录>/dsh-plugin     # 装本仓库这份改造版
```

装完按 **Ctrl+Shift+R** 硬刷新一次：DSH 的客户端 bundle 带 `max-age=31536000, immutable`，
而 URL 上的 `rev` 是进程 nonce、不随内容变化，普通 F5 会一直用第一次抓到的副本。

## 许可与署名

三层来源、三方版权，全部在 [LICENSE](LICENSE) 与 [CREDITS.md](CREDITS.md) 里写明。
**二次分发请保留 `LICENSE`、`CREDITS.md` 和 `dsh-plugin/README.upstream.md`**，
MIT 只要求这一件事。

`LICENSE` 里第三行留了 `Copyright (c) 2026 <在此填你的名字或 GitHub ID>` 占位符
—— 你要把它传上自己的 GitHub 的话，**先把这行改成你的名字**。

## 把这个仓库传上 GitHub

不需要装 git，网页就能传完：

1. github.com → 右上 `+` → **New repository** → 名字建议 `550c-boot-ports`
   → 选 Public 或 Private → **不要**勾 "Add a README"（本地已有）→ Create。
2. 新仓库页点 **uploading an existing file**。
3. 把本目录（解压后）里的 `README.md`、`CREDITS.md`、`LICENSE`、`docs`、`dsh-plugin`、
   `web-panel`、`tools` 一起**拖进**上传框（拖文件夹会保留子目录结构）。
4. 下方 Commit changes。

如果你已经装了 **GitHub Desktop**，也可以：`File → Add local repository` 选本目录 →
`Publish repository`，比拖拽更稳，还能看每次改动。
