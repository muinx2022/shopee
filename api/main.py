from collections import deque
from functools import lru_cache
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock
from typing import Dict, List, Optional, Tuple
from urllib.parse import parse_qs, unquote, urlparse
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
from zipfile import ZipFile
import argparse
import json
import os
import posixpath
import re
import shutil
import time
import xml.etree.ElementTree as ET

try:
    from fastapi import FastAPI
    from fastapi.middleware.cors import CORSMiddleware

    FASTAPI_AVAILABLE = True
except Exception:
    FASTAPI_AVAILABLE = False

API_DIR = Path(__file__).resolve().parent
ROOT_DIR = API_DIR.parent
EXCEL_PATH = Path(os.environ.get("SHOPEE_WORKBOOK_PATH", ROOT_DIR / "data" / "data.xlsx"))
VIDEO_OUTPUT_DIR = Path(r"D:\videos")

# ---------------------------------------------------------------------------
# Rate limiter: sliding-window counter per endpoint group
# ---------------------------------------------------------------------------

class RateLimiter:
    """Sliding-window rate limiter. Thread-safe."""

    def __init__(self, max_calls: int, window_seconds: float):
        self._max_calls = max_calls
        self._window = window_seconds
        self._timestamps: deque = deque()
        self._lock = Lock()

    def check(self) -> None:
        """Raise APIError(429) if the limit is exceeded."""
        now = time.monotonic()
        with self._lock:
            cutoff = now - self._window
            while self._timestamps and self._timestamps[0] < cutoff:
                self._timestamps.popleft()
            if len(self._timestamps) >= self._max_calls:
                raise APIError(429, "Quá nhiều yêu cầu. Vui lòng thử lại sau.")
            self._timestamps.append(now)


# Allow generous limits for a local-only API; these guard against runaway loops.
_rate_data     = RateLimiter(max_calls=30, window_seconds=1.0)   # data/sheets endpoints
_rate_download = RateLimiter(max_calls=5,  window_seconds=1.0)   # video download


# ---------------------------------------------------------------------------
# Input validation helpers
# ---------------------------------------------------------------------------

def parse_row_param(value: Optional[str], name: str, default: Optional[int] = None) -> Optional[int]:
    """Parse and validate a row number query parameter."""
    if value is None or value == "" or value == "null":
        return default
    try:
        n = int(value)
    except (TypeError, ValueError):
        raise APIError(400, f"Tham số '{name}' phải là số nguyên.")
    if n < 1:
        raise APIError(400, f"Tham số '{name}' phải >= 1.")
    return n


NS = {
    "main": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
    "rel": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
    "pkgrel": "http://schemas.openxmlformats.org/package/2006/relationships",
}


class APIError(Exception):
    def __init__(self, status_code: int, detail: str):
        super().__init__(detail)
        self.status_code = status_code
        self.detail = detail


def ensure_excel_exists() -> None:
    if not EXCEL_PATH.exists():
        raise APIError(404, f"File not found: {EXCEL_PATH}")


def col_to_index(col_ref: str) -> int:
    index = 0
    for ch in col_ref:
        if not ch.isalpha():
            break
        index = index * 26 + (ord(ch.upper()) - 64)
    return index


def cell_ref_to_col(cell_ref: str) -> int:
    match = re.match(r"([A-Z]+)", cell_ref or "")
    if not match:
        return 0
    return col_to_index(match.group(1))


def col_index_to_letter(index: int) -> str:
    result = ""
    while index > 0:
        index, remainder = divmod(index - 1, 26)
        result = chr(65 + remainder) + result
    return result


def resolve_archive_path(target: str) -> str:
    clean_target = (target or "").lstrip("/")
    if clean_target.startswith("xl/"):
        return clean_target
    return posixpath.normpath(f"xl/{clean_target}")


