# 550C 启动动画 · 移植与应用合集

这个仓库装着同一段 **550C 开机动画**在三个地方的落地：DSH 客户端本体、Windows 游戏副屏面板、以及配套的侧边栏修补工具。

动画原稿由 **Voidpoket**（GitHub [@Voidpoket](https://github.com/Voidpoket)，B站 @图寻患者）提供，本仓库的移植、面板与工具由其本人完成。详见各子目录的 CREDITS。

---

## 目录

| 目录 | 内容 |
|---|---|
| [`01-syspanel/`](01-syspanel/) | **游戏副屏面板**（SysPanel）—— C# 通用硬件监控面板，自带 550C 片头 |
| [`02-dsh-550c-boot/`](02-dsh-550c-boot/) | **DSH 启动动画** —— 给 DeepSeek Harness 客户端加 550C 全屏片头 |
| [`03-sidebar-patch/`](03-sidebar-patch/) | **侧边栏修补** —— 让 DSH 侧边栏默认收起的小工具 |

---

## 01-syspanel

用 C# 写的通用副屏面板，直接读本机硬件（CPU / GPU / 内存 / 显存 / 磁盘 / 网络），
自己起一个本地 HTTP 服务把界面推给浏览器。**不限定 Intel 还是 AMD 平台**，
Windows 上跑得起来就行。

```
SysPanel.exe            直接双击运行
build.bat               从源码重新编译
src/                    C# 源码（Metrics / Native / PanelServer / Program）
web/                    面板界面（index.html + 550C 片头）
```

片头与本体是一套：面板启动时先播 550C，播完渐出，压暗模糊成界面底纹。

## 02-dsh-550c-boot

给 DeepSeek Harness（DSH）加一段开机片头。两种档位：

| 档位 | 时长 | 内容 |
|---|---|---|
| 简易 | ~4 秒 | 550C logo 逐路径书写 |
| 完整 | ~16 秒 | logo → 基站接管终端 → 47 节点逐点覆写 → `SYSTEM IS REWRITTEN` |

```
dsh-plugin/             插件本体（可直接装进 DSH）
web-panel/              独立网页版（脱离 DSH 也能播）
docs/踩过的坑.md          移植过程中踩到的坑与解法
tools/                  从原稿提取动画的脚本
```

安装：

```sh
dsh plugin --profile web add github:<你的用户名>/dsh-550c-suite
```

装完**重启 DSH**，模式开关在 **设置 → 通用 → 550C 开机动画**。

## 03-sidebar-patch

DSH 的侧边栏默认展开占 280px，这个小工具把它改成默认收起（0px），
并**同步重写 app.asar 的 SHA256**，让客户端不会因为校验失败而拒绝启动。

```
修补.bat                以管理员身份运行，打补丁
还原.bat                撤销，从备份恢复
patch-asar-sidebar2.mjs 实际的补丁逻辑（Node）
patch-sidebar.ps1       PowerShell 外壳
说明.md                  原理与注意事项
```

**先读 `说明.md` 再动手**，它写了备份策略和回退办法。

---

## 许可

各子目录各自的 LICENSE 为准，主体为 **MIT**。

## 致谢

- **Voidpoket**（B站 @图寻患者）—— 550C 片头动画与 HTML 原稿
- 面板与插件工程 —— 本项目作者
