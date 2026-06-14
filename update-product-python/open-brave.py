import subprocess
import time
import os

# ================= CẤU HÌNH =================
CONFIG = {
    'BRAVE_PATH': r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
    'PROFILE_DIR': r"D:\playwright\profile",
    'DEBUG_PORT': 9223,
}
# ============================================

def log(msg):
    print(msg, flush=True)

if __name__ == "__main__":
    log("🦁 Đang mở Brave với profile cố định...")
    
    # Tạo thư mục profile nếu chưa có
    if not os.path.exists(CONFIG['PROFILE_DIR']):
        os.makedirs(CONFIG['PROFILE_DIR'])
        log(f"✅ Đã tạo thư mục profile: {CONFIG['PROFILE_DIR']}")
    else:
        log(f"📁 Thư mục profile đã tồn tại: {CONFIG['PROFILE_DIR']}")
    
    # Lệnh mở Brave với link BigSeller
    command = [
        CONFIG['BRAVE_PATH'],
        f"--remote-debugging-port={CONFIG['DEBUG_PORT']}",
        f"--user-data-dir={CONFIG['PROFILE_DIR']}",
        "https://www.bigseller.com/web/crawl/index.htm"
    ]
    
    log(f"🚀 Đang chạy lệnh:")
    log(f"   {' '.join(command)}")
    
    try:
        # Mở Brave (không đợi nó thoát)
        subprocess.Popen(command)
        
        log(f"\n{'='*60}")
        log(f"✅ ĐÃ MỞ BRAVE THÀNH CÔNG!")
        log(f"{'='*60}")
        log(f"📁 Profile: {CONFIG['PROFILE_DIR']}")
        log(f"🔌 Debug Port: {CONFIG['DEBUG_PORT']}")
        log(f"\n📝 HƯỚNG DẪN TIẾP THEO:")
        log(f"1. Đăng nhập BigSeller trong cửa sổ Brave vừa mở")
        log(f"2. Cài extension BigSeller (nếu chưa có)")
        log(f"3. Lần sau chạy script, session sẽ được giữ nguyên")
        log(f"4. Kiểm tra port hoạt động: http://127.0.0.1:{CONFIG['DEBUG_PORT']}")
        log(f"{'='*60}\n")
        
        # Đợi 3s để Brave mở
        time.sleep(3)
        
        log("✅ Brave đã sẵn sàng. Bạn có thể chạy script automation bây giờ!")
        
    except FileNotFoundError:
        log(f"\n❌ KHÔNG TÌM THẤY BRAVE!")
        log(f"Đường dẫn hiện tại: {CONFIG['BRAVE_PATH']}")
        log(f"\n📝 Hãy sửa lại đường dẫn trong CONFIG['BRAVE_PATH']")
        log(f"Một số đường dẫn phổ biến:")
        log(f"  - C:\\Program Files\\BraveSoftware\\Brave-Browser\\Application\\brave.exe")
        log(f"  - C:\\Program Files (x86)\\BraveSoftware\\Brave-Browser\\Application\\brave.exe")
    except Exception as e:
        log(f"❌ LỖI: {e}")