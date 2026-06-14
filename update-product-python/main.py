import time
import argparse
import subprocess
import os
import re
import shutil
import signal
import sys
from pathlib import Path
import urllib.request

SCRIPT_DIR = Path(__file__).resolve().parent
BASE_DIR = SCRIPT_DIR.parents[1]
for path in (str(SCRIPT_DIR), str(BASE_DIR)):
    if path not in sys.path:
        sys.path.insert(0, path)

# --- IMPORT MODULE ---
try:
    from product_processor import (
        DEFAULT_API_KEY_FILE_PATH,
        DEFAULT_MODEL,
        extract_bigseller_edit_id,
        inspect_edit_page_for_update,
        prepare_rewritten_product_names,
        process_product,
    )
    from image_manager import delete_all_images
except ImportError as error:
    print(f"ERROR: Khong tai duoc module BigSeller: {error}", flush=True)
    print(f"Chay: \"{sys.executable}\" -m pip install -r \"{SCRIPT_DIR / 'requirements.txt'}\"", flush=True)
    print(f"     \"{sys.executable}\" -m playwright install chromium", flush=True)
    raise SystemExit(1) from error

# ================= CẤU HÌNH =================
DEFAULT_BRAVE_PATH = (
    os.environ.get("BRAVE_PATH")
    or shutil.which("brave-browser")
    or shutil.which("brave")
    or shutil.which("google-chrome")
    or shutil.which("chromium")
    or r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"
)
CONFIG = {
    'BRAVE_PATH': DEFAULT_BRAVE_PATH,
    'PROFILE_DIR': os.environ.get("BIGSELLER_PROFILE_DIR", str(Path.home() / ".bigseller-playwright-profile")),
    'DEBUG_PORT': int(os.environ.get("BIGSELLER_DEBUG_PORT", "9223")),
    'BIGSELLER_URL': 'https://www.bigseller.com/web/crawl/index.htm',
    'LISTING_URL': os.environ.get(
        "BIGSELLER_LISTING_URL",
        "https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1",
    ),
    'CHECK_INTERVAL': 10,
    'DRAFT_RELOAD_SECONDS': int(os.environ.get("BIGSELLER_DRAFT_RELOAD_SECONDS", "20")),
    'WORKBOOK_PATH': os.environ.get("SHOPEE_WORKBOOK_PATH", str(BASE_DIR / "data" / "data.xlsx")),
    'DATA_SHEET': os.environ.get("SHOPEE_DATA_SHEET", "bizly"),
    'START_ROW': int(os.environ.get("SHOPEE_START_ROW", "2")),
    'END_ROW': int(os.environ.get("SHOPEE_END_ROW", "0") or "0"),
    'SHOP_NAME': os.environ.get("BIGSELLER_SHOP_NAME", "Bizly Store"),
    'MODEL': os.environ.get("OPENAI_PRODUCT_NAME_MODEL", DEFAULT_MODEL),
    'API_KEY_FILE': os.environ.get("OPENAI_API_KEY_FILE", str(DEFAULT_API_KEY_FILE_PATH)),
    'BATCH_SIZE': int(os.environ.get("OPENAI_PRODUCT_NAME_BATCH_SIZE", "40")),
    
    # Cấu hình Update
    'IMAGE_PATH': os.environ.get("BIGSELLER_IMAGE_PATH", r"D:\images\1.jpeg"),
    'VIDEO_FOLDER': os.environ.get(
        "SHOPEE_VIDEO_DIR",
        os.environ.get("BIGSELLER_VIDEO_FOLDER", r"D:\videos"),
    ),
    'ADD_PRICE': 165000,
    'STOCK_VALUE': "30069",
    'WEIGHT_VAL': "500",
    'DELETE_IMAGES_AFTER': 10,  # Xóa thư viện ảnh sau mỗi X sản phẩm
}
# ============================================

_STATUS_PAGES = []
_STATUS_MESSAGES = []
_STATUS_MAX_MESSAGES = 7


def _print_log(msg):
    try:
        print(msg, flush=True)
    except UnicodeEncodeError:
        encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
        print(str(msg).encode(encoding, errors="replace").decode(encoding), flush=True)


def _short_status_message(msg, max_len=240):
    text = re.sub(r"\s+", " ", str(msg or "")).strip()
    if not text or set(text) <= {"="}:
        return ""
    return text[: max_len - 1] + "…" if len(text) > max_len else text


def register_status_page(page):
    if page is None:
        return
    if not any(existing is page for existing in _STATUS_PAGES):
        _STATUS_PAGES.append(page)
    _render_status_overlay(page)


def _render_status_overlay(page):
    try:
        if page is None or page.is_closed():
            return
        page.evaluate(
            """
            ({ messages }) => {
                const id = 'bigseller-tools-status-overlay';
                let box = document.getElementById(id);
                if (!box) {
                    box = document.createElement('div');
                    box.id = id;
                    box.style.cssText = [
                        'position:fixed',
                        'left:12px',
                        'bottom:12px',
                        'z-index:2147483647',
                        'max-width:min(520px, calc(100vw - 24px))',
                        'max-height:38vh',
                        'overflow:hidden',
                        'padding:10px 12px',
                        'border-radius:8px',
                        'background:rgba(15, 23, 42, 0.92)',
                        'color:#f8fafc',
                        'box-shadow:0 12px 32px rgba(0,0,0,.32)',
                        'font:12px/1.45 Consolas, "Segoe UI", Arial, sans-serif',
                        'pointer-events:none',
                        'white-space:normal'
                    ].join(';');
                    document.documentElement.appendChild(box);
                }

                const latest = messages[messages.length - 1] || 'Đang chờ log...';
                const isWarn = /⚠|❌|failed|thất bại|lỗi|không/i.test(latest);
                box.innerHTML = '';

                const title = document.createElement('div');
                title.textContent = 'BigSeller Tools';
                title.style.cssText = [
                    'font-weight:700',
                    'font-size:12px',
                    'margin-bottom:5px',
                    `color:${isWarn ? '#fecaca' : '#bbf7d0'}`
                ].join(';');
                box.appendChild(title);

                for (const message of messages.slice(-7)) {
                    const line = document.createElement('div');
                    line.textContent = message;
                    line.style.cssText = [
                        'overflow:hidden',
                        'text-overflow:ellipsis',
                        'display:-webkit-box',
                        '-webkit-line-clamp:2',
                        '-webkit-box-orient:vertical',
                        'opacity:.96',
                        'margin-top:2px'
                    ].join(';');
                    box.appendChild(line);
                }
            }
            """,
            {"messages": _STATUS_MESSAGES[-_STATUS_MAX_MESSAGES:]},
        )
    except Exception:
        pass


def _push_status_overlay(msg):
    text = _short_status_message(msg)
    if not text:
        return
    _STATUS_MESSAGES.append(text)
    del _STATUS_MESSAGES[:-_STATUS_MAX_MESSAGES]

    alive_pages = []
    for page in _STATUS_PAGES:
        try:
            if page is not None and not page.is_closed():
                alive_pages.append(page)
                _render_status_overlay(page)
        except Exception:
            pass
    _STATUS_PAGES[:] = alive_pages


def log(msg):
    _print_log(msg)
    _push_status_overlay(msg)


