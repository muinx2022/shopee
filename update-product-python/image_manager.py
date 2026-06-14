"""
Module quản lý ảnh trong BigSeller Material Center
"""
import time

MATERIAL_CENTER_URL = "https://www.bigseller.com/web/product/materialCenter/index.htm"

def log(msg):
    print(msg, flush=True)

def close_popup_if_exists(page):
    """Đóng popup cảnh báo dung lượng nếu có"""
    try:
        log("🔍 Kiểm tra popup cảnh báo...")
        
        # Thử các cách đóng popup khác nhau
        close_methods = [
            # Cách 1: Click nút X đóng
            ("button.ant-modal-close", "Nút đóng (X)"),
            # Cách 2: Click nút Hủy
            ("div.ant-modal-footer button.ant-btn:has-text('Hủy')", "Nút Hủy"),
            # Cách 3: Click vào nút close bằng icon
            ("button[aria-label='Close']", "Nút Close"),
        ]
        
        for selector, name in close_methods:
            try:
                close_btn = page.locator(selector).first
                if close_btn.count() > 0 and close_btn.is_visible():
                    log(f"   ⚠️ Phát hiện popup! Đang đóng bằng {name}...")
                    close_btn.click(force=True, timeout=2000)
                    time.sleep(1)
                    
                    # Kiểm tra popup đã đóng chưa
                    modal = page.locator("div.ant-modal-content")
                    if modal.count() == 0 or not modal.is_visible():
                        log(f"   ✅ Đã đóng popup thành công!")
                        return True
            except:
                continue
        
        log("   ℹ️ Không có popup hoặc đã đóng")
        return True
        
    except Exception as e:
        log(f"   ⚠️ Lỗi khi đóng popup: {e}")
        return True  # Tiếp tục dù có lỗi

def _is_select_all_checked(select_all):
    try:
        checkbox = select_all.locator("input[type='checkbox']").first
        if checkbox.count() > 0:
            return bool(checkbox.is_checked())
    except Exception:
        pass

    try:
        class_name = select_all.get_attribute("class") or ""
        return "checked" in class_name.lower()
    except Exception:
        return False


def _is_button_enabled(button):
    try:
        if not button.is_enabled():
            return False
    except Exception:
        pass

    try:
        if button.get_attribute("disabled") is not None:
            return False
    except Exception:
        pass

    try:
        if str(button.get_attribute("aria-disabled") or "").lower() == "true":
            return False
    except Exception:
        pass

    try:
        class_name = button.get_attribute("class") or ""
        if "disabled" in class_name.lower():
            return False
    except Exception:
        pass

    return True


def _find_delete_batch_button(page):
    delete_selectors = [
        "section.material_action_row button:has-text('Xóa hàng loạt')",
        "button:has-text('Xóa hàng loạt')",
        "section.material_action_row button:has(i.bsicon_trash_2)",
        "button:has(i.bsicon_trash_2)",
        "button.ant-btn-success:has-text('Xóa')",
        "button:has-text('Xóa')",
        ".ant-btn-success"
    ]

    for selector in delete_selectors:
        try:
            button = page.locator(selector).first
            if button.count() > 0 and button.is_visible():
                return button
        except Exception:
            continue
    return None


def _wait_for_delete_batch_enabled(page, timeout_ms=8000):
    deadline = time.time() + (timeout_ms / 1000)
    last_button = None
    while time.time() < deadline:
        last_button = _find_delete_batch_button(page)
        if last_button is not None and _is_button_enabled(last_button):
            return last_button
        time.sleep(0.3)
    return last_button if last_button is not None and _is_button_enabled(last_button) else None


def _is_material_empty(page):
    empty_selectors = [
        "section.material_state_panel .bs-micro-empty:has-text('Trống')",
        "section.material_state_panel .bs-micro-empty",
        ".bs-micro-empty:has-text('Trống')",
        ".bs-micro-empty-description:has-text('Trống')",
        "div.page_list_empty",
        "div:has-text('Không có dữ liệu')",
        "div:has-text('No Data')",
        ".page_list_empty",
    ]

    for selector in empty_selectors:
        try:
            empty = page.locator(selector).first
            if empty.count() > 0 and empty.is_visible():
                return True
        except Exception:
            continue
    return False


def _find_select_all(page):
    select_all_selectors = [
        "section.material_action_row label.bs-micro-checkbox-wrapper:has-text('Chọn tất cả')",
        "label.bs-micro-checkbox-wrapper:has-text('Chọn tất cả')",
        "label.bs-antd-check-all",
        "label.ant-checkbox-wrapper:has-text('Chọn tất cả')",
        ".bs-antd-check-all",
    ]

    for selector in select_all_selectors:
        try:
            select_all = page.locator(selector).first
            if select_all.count() > 0 and select_all.is_visible():
                return select_all
        except Exception:
            continue
    return None


