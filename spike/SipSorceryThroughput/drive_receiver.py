"""驱动 Chromium 作为 WebRTC 接收端，跑完 spike 并把结果打出来。

用法： python drive_receiver.py [--port 5080] [--timeout 300]
前提： spike 服务端已在运行。
"""
import argparse
import sys
import time

from playwright.sync_api import sync_playwright

DONE_MARKERS = ("接收完成", "发送完成", "字节数不符", "分片校验失败", "通道提前关闭")

# Windows 控制台默认 GBK，直接 print 中文和 ✓ 会炸
for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except AttributeError:
        pass


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5080)
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--shot", default="/tmp/spike.png")
    ap.add_argument("--headed", action="store_true",
                    help="用有头模式跑，排除 headless 拖慢接收的可能")
    ap.add_argument("--channel", default=None,
                    help="用系统安装的浏览器，例如 chrome / msedge")
    args = ap.parse_args()

    with sync_playwright() as p:
        launch_kwargs = {"headless": not args.headed}
        if args.channel:
            launch_kwargs["channel"] = args.channel
        browser = p.chromium.launch(**launch_kwargs)
        page = browser.new_page()
        page.on("console", lambda m: print(f"  [console/{m.type}] {m.text}", flush=True))
        page.on("pageerror", lambda e: print(f"  [pageerror] {e}", flush=True))

        page.goto(f"http://localhost:{args.port}", wait_until="networkidle")
        print("页面已加载，等待传输完成…", flush=True)

        deadline = time.time() + args.timeout
        last_recv = ""
        while time.time() < deadline:
            log_text = page.locator("#log").inner_text()
            recv = page.locator("#recv").inner_text()
            if recv != last_recv:
                rate = page.locator("#rate").inner_text()
                print(f"  浏览器侧：已接收 {recv}，速率 {rate}", flush=True)
                last_recv = recv
            if any(marker in log_text for marker in DONE_MARKERS):
                break
            page.wait_for_timeout(1000)
        else:
            print("!! 超时：传输未在限定时间内结束", flush=True)
            page.screenshot(path=args.shot, full_page=True)
            browser.close()
            return 1

        page.wait_for_timeout(1500)
        page.screenshot(path=args.shot, full_page=True)

        print("\n--- 浏览器端日志 ---", flush=True)
        print(page.locator("#log").inner_text(), flush=True)
        print("\n--- 浏览器端指标 ---", flush=True)
        for label, sel in [("已接收", "#recv"), ("速率", "#rate"),
                           ("耗时", "#elapsed"), ("校验", "#seq"),
                           ("瓶颈", "#bottleneck")]:
            print(f"  {label}: {page.locator(sel).inner_text()}", flush=True)

        browser.close()
        return 0


if __name__ == "__main__":
    sys.exit(main())