def patch_module_logs():
    for module_name in (
        "product_processor",
        "product_runtime",
        "product_workbook",
        "product_name_rewrite",
        "bigseller_edit_page",
        "product_update_flow",
        "video_uploader",
        "ai_content_generator",
        "image_manager",
    ):
        module = sys.modules.get(module_name)
        if module is not None:
            try:
                module.log = log
            except Exception:
                pass


def close_page_accepting_dialog(page, timeout_ms=4000):
    if page is None:
        return True
    try:
        if page.is_closed():
            return True
    except Exception:
        return True

    def _accept_dialog(dialog):
        try:
            msg = (dialog.message or "").strip()
            if msg:
                log(f"⚠️ Browser alert khi đóng tab: {msg}")
            else:
                log("⚠️ Browser alert khi đóng tab edit (beforeunload / chưa lưu).")
        except Exception:
            log("⚠️ Browser alert khi đóng tab edit.")
        try:
            dialog.accept()
        except Exception:
            try:
                dialog.dismiss()
            except Exception:
                pass

    try:
        page.once("dialog", _accept_dialog)
    except Exception:
        pass

    # Đóng nhanh trước — tránh treo 5s vì beforeunload khi tab không có thay đổi.
    try:
        page.close()
    except Exception:
        pass

    deadline = time.time() + (timeout_ms / 1000)
    while time.time() < deadline:
        try:
            if page.is_closed():
                return True
        except Exception:
            return True
        time.sleep(0.15)

    try:
        page.once("dialog", _accept_dialog)
    except Exception:
        pass
    try:
        page.close(run_before_unload=True)
    except TypeError:
        try:
            page.close()
        except Exception:
            pass
    except Exception:
        pass

    try:
        return page.is_closed()
    except Exception:
        return True


def _hydrate_openai_api_key_from_config():
    """Đưa API key từ file config vào env để rewrite tên + mô tả AI dùng chung."""
    if (os.environ.get("OPENAI_API_KEY") or "").strip():
        return
    try:
        from process_data.process_sheet_data import get_openai_api_key

        key = get_openai_api_key(Path(CONFIG["API_KEY_FILE"]))
        if key:
            os.environ["OPENAI_API_KEY"] = key.strip()
    except Exception:
        pass


def apply_cli_args():
    parser = argparse.ArgumentParser(description="Import and update BigSeller products from XLSX data.")
    parser.add_argument("--sheet", default=CONFIG["DATA_SHEET"], help="Sheet trong data/data.xlsx để lookup sản phẩm.")
    parser.add_argument("--start-row", type=int, default=CONFIG["START_ROW"], help="Dòng bắt đầu đọc dữ liệu trong sheet XLSX.")
    parser.add_argument("--end-row", type=int, default=CONFIG["END_ROW"], help="Dòng kết thúc. 0 = tới cuối sheet.")
    parser.add_argument("--shop", default=CONFIG["SHOP_NAME"], help="Tên shop trong modal Import to Stores.")
    parser.add_argument("--workbook", default=CONFIG["WORKBOOK_PATH"], help="Đường dẫn workbook XLSX.")
    parser.add_argument("--image", default=CONFIG["IMAGE_PATH"], help="Ảnh mặc định để upload.")
    parser.add_argument("--video-folder", default=CONFIG["VIDEO_FOLDER"], help="Thư mục video theo SKU.")
    parser.add_argument("--debug-port", type=int, default=CONFIG["DEBUG_PORT"], help="Chrome/Brave remote debugging port.")
    parser.add_argument("--listing-url", default=CONFIG["LISTING_URL"], help="URL danh sách Shopee (bsStatus=1) để quét sản phẩm.")
    parser.add_argument("--draft-reload-seconds", type=int, default=CONFIG["DRAFT_RELOAD_SECONDS"], help="Chu kỳ reload danh sách Shopee để lấy item mới (giây).")
    parser.add_argument("--model", default=CONFIG["MODEL"], help="OpenAI model dùng để rewrite tên sản phẩm.")
    parser.add_argument("--api-key-file", default=CONFIG["API_KEY_FILE"], help="File chứa OpenAI API key.")
    parser.add_argument("--batch-size", type=int, default=CONFIG["BATCH_SIZE"], help="Số tên sản phẩm unique gửi mỗi batch.")
    parser.add_argument("--name-only", action="store_true", help="Chỉ cập nhật cột tên sản phẩm đã sửa trong XLSX rồi dừng.")
    parser.add_argument("--skip-name-update", action="store_true", help="Bỏ qua bước cập nhật cột tên sản phẩm đã sửa trong XLSX.")
    args = parser.parse_args()
    CONFIG["DATA_SHEET"] = args.sheet
    CONFIG["START_ROW"] = max(2, args.start_row)
    CONFIG["END_ROW"] = max(0, int(args.end_row or 0))
    CONFIG["SHOP_NAME"] = args.shop
    CONFIG["WORKBOOK_PATH"] = args.workbook
    CONFIG["IMAGE_PATH"] = args.image
    CONFIG["VIDEO_FOLDER"] = args.video_folder
    CONFIG["DEBUG_PORT"] = args.debug_port
    CONFIG["LISTING_URL"] = args.listing_url
    CONFIG["DRAFT_RELOAD_SECONDS"] = max(3, int(args.draft_reload_seconds))
    CONFIG["MODEL"] = args.model
    CONFIG["API_KEY_FILE"] = args.api_key_file
    CONFIG["BATCH_SIZE"] = max(1, args.batch_size)
    CONFIG["NAME_ONLY"] = args.name_only
    CONFIG["SKIP_NAME_UPDATE"] = args.skip_name_update


def find_draft_row_by_name(page, product_name, timeout_ms=30000):
    """Find the draft table row for the recently imported product by name."""
    deadline = time.time() + (timeout_ms / 1000)
    normalized_name = (product_name or "").strip()

    while time.time() < deadline:
        try:
            page.wait_for_selector("tbody.ant-table-tbody tr", timeout=5000)

            if normalized_name:
                matching_row = page.locator(
                    "tbody.ant-table-tbody tr",
                    has_text=normalized_name
                ).first
                if matching_row.count() > 0 and matching_row.is_visible():
                    return matching_row
        except:
            pass

        time.sleep(1)

    return None

def clear_brave_session_tabs(profile_dir):
    """Remove Chromium session files so Brave opens only the requested URL."""
    profile_path = Path(profile_dir)
    session_patterns = [
        "Current Session",
        "Current Tabs",
        "Last Session",
        "Last Tabs",
        "Session_*",
        "Tabs_*",
    ]
    candidate_dirs = [profile_path, profile_path / "Default"]
    candidate_dirs.extend(profile_path.glob("Profile *"))

    removed_count = 0
    for candidate_dir in candidate_dirs:
        sessions_dir = candidate_dir / "Sessions"
        search_dirs = [candidate_dir]
        if sessions_dir.exists():
            search_dirs.append(sessions_dir)

        for search_dir in search_dirs:
            if not search_dir.exists():
                continue
            for pattern in session_patterns:
                for session_file in search_dir.glob(pattern):
                    try:
                        if session_file.is_file():
                            session_file.unlink()
                            removed_count += 1
                    except Exception as e:
                        log(f"⚠️ Không xoá được session file {session_file}: {e}")

    if removed_count:
        log(f"🧹 Đã xoá {removed_count} file session/tab cũ.")


