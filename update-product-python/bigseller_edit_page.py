"""BigSeller edit-page automation helpers."""
import re
import time

from product_runtime import close_page_accepting_dialog, log
from product_workbook import (
    SHOPEE_BLOCKED_MARKER,
    extract_shopee_id,
    is_shopee_blocked_url,
    search_in_workbook,
)

def is_image_uploaded(edit_page):
    """Check uploaded image DOM strictly by img[src]."""
    try:
        uploaded_img = edit_page.locator(
            "div.supp_size_chat div.page_edit_img_item.comm_img_module img[src]"
        ).first
        if uploaded_img.count() == 0 or not uploaded_img.is_visible():
            return False

        img_src = uploaded_img.get_attribute("src") or ""
        return bool(img_src.strip())
    except:
        return False


def is_upload_menu_visible(edit_page):
    try:
        upload_menu = edit_page.locator("div.spc_box ul.spc_cho li").first
        return upload_menu.count() > 0 and upload_menu.is_visible()
    except:
        return False

def upload_product_image_with_retry(edit_page, image_path, max_attempts=3, verify_timeout_ms=5000):
    """Upload image and verify uploaded DOM, retry up to max_attempts."""
    for attempt in range(1, max_attempts + 1):
        try:
            log(f"upload image attempt {attempt}/{max_attempts}")

            if is_image_uploaded(edit_page):
                log("image already exists on page")
                return True

            spc_box = edit_page.locator("div.spc_box").first
            spc_box.wait_for(state="visible", timeout=5000)
            spc_box.scroll_into_view_if_needed()
            spc_box.click()

            menu_item = edit_page.locator("div.spc_box ul.spc_cho li").first
            menu_item.wait_for(state="visible", timeout=5000)

            with edit_page.expect_file_chooser(timeout=5000) as fc_info:
                menu_item.click()

            file_chooser = fc_info.value
            file_chooser.set_files(image_path)

            uploaded_img = edit_page.locator(
                "div.supp_size_chat div.page_edit_img_item.comm_img_module img[src]"
            ).first
            uploaded_img.wait_for(state="visible", timeout=verify_timeout_ms)

            img_src = uploaded_img.get_attribute("src") or ""
            if img_src.strip():
                log(f"upload image success at attempt {attempt}")
                return True

            raise Exception("uploaded image is visible but src is empty")
        except Exception as e:
            log(f"upload image failed at attempt {attempt}: {e}")
            time.sleep(2.5)

    return False


def _normalize_text(text):
    import unicodedata

    text = text or ""
    text = unicodedata.normalize("NFD", text)
    text = "".join(ch for ch in text if unicodedata.category(ch) != "Mn")
    return text.lower()


def detect_save_error_message(edit_page, timeout_ms=2500):
    """Check fast validation/toast errors that may disappear quickly after save click."""
    target_texts = [
        "bieu do kich co khong duoc de trong",
        "brand cannot be empty",
    ]

    deadline = time.time() + (timeout_ms / 1000)
    while time.time() < deadline:
        try:
            raw_texts = edit_page.evaluate(
                """() => {
                    const selectors = [
                        'div.ant-message',
                        'div.ant-message-notice',
                        'div.ant-message-notice-content',
                        'div.ant-notification',
                        'div.ant-modal-root',
                        \"div[role='alert']\",
                        'body'
                    ];

                    const values = [];
                    for (const selector of selectors) {
                        for (const el of document.querySelectorAll(selector)) {
                            const text = (el.innerText || el.textContent || '').trim();
                            if (text) values.push(text);
                        }
                    }
                    return values;
                }"""
            )
        except:
            raw_texts = []

        for raw_text in raw_texts:
            normalized_text = _normalize_text(raw_text)
            for target_text in target_texts:
                if target_text in normalized_text:
                    for line in raw_text.splitlines():
                        line = line.strip()
                        if line and target_text in _normalize_text(line):
                            return line
                    return raw_text.strip()

        time.sleep(0.1)

    return None


