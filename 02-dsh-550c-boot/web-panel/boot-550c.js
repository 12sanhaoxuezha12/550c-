/* 550C 开机动画 · 播放逻辑
 * 摘自 GameHud 的 web/monitor.html 第 674-762 行（原样摘出，未改写）。
 * 动画与 SVG 原稿：Voidpoket；插件工程与移植：Ziyang Song (dsh-550c-boot, MIT)。
 * 本文件由 tools/extract-web.mjs 生成，请勿手工编辑；改动画请改源文件后重跑脚本。
 */
/* ============================================================ 开机动画
   550C logo 逐笔书写，移植自 DSH 插件 dsh-550c-boot 的「简易档」。
   原理：所有路径先压到 8% 透明，按 x 坐标从左到右，把 clip-path 从
   inset(0 100% 0 0)（整条裁掉）过渡到 inset(0 0 0 0)（全放开），
   于是一个字形一个字形地"擦"出来；写完发光呼吸，再淡出。
   只在面板页面加载时播一次；点画面或按 Esc 可跳过。 */
let c550Busy = false;

function playBootAnim(enabled) {
  const boot = $('c550Boot');
  if (!boot) return;
  if (enabled === 0) { boot.style.display = 'none'; return; }   // config 里 bootanim=0
  const logo = $('c550Logo');
  const text = $('c550Text');
  if (!logo) { boot.style.display = 'none'; return; }

  c550Busy = true;
  boot.style.display = 'block';          // 到这一步才显示；没连上后端时不会留一块黑屏
  const paths = Array.prototype.slice.call(logo.querySelectorAll('path'));
  paths.forEach(function (p) {
    p.style.opacity = '0.08';
    p.style.clipPath = 'inset(0 0 0 0)';
    p.style.webkitClipPath = 'inset(0 0 0 0)';
  });
  // 按 x 坐标排序 —— 保证是"从左往右写"，跟 SVG 里的先后顺序无关
  const sorted = paths.map(function (p) { return { el: p, x: p.getBBox().x }; })
    .sort(function (a, b) { return a.x - b.x; });

  const timers = [];
  sorted.forEach(function (item, i) {
    timers.push(setTimeout(function () {
      const p = item.el;
      p.style.clipPath = 'inset(0 100% 0 0)';
      p.style.webkitClipPath = 'inset(0 100% 0 0)';
      requestAnimationFrame(function () {
        requestAnimationFrame(function () {
          p.style.transition = 'clip-path .6s cubic-bezier(.4,0,.2,1), opacity .35s ease';
          p.style.opacity = '1';
          p.style.clipPath = 'inset(0 0 0 0)';
          p.style.webkitClipPath = 'inset(0 0 0 0)';
        });
      });
    }, 250 + i * 300));
  });

  const total = 250 + sorted.length * 300 + 400;          // 全部写完的时刻
  timers.push(setTimeout(function () { if (text) text.classList.add('c550-show'); }, 2400));
  timers.push(setTimeout(function () { logo.classList.add('c550-finished'); }, total));
  timers.push(setTimeout(finishBootAnim, total + 1800));  // 发一会儿光再淡出

  boot._t = timers;
  boot._skip = function () { finishBootAnim(); };
  boot.addEventListener('click', boot._skip);
  document.addEventListener('keydown', function esc(e) {
    if (e.key === 'Escape') { document.removeEventListener('keydown', esc); finishBootAnim(); }
  });
}

// 保险丝：任何异常路径都不该把动画层留在屏幕上
setTimeout(function () {
  const b = document.getElementById('c550Boot');
  if (b && !b.classList.contains('c550-watermark')) b.style.display = 'none';   // 已成水印就别动它
}, 6000);

function finishBootAnim() {
  const boot = $('c550Boot');
  if (!boot || !c550Busy) return;
  c550Busy = false;
  if (boot._t) { boot._t.forEach(clearTimeout); boot._t = null; }
  if (boot._skip) { boot.removeEventListener('click', boot._skip); boot._skip = null; }
  // 收尾：黑底淡掉、字形压暗，然后把这一层切成“背光水印”常驻在面板背后。
  // 必须把各 path 的内联样式清掉 —— 动画期间是 JS 直接写 style 的，内联优先级高于 class，
  // 不清掉的话水印态那几条 CSS 根本盖不过去（这一步漏了就会看到全亮的字压在面板底下）。
  boot.classList.remove('c550-fade');
  boot.classList.add('c550-toWater');
  try {
    const ps = $('c550Logo') ? $('c550Logo').querySelectorAll('path') : [];
    for (let i = 0; i < ps.length; i++) {
      ps[i].style.opacity = '';
      ps[i].style.clipPath = '';
      ps[i].style.webkitClipPath = '';
      ps[i].style.transition = '';
    }
  } catch (e) { }
  setTimeout(function () {
    boot.classList.remove('c550-toWater');
    boot.style.display = 'none';   // 背光交给独立的 #c550Bg 层，这里直接收掉
  }, 1600);
}