def _normalize_profile_token(profile_dir):
    return str(Path(profile_dir).expanduser().resolve()).lower()


def is_cdp_ready(debug_port, timeout_sec=2):
    try:
        with urllib.request.urlopen(
            f"http://127.0.0.1:{int(debug_port)}/json/version",
            timeout=timeout_sec,
        ) as response:
            return response.status == 200
    except Exception:
        return False


def wait_for_cdp_ready(debug_port, timeout_sec=45):
    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        if is_cdp_ready(debug_port):
            return True
        time.sleep(0.5)
    return False


def _extract_user_data_dir(command_line):
    if not command_line:
        return ""

    lowered = command_line.lower()
    flag = "--user-data-dir="
    idx = lowered.find(flag)
    if idx < 0:
        return ""

    value = command_line[idx + len(flag):].lstrip()
    if not value:
        return ""

    if value[0] == '"':
        end = value.find('"', 1)
        return value[1:end] if end > 0 else value[1:]
    if value[0] == "'":
        end = value.find("'", 1)
        return value[1:end] if end > 0 else value[1:]

    token = value.split()[0]
    return token.strip('"')


def _command_line_matches_brave_profile(command_line, profile_path, debug_port):
    if not command_line:
        return False

    expected_dir = _normalize_profile_token(profile_path)
    actual_dir = _normalize_profile_token(_extract_user_data_dir(command_line))
    if expected_dir and actual_dir and actual_dir == expected_dir:
        return True

    normalized = command_line.lower()
    return f"--remote-debugging-port={int(debug_port)}" in normalized


def _find_brave_pids_windows(profile_path, debug_port):
    matched_pids = []
    try:
        ps_script = (
            "Get-CimInstance Win32_Process -Filter \"Name='brave.exe'\" | "
            "Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress"
        )
        result = subprocess.run(
            ["powershell", "-NoProfile", "-Command", ps_script],
            capture_output=True,
            text=True,
            timeout=30,
            encoding="utf-8",
            errors="replace",
        )
        if result.returncode != 0:
            return matched_pids

        raw = (result.stdout or "").strip()
        if not raw:
            return matched_pids

        import json

        payload = json.loads(raw)
        rows = payload if isinstance(payload, list) else [payload]
        for row in rows:
            if not isinstance(row, dict):
                continue
            command_line = row.get("CommandLine") or ""
            if not _command_line_matches_brave_profile(command_line, profile_path, debug_port):
                continue
            pid = row.get("ProcessId")
            if pid:
                matched_pids.append(int(pid))
    except Exception as e:
        log(f"⚠️ Không quét được Brave trên Windows: {e}")

    return sorted(set(matched_pids))


def find_brave_processes_for_profile(profile_dir, debug_port):
    profile_path = str(Path(profile_dir).expanduser().resolve())
    debug_port_arg = f"--remote-debugging-port={debug_port}"
    matched_pids = []

    if os.name == "nt":
        return _find_brave_pids_windows(profile_path, debug_port)

    proc_root = Path("/proc")
    for proc_dir in proc_root.iterdir():
        if not proc_dir.name.isdigit():
            continue

        cmdline_path = proc_dir / "cmdline"
        try:
            raw_cmdline = cmdline_path.read_bytes()
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue

        if not raw_cmdline:
            continue

        cmdline = raw_cmdline.replace(b"\x00", b" ").decode("utf-8", errors="ignore")
        if "brave" not in cmdline.lower():
            continue

        has_profile = (
            _normalize_profile_token(_extract_user_data_dir(cmdline))
            == _normalize_profile_token(profile_path)
        )
        has_debug_port = debug_port_arg in cmdline
        if has_profile or has_debug_port:
            matched_pids.append(int(proc_dir.name))

    return matched_pids


def terminate_brave_profile(profile_dir, debug_port):
    pids = find_brave_processes_for_profile(profile_dir, debug_port)
    if not pids:
        log("🧹 Không có Brave BigSeller cũ cần đóng.")
        return

    log(f"🧹 Đóng Brave Update (profile/port riêng): {len(pids)} process.")
    for pid in pids:
        try:
            if os.name == "nt":
                subprocess.run(
                    ["taskkill", "/PID", str(pid), "/T", "/F"],
                    capture_output=True,
                    text=True,
                    timeout=15,
                )
            else:
                os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        except Exception as e:
            log(f"⚠️ Không đóng được PID {pid}: {e}")

    deadline = time.time() + 8
    while time.time() < deadline:
        alive_pids = find_brave_processes_for_profile(profile_dir, debug_port)
        if not alive_pids:
            time.sleep(0.5)
            return
        time.sleep(0.3)

    for pid in find_brave_processes_for_profile(profile_dir, debug_port):
        try:
            if os.name == "nt":
                subprocess.run(
                    ["taskkill", "/PID", str(pid), "/T", "/F"],
                    capture_output=True,
                    text=True,
                    timeout=15,
                )
            else:
                os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        except Exception as e:
            log(f"⚠️ Không force-kill được PID {pid}: {e}")


def open_brave():
    debug_port = int(CONFIG["DEBUG_PORT"])
    profile_dir = CONFIG["PROFILE_DIR"]

    if is_cdp_ready(debug_port):
        log(f"✅ Brave CDP port {debug_port} đã sẵn sàng — dùng instance hiện có.")
        return True

    log("🧹 Dọn dẹp Brave BigSeller cũ...")
    terminate_brave_profile(profile_dir, debug_port)
    time.sleep(0.5)

    if not os.path.exists(profile_dir):
        os.makedirs(profile_dir)
    clear_brave_session_tabs(profile_dir)

    if not os.path.exists(CONFIG["BRAVE_PATH"]):
        log(f"❌ Không tìm thấy Brave: {CONFIG['BRAVE_PATH']}")
        return False

    log("🦁 Mở Brave mới...")
    command = [
        CONFIG["BRAVE_PATH"],
        f"--remote-debugging-port={debug_port}",
        f"--user-data-dir={profile_dir}",
        "--no-first-run",
        "--no-default-browser-check",
        "--no-session-restore",
        "--restore-last-session=false",
        "--disable-session-crashed-bubble",
        "--disable-gpu",
        "--disable-dev-shm-usage",
        "--disable-software-rasterizer",
        CONFIG["LISTING_URL"],
    ]
    subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    if wait_for_cdp_ready(debug_port, timeout_sec=45):
        log(f"✅ Brave CDP port {debug_port} sẵn sàng.")
        return True

    log(f"❌ Không kết nối được Brave CDP port {debug_port} sau 45s.")
    return False

# --- HÀM XÓA SẢN PHẨM TRÊN LISTING ---
def extract_listing_edit_id(edit_url):
    """Alias: BigSeller edit id từ URL tab edit (không phải Shopee ID)."""
    return extract_bigseller_edit_id(edit_url)


def find_listing_row_on_current_page(listing_page, edit_url=None):
    """Tìm dòng theo BigSeller edit id (ổn định hơn row_key khi F5/import)."""
    listing_edit_id = extract_listing_edit_id(edit_url)
    if not listing_edit_id:
        return None

    rows = listing_page.locator("tbody.ant-table-tbody tr")
    for index in range(rows.count()):
        row = rows.nth(index)
        try:
            row_html = row.evaluate("el => el.outerHTML") or ""
        except Exception:
            row_html = ""
        if listing_edit_id in row_html:
            return row
    return None