def select_no_brand(edit_page, timeout_ms=10000):
    """Select BigSeller/Shopee Brand = No brand and verify the selected value."""
    brand_box_locators = [
        edit_page.locator(
            "//div[contains(@class, 'page_edit_item')][.//*[contains(normalize-space(), 'Thương hiệu') or contains(normalize-space(), 'Brand')]]"
            "//div[contains(@class, 'ant-select-selection')]"
        ).first,
        edit_page.locator(
            "//div[contains(@class, 'ant-form-item')][.//*[contains(normalize-space(), 'Thương hiệu') or contains(normalize-space(), 'Brand')] "
            "or .//div[contains(@class, 'ant-form-explain') and contains(normalize-space(), 'Brand cannot be empty')]]"
            "//div[contains(@class, 'ant-select-selection')]"
        ).first,
        edit_page.locator(
            "div.ant-form-item:has(div.ant-form-explain:has-text('Brand cannot be empty')) div.ant-select-selection"
        ).first,
    ]

    brand_box = None
    for locator in brand_box_locators:
        try:
            if locator.count() > 0 and locator.is_visible():
                brand_box = locator
                break
        except Exception:
            pass

    if brand_box is None:
        log("   => Không tìm thấy ô Brand/Thương hiệu.")
        return False

    def read_selected_value():
        try:
            text = brand_box.locator(".ant-select-selection-selected-value").first.text_content(timeout=1000)
        except Exception:
            try:
                text = brand_box.text_content(timeout=1000)
            except Exception:
                text = ""
        return (text or "").strip()

    current_val = read_selected_value()
    if _normalize_text(current_val).replace(" ", "") == "nobrand":
        log("   => Đã chọn sẵn.")
        return True

    brand_box.scroll_into_view_if_needed()
    brand_box.click(force=True)
    time.sleep(0.4)

    search_inputs = [
        edit_page.locator(".ant-select-open input.ant-select-search__field").first,
        edit_page.locator("input.ant-select-search__field:visible").first,
    ]
    filled_search = False
    for search_input in search_inputs:
        try:
            if search_input.count() > 0 and search_input.is_visible():
                search_input.fill("No brand")
                filled_search = True
                break
        except Exception:
            pass
    if not filled_search:
        edit_page.keyboard.press("Control+A")
        edit_page.keyboard.press("Backspace")
        edit_page.keyboard.type("No brand", delay=30)

    try:
        edit_page.wait_for_selector(
            ".ant-select-dropdown:not(.ant-select-dropdown-hidden), div.option",
            state="visible",
            timeout=timeout_ms,
        )
    except Exception:
        pass

    option_locators = [
        edit_page.locator(
            ".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-dropdown-menu-item"
        ).filter(has_text=re.compile(r"^\s*No\s*brand\s*$", re.I)).first,
        edit_page.locator(
            ".ant-select-dropdown:not(.ant-select-dropdown-hidden) [role='option']"
        ).filter(has_text=re.compile(r"^\s*No\s*brand\s*$", re.I)).first,
        edit_page.locator("div.option:visible").filter(has_text=re.compile(r"^\s*No\s*brand\s*$", re.I)).first,
    ]

    clicked_option = False
    for option in option_locators:
        try:
            if option.count() > 0 and option.is_visible():
                option.click(force=True)
                clicked_option = True
                break
        except Exception:
            pass

    if not clicked_option:
        edit_page.keyboard.press("Enter")

    deadline = time.time() + 5
    while time.time() < deadline:
        selected_val = read_selected_value()
        if _normalize_text(selected_val).replace(" ", "") == "nobrand":
            log("   => Đã chọn 'No brand'.")
            return True
        time.sleep(0.25)

    log(f"   => Chọn Brand chưa thành công, giá trị hiện tại: '{read_selected_value()}'.")
    return False


