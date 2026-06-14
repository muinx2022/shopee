import os
import subprocess
import sys
import time
from pathlib import Path

from playwright.sync_api import sync_playwright

from product_processor import process_product

BASE_DIR = Path(__file__).resolve().parents[1]
if str(BASE_DIR) not in sys.path:
    sys.path.insert(0, str(BASE_DIR))
from video_paths import get_video_dir

CONFIG = {
    "BRAVE_PATH": r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
    "PROFILE_DIR": r"D:\playwright\profile",
    "DEBUG_PORT": 9223,
    "TEST_EDIT_URL": "https://www.bigseller.com/web/listing/shopee/edit/963509790.htm?bsStatus=1",
    "CSV_FILE": "data.csv",
    "IMAGE_PATH": r"D:\images\1.jpeg",
    "VIDEO_FOLDER": r"D:\videos",
    "ADD_PRICE": 165000,
    "STOCK_VALUE": "30069",
    "WEIGHT_VAL": "500",
    "DELETE_IMAGES_AFTER": 10,
}


def log(msg):
    print(msg, flush=True)


def open_brave(url):
    log("Dong Brave cu...")
    try:
        if os.name == "nt":
            os.system("taskkill /f /im brave.exe >nul 2>&1")
        else:
            os.system("pkill -f brave")
        time.sleep(2)
    except Exception:
        pass

    log("Mo Brave voi trang test...")
    if not os.path.exists(CONFIG["PROFILE_DIR"]):
        os.makedirs(CONFIG["PROFILE_DIR"])

    command = [
        CONFIG["BRAVE_PATH"],
        f"--remote-debugging-port={CONFIG['DEBUG_PORT']}",
        f"--user-data-dir={CONFIG['PROFILE_DIR']}",
        "--no-first-run",
        "--no-default-browser-check",
        url,
    ]
    subprocess.Popen(command, shell=True)
    time.sleep(5)
    return True


if __name__ == "__main__":
    log("Bat dau test trang edit truc tiep...")
    p = None

    try:
        test_url = CONFIG["TEST_EDIT_URL"]
        if not open_brave(test_url):
            raise RuntimeError("Khong mo duoc Brave")

        p = sync_playwright().start()
        log("Ket noi Brave...")

        browser = None
        for _ in range(5):
            try:
                browser = p.chromium.connect_over_cdp(
                    f"http://127.0.0.1:{CONFIG['DEBUG_PORT']}",
                    timeout=30000,
                )
                break
            except Exception:
                time.sleep(3)

        if not browser:
            raise RuntimeError("Khong ket noi duoc Brave")

        context = browser.contexts[0]
        edit_page = None

        for page in context.pages:
            if "bigseller.com" in page.url:
                edit_page = page
                break

        if not edit_page:
            edit_page = context.new_page()

        edit_page.goto(test_url)
        edit_page.bring_to_front()
        edit_page.wait_for_load_state("domcontentloaded")
        time.sleep(5)

        log("Goi process_product() tren trang edit...")
        success = process_product(edit_page, None, CONFIG)
        log(f"Ket qua process_product: {success}")

    except Exception as e:
        log(f"Loi main_test: {e}")
    finally:
        if p:
            p.stop()
