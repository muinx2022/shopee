import time
import subprocess
import os
import re
from playwright.sync_api import sync_playwright

# ================= CẤU HÌNH =================
CONFIG = {
    'BRAVE_PATH': r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
    'PROFILE_DIR': r"D:\playwright\profile",
    'DEBUG_PORT': 9223,
    'LISTING_URL': 'https://www.bigseller.com/web/listing/shopee/index.htm?bsStatus=1',
}
# ============================================

def log(msg):
    print(msg, flush=True)

def extract_shopee_id(url):
    """Cắt ID từ URL Shopee - lấy số cuối cùng sau i.xxx.yyy"""
    try:
        # Dạng 1: ...-i.ShopID.ItemID
        match = re.search(r"i\.\d+\.(\d+)", url)
        if match: return match.group(1)
        
        # Dạng 2: .../product/ShopID/ItemID
        match2 = re.search(r"product\/\d+\/(\d+)", url)
        if match2: return match2.group(1)

        # Dạng 3: Link rút gọn hoặc query param
        match3 = re.search(r"i\.\d+\.(\d+)\?", url)
        if match3: return match3.group(1)
    except: pass
    return None

def open_brave():
    log("🧹 Dọn dẹp Brave cũ...")
    try:
        if os.name == 'nt': os.system("taskkill /f /im brave.exe >nul 2>&1")
        else: os.system("pkill -f brave")
        time.sleep(2)
    except: pass

    log("🦁 Mở Brave mới...")
    if not os.path.exists(CONFIG['PROFILE_DIR']): os.makedirs(CONFIG['PROFILE_DIR'])
    
    command = [
        CONFIG['BRAVE_PATH'],
        f"--remote-debugging-port={CONFIG['DEBUG_PORT']}",
        f"--user-data-dir={CONFIG['PROFILE_DIR']}",
        "--no-first-run", "--no-default-browser-check",
        CONFIG['LISTING_URL']
    ]
    subprocess.Popen(command, shell=True)
    time.sleep(5)
    return True

# ================= MAIN PROGRAM =================
if __name__ == "__main__":
    log("📌 Khởi động test lấy link nguồn SP...")
    p = None
    
    try:
        if not open_brave(): exit(1)
        
        p = sync_playwright().start()
        log(f"🔗 Kết nối Brave...")
        
        browser = None
        for _ in range(5):
            try:
                browser = p.chromium.connect_over_cdp(f"http://127.0.0.1:{CONFIG['DEBUG_PORT']}", timeout=30000)
                break
            except: time.sleep(3)
        
        if not browser: exit(1)
        context = browser.contexts[0]
        
        # Tìm tab BigSeller
        listing_page = None
        for page in context.pages:
            if "bigseller.com" in page.url:
                listing_page = page; break
        
        if not listing_page:
            if len(context.pages) > 0:
                listing_page = context.pages[0]
                listing_page.goto(CONFIG['LISTING_URL'])
            else: exit(1)
        
        listing_page.bring_to_front()
        log(f"\n{'='*60}\n📄 Trang Listing đã sẵn sàng\n{'='*60}")
        
        while True:
            log("\n" + "="*60)
            log("🔍 Tìm sản phẩm đầu tiên trong danh sách...")
            
            # Tìm hàng đầu tiên trong bảng
            try:
                first_row = listing_page.locator("tbody.ant-table-tbody tr").first
                first_row.wait_for(timeout=10000)
                
                # Lấy tên sản phẩm
                product_link = first_row.locator("a.list_tit_link")
                if product_link.count() > 0:
                    product_name = product_link.text_content().strip()[:50]
                    log(f"📦 Sản phẩm: {product_name}")
                
                # Click nút "Chỉnh sửa" để vào trang sản phẩm
                log("✏️ Click nút Chỉnh sửa...")
                listing_page.wait_for_selector("a.action_btn.addEditProduct", timeout=10000)
                
                with context.expect_page() as edit_info:
                    first_row.locator("a.action_btn.addEditProduct").click()
                
                edit_page = edit_info.value
                edit_page.bring_to_front()
                log("✅ Đã mở trang chỉnh sửa sản phẩm")
                time.sleep(3)
                
                # --- LẤY LINK NGUỒN SẢN PHẨM ---
                log("\n" + "="*60)
                log("📋 BẮT ĐẦU LẤY LINK NGUỒN SẢN PHẨM")
                log("="*60)
                
                try:
                    # Tìm input chứa link nguồn sản phẩm - có autoid='product_source_link_text'
                    link_input = edit_page.locator("input[autoid='product_source_link_text']")
                    
                    if link_input.count() == 0:
                        log("❌ Không tìm thấy input link nguồn sản phẩm")
                        edit_page.close()
                        continue
                    
                    log("✅ Tìm thấy input link nguồn sản phẩm")
                    
                    # Tìm nút Chép tương ứng - nằm trong cùng div.com_input_box
                    # Cách an toàn: tìm div chứa input này, rồi tìm button bên trong
                    parent_div = edit_page.locator("div.com_input_box:has(input[autoid='product_source_link_text'])")
                    copy_btn = parent_div.locator("button.com_input_right:has-text('chép')")
                    
                    if copy_btn.count() == 0:
                        log("❌ Không tìm thấy nút Chép")
                        edit_page.close()
                        continue
                    
                    # Click nút Chép
                    log("📋 Click nút 'Chép' (link nguồn SP)...")
                    copy_btn.click()
                    time.sleep(1)
                    
                    # Lấy giá trị link từ input
                    shopee_url = link_input.input_value()
                    
                    if not shopee_url:
                        log("❌ Không lấy được link Shopee từ input")
                        edit_page.close()
                        continue
                    
                    log("\n" + "🟢"*60)
                    log("✅ LINK NGUỒN SẢN PHẨM:")
                    log(shopee_url)
                    
                    # Extract ID từ link
                    shopee_id = extract_shopee_id(shopee_url)
                    if shopee_id:
                        log(f"\n🆔 ID sản phẩm: {shopee_id}")
                    else:
                        log("\n⚠️ Không extract được ID từ link")
                    
                    log("🟢"*60 + "\n")
                    
                except Exception as e:
                    log(f"❌ Lỗi khi lấy link: {e}")
                    import traceback
                    traceback.print_exc()
                
                # Dừng lại để kiểm tra
                log("\n" + "🔴"*60)
                log("⏸️  DỪNG LẠI ĐỂ KIỂM TRA")
                log("🔴"*60)
                log("\n📋 Kiểm tra:")
                log("   1. Link có đúng không?")
                log("   2. ID có chính xác không?")
                log("\n💡 Nhấn Enter để đóng tab và tiếp tục sản phẩm tiếp theo")
                log("💡 Hoặc Ctrl+C để dừng hẳn")
                log("🔴"*60)
                
                input("\n👉 Nhấn Enter để tiếp tục: ")
                
                # Đóng tab edit
                edit_page.close()
                log("✅ Đã đóng tab chỉnh sửa\n")
                
            except KeyboardInterrupt:
                log("\n⚠️ Người dùng dừng chương trình")
                break
            except Exception as e:
                log(f"❌ Lỗi: {e}")
                import traceback
                traceback.print_exc()
                time.sleep(3)
                continue

    except Exception as e: 
        log(f"❌ LỖI TỔNG: {e}")
        import traceback
        traceback.print_exc()
    finally:
        if p: p.stop()