def wait_for_save_success_dialog(edit_page, timeout_ms=60000):
    """Wait for the save-success modal in Vietnamese or English."""
    success_locators = [
        edit_page.locator(".ant-modal-confirm-title, .ant-modal-title").filter(has_text="Thao tác thành công"),
        edit_page.locator(".ant-modal-confirm-title, .ant-modal-title").filter(has_text=re.compile(r"^\s*Successfully\s*$", re.I)),
        edit_page.locator(".ant-modal-body, .ant-modal-confirm-content").filter(has_text="đệ trình"),
        edit_page.locator(".ant-modal-body, .ant-modal-confirm-content").filter(has_text="Thao tác thành công"),
        edit_page.locator(".ant-modal-body, .ant-modal-confirm-content").filter(has_text=re.compile(r"pending by Shopee", re.I)),
        edit_page.locator(".ant-modal-body, .ant-modal-confirm-content").filter(has_text=re.compile(r"Publishing\s*/\s*Failed\s*/\s*Active", re.I)),
        edit_page.locator(".ant-modal:visible button").filter(has_text=re.compile(r"Close this page", re.I)),
    ]
    deadline = time.time() + (timeout_ms / 1000)
    while time.time() < deadline:
        for locator in success_locators:
            try:
                if locator.count() > 0 and locator.first.is_visible():
                    return True
            except Exception:
                pass
        time.sleep(0.25)
    return False


def save_product_with_image_retry(edit_page, image_path, max_attempts=3):
    """Try saving product; if save fails with image-related toast, re-upload image and retry."""
    save_btn_wrapper = edit_page.locator("div[autoid='save_and_publish_button']")
    if not save_btn_wrapper.is_visible():
        log("save button not found")
        return False

    for attempt in range(1, max_attempts + 1):
        log(f"save product attempt {attempt}/{max_attempts}")

        if not is_image_uploaded(edit_page):
            log("image not ready before save")
            upload_success = upload_product_image_with_retry(
                edit_page,
                image_path,
                max_attempts=1,
                verify_timeout_ms=5000
            )
            if not upload_success:
                log("re-upload image failed before save")
                if attempt >= max_attempts:
                    return False
                time.sleep(2.5)
                continue

        save_btn_wrapper.scroll_into_view_if_needed()
        save_btn_wrapper.hover()
        time.sleep(1)

        save_option = edit_page.locator("li[autoid='save_and_publish_option']")
        if save_option.is_visible():
            save_option.click(force=True)
            log("clicked save menu")
        else:
            log("save menu not visible, click button directly")
            save_btn_wrapper.locator("button").click()
            log("clicked save button")

        save_error = detect_save_error_message(edit_page, timeout_ms=4000)
        if save_error:
            log(f"toast msg: > {save_error}")
            if "brand cannot be empty" in _normalize_text(save_error):
                log("retry select Brand before save")
                if not select_no_brand(edit_page) or attempt >= max_attempts:
                    return False
                time.sleep(1)
                continue

            if attempt >= max_attempts:
                return False

            log("re-upload image before retry save")
            upload_success = upload_product_image_with_retry(
                edit_page,
                image_path,
                max_attempts=1,
                verify_timeout_ms=5000
            )
            if not upload_success:
                log("re-upload image failed")
                if attempt >= max_attempts:
                    return False
            time.sleep(2.5)
            continue

        log("wait for confirm dialog")
        try:
            confirm_btn = edit_page.locator("div.ant-modal-confirm-btns button.ant-btn-primary")
            confirm_btn.wait_for(state="visible", timeout=5000)
            confirm_btn.click()
            log("clicked confirm")

            save_error = detect_save_error_message(edit_page, timeout_ms=4000)
            if save_error:
                log(f"toast msg: > {save_error}")
                if "brand cannot be empty" in _normalize_text(save_error):
                    log("retry select Brand before save")
                    if not select_no_brand(edit_page) or attempt >= max_attempts:
                        return False
                    time.sleep(1)
                    continue

                if attempt >= max_attempts:
                    return False

                log("re-upload image before retry save")
                upload_success = upload_product_image_with_retry(
                    edit_page,
                    image_path,
                    max_attempts=1,
                    verify_timeout_ms=5000
                )
                if not upload_success:
                    log("re-upload image failed")
                    if attempt >= max_attempts:
                        return False
                time.sleep(2.5)
                continue
        except:
            log("confirm dialog not found")
            if (not is_image_uploaded(edit_page)) or is_upload_menu_visible(edit_page):
                log("image upload DOM check failed after save click")
                if attempt >= max_attempts:
                    return False

                upload_success = upload_product_image_with_retry(
                    edit_page,
                    image_path,
                    max_attempts=1,
                    verify_timeout_ms=5000
                )
                if not upload_success:
                    log("re-upload image failed")
                    if attempt >= max_attempts:
                        return False
                time.sleep(2.5)
                continue

        log("wait for success dialog")
        try:
            if wait_for_save_success_dialog(edit_page, timeout_ms=60000):
                log("✅ Save success — thấy hộp thoại thành công, đóng tab edit (không click Đóng).")
                close_page_accepting_dialog(edit_page)
                return True
            raise TimeoutError("success dialog not found within timeout")
        except Exception as e:
            log(f"wait success dialog failed: {e}")
            if attempt >= max_attempts:
                return False
            time.sleep(2.5)

    return False


