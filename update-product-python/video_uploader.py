"""
Module upload video từ máy tính
"""
import os
import shutil
import subprocess
import time

MAX_VIDEO_DURATION_SECONDS = 60


def log(msg):
    print(msg, flush=True)


def get_video_duration_seconds(video_path):
    path = str(video_path)
    if not os.path.exists(path):
        return None

    try:
        from mutagen.mp4 import MP4

        duration = MP4(path).info.length
        if duration is not None and duration > 0:
            return float(duration)
    except Exception:
        pass

    ffprobe = shutil.which("ffprobe")
    if ffprobe:
        try:
            result = subprocess.run(
                [
                    ffprobe,
                    "-v",
                    "error",
                    "-show_entries",
                    "format=duration",
                    "-of",
                    "default=noprint_wrappers=1:nokey=1",
                    path,
                ],
                capture_output=True,
                text=True,
                check=True,
                timeout=30,
            )
            duration = float(str(result.stdout or "").strip())
            if duration > 0:
                return duration
        except Exception:
            pass

    return None


def should_skip_video_upload(video_path, max_duration=MAX_VIDEO_DURATION_SECONDS):
    duration = get_video_duration_seconds(video_path)
    if duration is None:
        return False, None
    return duration >= max_duration, duration


def open_local_video_upload_option(page):
    """Go back to the video upload control and open Upload from local menu."""
    try:
        page.keyboard.press("Escape")
        time.sleep(0.5)
    except:
        pass

    add_video_btn = page.locator("button:has-text('Thêm video')").first
    if add_video_btn.count() == 0:
        log("   ⚠️ Không tìm thấy nút 'Thêm video'")
        return None

    add_video_btn.scroll_into_view_if_needed()
    time.sleep(0.5)

    for attempt in range(1, 4):
        log(f"   🖱️ Mở menu video lần {attempt}/3...")
        try:
            add_video_btn.hover()
            time.sleep(1)

            upload_option = page.locator("li[autoid='upload_local_video_option']").first
            if upload_option.count() > 0:
                upload_option.wait_for(state="visible", timeout=5000)
                return upload_option
        except Exception as e:
            log(f"   ⚠️ Chưa mở được menu video: {e}")

        try:
            add_video_btn.click(force=True)
            time.sleep(1)
            upload_option = page.locator("li[autoid='upload_local_video_option']").first
            if upload_option.count() > 0:
                upload_option.wait_for(state="visible", timeout=5000)
                return upload_option
        except:
            pass

    log("   ⚠️ Không tìm thấy menu 'Tải lên từ máy'")
    return None


def delete_uploaded_video(page):
    """Delete the current video block when upload is stuck or failed."""
    log("   🗑️ Đang xóa video upload lỗi...")
    try:
        video_items = page.locator("div.pro_vid_box div.page_edit_img_item.comm_img_module")
        count = video_items.count()
        if count == 0:
            log("   ℹ️ Không tìm thấy video box để xóa.")
            return True

        for index in range(count):
            video_item = video_items.nth(index)
            if not video_item.is_visible():
                continue

            video_item.scroll_into_view_if_needed()
            video_item.hover()
            time.sleep(0.5)

            delete_btn = video_item.locator("span.action_btn[title='Xóa']").first
            if delete_btn.count() == 0:
                delete_btn = video_item.locator("span[title='Xóa']").first
            if delete_btn.count() == 0:
                continue

            delete_btn.click(force=True)
            time.sleep(0.5)

            confirm_btn = page.locator(
                "div.ant-modal-confirm-btns button.ant-btn-primary, "
                "div.ant-modal-footer button.ant-btn-primary"
            ).last
            if confirm_btn.count() > 0 and confirm_btn.is_visible():
                confirm_btn.click(force=True)
                time.sleep(1)

            log("   ✅ Đã xóa video upload lỗi.")
            return True

        log("   ⚠️ Không tìm thấy nút xóa video.")
        return False
    except Exception as e:
        log(f"   ⚠️ Lỗi khi xóa video upload lỗi: {e}")
        return False


def _visible_count(locator):
    try:
        count = locator.count()
        visible = 0
        for index in range(count):
            try:
                if locator.nth(index).is_visible():
                    visible += 1
            except Exception:
                continue
        return visible
    except Exception:
        return 0


def _detect_video_upload_error(page):
    error_selectors = [
        ".ant-message-error",
        ".ant-notification-notice-message",
        ".ant-notification-notice-description",
        ".ant-message-notice-content",
        ".toast",
        ".el-message--error",
    ]
    error_keywords = [
        "thất bại",
        "không thành công",
        "lỗi",
        "fail",
        "failed",
        "error",
        "quá",
        "không hỗ trợ",
    ]

    for selector in error_selectors:
        try:
            items = page.locator(selector)
            for index in range(min(items.count(), 5)):
                item = items.nth(index)
                if not item.is_visible():
                    continue
                text = (item.inner_text(timeout=1000) or "").strip()
                if text and any(keyword in text.lower() for keyword in error_keywords):
                    return text
        except Exception:
            continue
    return ""