def find_listing_row_across_pages(listing_page, edit_url, max_pages=30):
    """Quét từng trang listing theo edit id — dùng khi mất reference row."""
    listing_edit_id = extract_listing_edit_id(edit_url)
    if not listing_edit_id:
        return None

    _pagination_go_first(listing_page)
    time.sleep(0.8)
    for _ in range(max_pages):
        row = find_listing_row_on_current_page(listing_page, edit_url=edit_url)
        if row is not None:
            return row
        if not _pagination_next_enabled(listing_page):
            break
        _pagination_go_next(listing_page)
    return None


def delete_listing_row_safe(listing_page, edit_url, row=None, restore_page=1):
    """
    Xóa dòng listing theo thứ tự ưu tiên:
    1) row vừa click edit (reference trực tiếp, nhanh nhất)
    2) edit id trên trang hiện tại
    3) quét pagination theo edit id
    """
    listing_page.bring_to_front()

    if row is not None:
        try:
            if row.locator("a.action_btn.addEditProduct").count() > 0:
                log("🗑️ Xóa bằng dòng listing vừa click edit...")
                if delete_listing_row_actions(listing_page, row):
                    return True
        except Exception as error:
            log(f"⚠️ Xóa bằng row reference lỗi: {error}")

    target = find_listing_row_on_current_page(listing_page, edit_url=edit_url)
    if target is not None:
        log(f"🗑️ Xóa bằng edit_id trên trang hiện tại ({extract_listing_edit_id(edit_url)})...")
        if delete_listing_row_actions(listing_page, target):
            return True

    log(f"🔎 Không thấy dòng trên trang hiện tại, quét pagination theo edit_id={extract_listing_edit_id(edit_url)}...")
    target = find_listing_row_across_pages(listing_page, edit_url)
    if target is not None and delete_listing_row_actions(listing_page, target):
        if restore_page == 1:
            _pagination_go_first(listing_page)
        return True

    log(f"⚠️ Không tìm thấy dòng listing để xóa (edit_url={edit_url}).")
    return False


def delete_listing_row_actions(listing_page, row_elem):
    """Click icon Xóa trên dòng listing -> xác nhận Xóa trong modal."""
    try:
        delete_btn = row_elem.locator("a.action_btn[title='Xóa']").first
        if delete_btn.count() == 0:
            delete_btn = row_elem.locator("a[title='Xóa']").first
        if delete_btn.count() == 0:
            delete_btn = row_elem.locator("a.action_btn:has(span.bsicon_trash_2)").first
        if delete_btn.count() == 0:
            log("⚠️ Không tìm thấy nút Xóa trên dòng listing.")
            return False

        delete_btn.click(timeout=5000)
        confirm_btn = listing_page.locator(".ant-modal-confirm-btns button").filter(has_text="Xóa").first
        if confirm_btn.count() == 0:
            confirm_btn = listing_page.locator(".ant-modal-confirm-btns button.ant-btn-primary").first
        confirm_btn.wait_for(state="visible", timeout=5000)
        confirm_btn.click()
        time.sleep(1.5)
        return True
    except Exception as e:
        log(f"⚠️ Lỗi khi xóa dòng listing: {e}")
        return False


def delete_product_real(page, row_elem):
    """Giữ tương thích cũ."""
    return delete_listing_row_actions(page, row_elem)


def dismiss_blocking_modal(page):
    """Đóng modal ant-design (confirm/thông báo) đang che listing để click không bị chặn."""
    closed_any = False
    for _ in range(3):
        try:
            wrap = page.locator("div.ant-modal-wrap:visible").first
            if wrap.count() == 0 or not wrap.is_visible():
                break
        except Exception:
            break

        log("⚠️ Phát hiện modal đang che listing -> đóng modal...")
        dismissed = False
        # Ưu tiên nút hủy/đóng để không kích hoạt hành động nguy hiểm trong modal lạ.
        for selector in (
            ".ant-modal-confirm-btns button:not(.ant-btn-primary)",
            ".ant-modal-close",
            ".ant-modal-footer button:not(.ant-btn-primary)",
            ".ant-modal-confirm-btns button",
            ".ant-modal-footer button",
        ):
            try:
                btn = wrap.locator(selector).first
                if btn.count() > 0 and btn.is_visible():
                    btn.click(timeout=2000)
                    dismissed = True
                    break
            except Exception:
                continue
        if not dismissed:
            try:
                page.keyboard.press("Escape")
            except Exception:
                break
        closed_any = True
        time.sleep(0.6)
    return closed_any


# Lý do skip -> đóng tab edit, quay listing, xóa dòng (một hàm dùng chung).
LISTING_ROW_DELETE_REASONS = {
    "missing_product_name": "cột F và G trống (thiếu tên SP)",
    "missing_shopee_id": "không có link Shopee hợp lệ trong ô nguồn",
    "shopee_blocked": "link Shopee captcha/traffic (không lấy được ID)",
    "not_in_xlsx": "Shopee ID không có trong XLSX",
    "save_failed": "không lưu được sau nhiều lần thử",
    "session_skip": "đã xử lý/skip trong phiên này",
}


def should_delete_listing_row(status):
    """Mọi trường hợp không update được đều xóa dòng khỏi listing."""
    return (status or "").strip() != "needs_update"


def close_edit_tab_and_delete_listing_row(listing_page, row, edit_page=None, reason=""):
    """Đóng tab edit (nếu có), quay listing, xóa một dòng trên bảng BigSeller."""
    try:
        if edit_page is not None and not edit_page.is_closed():
            close_page_accepting_dialog(edit_page)
    except Exception:
        pass

    try:
        listing_page.bring_to_front()
        time.sleep(0.8)
    except Exception:
        pass

    label = (reason or "không đủ điều kiện update").strip()
    log(f"🗑️ {label} -> xóa dòng khỏi danh sách BigSeller...")
    if delete_listing_row_actions(listing_page, row):
        log("✅ Đã xóa dòng listing.")
        return True

    log("⚠️ Không xóa được dòng listing; giữ skip trong phiên để tránh mở lại.")
    return False


def handle_listing_row_delete_for_status(
    listing_page,
    row,
    edit_page,
    status,
    *,
    reason="",
    remember_skipped=None,
    shopee_id="",
    bigseller_edit_id="",
    listing_row_key=None,
):
    """
    Đóng tab edit + xóa dòng listing khi không update (mọi lý do skip).
    Returns: None (needs_update), 'deleted', hoặc 'skipped'.
    """
    status = (status or "").strip()
    if not should_delete_listing_row(status):
        return None

    label = (reason or LISTING_ROW_DELETE_REASONS.get(status) or f"skip ({status or 'unknown'})").strip()
    log(f"⛔ {label} -> đóng tab edit và xóa dòng listing.")

    if remember_skipped is not None:
        remember_skipped(
            shopee_id=shopee_id,
            edit_id=bigseller_edit_id,
            row_key=listing_row_key,
        )

    if close_edit_tab_and_delete_listing_row(listing_page, row, edit_page, reason=label):
        return "deleted"
    return "skipped"