def cell_text(cell: ET.Element, shared_strings: List[str]) -> str:
    cell_type = cell.attrib.get("t")
    value_node = cell.find("main:v", NS)
    inline_text = cell.find("main:is/main:t", NS)
    value = value_node.text if value_node is not None and value_node.text is not None else ""

    if cell_type == "s" and value:
        shared_index = int(value)
        return shared_strings[shared_index] if 0 <= shared_index < len(shared_strings) else ""
    if cell_type == "inlineStr" and inline_text is not None:
        return inline_text.text or ""
    if cell_type == "b":
        return "TRUE" if value == "1" else "FALSE"
    if cell_type == "str":
        return value
    return value


@lru_cache(maxsize=1)
def load_workbook_index(file_mtime: float) -> Tuple[List[Dict[str, str]], List[str]]:
    ensure_excel_exists()

    with ZipFile(EXCEL_PATH) as archive:
        workbook = ET.fromstring(archive.read("xl/workbook.xml"))
        rels = ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))

        rel_map = {}
        for rel in rels.findall("pkgrel:Relationship", NS):
            rel_map[rel.attrib["Id"]] = rel.attrib["Target"]

        sheets = []
        sheets_node = workbook.find("main:sheets", NS)
        if sheets_node is not None:
            for sheet in sheets_node.findall("main:sheet", NS):
                rel_id = sheet.attrib.get(f"{{{NS['rel']}}}id")
                target = rel_map.get(rel_id, "")
                if target:
                    target = resolve_archive_path(target)
                sheets.append(
                    {
                        "name": sheet.attrib.get("name", ""),
                        "path": target,
                    }
                )

        shared_strings: List[str] = []
        if "xl/sharedStrings.xml" in archive.namelist():
            shared_root = ET.fromstring(archive.read("xl/sharedStrings.xml"))
            for item in shared_root.findall("main:si", NS):
                texts = [node.text or "" for node in item.iterfind(".//main:t", NS)]
                shared_strings.append("".join(texts))

        return sheets, shared_strings


def get_workbook_index() -> Tuple[List[Dict[str, str]], List[str]]:
    ensure_excel_exists()
    return load_workbook_index(EXCEL_PATH.stat().st_mtime)


def sheet_info(sheet_name: str) -> Dict[str, str]:
    sheets, _ = get_workbook_index()
    for sheet in sheets:
        if sheet["name"] == sheet_name:
            return sheet
    raise APIError(404, f"Sheet not found: {sheet_name}")


def read_sheet_rows(sheet_name: str) -> Tuple[List[str], List[List[str]]]:
    sheet = sheet_info(sheet_name)
    _, shared_strings = get_workbook_index()

    if not sheet.get("path"):
        raise APIError(404, f"Sheet path not found: {sheet_name}")

    with ZipFile(EXCEL_PATH) as archive:
        root = ET.fromstring(archive.read(resolve_archive_path(sheet["path"])))
        rows: Dict[int, Dict[int, str]] = {}
        max_col = 0

        sheet_data = root.find("main:sheetData", NS)
        if sheet_data is not None:
            for row in sheet_data.findall("main:row", NS):
                row_index = int(row.attrib.get("r", "0") or "0")
                row_cells: Dict[int, str] = {}
                for cell in row.findall("main:c", NS):
                    col_index = cell_ref_to_col(cell.attrib.get("r", ""))
                    if col_index <= 0:
                        continue
                    row_cells[col_index] = cell_text(cell, shared_strings)
                    max_col = max(max_col, col_index)
                if row_index > 0:
                    rows[row_index] = row_cells

        header_row = rows.get(1, {})
        if max_col == 0:
            max_col = max(header_row.keys(), default=0)

        headers: List[str] = []
        for col_index in range(1, max_col + 1):
            header = header_row.get(col_index, "").strip()
            headers.append(header or col_index_to_letter(col_index))

        data_rows: List[List[str]] = []
        for row_index in sorted(index for index in rows.keys() if index > 1):
            row_cells = rows[row_index]
            values = [row_cells.get(col_index, "") for col_index in range(1, max_col + 1)]
            data_rows.append(values)

        return headers, data_rows


