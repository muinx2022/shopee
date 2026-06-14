"""
Module tạo mô tả sản phẩm bằng ChatGPT
"""
import os
import json
from pathlib import Path

import requests

MAX_SHOPEE_DESCRIPTION_CHARS = 3000
TARGET_DESCRIPTION_MIN_CHARS = 2800
TRIMMED_DESCRIPTION_MAX_CHARS = 2900

try:
    from process_data.process_sheet_data import get_openai_api_key as read_openai_api_key_from_file
except ImportError:
    read_openai_api_key_from_file = None


def _parse_api_key_line(line: str) -> str:
    line = str(line or "").strip()
    if not line or line.startswith("#"):
        return ""
    if "=" in line and not line.startswith("sk-"):
        _, _, line = line.partition("=")
        line = line.strip().strip('"').strip("'")
    return line


def get_openai_api_key(api_key_file=None) -> str:
    """Ưu tiên OPENAI_API_KEY env, sau đó file key (cùng nguồn với rewrite tên)."""
    env_key = os.environ.get("OPENAI_API_KEY") or ""
    for line in env_key.splitlines():
        parsed = _parse_api_key_line(line)
        if parsed:
            return parsed

    candidates = [
        api_key_file,
        os.environ.get("OPENAI_API_KEY_FILE"),
    ]
    for candidate in candidates:
        path = str(candidate or "").strip()
        if not path:
            continue
        if read_openai_api_key_from_file is not None:
            key = read_openai_api_key_from_file(Path(path))
            if key:
                return key.strip()
        if Path(path).exists():
            for line in Path(path).read_text(encoding="utf-8").splitlines():
                parsed = _parse_api_key_line(line)
                if parsed:
                    return parsed
    return ""

SYSTEM_PROMPT = """Bạn là chuyên gia SEO TMĐT chuyên viết mô tả sản phẩm GIÀY – DÉP NỮ để đăng Shopee, Lazada, Tiki, Ozon.

NHIỆM VỤ:
Viết MỘT bài mô tả sản phẩm duy nhất, sẵn sàng đăng bán.

YÊU CẦU BẮT BUỘC:
- ĐỘ DÀI: cố gắng trong khoảng 2800–2900 ký tự.
- TUYỆT ĐỐI KHÔNG VƯỢT 3000 ký tự vì Shopee sẽ báo lỗi.
- Chuẩn SEO theo hành vi tìm kiếm người mua giày nữ online.
- Lặp tự nhiên từ khóa chính và biến thể liên quan đến giày nữ, không spam.
- Văn phong chuyên nghiệp, dễ đọc, tập trung lợi ích người dùng nữ.

CẤU TRÚC:
- Mở bài: giới thiệu sản phẩm, nêu từ khóa chính.
- Thân bài: thiết kế, chất liệu, đế, form, cảm giác mang, tính ứng dụng.
- Kết bài: gợi ý phối đồ, đối tượng phù hợp, kêu gọi mua.

QUY ĐỊNH:
- Không chèn tiêu đề thừa.
- Không ghi "Thông số", "Cam kết", "Chính sách".
- Không giải thích SEO.

HASHTAG:
- Đặt NGAY SAU đoạn mô tả cuối cùng.
- Viết liền, không tiêu đề.
- Đúng ngành giày nữ, có mã sản phẩm.
- CHÍNH XÁC 18 hashtag.

NGUYÊN TẮC CUỐI:
- Nếu cần điều chỉnh, chỉ thay đổi độ dài câu để nằm trong khoảng 2800–2900 ký tự.
- Tuyệt đối không thêm hoặc bớt hashtag.
"""

def log(msg):
    print(msg, flush=True)


def trim_description_for_shopee(content, max_chars=MAX_SHOPEE_DESCRIPTION_CHARS):
    text = str(content or "").replace("\r\n", "\n").replace("\r", "\n").strip()
    if len(text) <= max_chars:
        return text

    target_max = min(TRIMMED_DESCRIPTION_MAX_CHARS, max_chars)
    clipped = text[:target_max].rstrip()
    lower_bound = min(TARGET_DESCRIPTION_MIN_CHARS, max(0, target_max - 220))

    for separator in ("\n\n", "\n", ". ", "! ", "? "):
        pos = clipped.rfind(separator, lower_bound)
        if pos >= lower_bound:
            end = pos + (1 if separator in (". ", "! ", "? ") else 0)
            return clipped[:end].strip()

    pos = clipped.rfind(" ", lower_bound)
    if pos >= lower_bound:
        return clipped[:pos].strip()

    return clipped.strip()