def wait_for_edit_page_ready(edit_page, max_attempts=3, timeout_ms=15000):
    """Reload blank/stuck BigSeller edit pages until the source-link input is available."""
    selector = "input[autoid='product_source_link_text']"
    for attempt in range(1, max_attempts + 1):
        try:
            log(f"⏳ Đợi trang edit load input link nguồn ({attempt}/{max_attempts})...")
            edit_page.wait_for_load_state("domcontentloaded", timeout=timeout_ms)
            edit_page.wait_for_selector(selector, state="visible", timeout=timeout_ms)
            return True
        except Exception as e:
            current_url = ""
            try:
                current_url = edit_page.url
            except:
                pass
            log(f"⚠️ Trang edit chưa sẵn sàng: {e}")
            if current_url:
                log(f"   URL hiện tại: {current_url}")
            if attempt >= max_attempts:
                break
            log("🔄 Trang edit có thể bị trắng/đứng, F5 rồi thử lại...")
            try:
                edit_page.reload(wait_until="domcontentloaded", timeout=30000)
            except Exception as reload_error:
                log(f"⚠️ Reload trang edit lỗi: {reload_error}")
            time.sleep(3)

    return False


def fill_product_name_on_edit_page(edit_page, product_name_new, timeout_ms=15000, max_attempts=3):
    name_sel = "input[autoid='product_name_text']"
    target_name = str(product_name_new or "").strip()
    if not target_name:
        log("ℹ️ [1] Bỏ qua Tên (tên rỗng).")
        return False

    name_input = edit_page.locator(name_sel).first
    for attempt in range(1, max_attempts + 1):
        try:
            edit_page.evaluate("window.scrollTo(0, 0)")
            edit_page.wait_for_selector(name_sel, state="visible", timeout=timeout_ms)
            name_input.wait_for(state="visible", timeout=timeout_ms)

            current_value = str(name_input.input_value() or "").strip()
            if current_value == target_name:
                log("✅ [1] Tên đã đúng, không cần nhập lại.")
                return True

            log(f"   ✏️ Nhập Tên (lần {attempt}/{max_attempts})...")
            name_input.scroll_into_view_if_needed()
            name_input.click(timeout=timeout_ms)
            name_input.fill(target_name, timeout=timeout_ms)
            name_input.evaluate("el => el.dispatchEvent(new Event('input', { bubbles: true }))")
            edit_page.keyboard.press("Space")
            edit_page.keyboard.press("Backspace")
            time.sleep(0.3)

            updated_value = str(name_input.input_value() or "").strip()
            if updated_value == target_name:
                log("✅ [1] Đã nhập Tên.")
                return True

            log(f"   ⚠️ Sau khi nhập, giá trị ô Tên vẫn khác (hiện: {updated_value[:80]}...).")
        except Exception as error:
            log(f"   ⚠️ Lỗi nhập Tên lần {attempt}/{max_attempts}: {error}")
            if attempt < max_attempts:
                time.sleep(2)
                try:
                    edit_page.reload(wait_until="domcontentloaded", timeout=30000)
                except Exception as reload_error:
                    log(f"   ⚠️ Reload trang edit lỗi: {reload_error}")
                time.sleep(2)

    log("ℹ️ [1] Bỏ qua Tên (không điền được sau nhiều lần thử).")
    return False