def slice_rows(rows: List[List[str]], start_row: int, end_row: Optional[int]) -> List[List[str]]:
    if start_row < 1:
        raise APIError(400, "start_row must be >= 1")
    if end_row is not None and end_row < start_row:
        raise APIError(400, "end_row must be >= start_row")

    start_idx = start_row - 1
    end_idx = len(rows) if end_row is None else end_row
    return rows[start_idx:end_idx]


def handle_sheets() -> Dict[str, List[str]]:
    _rate_data.check()
    sheets, _ = get_workbook_index()
    return {"sheets": [sheet["name"] for sheet in sheets]}


def handle_data(sheet_name: str, start_row: int = 1, end_row: Optional[int] = None) -> Dict[str, object]:
    _rate_data.check()
    if start_row < 1:
        raise APIError(400, "Tham số 'start_row' phải >= 1.")
    if end_row is not None and end_row < start_row:
        raise APIError(400, "Tham số 'end_row' phải >= start_row.")
    headers, rows = read_sheet_rows(sheet_name)
    selected_rows = slice_rows(rows, start_row, end_row)
    data = [dict(zip(headers, row)) for row in selected_rows]
    return {"data": data, "total_rows": len(rows)}


def sanitize_filename(name: str) -> str:
    cleaned = re.sub(r'[<>:"/\\|?*]+', "_", name or "")
    cleaned = cleaned.strip().strip(".")
    return cleaned or "video"


def normalize_duration(value) -> Optional[float]:
    try:
        if value is None or value == "":
            return None
        duration = float(value)
        if duration != duration:  # NaN check
            return None
        return duration
    except (TypeError, ValueError):
        return None


def normalize_video_url(value: str) -> str:
    if not isinstance(value, str):
        return ""
    value = value.strip()
    if value.startswith("http://") or value.startswith("https://"):
        return value
    return ""


def probe_content_length(url: str) -> Optional[int]:
    if not url:
        return None

    headers = {"User-Agent": "Mozilla/5.0"}
    requests = [
        Request(url, method="HEAD", headers=headers),
        Request(url, method="GET", headers={**headers, "Range": "bytes=0-0"}),
    ]

    for req in requests:
        try:
            with urlopen(req, timeout=30) as response:
                content_length = response.headers.get("Content-Length")
                if content_length:
                    try:
                        return int(content_length)
                    except ValueError:
                        pass

                content_range = response.headers.get("Content-Range")
                if content_range:
                    match = re.search(r"/(\d+)$", content_range)
                    if match:
                        return int(match.group(1))
        except (HTTPError, URLError, TimeoutError, ValueError):
            continue

    return None


def download_url_to_file(url: str, destination: Path) -> int:
    destination.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = destination.with_suffix(destination.suffix + ".part")
    headers = {"User-Agent": "Mozilla/5.0"}
    req = Request(url, headers=headers)

    downloaded = 0
    with urlopen(req, timeout=60) as response, open(tmp_path, "wb") as file_handle:
        shutil.copyfileobj(response, file_handle)
        downloaded = tmp_path.stat().st_size

    if destination.exists():
        destination.unlink()
    tmp_path.replace(destination)
    return downloaded


def handle_video_download(payload: Dict[str, object]) -> Dict[str, object]:
    _rate_download.check()
    sku = sanitize_filename(str(payload.get("sku") or "video"))
    raw_candidates = payload.get("candidates") or []
    if not isinstance(raw_candidates, list):
      raise APIError(400, "candidates must be a list")

    candidates = []
    for item in raw_candidates:
        if not isinstance(item, dict):
            continue
        url = normalize_video_url(str(item.get("url") or ""))
        duration = normalize_duration(item.get("duration"))
        if not url or duration is None or duration >= 60:
            continue
        candidates.append(
            {
                "url": url,
                "duration": duration,
                "label": str(item.get("label") or ""),
            }
        )

    if not candidates:
        raise APIError(404, "No video candidates with duration < 60 seconds")

    enriched = []
    for candidate in candidates:
        size = probe_content_length(candidate["url"])
        if size is not None:
            candidate["size"] = size
        enriched.append(candidate)

    enriched.sort(key=lambda item: item.get("size") or 0, reverse=True)
    best = enriched[0]
    best_url = best["url"]
    best_size = best.get("size")

    destination = VIDEO_OUTPUT_DIR / f"{sku}.mp4"
    downloaded_size = download_url_to_file(best_url, destination)

    return {
        "success": True,
        "sku": sku,
        "url": best_url,
        "duration": best["duration"],
        "size": best_size,
        "downloaded_size": downloaded_size,
        "saved_path": str(destination),
    }


