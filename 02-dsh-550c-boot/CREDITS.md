# 来源与署名（CREDITS）

这个仓库里的 550C 动画**不是原创**。它有三层来源，请按下面如实标注，别把上游的功劳写成自己的。

## 1. 动画原稿

- **550C 片头动画、SVG 路径与最初的 HTML 实现**：**Voidpoket**
  <https://github.com/Voidpoket>
- 也就是本仓库里那份 `viewBox="0 0 800 230"`、4 个字形（`five1` / `five2` / `red0` / `cee`）、
  6 条 `<path>`、带 `despike` 滤镜的 SVG。这段素材在 `dsh-plugin/lib/client.js`（常量 `BOOT_MARKUP`）
  和 `web-panel/boot-550c.html` 里各有一份，**内容一致，都没有改过路径数据**。

## 2. DSH 插件工程

- **把它做成 DeepSeek Harness 插件**：**Ziyang Song**（`@yannicksong0106`）
  <https://github.com/yannicksong0106/dsh-550c-boot>
- npm：<https://www.npmjs.com/package/dsh-550c-boot>（本仓库基于 **0.3.3**）
- 许可：**MIT**（原文见 `LICENSE`）
- 本仓库的 `dsh-plugin/` 就是它 0.3.3 的完整包 + 本地改造（见下），
  `dsh-plugin/README.upstream.md`、`dsh-plugin/CHANGELOG.md` 是上游原文，保留以便溯源比对。

## 3. 本仓库新增的部分

- **水印常驻层**：片头播完不收干净，改成把 550C 压暗、加 8px 模糊，留作界面底纹
  （DSH 侧 `WATERMARK_CSS`，`#logo .white{fill:#333333}`；Web 侧 `#c550Bg` 独立层）。
- **退场兜底（P0/P1）**：不再只依赖 CSS transition —— 后台标签页/最小化窗口会被
  Chromium 节流甚至丢弃过渡，结果会停在"看不见但能点"的中间态。现在用
  `transitionend` + 超时双保险，并在交接完成后真正把 `#boot` 设成 `display:none`，
  而不是让它当一层透明的全屏合成层继续压在上面。
- **Web 面板移植**：把「简易档」移植进 GameHud 的 `web/monitor.html`，
  并拆成 `web-panel/` 下的可复用三件套（CSS / DOM 片段 / 播放逻辑）。

## 上游的坑，也一并致谢

`docs/03-踩过的坑.md` 里凡是标注「上游」的条目，都是 0.3.3 代码注释里已经写明白的
（首帧注入时序、`[data-dsh-boot-splash]` 被第三方样式表夺走的排查、
macOS `-webkit-app-region` 与 `pointer-events` 的关系）。照抄结论可以，别把结论说成自己发现的。
