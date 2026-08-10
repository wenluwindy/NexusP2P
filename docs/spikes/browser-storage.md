# 浏览器落盘能力实测（Task 0.2）

> 测于 2026-08-08，Windows 11，NVMe 系统盘剩余 105 GB。
> 代码：[`spike/BrowserStorage/`](../../spike/BrowserStorage/)。
> 复跑：`cd spike/BrowserStorage && npm install && node run.mjs --sizes 1,2,5 [--persistent]`

按 AD-6，**不做浏览器限制**。所以这个 spike 不是为了决定排除谁，
而是测出每种策略的实际上限，作为运行时自适应的依据，以及界面上那些数字的来源。

## 一、能力探测（不读 UA）

| | showSaveFilePicker | OPFS `createWritable` | 内存 Blob |
|---|---|---|---|
| Chromium 151 | ✅ | ✅ | ✅ |
| Edge 151 | ✅ | ✅ | ✅ |
| Chrome 151 | ✅ | ✅ | ✅ |
| Firefox 153 | **❌** | ✅ | ✅ |

**Firefox 没有 `showSaveFilePicker`。** 这意味着「流式落盘」不是所有人都有 ——
必须真的实现降级路径，而不是把它当成理所当然的默认。

全部用 `typeof window.showSaveFilePicker === 'function'` 这类特性判断得出，
**代码里没有任何一处读 UA 字符串**。

## 二、尺寸梯度：普通窗口

四个浏览器，OPFS 与 Blob **都写到了 5 GiB 并通过内容校验**。

| 浏览器 | 策略 | 1 GiB | 2 GiB | 5 GiB | 吞吐 |
|---|---|---|---|---|---|
| Chromium | OPFS | ✅ | ✅ | ✅ | ~120 MiB/s |
| Chromium | Blob | ✅ | ✅ | ✅ | ~145 MiB/s |
| Edge | OPFS | ✅ | ✅ | ✅ | ~120 MiB/s |
| Edge | Blob | ✅ | ✅ | ✅ | ~146 MiB/s |
| Chrome | OPFS | ✅ | ✅ | ✅ | ~121 MiB/s |
| Chrome | Blob | ✅ | ✅ | ✅ | ~146 MiB/s |
| Firefox | OPFS | ✅ | ✅ | ✅ | **~388 MiB/s** |
| Firefox | Blob | ✅ | ✅ | ✅ | **~427 MiB/s** |

吞吐最低的一档（120 MiB/s ≈ 960 Mbps）仍然远高于任何现实的跨公网速度。
**落盘不会是网页端的瓶颈**，别为这件事做优化。

## 三、内存峰值：这才是真正的分水岭

JS 堆峰值（Chrome / Edge 通道的实测值）：

| 文件大小 | OPFS | 内存 Blob |
|---|---|---|
| 1 GiB | 5 MiB | **1030 MiB** |
| 2 GiB | 9 MiB | **2074 MiB** |
| 5 GiB | 10 MiB | **5132 MiB** |

**Blob 的内存占用与文件大小 1:1，OPFS 恒定在 10 MiB 以内。**

这条曲线决定了两件事：

- 内存 Blob 只能是最后兜底，而且必须在界面上**明说上限**。
  一台 8 GB 内存的笔记本收 5 GiB 文件，浏览器标签页会先死。
- 用户要传的是 10~20 GB。**Blob 路径在这个量级上根本不成立** ——
  不是慢，是必然失败。

> 两个测量口径的坑，记下来免得下次误判：
> Playwright 自带的 Chromium 构建里 `performance.memory` 恒报 ~10 MiB，
> 是假的；Chrome / Edge 通道报的是真值。**Firefox 根本没有这个 API**，
> 所以表里 Firefox 一栏是 `null` 而不是 0 —— 没测到就是没测到。

## 四、无痕 / 临时上下文：配额会突然缩水

同一套代码，跑在临时上下文（近似无痕窗口）里：

| 浏览器 | `estimate().quota` 报告 | OPFS 实际 | Blob 实际 |
|---|---|---|---|
| Chromium | 6 GiB | **2 GiB 就 `QuotaExceededError`** | 2 GiB 就 `NotReadableError` |
| Edge | 6 GiB | **2 GiB 失败** | 2 GiB 失败 |
| Chrome | 6 GiB | **2 GiB 失败** | 2 GiB 失败 |
| Firefox | 10 GiB | 5 GiB 通过 | 5 GiB 通过 |

**`navigator.storage.estimate().quota` 报 6 GiB，而 1 GiB 能写、2 GiB 就炸。**

所以：

> **不要拿 `quota` 当承诺给用户看。** 它是一个上界的上界，
> 在无痕模式下能虚报三倍。按它显示「可以接收 6 GB」，
> 用户传到第 40 分钟才发现失败，而那时已经没有任何补救余地。

Firefox 在这一项上没有退化。

## 五、结论：运行时怎么选

判定顺序（按能力探测，逐级降级）：

```
1. showSaveFilePicker 可用  → 流式落盘。不占内存、不受配额限制，唯一能扛 20 GB 的路径
2. 否则 OPFS 可用           → OPFS。内存恒定，但受配额限制且不可预测
3. 否则                     → 内存 Blob。内存 = 文件大小，只适合小文件
```

每种策略要向用户展示的话术与上限：

| 策略 | 界面提示 | 建议上限 |
|---|---|---|
| 流式落盘 | 「直接写入你选的文件」 | 不设上限（受磁盘剩余空间限制） |
| OPFS | 「先存在浏览器里，收完再另存」**并提示配额可能不足** | 不承诺数字；**开传前先试占位** |
| 内存 Blob | 「整个文件会留在内存里」**并明确劝阻大文件** | 1 GiB，超过就明确警告 |

**「开传前先试占位」是这次实测最直接的产出。**
既然 `quota` 不可信，唯一可靠的做法是在**开始传之前**就按清单总大小
在 OPFS 里真的写一个占位文件；成功才开始，失败就当场告诉用户，
而不是让他等到第 40 分钟。exe 端的 `PieceStore.EnsureSpaceAvailable`
已经是这个思路，网页端要照做。

## 六、还没测的部分

诚实记录：

- **`showSaveFilePicker` 的实际上限没有自动测过。** 它必须由真实的用户手势
  触发并弹出系统保存对话框，自动化跑不了。页面上留了按钮，
  用浏览器打开 `spike/BrowserStorage/index.html` 手动点「跑流式落盘」即可。
  按其设计（直接写进用户选的文件、不进配额），预期上限就是磁盘剩余空间。
- **手机浏览器**没测。用户场景是电脑之间传大文件，优先级低。
- **Safari** 没测（没有 macOS 机器）。Safari 支持 OPFS，
  不支持 `showSaveFilePicker`，行为预计与 Firefox 一档接近 —— 但这是推测，不是实测。
- 5 GiB 以上没测。用户的真实场景是 10~20 GB，
  而那个量级在网页端**只有流式落盘一条路**，与其测 OPFS 在 10 GiB 上怎么死，
  不如把流式落盘做扎实。