def is_crawl_page(page):
    return "bigseller.com/web/crawl/index.htm" in (page.url or "")


def is_crawl_page_ready(page, timeout_ms=8000):
    if not is_crawl_page(page):
        return False

    ready_selector = (
        "a.action_btn[title='Import to Stores'], "
        "tbody.ant-table-tbody tr, "
        ".ant-empty, "
        ".ant-table-placeholder"
    )
    try:
        page.wait_for_selector(ready_selector, state="attached", timeout=timeout_ms)
        return True
    except Exception:
        return False


def stop_page_loading(page):
    try:
        page.evaluate("window.stop()")
    except Exception:
        pass


def navigate_to_crawl_url(page):
    stop_page_loading(page)
    try:
        page.goto(
            CONFIG["BIGSELLER_URL"],
            wait_until="commit",
            timeout=12000,
        )
        return True
    except Exception as commit_error:
        log(f"⚠️ Commit navigation lỗi, thử chuyển bằng JS: {commit_error}")

    try:
        page.evaluate("url => { window.location.href = url; }", CONFIG["BIGSELLER_URL"])
        page.wait_for_url("**/web/crawl/index.htm**", wait_until="commit", timeout=12000)
        return True
    except Exception as js_error:
        log(f"⚠️ JS navigation lỗi: {js_error}")

    try:
        page.goto(
            CONFIG["BIGSELLER_URL"],
            wait_until="domcontentloaded",
            timeout=12000,
        )
        return True
    except Exception as dom_error:
        log(f"⚠️ DOM navigation lỗi: {dom_error}")
        return False


def go_to_crawl_page(page, force_reload=False, max_attempts=3):
    if is_crawl_page(page) and not force_reload:
        if is_crawl_page_ready(page, timeout_ms=3000):
            return True
        log("⚠️ Đang ở URL Crawl List nhưng nội dung chưa sẵn sàng, reload lại...")

    for attempt in range(1, max_attempts + 1):
        log("🔙 Đang chuyển về trang Crawl List...")
        try:
            if not navigate_to_crawl_url(page):
                raise TimeoutError("Không commit được navigation tới Crawl List.")
            if is_crawl_page_ready(page, timeout_ms=10000):
                time.sleep(2)
                return True
            raise TimeoutError("Crawl List URL opened but table/import controls are not ready.")
        except Exception as e:
            current_url = page.url or ""
            if is_crawl_page(page) and is_crawl_page_ready(page, timeout_ms=3000):
                log(f"⚠️ Crawl List đã mở, bỏ qua lỗi load phụ: {e}")
                time.sleep(2)
                return True

            log(f"⚠️ Chuyển Crawl List lỗi lần {attempt}/{max_attempts}: {e}")
            if attempt < max_attempts:
                stop_page_loading(page)
                time.sleep(3)
            else:
                log(f"❌ Không mở được Crawl List. URL hiện tại: {current_url}")
                return False

# --- SHOPEE LISTING (bsStatus=1) ---
def is_draft_page(page):
    url = (page.url or "").lower()
    compact_url = url.replace(" ", "")
    return (
        "bigseller.com/web/listing/shopee/" in compact_url
        and "/edit/" not in compact_url
        and "bsstatus=1" in compact_url
    )


def is_draft_page_ready(page, timeout_ms=8000):
    ready_selector = (
        "tbody.ant-table-tbody tr, "
        ".ant-empty, "
        ".ant-table-placeholder, "
        "a.action_btn.addEditProduct"
    )
    try:
        page.wait_for_selector(ready_selector, state="attached", timeout=timeout_ms)
        return True
    except Exception:
        return False


def navigate_to_listing_url(page):
    listing_url = CONFIG["LISTING_URL"]
    stop_page_loading(page)
    try:
        page.goto(listing_url, wait_until="domcontentloaded", timeout=45000)
        return True
    except Exception as goto_error:
        log(f"⚠️ goto listing lỗi, thử JS: {goto_error}")
    try:
        page.evaluate("url => { window.location.href = url; }", listing_url)
        page.wait_for_url("**/web/listing/shopee/**", wait_until="domcontentloaded", timeout=30000)
        return True
    except Exception as js_error:
        log(f"⚠️ JS listing navigation lỗi: {js_error}")
        return False


def go_to_draft_page(page, force_reload=False, max_attempts=3):
    """Mở thẳng trang danh sách Shopee (bsStatus=1) để quét sản phẩm — không qua Crawl/import/Hộp nháp."""
    if is_draft_page(page) and not force_reload:
        if is_draft_page_ready(page, timeout_ms=3000):
            return True
        log("⚠️ Đang ở danh sách Shopee nhưng bảng chưa sẵn sàng, reload một lần...")
        try:
            page.reload(wait_until="domcontentloaded", timeout=30000)
            time.sleep(1.5)
        except Exception:
            pass
        if is_draft_page_ready(page, timeout_ms=15000):
            time.sleep(1)
            return True

    for attempt in range(1, max_attempts + 1):
        try:
            if is_draft_page(page):
                if force_reload:
                    log("🔄 Reload danh sách Shopee (đã ở đúng URL)...")
                    try:
                        page.reload(wait_until="domcontentloaded", timeout=30000)
                        time.sleep(1.5)
                    except Exception:
                        pass
                elif is_draft_page_ready(page, timeout_ms=5000):
                    time.sleep(1)
                    return True
                else:
                    log("🔄 Reload danh sách Shopee (chờ bảng sẵn sàng)...")
                    try:
                        page.reload(wait_until="domcontentloaded", timeout=30000)
                        time.sleep(1.5)
                    except Exception:
                        pass
            else:
                log(f"📝 Mở danh sách Shopee: {CONFIG['LISTING_URL']}")
                if not navigate_to_listing_url(page):
                    raise TimeoutError("Không điều hướng được tới listing Shopee.")

            if is_draft_page_ready(page, timeout_ms=20000):
                time.sleep(1)
                return True

            raise TimeoutError("Listing page opened but table/edit controls are not ready.")
        except Exception as e:
            log(f"⚠️ Mở danh sách Shopee lỗi lần {attempt}/{max_attempts}: {e}")
            if attempt < max_attempts:
                stop_page_loading(page)
                time.sleep(3)
            else:
                log(f"❌ Không mở được danh sách Shopee. URL hiện tại: {page.url or ''}")
                return False

def _draft_row_key(row_locator):
    """
    Tạo key tương đối ổn định để tránh schedule trùng.
    Ưu tiên data-row-key nếu có, fallback sang text rút gọn.
    """
    try:
        key = row_locator.get_attribute("data-row-key")
        if key:
            return f"key:{key}"
    except Exception:
        pass
    try:
        txt = (row_locator.inner_text() or "").strip()
        txt = re.sub(r"\s+", " ", txt)
        return f"txt:{txt[:200]}"
    except Exception:
        return None


def _pagination_next_enabled(page):
    try:
        next_btn = page.locator("li.ant-pagination-next").first
        if next_btn.count() == 0:
            return False
        cls = (next_btn.get_attribute("class") or "")
        return "ant-pagination-disabled" not in cls
    except Exception:
        return False


def _pagination_go_next(page):
    try:
        next_btn = page.locator("li.ant-pagination-next button, li.ant-pagination-next a").first
        if next_btn.count() == 0:
            return False
        next_btn.click(timeout=5000)
        time.sleep(1.2)
        return True
    except Exception:
        return False