def extract_bigseller_edit_id(edit_page_or_url):
    """Lấy ID sản phẩm BigSeller từ URL tab edit, vd /edit/963509790.htm — chỉ để khớp dòng listing."""
    url = edit_page_or_url if isinstance(edit_page_or_url, str) else (getattr(edit_page_or_url, "url", None) or "")
    match = re.search(r"/edit/(\d+)\.htm", url, re.IGNORECASE)
    return match.group(1) if match else None


def read_shopee_id_from_edit_page(edit_page, max_attempts=2, timeout_ms=12000, log_steps=True):
    """Read Shopee item ID from the BigSeller edit page source-link input."""
    if not wait_for_edit_page_ready(edit_page, max_attempts=max_attempts, timeout_ms=timeout_ms):
        if log_steps:
            log("Trang edit chua san sang de doc ID Shopee.")
        return None

    link_input = edit_page.locator("input[autoid='product_source_link_text']")
    if link_input.count() == 0:
        if log_steps:
            log("Khong tim thay input link nguon san pham.")
        return None

    input_url = ""
    try:
        input_url = (link_input.input_value(timeout=3000) or "").strip()
    except Exception:
        input_url = ""

    clipboard_url = ""
    copy_btn = edit_page.locator(
        "div.com_input_box:has(input[autoid='product_source_link_text']) button"
    ).first
    if copy_btn.count() > 0:
        if log_steps:
            log("Click nut Chep cua Link nguon san pham...")
        try:
            edit_page.context.grant_permissions(["clipboard-read", "clipboard-write"], origin="https://www.bigseller.com")
        except Exception:
            pass
        try:
            copy_btn.click(timeout=5000)
            time.sleep(1)
            clipboard_url = (edit_page.evaluate("() => navigator.clipboard.readText()") or "").strip()
        except Exception as e:
            if log_steps:
                log(f"Khong doc duoc clipboard sau khi click Chep: {e}")
    elif log_steps:
        log("Khong tim thay nut Chep cua Link nguon san pham.")

    blocked_seen = False
    for source, shopee_url in (("input", input_url), ("clipboard", clipboard_url)):
        if not shopee_url:
            continue
        if is_shopee_blocked_url(shopee_url):
            blocked_seen = True
            if log_steps:
                log(f"⛔ Shopee chan truy cap ({source}): {shopee_url}")
            continue
        if log_steps:
            log("=" * 80)
            log(f"LINK SHOPEE ({source}):")
            log(shopee_url)
            log("=" * 80)
        shopee_id = extract_shopee_id(shopee_url)
        if shopee_id:
            if log_steps:
                log(f"Shopee item ID: {shopee_id}")
            return shopee_id
        if log_steps:
            log(f"Link khong chua ID hop le ({source}): {shopee_url}")

    if blocked_seen:
        if log_steps:
            log(
                "⛔ Shopee yeu cau xac minh (captcha/traffic). "
                "Mo tab Shopee trong Brave, giai captcha thu cong, roi chay lai script."
            )
        return SHOPEE_BLOCKED_MARKER

    if log_steps:
        log("Khong lay duoc link Shopee hop le tu input hoac clipboard.")
    return None

