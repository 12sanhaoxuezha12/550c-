# web-panel/ —— 把 550C 片头搬进你自己的网页

这里是**从游戏面板 `web/monitor.html` 里原样摘出**的动画三件套，没有改写动画本身，
只加了溯源注释头。摘取由 [`../tools/extract-web.mjs`](../tools/extract-web.mjs) 完成（锚点定位 + 14 项结构断言），
**不要手抄**：logo 那段 SVG 光路径数据就 31 KB，手抄必错。要改动画，请改源文件后重跑脚本。

## 三个文件

| 文件 | 内容 | 放哪 |
|---|---|---|
| `boot-550c.css` | 动画样式：全屏黑底、扫描线、逐笔书写、发光呼吸、收尾/水印态 | 页面的 `<style>` 里，或抽成独立 `.css` |
| `boot-550c.html` | DOM：动画层 `#c550Boot`（内含那一个 800×230 的 SVG）+ 背光层 `#c550Bg` | `<body>` 里，尽量靠前 |
| `boot-550c.js` | 播放逻辑：`playBootAnim(enabled)`、`finishBootAnim()`、6 秒保险丝 | 一个 `<script>`，**放最后** |
| `demo.html` | 上面三段内联成的单文件演示页，双击即看 | 不用管，参考用 |

## 嵌进自己页面的四步

```html
<!-- 1) 样式：随便放 <head> 里 -->
<style>
  :root{ --mono: ui-monospace, Menlo, Consolas, "Courier New", monospace; }  /* 见下方"依赖" */
</style>
<style>/* boot-550c.css 的全部内容 */</style>

<!-- 2) DOM 片段：放 <body> 开头 -->
<!-- boot-550c.html 的全部内容 -->

<!-- 3) 片段依赖一个取元素的帮手，宿主页面里补上（GameHud 里它叫 $） -->
<script>function $(id){ return document.getElementById(id) }</script>

<!-- 4) 播放逻辑 + 触发 -->
<script>/* boot-550c.js 的全部内容 */</script>
<script>
  // 面板加载完成后播一次；传 0 就是不播（等价于 GameHud 的 config.txt: bootanim=0）
  window.addEventListener('load', function () { playBootAnim(1) })
</script>
```

## 依赖与约束（照做，别猜）

1. **CSS 只依赖一个自定义属性：`--mono`** —— 底部那行 `550C SYSTEM BOOT` 的字体。
   实测（脚本扫过全段 CSS）没有第二个。没定义的话那行会退化成默认字体，不影响播放。
2. **`#c550Bg` 靠 `<use href="#c550Logo">` 复用动画层里那份 SVG。**
   两条硬约束：`#c550Logo` 必须和 `#c550Bg` **在同一个文档里**（跨文档 / 跨 iframe 引用不了），
   而且**同一个页面只能嵌一份** —— 嵌两份会撞 `id`，`<use>` 会指错。
3. **`getBBox()` 要求 SVG 已经渲染且有布局。** 所以：
   - 动画层**不能**是 `display:none` 状态下启动 —— 现有逻辑是 `playBootAnim` 里先
     `boot.style.display='block'` 再取包围盒，**这个顺序别改**。
   - 后端没连上时不要留一块黑屏：GameHud 的做法是确认拿到 `#c550Logo` 才显示该层，
     拿不到就直接 `display:none` 收摊。
4. **`playBootAnim` 必须在路径数据到位之后调用**，也就是脚本放在 DOM 片段后面。

## 移植到别的页面要重算的几个数

现有取值是配 GameHud 面板的层级与尺寸的，换个宿主环境**必须重新校**：

| 位置 | 现值 | 说明 |
|---|---|---|
| `#c550Boot` 的 `z-index` | `400` | 要压过宿主页面所有内容 |
| 水印态 `#c550Boot.c550-watermark` 的 `z-index` | `260` | 水印要**高于**内容层才能被看见（低于内容会被卡片的 `backdrop-filter` 吃掉，见坑 9） |
| `#c550Bg` 的 `z-index` | `0` | 独立背光层，压在内容之下 |
| SVG 宽度 | `min(680px, 90vw)` | 动画层 |
| 背光模糊 / 透明 | `blur(8px)` / `opacity:.55` | **按标记实际尺寸调**，不是通用值 |
| 收尾淡出 | 动画层 `1.6s`；页面变色另有一个时钟 | 两个时钟是**故意分开**的，合并会让 logo 像在融化 |

## 时间轴

`250ms` 起、每条 path 间隔 `300ms`、过渡 `.6s`；`2400ms` 状态行淡入；
`2450ms`（`250 + 6×300 + 400`）书写收尾并进入发光呼吸；`4250ms` 起整体淡出；
`5850ms` 收层；另有 `6000ms` 一次性保险丝兜底。
完整推导见 [仓库根 README 的「时间轴」一节](../README.md#时间轴两处共用同一套数已逐行比对)。

## 开关与跳过

- **开关**：`playBootAnim(1)` 播，`playBootAnim(0)` 不播（GameHud 从 `config.txt` 的 `bootanim` 读）。
- **跳过**：点画面任意处，或按 `Esc`。
- 动画**只在页面加载时播一次**（GameHud 是 `location.search` 里带 `report` 时不播）。
- 演示页的「重播」按钮走**整页刷新**：片段里那个 6 秒保险丝是加载期一次性定时器，
  原地重播有可能被它打断，刷新最稳。

## 验证到什么程度

如实说明：这三段是**原样可用**的代码（来自一个日常在跑的面板），
摘取时有 14 项结构断言（全 PASS），演示页本身**没有在浏览器里实际渲染验证过** ——
演示页的骨架（工具栏、假面板卡片）是后加的。第一次打开请自己看一眼。