def _is_video_item_ready(video_item):
    try:
        text = (video_item.inner_text(timeout=1000) or "").lower()
    except Exception:
        text = ""

    loading_keywords = [
        "đang upload",
        "đang tải",
        "uploading",
        "loading",
    ]
    if any(keyword in text for keyword in loading_keywords):
        return False
    if "%" in text and "100%" not in text:
        return False

    ready_selectors = [
        "video",
        "img[src]",
        "canvas",
        "span.action_btn[title='Xóa']",
        "span[title='Xóa']",
        ".bsicon_trash_2",
        ".top_status.bk_green",
    ]
    for selector in ready_selectors:
        try:
            marker = video_item.locator(selector).first
            if marker.count() > 0 and marker.is_visible():
                return True
        except Exception:
            continue
    return False


def is_uploaded_video_ready(page, previous_count=0):
    video_items = page.locator("div.pro_vid_box div.page_edit_img_item.comm_img_module")
    try:
        total = video_items.count()
    except Exception:
        return False

    if total <= 0:
        return False

    if total > previous_count:
        for index in range(total):
            try:
                item = video_items.nth(index)
                if item.is_visible() and _is_video_item_ready(item):
                    return True
            except Exception:
                continue

    success_selectors = [
        "span.top_status.bk_green:has-text('Tải lên thành công')",
        "span:has-text('Tải lên thành công')",
        "div:has-text('Tải lên thành công')",
        ".ant-message-success:has-text('thành công')",
        ".ant-message-notice-content:has-text('thành công')",
    ]
    for selector in success_selectors:
        try:
            success = page.locator(selector).first
            if success.count() > 0 and success.is_visible():
                return True
        except Exception:
            continue

    return False

def upload_video(page, video_path):
    """
    Upload video từ máy tính vào BigSeller
    
    Args:
        page: Playwright page object
        video_path: Đường dẫn đến file video
        
    Returns:
        bool: True nếu upload thành công, False nếu thất bại
    """
    log("   🎥 Đang upload video...")

    try:
        if not os.path.exists(video_path):
            log(f"   ❌ File không tồn tại: {video_path}")
            return False

        skip_upload, duration = should_skip_video_upload(video_path)
        if skip_upload:
            log(
                f"   ⏭️ Bỏ qua video (thời lượng {duration:.1f}s >= {MAX_VIDEO_DURATION_SECONDS}s): "
                f"{os.path.basename(video_path)}"
            )
            return False

        if duration is not None:
            log(f"   ⏱️ Thời lượng: {duration:.1f}s")
        else:
            log("   ⚠️ Không đọc được thời lượng video, vẫn thử upload.")

        log(f"   📊 Size: {os.path.getsize(video_path) / 1024 / 1024:.2f} MB")
        
        existing_video_count = _visible_count(page.locator("div.pro_vid_box div.page_edit_img_item.comm_img_module"))

        # Quay lại đúng đoạn upload: hover nút "Thêm video" và chọn "Tải lên từ máy"
        upload_option = open_local_video_upload_option(page)
        if not upload_option:
            return False
        
        # Click vào "Tải lên từ máy" và chờ file chooser
        log("   📤 Đang chọn file...")
        
        # Xử lý file chooser với try-catch để bắt lỗi Windows
        try:
            with page.expect_file_chooser(timeout=10000) as fc_info:
                upload_option.click()
            
            file_chooser = fc_info.value
            file_chooser.set_files(video_path)
            
        except Exception as file_error:
            log(f"   ❌ Lỗi chọn file: {file_error}")
            # Nếu Windows báo "can't find", báo rõ ràng
            if "can't find" in str(file_error).lower() or "cannot find" in str(file_error).lower():
                log(f"   ⚠️ Windows không tìm thấy file: {video_path}")
            return False
        
        log("   ✅ Đã chọn file!")
        log("   ⏳ Đang upload...")
        
        # Chờ và kiểm tra trạng thái upload
        max_wait = 60  # Chờ tối đa 1 phút mỗi lần retry
        upload_success = False
        
        for i in range(max_wait):
            time.sleep(1)
            
            # Tìm thông báo "Tải lên thành công" - đây là tín hiệu chính thức để xác nhận upload.
            success_status = page.locator("span.top_status.bk_green:has-text('Tải lên thành công')").first

            if success_status.count() > 0 and success_status.is_visible():
                log(f"   ✅ Upload hoàn tất sau {i+1}s!")
                upload_success = True
                break

            upload_error = _detect_video_upload_error(page)
            if upload_error:
                log(f"   ❌ BigSeller báo lỗi upload video: {upload_error}")
                return False
            
            # Log mỗi 10s
            if i > 0 and i % 10 == 0:
                log(f"   ⏳ Đang upload... ({i}s)")
        
        if not upload_success:
            current_video_count = _visible_count(page.locator("div.pro_vid_box div.page_edit_img_item.comm_img_module"))
            log(f"   ℹ️ Video boxes: trước upload={existing_video_count}, hiện tại={current_video_count}")
            log("   ⚠️ Không thấy thông báo 'Tải lên thành công' sau 1 phút")
            return False
        
        return True
        
    except Exception as e:
        log(f"   ❌ Lỗi: {e}")
        return False
