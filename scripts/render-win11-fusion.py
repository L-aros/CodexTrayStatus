from pathlib import Path
from playwright.sync_api import sync_playwright
root = Path(__file__).resolve().parents[1]
source = (root / "assets" / "douyin" / "win11-fusion-v2.html").as_uri()
out = root / "assets" / "douyin" / "01-taskbar-win11-faithful-v2.png"
with sync_playwright() as p:
    edge = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
    browser = p.chromium.launch(executable_path=str(edge))
    page = browser.new_page(viewport={"width": 1080, "height": 1440}, device_scale_factor=1)
    page.goto(source, wait_until="networkidle")
    page.screenshot(path=str(out), full_page=False)
    browser.close()
