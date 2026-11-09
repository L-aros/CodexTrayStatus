from pathlib import Path
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parents[1]
source = (root / "assets" / "douyin" / "faithful-posters.html").as_uri()
out = root / "assets" / "douyin"
names = {
    1: "01-taskbar-faithful.png",
    2: "02-overview-faithful.png",
    3: "03-statistics-faithful.png",
    4: "04-privacy-native-faithful.png",
}

with sync_playwright() as p:
    edge = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
    browser = p.chromium.launch(executable_path=str(edge))
    page = browser.new_page(viewport={"width": 1080, "height": 1440}, device_scale_factor=1)
    for number, filename in names.items():
        page.goto(f"{source}?poster={number}", wait_until="networkidle")
        page.screenshot(path=str(out / filename), full_page=False)
    browser.close()


