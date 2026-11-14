# 合成托管调研笔记

> 2026-11 记录。结论：**技术可行，但未能集成进现有宿主**；功能已从产品中移除（v1.2.0）。
> 这份文档保留验证过程、失败现象、排除过的假设和下一步的排查方法，供以后重启这条线时使用。

## 1. 要解决的问题

v1.1.0 的「背景不透明度」是用截屏伪造的：定时把自己从截图里排除，抓取窗口背后的画面做模糊底图。两个缺陷绕不过去：

- 背景是定时刷新的快照，背后窗口变化有约 1 秒延迟；
- 透明状态下组件不会出现在录屏与截图里。

真透明需要「背景与桌面实时合成、内容不透明」。窗口模式的 WebView2 做不到，因为它是一个不透明的子 HWND。

## 2. 机制

```
普通模式（窗口模式）
  页面 → Chromium 合成 → WebView2 子 HWND（不透明）→ DWM 原样贴上
                                                    ↑ alpha 在这里被吃掉

合成托管（visual hosting）
  页面 → Chromium 合成 → 带 alpha 的纹理
                             ↓
                    我们自己的 DirectComposition 视觉树
                             ↓
            窗口（WS_EX_NOREDIRECTIONBITMAP，无重定向表面）
                             ↓
                        DWM 与桌面合成
```

三个必要条件，缺一即退化为不透明：

1. 窗口带 `WS_EX_NOREDIRECTIONBITMAP` —— 不画重定向表面，否则 DWM 贴的是那块不透明表面；
2. 建好 DComp 视觉树并 **Commit**；
3. `CoreWebView2CompositionController` 的 `DefaultBackgroundColor = Transparent`、`RootVisualTarget = 我们的 visual`，再 **Commit**。

### 最小可用代码骨架

```csharp
[ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDCompositionDevice {
    void Commit();
    void WaitForCommitCompletion();
    void GetFrameStatistics(IntPtr p);
    void CreateTargetForHwnd(IntPtr hwnd, bool topmost, out IDCompositionTarget target);
    void CreateVisual(out IDCompositionVisual visual);
}
[ComImport, Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDCompositionTarget { void SetRoot(IDCompositionVisual visual); }
[ComImport, Guid("4D93059D-097B-4651-9A60-F0F25116E2F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDCompositionVisual { void SetOffsetX(float value); }   // 只为拿指针
[ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ID3D11Device { }
[ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDXGIDevice { }

// 建立视觉树
D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/,
    IntPtr.Zero, 0, 7, out d3d, out level, out ctx);
IDXGIDevice dxgi = (IDXGIDevice)d3d;
IntPtr dxgiPtr = Marshal.GetComInterfaceForObject(dxgi, typeof(IDXGIDevice));
DCompositionCreateDevice(dxgiPtr, ref iidDevice, out IntPtr dcompPtr);
var dcomp = (IDCompositionDevice)Marshal.GetObjectForIUnknown(dcompPtr);
dcomp.CreateTargetForHwnd(hwnd, true, out target);
dcomp.CreateVisual(out visual);
target.SetRoot(visual);
dcomp.Commit();                                  // ← 少了这句窗口全空

// 挂 WebView2
var controller = await env.CreateCoreWebView2CompositionControllerAsync(hwnd);
controller.DefaultBackgroundColor = Color.Transparent;
controller.RootVisualTarget = visual;
dcomp.Commit();                                  // ← 挂载后要再 Commit 一次
controller.Bounds = new Rectangle(0, 0, w, h);
controller.CoreWebView2.Navigate(url);
```

窗口一侧：

```csharp
protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x00200000 /*WS_EX_NOREDIRECTIONBITMAP*/; return cp; }
}
```

## 3. spike 的结论（一个最小窗口）

spike 是一个从零写的 420×300 无边框窗口：无托盘、无 `TopMost`、无第二个窗口、无 DWM 圆角，控制器在 `OnHandleCreated` 里创建。

| 指标 | 结果 |
| --- | --- |
| 页面透明处露出桌面 | ✅ 白窗口与深色终端都能透过卡片看到 |
| CSS 半透明/圆角/渐变 | ✅ 无锯齿、无色带 |
| 中文字形 | ✅ 像素级清晰 |
| 浏览器鼠标输入（`SendMouseInput`） | ❌ 事件从未进入页面 |
| 原生拖动（`WM_NCLBUTTONDOWN` + `HTCAPTION`） | ✅ 窗口位置完全跟随 |

