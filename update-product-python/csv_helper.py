"""
Module xử lý CSV
"""
import csv

def log(msg):
    print(msg, flush=True)

def read_all_rows(path):
    """Đọc tất cả các dòng từ CSV"""
    rows = []
    try:
        with open(path, newline="", encoding="utf-8") as f:
            reader = csv.reader(f)
            header = next(reader)
            for i, row in enumerate(reader, start=2):
                if row and row[0].startswith("http"):
                    rows.append((i, row, header))
    except Exception as e:
        log(f"⚠️ Lỗi đọc CSV: {e}")
    return rows

def remove_processed_row(path, line_number):
    """Xóa dòng đã xử lý khỏi CSV"""
    try:
        with open(path, 'r', newline="", encoding="utf-8") as f:
            lines = f.readlines()
        
        with open(path, 'w', newline="", encoding="utf-8") as f:
            for i, line in enumerate(lines):
                if i != line_number - 1:  # line_number bắt đầu từ 1
                    f.write(line)
        log(f"   ✅ Đã xóa dòng {line_number} khỏi CSV")
    except Exception as e:
        log(f"   ⚠️ Lỗi xóa dòng: {e}")