def _ensure_select_all_selected(page):
    select_all = _find_select_all(page)
    if select_all is None:
        return "missing"

    if _is_select_all_checked(select_all):
        log("☑️ 'Chọn tất cả' đã được chọn")
        return "checked"

    log("☑️ Click 'Chọn tất cả'")
    click_targets = [
        select_all.locator("span.bs-micro-checkbox-inner").first,
        select_all.locator("input[type='checkbox']").first,
        select_all,
    ]

    for target in click_targets:
        try:
            if target.count() == 0 or not target.is_visible():
                continue
            target.scroll_into_view_if_needed()
            time.sleep(0.3)
            target.click(force=True, timeout=3000)
            for _ in range(12):
                time.sleep(0.25)
                refreshed = _find_select_all(page)
                if refreshed is not None and _is_select_all_checked(refreshed):
                    return "checked"
                if _wait_for_delete_batch_enabled(page, timeout_ms=250):
                    return "checked"
        except Exception:
            continue

    try:
        changed = page.evaluate(
            """
            () => {
                const label = Array.from(document.querySelectorAll('label'))
                    .find(el => (el.textContent || '').includes('Chọn tất cả'));
                if (!label) { return false; }
                const input = label.querySelector('input[type="checkbox"]');
                if (input && !input.checked) {
                    input.click();
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                    return true;
                }
                label.click();
                return true;
            }
            """
        )
        if changed:
            for _ in range(16):
                time.sleep(0.25)
                refreshed = _find_select_all(page)
                if refreshed is not None and _is_select_all_checked(refreshed):
                    return "checked"
                if _wait_for_delete_batch_enabled(page, timeout_ms=250):
                    return "checked"
    except Exception:
        pass

    refreshed = _find_select_all(page)
    if refreshed is not None and not _is_select_all_checked(refreshed):
        log("✅ Click 'Chọn tất cả' nhưng checkbox vẫn un-checked -> coi như đã hết media.")
        return "empty"

    log("⚠️ Click 'Chọn tất cả' không xác nhận được trạng thái checkbox.")
    return "unknown"


def delete_all_images(context):
    """Xóa tất cả media trong BigSeller Material Center."""
    log("\n" + "="*60)
    log("🗑️ BẮT ĐẦU XÓA MEDIA TRONG MATERIAL CENTER")
    log("="*60)
    
    picture_page = None
    try:
        # Mở tab Material Center
        picture_page = context.new_page()
        picture_page.goto(MATERIAL_CENTER_URL, wait_until="domcontentloaded", timeout=60000)
        picture_page.bring_to_front()
        log("📂 Đã mở Material Center")
        
        # Chờ trang load
        time.sleep(3)
        
        # Đóng popup cảnh báo nếu có
        close_popup_if_exists(picture_page)
        time.sleep(1)
        
        # Vòng lặp xóa cho đến khi không còn ảnh
        loop_count = 0
        disabled_delete_count = 0
        while loop_count < 50:  # Giới hạn 50 lần để tránh vòng lặp vô hạn
            loop_count += 1
            log(f"\n--- Vòng lặp {loop_count} ---")
            
            try:
                # Kiểm tra popup trước mỗi vòng lặp
                close_popup_if_exists(picture_page)
                
                if _is_material_empty(picture_page):
                    log("✅ Material Center trống - không còn media để xóa")
                    return
                
                select_result = _ensure_select_all_selected(picture_page)
                if select_result == "empty":
                    return
                if select_result == "missing":
                    log("❌ Không tìm thấy nút 'Chọn tất cả'")
                    # Thử đóng popup một lần nữa
                    close_popup_if_exists(picture_page)
                    time.sleep(2)
                    continue  # Thử lại vòng lặp
                
                delete_btn = _wait_for_delete_batch_enabled(picture_page)
                if delete_btn is None:
                    if _is_material_empty(picture_page):
                        log("✅ Material Center trống - không còn media để xóa")
                        return
                    log("❌ Nút 'Xóa hàng loạt' chưa enabled sau khi chọn tất cả")
                    disabled_delete_count += 1
                    if disabled_delete_count >= 3:
                        log("✅ Nút xóa vẫn disabled sau nhiều lần chọn tất cả -> coi như đã hết media.")
                        return
                    time.sleep(2)
                    continue
                disabled_delete_count = 0

                try:
                    log("🗑️ Click nút 'Xóa hàng loạt'")
                    delete_btn.scroll_into_view_if_needed()
                    time.sleep(0.5)
                    delete_btn.click(force=True)
                    time.sleep(2)
                except Exception as e:
                    log(f"❌ Không click được nút 'Xóa hàng loạt': {e}")
                    break
                
                # Xác nhận xóa nếu có popup
                confirm_selectors = [
                    ".bs-micro-modal-confirm-btns button.bs-micro-btn-dangerous:has-text('Xóa')",
                    ".bs-micro-modal-confirm-btns button:has-text('Xóa')",
                    ".bs-micro-modal-confirm button:has-text('Xóa')",
                    "button.ant-btn-primary:has-text('OK')",
                    "button.ant-btn-primary:has-text('Xác nhận')",
                    "button.ant-btn-primary:has-text('Xóa')",
                    "button:has-text('OK')",
                    "button:has-text('Xác nhận')",
                    ".ant-modal button.ant-btn-primary"
                ]
                
                time.sleep(1)
                for selector in confirm_selectors:
                    try:
                        confirm_btn = picture_page.locator(selector).first
                        if confirm_btn.count() > 0 and confirm_btn.is_visible():
                            confirm_btn.click(force=True, timeout=2000)
                            log("✅ Đã xác nhận xóa")
                            time.sleep(3)
                            break
                    except:
                        continue
                
                # Chờ xóa xong
                time.sleep(3)
                    
            except Exception as e:
                log(f"⚠️ Lỗi trong vòng lặp {loop_count}: {e}")
                # Thử đóng popup nếu có lỗi
                close_popup_if_exists(picture_page)
                time.sleep(2)
                continue  # Thử tiếp vòng lặp tiếp theo
        
        log("✅ HOÀN TẤT XÓA MEDIA")
        
    except Exception as e:
        log(f"❌ LỖI KHI XÓA MEDIA: {e}")
    finally:
        # Luôn đóng tab Material Center
        if picture_page:
            try:
                picture_page.close()
                log("🔄 Đã đóng tab Material Center\n")
            except:
                pass