def generate_product_content(product_name: str, api_key_file=None, model=None) -> str:
    """
    Tạo mô tả sản phẩm bằng ChatGPT
    
    Args:
        product_name: Tên sản phẩm
        
    Returns:
        Mô tả sản phẩm (tối đa 3000 ký tự)
    """
    log(f"🤖 Đang tạo mô tả AI cho: {product_name}")
    
    url = "https://api.openai.com/v1/chat/completions"

    payload = {
        "model": model or os.environ.get("OPENAI_PRODUCT_NAME_MODEL") or "gpt-4o-mini",
        "messages": [
            {
                "role": "system",
                "content": SYSTEM_PROMPT
            },
            {
                "role": "user",
                "content": (
                    f"Tên sản phẩm: {product_name}\n"
                    f"Giới hạn bắt buộc: tối đa {MAX_SHOPEE_DESCRIPTION_CHARS} ký tự, "
                    f"nên nằm khoảng {TARGET_DESCRIPTION_MIN_CHARS}-{TRIMMED_DESCRIPTION_MAX_CHARS} ký tự."
                )
            }
        ],
        "temperature": 0.6,
        "max_tokens": 1200
    }

    try:
        api_key = get_openai_api_key(api_key_file)
        if not api_key:
            raise Exception(
                "Missing OpenAI API key. Set OPENAI_API_KEY, OPENAI_API_KEY_FILE, "
                "hoặc truyền --api-key-file."
            )
        response = requests.post(
            url,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {api_key}",
            },
            data=json.dumps(payload),
            timeout=60
        )

        if response.status_code != 200:
            log(f"❌ API lỗi: {response.status_code}")
            raise Exception(response.text)

        content = response.json()["choices"][0]["message"]["content"]
        original_length = len(content)
        content = trim_description_for_shopee(content)
        
        if len(content) != original_length:
            log(f"✂️ Mô tả AI dài {original_length} ký tự, đã cắt còn {len(content)} ký tự")
        log(f"✅ Đã tạo mô tả: {len(content)} ký tự")
        return content
        
    except Exception as e:
        log(f"❌ Lỗi tạo AI content: {e}")
        raise


def update_product_description(edit_page, ai_content):
    """
    Cập nhật mô tả sản phẩm bằng AI content
    
    Args:
        edit_page: Playwright page object
        ai_content: Nội dung từ ChatGPT
    """
    ai_content = trim_description_for_shopee(ai_content)
    log("\n📝 Đang cập nhật mô tả sản phẩm...")
    
    try:
        # Tìm textarea mô tả
        desc_textarea = edit_page.locator("textarea[autoid='product_description_text']")
        
        if not desc_textarea.is_visible():
            log("⚠️ Không tìm thấy textarea mô tả")
            return False
        
        # Scroll đến textarea
        desc_textarea.scroll_into_view_if_needed()
        
        # Xóa nội dung cũ
        log("   🗑️ Xóa nội dung cũ...")
        desc_textarea.click()
        desc_textarea.evaluate("el => el.value = ''")
        desc_textarea.fill("")
        
        # Điền nội dung mới
        log(f"   ✍️ Điền nội dung mới ({len(ai_content)} ký tự)...")
        desc_textarea.fill(ai_content)
        
        # Trigger events
        desc_textarea.evaluate("el => el.dispatchEvent(new Event('input', {bubbles:true}))")
        desc_textarea.evaluate("el => el.blur()")
        
        # Kiểm tra độ dài
        import time
        time.sleep(1)
        
        # Lấy số ký tự hiện tại từ count_box
        try:
            count_box = edit_page.locator("span.count_box").first
            count_text = count_box.text_content()
            current_count = count_text.split('/')[0].strip()
            current_count_int = int("".join(ch for ch in current_count if ch.isdigit()) or "0")
            if current_count_int > MAX_SHOPEE_DESCRIPTION_CHARS:
                log(f"   ❌ Mô tả vượt giới hạn Shopee: {current_count_int}/{MAX_SHOPEE_DESCRIPTION_CHARS} ký tự")
                return False
            log(f"   ✅ Đã cập nhật: {current_count} ký tự")
        except:
            log(f"   ✅ Đã cập nhật mô tả")
        
        return True
        
    except Exception as e:
        log(f"   ❌ Lỗi cập nhật mô tả: {e}")
        return False


# ===== TEST =====
if __name__ == "__main__":
    content = generate_product_content(
        "Giày búp bê nữ mũi tròn đế mềm BBN-238"
    )
    print("\n===== AI CONTENT =====")
    print(content)
    print("======================")
    print(f"Ký tự: {len(content)}")
