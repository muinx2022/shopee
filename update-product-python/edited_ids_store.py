"""Lưu persistent danh sách Shopee item ID (từ link sau nút Chép) đã update thành công."""
import json
import re
import threading
from datetime import datetime, timezone
from pathlib import Path

_store_lock = threading.Lock()


def default_edited_ids_path(workbook_path, sheet_name, base_dir=None):
    workbook_path = Path(workbook_path)
    base = Path(base_dir) if base_dir else workbook_path.parent
    stem = workbook_path.stem or "workbook"
    safe_sheet = re.sub(r"[^\w\-]+", "_", str(sheet_name or "").strip()) or "sheet"
    return base / f"edited_ids_{stem}_{safe_sheet}.json"


def resolve_edited_ids_path(config, base_dir=None):
    custom = (config.get("EDITED_IDS_FILE") or "").strip()
    if custom:
        return Path(custom)
    return default_edited_ids_path(
        config.get("WORKBOOK_PATH", ""),
        config.get("DATA_SHEET", ""),
        base_dir or Path(config.get("WORKBOOK_PATH", "")).parent,
    )


def load_edited_ids(store_path):
    store_path = Path(store_path)
    if not store_path.exists():
        return set()

    try:
        data = json.loads(store_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return set()

    if isinstance(data, dict):
        raw_ids = data.get("ids") or data.get("edited_ids") or []
    elif isinstance(data, list):
        raw_ids = data
    else:
        raw_ids = []

    return {str(item).strip() for item in raw_ids if str(item).strip()}


def save_edited_ids(store_path, ids):
    store_path = Path(store_path)
    store_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "ids": sorted(ids),
        "updated_at": datetime.now(timezone.utc).isoformat(),
    }
    store_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def is_edited(store_path, product_id):
    product_id = str(product_id or "").strip()
    if not product_id:
        return False
    with _store_lock:
        return product_id in load_edited_ids(store_path)


def mark_edited(store_path, product_id):
    product_id = str(product_id or "").strip()
    if not product_id:
        return False

    with _store_lock:
        ids = load_edited_ids(store_path)
        if product_id in ids:
            return False
        ids.add(product_id)
        save_edited_ids(store_path, ids)
        return True


def count_edited(store_path):
    with _store_lock:
        return len(load_edited_ids(store_path))