if FASTAPI_AVAILABLE:
    app = FastAPI()

    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=True,
        allow_methods=["*"],
        allow_headers=["*"],
    )

    @app.get("/sheets")
    async def get_sheets():
        return handle_sheets()

    @app.get("/data/{sheet_name}")
    async def get_sheet_data(sheet_name: str, start_row: int = 1, end_row: Optional[int] = None):
        if start_row < 1:
            from fastapi import HTTPException
            raise HTTPException(status_code=400, detail="Tham số 'start_row' phải >= 1.")
        if end_row is not None and end_row < start_row:
            from fastapi import HTTPException
            raise HTTPException(status_code=400, detail="Tham số 'end_row' phải >= start_row.")
        return handle_data(sheet_name, start_row, end_row)

    @app.post("/video/download")
    async def download_best_video(payload: dict):
        return handle_video_download(payload)
else:
    app = None


class RequestHandler(BaseHTTPRequestHandler):
    def _send_json(self, status_code: int, payload: Dict[str, object]) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):  # noqa: N802
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.end_headers()

    def do_GET(self):  # noqa: N802
        try:
            parsed = urlparse(self.path)
            path = parsed.path
            query = parse_qs(parsed.query)

            if path == "/sheets":
                self._send_json(200, handle_sheets())
                return

            if path.startswith("/data/"):
                sheet_name = unquote(path[len("/data/"):])
                start_row = parse_row_param(
                    query.get("start_row", ["1"])[0], "start_row", default=1
                )
                end_row = parse_row_param(
                    query.get("end_row", [None])[0], "end_row", default=None
                )
                self._send_json(200, handle_data(sheet_name, start_row, end_row))
                return

            self._send_json(404, {"detail": "Not found"})
        except APIError as error:
            self._send_json(error.status_code, {"detail": error.detail})
        except Exception as error:
            self._send_json(500, {"detail": str(error)})

    def do_POST(self):  # noqa: N802
        try:
            parsed = urlparse(self.path)
            if parsed.path == "/video/download":
                content_length = int(self.headers.get("Content-Length", "0") or "0")
                body = self.rfile.read(content_length) if content_length > 0 else b"{}"
                payload = json.loads(body.decode("utf-8") or "{}")
                self._send_json(200, handle_video_download(payload))
                return

            self._send_json(404, {"detail": "Not found"})
        except APIError as error:
            self._send_json(error.status_code, {"detail": error.detail})
        except Exception as error:
            self._send_json(500, {"detail": str(error)})

    def log_message(self, format, *args):  # noqa: A003
        return


def run_server(host: str = "127.0.0.1", port: int = 8012) -> None:
    if FASTAPI_AVAILABLE:
        try:
            import uvicorn
        except Exception as error:
            raise SystemExit(
                "FastAPI is installed but uvicorn is missing. Install uvicorn or run the fallback server.\n"
                f"Details: {error}"
            )
        uvicorn.run("api.main:app", host=host, port=port, reload=False)
        return

    server = ThreadingHTTPServer((host, port), RequestHandler)
    print(f"Serving on http://{host}:{port}")
    server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8012)
    parser.add_argument("--workbook", default="")
    args = parser.parse_args()
    if args.workbook:
        EXCEL_PATH = Path(args.workbook)
        os.environ["SHOPEE_WORKBOOK_PATH"] = str(EXCEL_PATH)
    run_server(host=args.host, port=args.port)
