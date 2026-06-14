"""Compatibility facade for update-product helpers.

The implementation is split into focused modules so callers can keep importing
from product_processor while maintenance happens in smaller files.
"""
import sys
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parents[1]
if str(BASE_DIR) not in sys.path:
    sys.path.insert(0, str(BASE_DIR))

from product_name_rewrite import (
    DEFAULT_API_KEY_FILE_PATH,
    DEFAULT_MODEL,
    prepare_rewritten_product_names,
)
from product_workbook import (
    PRICE_HEADERS,
    PRODUCT_LINK_HEADER,
    PRODUCT_NAME_HEADER,
    REWRITTEN_PRODUCT_NAME_HEADER,
    SKU_HEADER,
    find_first_header_column,
    find_header_column,
)
from bigseller_edit_page import extract_bigseller_edit_id, inspect_edit_page_for_update
from product_runtime import close_page_accepting_dialog, log
from product_update_flow import process_product

__all__ = [
    "DEFAULT_API_KEY_FILE_PATH",
    "DEFAULT_MODEL",
    "PRICE_HEADERS",
    "PRODUCT_LINK_HEADER",
    "PRODUCT_NAME_HEADER",
    "REWRITTEN_PRODUCT_NAME_HEADER",
    "SKU_HEADER",
    "close_page_accepting_dialog",
    "extract_bigseller_edit_id",
    "find_first_header_column",
    "find_header_column",
    "inspect_edit_page_for_update",
    "log",
    "prepare_rewritten_product_names",
    "process_product",
]