def inspect_edit_page_for_update(edit_page, config, log_steps=True):
    """
    Kiểm tra tab edit trước khi update/xóa.
    - Tra XLSX theo Shopee ID (link sau nút Chép) để lấy dữ liệu update.
    - bigseller_edit_id (URL tab edit): chỉ metadata để khớp dòng listing
    """
    bigseller_edit_id = extract_bigseller_edit_id(edit_page)
    if log_steps and bigseller_edit_id:
        log(f"🏷️ BigSeller edit_id (từ URL tab): {bigseller_edit_id}")

    shopee_id = read_shopee_id_from_edit_page(edit_page, max_attempts=2, timeout_ms=12000, log_steps=log_steps)
    if shopee_id == SHOPEE_BLOCKED_MARKER:
        return {
            "status": "shopee_blocked",
            "bigseller_edit_id": bigseller_edit_id,
            "shopee_id": None,
            "xlsx_result": None,
        }
    if not shopee_id:
        return {
            "status": "missing_shopee_id",
            "bigseller_edit_id": bigseller_edit_id,
            "shopee_id": None,
            "xlsx_result": None,
        }

    workbook_path = config.get("WORKBOOK_PATH")
    sheet_name = config.get("DATA_SHEET", "bizly")
    start_row = config.get("START_ROW", 2)
    end_row = config.get("END_ROW") or 0
    xlsx_result = search_in_workbook(shopee_id, workbook_path, sheet_name, start_row, end_row or None)
    if not xlsx_result:
        if log_steps:
            log(
                f"⛔ Shopee ID {shopee_id} không có trong XLSX sheet '{sheet_name}' "
                f"(BigSeller edit_id={bigseller_edit_id or '?'}) "
                f"-> đóng tab edit và xóa dòng listing."
            )
        return {
            "status": "not_in_xlsx",
            "bigseller_edit_id": bigseller_edit_id,
            "shopee_id": shopee_id,
            "xlsx_result": None,
        }

    if xlsx_result.get("status") == "missing_product_name":
        if log_steps:
            log(
                f"⏭️ Shopee ID {shopee_id} có trong XLSX dòng {xlsx_result.get('line_index')} "
                f"nhưng cả cột F và G trống -> đóng tab edit và xóa dòng listing."
            )
        return {
            "status": "missing_product_name",
            "bigseller_edit_id": bigseller_edit_id,
            "shopee_id": shopee_id,
            "xlsx_result": xlsx_result,
        }

    if log_steps:
        log(
            f"✅ Shopee ID {shopee_id} có trong XLSX dòng {xlsx_result['line_index']} "
            f"-> cần update (BigSeller edit_id={bigseller_edit_id or '?'})"
        )
    return {
        "status": "needs_update",
        "bigseller_edit_id": bigseller_edit_id,
        "shopee_id": shopee_id,
        "xlsx_result": xlsx_result,
    }


def close_visible_ant_modal(page, timeout_ms=5000):
    deadline = time.time() + (timeout_ms / 1000)
    close_selectors = [
        "div.ant-modal:visible:has(div.complete_Status) .ant-modal-footer button.ant-btn",
        "div.ant-modal:visible:has(div.complete_Status) button.ant-modal-close",
        "div.ant-modal:visible .ant-modal-footer button",
        "div.ant-modal:visible button.ant-btn",
        "div.ant-modal:visible button.ant-modal-close",
        "div.ant-modal:visible .ant-modal-close",
        "div.ant-modal:visible .ant-modal-close-x",
    ]

    while time.time() < deadline:
        try:
            if page.locator("div.ant-modal:visible").count() == 0:
                return True
        except Exception:
            pass

        for selector in close_selectors:
            try:
                locator = page.locator(selector)
                if locator.count() == 0:
                    continue
                button = locator.last
                if not button.is_visible():
                    continue
                button.click(timeout=1000)
                page.wait_for_timeout(300)
                if page.locator("div.ant-modal:visible").count() == 0:
                    return True
            except Exception:
                continue

        try:
            page.keyboard.press("Escape")
            page.wait_for_timeout(300)
            if page.locator("div.ant-modal:visible").count() == 0:
                return True
        except Exception:
            pass

        time.sleep(0.2)

    try:
        return page.locator("div.ant-modal:visible").count() == 0
    except Exception:
        return False