最大的收获不在透明，而在最后两行：**卡片这类"显示 + 少量热区"的界面，完全可以绕开浏览器输入**（见第 6 节）。

## 4. 集成进正式程序后的失败

页面自检正常：

```json
{"bg":"rgba(0,0,0,0)","base":"transparent","alpha":"0.55","cls":"card glass","supportsCalc":true}
```

但卡片空白处像素：

| 条件 | 像素 |
| --- | --- |
| 不透明度 55% | `168,163,156` |
| 不透明度 35% | `168,163,156` |
| 压在纯黑窗口上 | `168,163,156` |
| 压在桌面/其他窗口上（三个不同位置） | `168,163,156` |

**"35% 与 55% 完全一样"比"背景不透明"更严重**：即使窗口不透明，页面自身的 alpha 也应让像素随滑块移动（卡片色 ↔ WebView 底色）。像素恒定说明 **alpha 链在页面级就已失效**，不是只有窗口合成这一环。

## 5. 已排除的假设

| 假设 | 处理方式 | 结果 |
| --- | --- | --- |
| DWM 圆角裁掉了合成内容 | 合成模式下不调 `DwmSetWindowAttribute(33)` | 无效 |
| 与设置窗口的窗口模式 WebView2 共用环境 | 独立 `CoreWebView2Environment` + 独立用户数据目录 | 无效 |
| 窗体 `BackColor` 画了实底 | 合成模式下不设置 `BackColor` | 无效 |

排除过程中顺带修掉的三个真 bug（都在提交历史里）：

1. 挂载 `RootVisualTarget` 后漏了一次 `dcomp.Commit()` → 窗口全空；
2. `.glass` 背景最后一层是实色 `var(--bg-1)`，把透明窗口又填满；
3. `composition` 标志从 `settings` 里取，实际是 `init` 响应的顶层字段 → 页面一直以为自己是窗口模式。

## 6. 输入：绕开浏览器的设计

`SendMouseInput` 在合成模式下没有把事件送进页面（加了多层日志确认），但对于本产品这不成问题——卡片只有四个交互点，全部可以在宿主侧实现：

| 交互 | 需要什么 |
| --- | --- |
| 拖动窗口 | `WM_LBUTTONDOWN` → `ReleaseCapture()` → `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)`，走系统拖动循环（spike 已验证） |
| 置顶 / 设置 / 隐藏 | 三个固定矩形，宿主自己做命中检测 |
| 右键菜单 | 宿主已有的 `ContextMenuStrip` |

页面通过 IPC 把自己的热区坐标报给宿主（CSS 像素），宿主按 `ClientSize.Width / 400` 换算成窗口像素后命中检测；悬停高亮可用一条轻量消息切 class。这样页面退化成纯显示层，完全不需要鼠标事件。

## 7. 下一步：二分排查

从能透的 spike 出发，逐项加回真实宿主的特性，每加一项就用"压在纯黑窗口上取样"复验。第一个让像素恒定的步骤就是元凶。

1. `TopMost = true`
2. 控制器创建时机从 `OnHandleCreated` 移到 `OnShown`
3. 同进程内预热第二个窗口模式 WebView2（设置页）
4. 加入 `NotifyIcon` 托盘
5. 换成真实的 `index.html`（`html.native`、滚动数字、canvas 粒子）

复验脚本要点：先 `SetWindowPos` 把组件移到 (200,200)，垫一个纯黑的顶层窗口，`CopyFromScreen` 取卡片空白处的像素；再换三个位置重复，像素若恒定即失败。

## 8. 如果重来

- **不要从现有宿主改，从合成骨架长出来**：先做"空窗口能透能拖"，再把托盘、设置页、页面逐项加进去；加不进去当场暴露，不会到最后拼装时才炸。
- **把设置窗口放进独立进程**：它与卡片只通过 `settings.json` 和广播通信，分进程可彻底排除"同进程第二个 WebView2"这类干扰。
- **先确认 spike 的边界**：spike 证明的是 API 组合能出 alpha，不是"能在成熟宿主里出 alpha"；这两件事之间隔着窗口生命周期。

## 9. 参考

- 相关提交：`v1.1.0`（引入截屏方案）、`be63bd2` / `91f22cd`（移除不透明度）
- 实验代码在本地 `git stash`（`WIP: 合成托管集成`），未入库
- WebView2 文档关键词：`CoreWebView2CompositionController`、`RootVisualTarget`、`IDCompositionVisual`、`WS_EX_NOREDIRECTIONBITMAP`
