"""High-level product update workflow."""
import os
import time
from pathlib import Path

from ai_content_generator import generate_product_content, update_product_description
from bigseller_edit_page import (
    close_visible_ant_modal,
    fill_product_name_on_edit_page,
    inspect_edit_page_for_update,
    save_product_with_image_retry,
    select_no_brand,
    upload_product_image_with_retry,
)
from product_name_rewrite import REWRITTEN_PRODUCT_NAME_HEADER
from product_runtime import log
from product_workbook import (
    MISSING_REWRITTEN_NAME_FILL_COLOR,
    REWRITTEN_NAME_OK_FILL_COLOR,
    mark_workbook_row,
    parse_price,
)
from video_uploader import MAX_VIDEO_DURATION_SECONDS, get_video_duration_seconds, upload_video

try:
    from video_paths import get_video_dir, video_path_for_sku
except ImportError:
    def get_video_dir():
        return (Path(__file__).resolve().parents[1] / "api" / "videos").resolve()

    def video_path_for_sku(sku, video_dir=None):
        return Path(video_dir or get_video_dir()) / f"{sku}.mp4"

def process_product(edit_page, row_data, config):
    """Xử lý 1 sản phẩm - lookup đúng 1 dòng trong XLSX theo ID Shopee."""
    
    # --- [0] KIỂM TRA LINK SHOPEE VÀ XLSX TRƯỚC KHI UPDATE BẤT KỲ THÔNG TIN NÀO ---
    log(f"\n{'='*60}")
    log(f"🔍 [0] KIỂM TRA LINK SHOPEE & XLSX")
    log(f"{'='*60}")
    
    try:
        inspection = row_data if isinstance(row_data, dict) and row_data.get("status") else None
        if inspection is None:
            inspection = inspect_edit_page_for_update(edit_page, config, log_steps=True)
        status = inspection["status"]
        shopee_id = inspection["shopee_id"]

        if status == "missing_shopee_id":
            return False
        if status == "not_in_xlsx":
            log("🔙 Đóng tab edit (không có trong XLSX).")
            return False
        xlsx_result = inspection["xlsx_result"]
        log(f"🚀 Bắt đầu update sản phẩm, dòng {xlsx_result['line_index']}")

    except Exception as e:
        log(f"❌ Lỗi kiểm tra link/XLSX: {e}")
        import traceback
        traceback.print_exc()
        return False
    
    # --- TỪ ĐÂY MỚI BẮT ĐẦU UPDATE SẢN PHẨM VỚI DỮ LIỆU 1 DÒNG TỪ XLSX ---
    link = xlsx_result["link"]
    sku = xlsx_result["sku"]
    product_name_new = xlsx_result["product_name"]
    price_from_xlsx = xlsx_result["price"]
    index = xlsx_result["line_index"]
    workbook_path = config.get("WORKBOOK_PATH")

    log(f"\n{'='*60}")
    log(f"🚀 BẮT ĐẦU XỬ LÝ | Sheet: {xlsx_result['sheet']} | Dòng: {index} | SKU: {sku}")
    log(f"{'='*60}")

    # --- [1] ĐIỀN TÊN SẢN PHẨM ---
    if xlsx_result.get("has_rewritten_product_name"):
        log(f"📝 Tên dùng cột '{REWRITTEN_PRODUCT_NAME_HEADER}'")
    else:
        log(f"📝 Cột G trống, dùng tên gốc từ cột F")
    log(f"   Tên mục tiêu ({len(product_name_new)} ký tự): {product_name_new[:120]}{'...' if len(product_name_new) > 120 else ''}")
    fill_product_name_on_edit_page(edit_page, product_name_new)

    # --- [2] XỬ LÝ MD5 ---
    try:
        md5_btn = edit_page.locator("span.sell_md5").first
        if md5_btn.is_visible():
            md5_btn.scroll_into_view_if_needed()
            md5_btn.click()
            
            # --- CHỜ TRẠNG THÁI (TỐI ĐA 10 GIÂY) ---
            log("⏳ Đang chờ đồng bộ MD5 (Max 10s)...")
            
            status_done = edit_page.locator("div.ant-modal:visible div.complete_Status").first
            
            try:
                # Chỉ chờ đúng 10 giây (10000 ms)
                status_done.wait_for(state="visible", timeout=10000)
                log("✅ Đã thấy trạng thái: Hoàn thành đồng bộ.")
            except:
                # Nếu quá 10s không thấy thì vào đây
                log("⚠️ Quá 10s không thấy hoàn thành -> Bỏ qua (Pass) để đóng popup.")

            # --- ĐÓNG POPUP (Luôn thực hiện dù thành công hay thất bại) ---
            # Phải đóng popup thì mới làm việc tiếp được với các element bên dưới
            if close_visible_ant_modal(edit_page, timeout_ms=5000):
                log("Closed MD5 modal.")
            else:
                log("Could not close MD5 modal.")
            
    except Exception as e:
        log(f"❌ Lỗi xử lý luồng MD5: {e}")
        pass

    # --- [3] CLICK RADIO 'Tải lên hình ảnh' ---
    try:
        radio_label = edit_page.locator("label.ant-radio-wrapper").filter(has_text="Tải lên hình ảnh").first
        if radio_label.is_visible():
            radio_label.click()
            log("✅ [3] Đã click Radio ảnh.")
    except: 
        pass

    # --- [4] ĐIỀN SKU CHA ---
    try:
        sku_input = edit_page.locator("input[autoid='parent_sku_text']")
        if sku_input.is_visible():
            sku_input.fill(sku)
            sku_input.evaluate("el => el.dispatchEvent(new Event('input', {bubbles:true}))")
            log(f"✅ [4] Đã điền SKU Cha.")
    except: 
        pass

    # --- [5] CHỌN THƯƠNG HIỆU (NO BRAND) ---
    log("⏳ [5] Đang chọn Thương hiệu...")
    try:
        if not select_no_brand(edit_page):
            log("⚠️ Brand vẫn chưa có giá trị; BigSeller có thể đã đổi dropdown/option.")
    except Exception as e: 
        log(f"❌ Lỗi Brand: {e}")

    # --- [6] ĐIỀN SKU PHÂN LOẠI ---
    log("⏳ [6] Điền SKU phân loại...")
    try:
        sku_inputs = edit_page.locator("input[autoid^='variation_sku_text_']")
        count = sku_inputs.count()
        if count > 0:
            for i in range(count):
                inp = sku_inputs.nth(i)
                if inp.is_visible():
                    inp.scroll_into_view_if_needed()
                    inp.fill(sku)
                    inp.evaluate("el => el.dispatchEvent(new Event('input', {bubbles:true}))")
                    inp.evaluate("el => el.blur()")
            log(f"   => Đã điền {count} ô.")
    except: 
        pass

    # --- [7] ĐIỀN TỒN KHO ---
    log("⏳ [7] Điền Tồn kho...")
    try:
        stock_inputs = edit_page.locator("input[autoid^='variation_stock_text_']")
        count = stock_inputs.count()
        if count > 0:
            for i in range(count):
                inp = stock_inputs.nth(i)
                if inp.is_visible():
                    inp.scroll_into_view_if_needed()
                    inp.fill(config['STOCK_VALUE'])
                    inp.evaluate("el => el.dispatchEvent(new Event('input', {bubbles:true}))")
                    inp.evaluate("el => el.blur()")
            log(f"   => Đã điền {count} ô.")
    except: 
        pass

    # --- [8] CẬP NHẬT GIÁ ---
    new_price = parse_price(price_from_xlsx)
    log(f"⏳ [8] Cập nhật Giá từ XLSX: {price_from_xlsx} → {new_price:,}đ...")
    try:
        price_inputs = edit_page.locator("input[autoid^='variation_price_text_']")
        count = price_inputs.count()
        if count > 0:
            for i in range(count):
                inp = price_inputs.nth(i)
                if inp.is_visible():
                    inp.scroll_into_view_if_needed()
                    inp.fill(str(new_price))
                    inp.evaluate("el => el.dispatchEvent(new Event('input', {bubbles:true}))")
                    inp.evaluate("el => el.blur()")
            log("   => Xong.")
    except:
        pass

    # --- [9] CHỌN VẬN CHUYỂN 'NHANH' ---
    log("⏳ [9] Vận chuyển 'Nhanh'...")
    try:
        shipping_label = edit_page.locator("label.ant-checkbox-wrapper").filter(has_text="Nhanh").first
        if shipping_label.is_visible():
            shipping_label.scroll_into_view_if_needed()
            if shipping_label.locator(".ant-checkbox-checked").count() == 0:
                shipping_label.click()
                log("   => Đã tick.")
            else:
                log("   => Đã tick sẵn.")
    except: 
        pass

    # --- [10] NHẬP CÂN NẶNG ---
    log(f"⏳ [10] Cân nặng: {config['WEIGHT_VAL']}g...")
    try:
        w_inp = edit_page.locator("input[autoid='weight_text']")
        if w_inp.is_visible():
            w_inp.scroll_into_view_if_needed()
            w_inp.fill(config['WEIGHT_VAL'])
            w_inp.evaluate("el => el.dispatchEvent(new Event('input', {bubbles:true}))")
            w_inp.evaluate("el => el.blur()")
    except: 
        pass

    # Kiểm tra file video (cùng thư mục scrape: api/videos/{SKU}.mp4)
    video_path = None
    video_folder = config.get("VIDEO_FOLDER") or str(get_video_dir())
    try:
        candidate_video_path = video_path_for_sku(sku, video_folder)
        if candidate_video_path.exists():
            video_path = str(candidate_video_path)
            duration = get_video_duration_seconds(video_path)
            if duration is not None and duration >= MAX_VIDEO_DURATION_SECONDS:
                log(
                    f"⏭️ Bỏ qua video (thời lượng {duration:.1f}s >= {MAX_VIDEO_DURATION_SECONDS}s): "
                    f"{video_path}"
                )
                video_path = None
            else:
                if duration is not None:
                    log(f"✅ Tìm thấy video ({duration:.1f}s): {video_path}")
                else:
                    log(f"✅ Tìm thấy video: {video_path}")
    except ValueError:
        log(f"ℹ️ SKU không hợp lệ để tìm video: {sku}")

    # --- [10.5] UPLOAD VIDEO ---
    if video_path:
        log(f"\n🎥 [10.5] Upload video: {os.path.basename(video_path)}")
        video_upload_success = False
        max_video_upload_attempts = 3
        for attempt in range(1, max_video_upload_attempts + 1):
            try:
                log(f"   🔁 Thử upload video lần {attempt}/{max_video_upload_attempts}...")
                video_upload_success = upload_video(edit_page, video_path)
                if video_upload_success:
                    log("   ✅ Video đã upload thành công!")
                    break
                log("   ⚠️ Upload video thất bại")
            except Exception as e:
                log(f"   ❌ Lỗi upload video lần {attempt}: {e}")

            if attempt < max_video_upload_attempts:
                log("   ⏳ Chờ 3s rồi quay lại đúng bước upload video...")
                time.sleep(3)

        if not video_upload_success:
            log("   ⚠️ Upload video thất bại sau 3 lần. Giữ nguyên video trên trang rồi tiếp tục lưu sản phẩm.")
    else:
        log("ℹ️ [10.5] Bỏ qua upload video (không có file)")

    # --- [11] UPLOAD ẢNH ---
    if os.path.exists(config['IMAGE_PATH']):
        log(f"\n🖼️ [11] Upload ảnh: {config['IMAGE_PATH']}")
        try:
            upload_success = upload_product_image_with_retry(
                edit_page,
                config['IMAGE_PATH'],
                max_attempts=3,
                verify_timeout_ms=5000
            )
            if upload_success:
                log("✅ Đã upload ảnh thành công!")
            else:
                log("⚠️ Upload ảnh không thành công sau 3 lần, tiếp tục bước tiếp theo.")
        except Exception as e:
            log(f"❌ Lỗi Upload: {e}")
        
    else:
        log(f"⚠️ [11] Không tìm thấy ảnh tại: {config['IMAGE_PATH']}")

    # --- [12] TẠO MÔ TẢ AI & LƯU ---
    log(f"\n💾 [12] TẠO MÔ TẢ AI & LƯU SẢN PHẨM...")
    
    # --- [12.1] TẠO MÔ TẢ AI ---
    log("\n🤖 [12.1] Tạo mô tả AI...")
    try:
        ai_content = generate_product_content(
            product_name_new,
            api_key_file=config.get("API_KEY_FILE"),
            model=config.get("MODEL"),
        )
        if not (ai_content or "").strip():
            log("   ❌ Mô tả AI rỗng — dừng, không lưu sản phẩm.")
            return False
        log(f"   ✅ Đã tạo mô tả: {len(ai_content)} ký tự")

        if not update_product_description(edit_page, ai_content):
            log("   ❌ Không cập nhật được mô tả AI — dừng, không lưu sản phẩm.")
            return False
        log("   ✅ Đã cập nhật mô tả AI vào sản phẩm")
    except Exception as e:
        log(f"   ❌ Lỗi tạo/cập nhật mô tả AI: {e}")
        log("   ⛔ Dừng — không lưu sản phẩm.")
        return False
    
    # --- [12.2] LƯU SẢN PHẨM ---
    try:
        save_success = save_product_with_image_retry(
            edit_page,
            config['IMAGE_PATH'],
            max_attempts=3
        )
        if not save_success:
            log("⚠️ Lưu sản phẩm thất bại sau các lần thử.")
            return False
    except Exception as e:
        log(f"❌ Lỗi Lưu: {e}")
        return False

    marker_color = REWRITTEN_NAME_OK_FILL_COLOR if xlsx_result.get("has_rewritten_product_name") else MISSING_REWRITTEN_NAME_FILL_COLOR
    marker_label = "xanh lá" if xlsx_result.get("has_rewritten_product_name") else "xanh dương"
    if mark_workbook_row(workbook_path, xlsx_result["sheet"], index, marker_color):
        log(f"✅ Đã tô {marker_label} dòng {index} sau khi update BigSeller thành công.")
    else:
        log(f"ℹ️ Không đổi màu dòng {index}, có thể dòng đã được đánh dấu scrape ok màu tím.")

    log(f"✅ HOÀN TẤT XỬ LÝ SKU: {sku}")
    return True