def _pagination_go_first(page):
    try:
        first_btn = page.locator("li.ant-pagination-item-1 a, li.ant-pagination-item-1").first
        if first_btn.count() == 0:
            return False
        first_btn.click(timeout=5000)
        time.sleep(1.2)
        return True
    except Exception:
        return False


def _pagination_current_page_label(page):
    try:
        active = page.locator("li.ant-pagination-item-active").first
        if active.count() == 0:
            return "?"
        return (active.inner_text() or "").strip() or "?"
    except Exception:
        return "?"


LISTING_PAGE_EXHAUSTED = "page_exhausted"


def advance_listing_after_page_exhausted(page):
    """Hết dòng xử lý được trên trang hiện tại -> sang trang sau hoặc quay trang 1."""
    current = _pagination_current_page_label(page)
    if _pagination_go_next(page):
        nxt = _pagination_current_page_label(page)
        log(f"📄 Trang {current} xong — chuyển sang trang {nxt}.")
        time.sleep(1)
        return "next"

    log(f"📄 Trang {current} xong — hết trang listing, quay về trang 1 và đợi.")
    _pagination_go_first(page)
    time.sleep(1)
    return "wrap"



def log_listing_row_summary(row):
    try:
        name_cell = row.locator("a, span").first
        product_name_log = (name_cell.text_content() or "").strip()[:50]
        if product_name_log:
            log(f"listing row: {product_name_log}...")
        else:
            log("listing row")
    except Exception:
        log("listing row")


class SequentialUpdateRunner:
    def __init__(self, config, browser_context):
        self.config = config
        self.browser_context = browser_context
        self.edit_seq = 0
        self.update_count = 0
        self.skipped_shopee_ids = set()
        self.skipped_edit_ids = set()
        self.skipped_row_keys = set()
        self.save_fail_counts = {}
        self.max_save_fail_retries = 2
        self.click_blocked_streak = 0
        self.click_blocked_total = 0

    def run_first_listing_row(self, listing_page):
        try:
            if listing_page.is_closed():
                log("❌ Tab danh sách Shopee đã đóng.")
                return False
        except Exception:
            return False

        rows = listing_page.locator("tbody.ant-table-tbody tr")
        count = rows.count()
        if count == 0:
            return None

        for row_index in range(count):
            row = rows.nth(row_index)
            try:
                row.wait_for(timeout=15000)
            except Exception as e:
                if "closed" in str(e).lower():
                    log(f"❌ Tab listing đóng khi quét row #{row_index + 1}: {e}")
                    return False
                raise
            log_listing_row_summary(row)

            edit_link = row.locator("a.action_btn.addEditProduct").first
            if edit_link.count() == 0:
                log(f"Khong tim thay nut edit o listing row #{row_index + 1}, bo qua.")
                continue

            listing_row_key = _draft_row_key(row)
            if listing_row_key and listing_row_key in self.skipped_row_keys:
                log(f"Listing row #{row_index + 1} da skip trong phien -> xoa tren listing.")
                if delete_listing_row_actions(listing_page, row):
                    log("✅ Đã xóa dòng listing.")
                    return "deleted"
                continue

            listing_edit_id = self._get_edit_link_id(edit_link)
            if listing_edit_id and listing_edit_id in self.skipped_edit_ids:
                log(f"BigSeller edit_id {listing_edit_id} da skip trong phien -> xoa tren listing.")
                if delete_listing_row_actions(listing_page, row):
                    log("✅ Đã xóa dòng listing.")
                    return "deleted"
                continue

            result = self._run_listing_row(listing_page, row, edit_link, row_index, listing_edit_id, listing_row_key)
            if result == "skipped":
                continue
            if result == "retry":
                return "retry"
            return result

        page_no = _pagination_current_page_label(listing_page)
        log(
            f"Khong con row hop le tren trang {page_no}. "
            f"Da skip {len(self.skipped_shopee_ids)} Shopee ID, "
            f"{len(self.skipped_edit_ids)} BigSeller edit_id, "
            f"{len(self.skipped_row_keys)} listing row trong phien nay."
        )
        return LISTING_PAGE_EXHAUSTED

    def _get_edit_link_id(self, edit_link):
        try:
            href = edit_link.get_attribute("href") or ""
            edit_id = extract_listing_edit_id(href)
            if edit_id:
                return edit_id
            html = edit_link.evaluate("el => el.outerHTML") or ""
            return extract_listing_edit_id(html)
        except Exception:
            return None

    def _remember_skipped(self, shopee_id=None, edit_id=None, row_key=None):
        shopee_id = str(shopee_id or "").strip()
        edit_id = str(edit_id or "").strip()
        row_key = str(row_key or "").strip()
        if shopee_id:
            self.skipped_shopee_ids.add(shopee_id)
        if edit_id:
            self.skipped_edit_ids.add(edit_id)
        if row_key:
            self.skipped_row_keys.add(row_key)

    def _failure_key(self, shopee_id=None, edit_id=None, row_key=None):
        shopee_id = str(shopee_id or "").strip()
        edit_id = str(edit_id or "").strip()
        row_key = str(row_key or "").strip()
        if shopee_id:
            return f"shopee:{shopee_id}"
        if edit_id:
            return f"edit:{edit_id}"
        return f"row:{row_key}" if row_key else "row:unknown"

    def _run_listing_row(self, listing_page, row, edit_link, row_index, listing_edit_id=None, listing_row_key=None):
        self.edit_seq += 1
        log(f"start edit#{self.edit_seq} listing_row=#{row_index + 1}")

        edit_page = None
        keep_edit_open = False
        try:
            dismiss_blocking_modal(listing_page)
            with self.browser_context.expect_page() as edit_info:
                try:
                    edit_link.click(timeout=10000)
                except Exception as click_error:
                    click_text = str(click_error).lower()
                    if "intercept" not in click_text and "timeout" not in click_text:
                        raise
                    log("⚠️ Click edit bị modal chặn -> đóng modal rồi click lại...")
                    dismiss_blocking_modal(listing_page)
                    edit_link.click(timeout=10000)
            edit_page = edit_info.value
            self.click_blocked_streak = 0
            self.click_blocked_total = 0
            register_status_page(edit_page)
            edit_page.wait_for_load_state("domcontentloaded", timeout=30000)
            time.sleep(2)

            inspection = inspect_edit_page_for_update(edit_page, self.config, log_steps=True)
            shopee_id = str(inspection.get("shopee_id") or "").strip()
            status = inspection.get("status")
            bigseller_edit_id = str(inspection.get("bigseller_edit_id") or listing_edit_id or "").strip()

            if shopee_id and shopee_id in self.skipped_shopee_ids:
                delete_outcome = handle_listing_row_delete_for_status(
                    listing_page,
                    row,
                    edit_page,
                    "session_skip",
                    reason=f"Shopee ID {shopee_id} đã skip trong phiên",
                    remember_skipped=self._remember_skipped,
                    shopee_id=shopee_id,
                    bigseller_edit_id=bigseller_edit_id,
                    listing_row_key=listing_row_key,
                )
                if delete_outcome == "deleted":
                    edit_page = None
                    return "deleted"
                return "skipped"

            if bigseller_edit_id and bigseller_edit_id in self.skipped_edit_ids:
                delete_outcome = handle_listing_row_delete_for_status(
                    listing_page,
                    row,
                    edit_page,
                    "session_skip",
                    reason=f"BigSeller edit_id {bigseller_edit_id} đã skip trong phiên",
                    remember_skipped=self._remember_skipped,
                    shopee_id=shopee_id,
                    bigseller_edit_id=bigseller_edit_id,
                    listing_row_key=listing_row_key,
                )
                if delete_outcome == "deleted":
                    edit_page = None
                    return "deleted"
                return "skipped"

            delete_outcome = handle_listing_row_delete_for_status(
                listing_page,
                row,
                edit_page,
                status,
                remember_skipped=self._remember_skipped,
                shopee_id=shopee_id,
                bigseller_edit_id=bigseller_edit_id,
                listing_row_key=listing_row_key,
            )
            if delete_outcome == "deleted":
                edit_page = None
                return "deleted"
            if delete_outcome == "skipped":
                return "skipped"

            ok = bool(process_product(edit_page, inspection, self.config))
            log(f"[edit#{self.edit_seq}] done ok={ok}")
            if ok:
                self._record_success()
            else:
                fail_key = self._failure_key(shopee_id=shopee_id, edit_id=bigseller_edit_id, row_key=listing_row_key)
                fail_count = self.save_fail_counts.get(fail_key, 0) + 1
                self.save_fail_counts[fail_key] = fail_count
                if fail_count < self.max_save_fail_retries:
                    log(
                        f"⚠️ Edit#{self.edit_seq} chưa save được -> đóng tab, reload listing và thử lại "
                        f"({fail_count}/{self.max_save_fail_retries})."
                    )
                    return "retry"

                delete_outcome = handle_listing_row_delete_for_status(
                    listing_page,
                    row,
                    edit_page,
                    "save_failed",
                    remember_skipped=self._remember_skipped,
                    shopee_id=shopee_id,
                    bigseller_edit_id=bigseller_edit_id,
                    listing_row_key=listing_row_key,
                )
                if delete_outcome == "deleted":
                    edit_page = None
                    return "deleted"
                return "skipped"
            return ok
        except Exception as e:
            log(f"[edit#{self.edit_seq}] loi: {e}")
            err_text = str(e).lower()
            if "intercepts pointer events" in err_text or "ant-modal" in err_text:
                self.click_blocked_streak += 1
                self.click_blocked_total += 1
                if self.click_blocked_total >= 9:
                    log("❌ Modal chặn click lặp lại quá nhiều lần -> dừng.")
                    keep_edit_open = True
                    return False
                dismiss_blocking_modal(listing_page)
                if self.click_blocked_streak >= 3:
                    log("⚠️ Modal chặn click nhiều lần -> reload listing rồi thử tiếp.")
                    go_to_draft_page(listing_page, force_reload=True)
                    self.click_blocked_streak = 0
                return "retry"
            keep_edit_open = True
            return False
        finally:
            if edit_page is not None and not keep_edit_open:
                close_page_accepting_dialog(edit_page)
            if not keep_edit_open:
                try:
                    listing_page.bring_to_front()
                except Exception:
                    pass

    def _record_success(self):
        delete_after = int(self.config.get("DELETE_IMAGES_AFTER") or 0)
        if delete_after <= 0:
            return

        self.update_count += 1
        log(f"Da update {self.update_count}/{delete_after} SP")
        if self.update_count >= delete_after:
            log(f"\\n{'='*60}")
            log(f"DA UPDATE {delete_after} SP -> XOA THU VIEN ANH")
            log(f"{'='*60}")
            delete_all_images(self.browser_context)
            self.update_count = 0

