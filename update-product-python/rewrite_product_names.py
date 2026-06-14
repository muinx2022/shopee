import argparse
import os
from pathlib import Path

from openpyxl import load_workbook

from product_processor import (
    DEFAULT_API_KEY_FILE_PATH,
    DEFAULT_MODEL,
    PRICE_HEADERS,
    PRODUCT_LINK_HEADER,
    PRODUCT_NAME_HEADER,
    REWRITTEN_PRODUCT_NAME_HEADER,
    SKU_HEADER,
    find_first_header_column,
    find_header_column,
    prepare_rewritten_product_names,
)


BASE_DIR = Path(__file__).resolve().parents[1]
DEFAULT_WORKBOOK_PATH = os.environ.get("SHOPEE_WORKBOOK_PATH", str(BASE_DIR / "data" / "data.xlsx"))
DEFAULT_DATA_SHEET = os.environ.get("SHOPEE_DATA_SHEET", "bizly")
DEFAULT_START_ROW = int(os.environ.get("SHOPEE_START_ROW", "2"))
DEFAULT_BATCH_SIZE = int(os.environ.get("OPENAI_PRODUCT_NAME_BATCH_SIZE", "40"))
DEFAULT_API_KEY_FILE = os.environ.get("OPENAI_API_KEY_FILE", str(DEFAULT_API_KEY_FILE_PATH))
DEFAULT_MODEL_NAME = os.environ.get("OPENAI_PRODUCT_NAME_MODEL", DEFAULT_MODEL)


def log(message):
    print(message, flush=True)


def sheet_has_required_headers(sheet):
    return (
        find_header_column(sheet, PRODUCT_LINK_HEADER)
        and find_header_column(sheet, PRODUCT_NAME_HEADER)
        and find_header_column(sheet, SKU_HEADER)
        and find_first_header_column(sheet, PRICE_HEADERS)
    )


def get_rewrite_sheet_names(workbook_path):
    workbook = load_workbook(workbook_path, read_only=True, data_only=True)
    return [sheet.title for sheet in workbook.worksheets if sheet_has_required_headers(sheet)]


def parse_args():
    parser = argparse.ArgumentParser(
        description=(
            "Rewrite product names in an XLSX workbook. "
            f"Rows that already have '{REWRITTEN_PRODUCT_NAME_HEADER}' are skipped automatically."
        )
    )
    parser.add_argument("--workbook", default=DEFAULT_WORKBOOK_PATH, help="Đường dẫn workbook XLSX.")
    parser.add_argument("--sheet", default=DEFAULT_DATA_SHEET, help="Sheet cần rewrite, hoặc dùng --all-sheets.")
    parser.add_argument("--all-sheets", action="store_true", help="Rewrite tất cả sheet có đủ header cần thiết.")
    parser.add_argument("--list-sheets", action="store_true", help="Liệt kê sheet có thể rewrite rồi thoát.")
    parser.add_argument("--start-row", type=int, default=DEFAULT_START_ROW, help="Dòng bắt đầu đọc dữ liệu.")
    parser.add_argument("--end-row", type=int, default=0, help="Dòng kết thúc. 0 = tới cuối sheet.")
    parser.add_argument("--overwrite", action="store_true", help="Ghi lại cột tên đã sửa kể cả khi đã có giá trị.")
    parser.add_argument("--model", default=DEFAULT_MODEL_NAME, help="OpenAI model dùng để rewrite tên sản phẩm.")
    parser.add_argument("--api-key-file", default=DEFAULT_API_KEY_FILE, help="File chứa OpenAI API key.")
    parser.add_argument("--batch-size", type=int, default=DEFAULT_BATCH_SIZE, help="Số tên sản phẩm unique gửi mỗi batch.")
    return parser.parse_args()


def main():
    args = parse_args()
    workbook_path = Path(args.workbook)
    if not workbook_path.exists():
        raise FileNotFoundError(f"Không tìm thấy workbook: {workbook_path}")

    if args.list_sheets:
        sheet_names = get_rewrite_sheet_names(workbook_path)
        log(f"Workbook: {workbook_path}")
        log(f"Số sheet có thể rewrite: {len(sheet_names)}")
        for index, sheet_name in enumerate(sheet_names, start=1):
            log(f"  {index}. {sheet_name}")
        return 0

    if args.all_sheets:
        sheet_names = get_rewrite_sheet_names(workbook_path)
    else:
        sheet_names = [args.sheet]

    if not sheet_names:
        log("Không có sheet nào để rewrite.")
        return 0

    log("📌 Rewrite product names")
    log(f"📄 Workbook: {workbook_path}")
    log(f"📑 Sheet: {', '.join(sheet_names)}")
    log(f"🔢 Start row: {max(2, args.start_row)}")
    log(f"🤖 Model: {args.model}")
    log(f"📦 Batch size: {max(1, args.batch_size)}")

    total_updated = 0
    for sheet_name in sheet_names:
        log("")
        log(f"===== Sheet: {sheet_name} =====")
        total_updated += prepare_rewritten_product_names(
            workbook_path,
            sheet_name,
            max(2, args.start_row),
            args.model,
            Path(args.api_key_file),
            max(1, args.batch_size),
            end_row=args.end_row or None,
            overwrite=args.overwrite,
        )

    log("")
    log(f"✅ Xong. Tổng dòng đổi tên: {total_updated}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