# ================= MAIN PROGRAM =================
if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    apply_cli_args()
    patch_module_logs()
    _hydrate_openai_api_key_from_config()
    log("📌 Khởi động...")
    log(f"📄 Workbook: {CONFIG['WORKBOOK_PATH']}")
    log(f"📑 Sheet data: {CONFIG['DATA_SHEET']}")
    log(f"🔢 Dòng XLSX: {CONFIG['START_ROW']}-{CONFIG['END_ROW'] if CONFIG['END_ROW'] > 0 else 'het'}")
    log("📊 Cot XLSX co dinh: A=link, C=gia ban, D=SKU, E=ID sp goc, F=ten sp, G=ten sp da sua")
    log(f"🏬 Shop import: {CONFIG['SHOP_NAME']}")
    log(f"🤖 Name batch size: {CONFIG['BATCH_SIZE']}")
    log(f"🖼️ Ảnh mặc định: {CONFIG['IMAGE_PATH']}")
    log(f"🎥 Thư mục video: {CONFIG['VIDEO_FOLDER']}")
    log("Edit mode: first listing row, one edit at a time")
    log(f"📋 Listing URL: {CONFIG['LISTING_URL']}")
    log(f"🔄 Listing reload seconds: {CONFIG['DRAFT_RELOAD_SECONDS']}")
    if CONFIG.get("NAME_ONLY"):
        prepare_rewritten_product_names(
            CONFIG["WORKBOOK_PATH"],
            CONFIG["DATA_SHEET"],
            CONFIG["START_ROW"],
            CONFIG["MODEL"],
            CONFIG["API_KEY_FILE"],
            CONFIG["BATCH_SIZE"],
        )
        log("✅ Đã cập nhật tên sản phẩm, dừng trước bước BigSeller.")
        exit(0)

    if not CONFIG.get("SKIP_NAME_UPDATE"):
        prepare_rewritten_product_names(
            CONFIG["WORKBOOK_PATH"],
            CONFIG["DATA_SHEET"],
            CONFIG["START_ROW"],
            CONFIG["MODEL"],
            CONFIG["API_KEY_FILE"],
            CONFIG["BATCH_SIZE"],
        )
    p = None
    
    try:
        if not open_brave(): exit(1)

        if os.environ.get("BIGSELLER_SKIP_COOKIE_IMPORT", "0").strip() == "1":
            # Launcher nạp cookie rồi ghi kết quả vào bigseller-cookie-status.json trong profile dir.
            # Chờ file đó thay vì sleep mù: "expired" -> dừng ngay với hướng dẫn, khỏi reload vô hạn.
            log("Cookie: cho launcher nap cookie tu account...")
            _status_dir = (os.environ.get("BIGSELLER_PROFILE_DIR") or "").strip()
            _status_path = os.path.join(_status_dir, "bigseller-cookie-status.json") if _status_dir else ""
            _status = ""
            _wait_started = time.time()
            while _status_path and time.time() - _wait_started < 60:
                try:
                    if os.path.isfile(_status_path) and os.path.getmtime(_status_path) >= _wait_started - 30:
                        import json as _json
                        with open(_status_path, "r", encoding="utf-8") as _f:
                            _status = (_json.load(_f).get("status") or "").strip().lower()
                        break
                except Exception:
                    pass
                time.sleep(1)
            if _status == "expired":
                log("❌ Cookie BigSeller đã hết hạn — launcher không nạp được phiên sống.")
                log("👉 Mở tab Account trong launcher, bấm 'Open BigSeller', đăng nhập lại, bấm 'Save & close', rồi chạy lại Update product.")
                exit(1)
            if _status == "ok":
                log("Cookie: launcher xác nhận phiên BigSeller sẵn sàng.")
            else:
                log("Cookie: không thấy tín hiệu từ launcher sau 60s — chạy tiếp như cũ.")
                time.sleep(2)
        
        try:
            from playwright.sync_api import sync_playwright
        except ImportError:
            log("❌ Thiếu Playwright. Cài bằng: python -m pip install playwright && python -m playwright install chromium")
            exit(1)

        p = sync_playwright().start()
        log(f"🔗 Kết nối Brave...")
        
        browser = None
        last_connect_error = None
        for attempt in range(8):
            try:
                browser = p.chromium.connect_over_cdp(
                    f"http://127.0.0.1:{CONFIG['DEBUG_PORT']}",
                    timeout=30000,
                )
                break
            except Exception as exc:
                last_connect_error = exc
                log(f"⚠️ CDP connect lần {attempt + 1}/8: {exc}")
                time.sleep(3)

        if not browser:
            log(f"❌ Không kết nối Playwright CDP port {CONFIG['DEBUG_PORT']}: {last_connect_error}")
            exit(1)
        context = browser.contexts[0]

        # Import BigSeller cookies from account JSON file unless launcher already injected them.
        _bs_cookie_file = os.environ.get("BIGSELLER_COOKIE_FILE", "").strip()
        _skip_cookie_import = os.environ.get("BIGSELLER_SKIP_COOKIE_IMPORT", "0").strip() == "1"
        _force_cookie_import = os.environ.get("BIGSELLER_FORCE_COOKIE_IMPORT", "1").strip() != "0"
        if not _skip_cookie_import and _bs_cookie_file and os.path.isfile(_bs_cookie_file):
            try:
                import json as _json
                _existing = context.cookies()
                _has_bs = any("bigseller" in (c.get("domain") or "").lower() for c in _existing)
                if _force_cookie_import or not _has_bs:
                    with open(_bs_cookie_file, encoding="utf-8-sig") as _f:
                        _raw = _json.load(_f)
                    _all = _raw if isinstance(_raw, list) else _raw.get("cookies", [])
                    _bs = [c for c in _all if "bigseller" in (c.get("domain") or "").lower()]
                    if _bs:
                        _to_add = []
                        for c in _bs:
                            domain = (c.get("domain") or "").strip()
                            entry = {
                                "name": c["name"],
                                "value": c["value"],
                                "domain": domain,
                                "path": c.get("path", "/"),
                            }
                            ds = domain.lstrip(".")
                            if ds:
                                entry["url"] = f"https://{ds}/"
                            if c.get("expires") not in (None, -1):
                                entry["expires"] = float(c["expires"])
                            if c.get("secure") is not None:
                                entry["secure"] = bool(c["secure"])
                            if c.get("httpOnly") is not None:
                                entry["httpOnly"] = bool(c["httpOnly"])
                            _to_add.append(entry)
                        context.add_cookies(_to_add)
                        _target = CONFIG["LISTING_URL"]
                        for page in context.pages:
                            if "bigseller" in (page.url or "").lower():
                                try:
                                    page.goto(_target, wait_until="domcontentloaded", timeout=30000)
                                    time.sleep(2)
                                except Exception:
                                    pass
                        log(f"Cookie: da import {len(_to_add)} BigSeller cookie tu account.")
                    else:
                        log("Cookie: file cookie khong co BigSeller cookie.")
                else:
                    _cnt = sum(1 for c in _existing if "bigseller" in (c.get("domain") or "").lower())
                    log(f"Cookie: da co {_cnt} BigSeller cookie trong profile, bo qua import.")
            except Exception as _ce:
                log(f"Cookie import loi: {_ce}")
        elif _skip_cookie_import:
            log("Cookie: launcher da nap cookie tu account, bo qua import trong Python.")
        elif _bs_cookie_file:
            log(f"Cookie: khong tim thay file {_bs_cookie_file}")

        # Tìm tab BigSeller, ưu tiên tab danh sách Shopee (bsStatus=1).
        bigseller_page = None
        for page in context.pages:
            if is_draft_page(page):
                bigseller_page = page
                break
        for page in context.pages:
            if not bigseller_page and "bigseller.com" in (page.url or ""):
                bigseller_page = page
                break

        if not bigseller_page:
            if len(context.pages) > 0:
                bigseller_page = context.pages[0]
            else:
                exit(1)
        
        bigseller_page.bring_to_front()
        register_status_page(bigseller_page)
        if not go_to_draft_page(bigseller_page, force_reload=False):
            exit(1)
        log(f"\n{'='*60}\n📄 BẮT ĐẦU QUÉT\n{'='*60}")

        runner = SequentialUpdateRunner(CONFIG, context)
        last_listing_reload_at = time.time()
        listing_error_streak = 0

        while True:
            try:
                try:
                    if bigseller_page.is_closed():
                        log("❌ Tab BigSeller đã đóng — dừng script.")
                        break
                except Exception:
                    log("❌ Không kiểm tra được tab BigSeller — dừng script.")
                    break

                if not is_draft_page(bigseller_page) or not is_draft_page_ready(bigseller_page, timeout_ms=1500):
                    if not go_to_draft_page(bigseller_page, force_reload=False):
                        listing_error_streak += 1
                        time.sleep(min(5 + listing_error_streak, 15))
                        if listing_error_streak >= 5:
                            log("❌ Không khôi phục được danh sách Shopee sau nhiều lần thử — dừng.")
                            break
                        continue
                    listing_error_streak = 0

                result = runner.run_first_listing_row(bigseller_page)
                listing_error_streak = 0
                if result is None:
                    log("Danh sach Shopee trong (khong co dong), doi roi reload lai listing.")
                    time.sleep(CONFIG['CHECK_INTERVAL'])
                elif result == LISTING_PAGE_EXHAUSTED:
                    wrap = advance_listing_after_page_exhausted(bigseller_page)
                    if wrap == "wrap":
                        time.sleep(CONFIG['CHECK_INTERVAL'])
                elif result is False:
                    log("Dung script (loi edit hoac Shopee chan captcha).")
                    break
                elif result == "retry":
                    time.sleep(1.2)
                else:
                    time.sleep(0.8)

                now = time.time()
                if now - last_listing_reload_at >= CONFIG["DRAFT_RELOAD_SECONDS"]:
                    log("🔄 Reload danh sách Shopee (chu kỳ)...")
                    if go_to_draft_page(bigseller_page, force_reload=True):
                        last_listing_reload_at = now
                else:
                    try:
                        bigseller_page.bring_to_front()
                    except Exception:
                        pass
            except Exception as e:
                log(f"Loi scan listing: {e}")
                listing_error_streak += 1
                if "closed" in str(e).lower():
                    log("❌ Browser/tab đã đóng — dừng script.")
                    break
                time.sleep(min(5 + listing_error_streak, 15))
                try:
                    if not bigseller_page.is_closed():
                        go_to_draft_page(bigseller_page, force_reload=False)
                except Exception:
                    pass
                if listing_error_streak >= 5:
                    log("❌ Lỗi listing lặp lại — dừng script.")
                    break

    except Exception as e: log(f"❌ LỖI TỔNG: {e}")
    finally:
        if p: p.stop()
