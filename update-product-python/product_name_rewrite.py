"""Product-name parsing and AI rewrite helpers for update-product."""
import json
import os
import re
import time
import urllib.error
import urllib.request
from pathlib import Path
import sys

BASE_DIR = Path(__file__).resolve().parents[1]
if str(BASE_DIR) not in sys.path:
    sys.path.insert(0, str(BASE_DIR))

try:
    from pydantic import BaseModel
    from pydantic_ai import Agent
except ImportError:
    BaseModel = None
    Agent = None

from process_data.process_sheet_data import (
    DEFAULT_API_KEY_FILE_PATH,
    DEFAULT_MODEL,
    describe_http_error,
    extract_response_text,
    get_openai_api_key,
    request_product_name_structures_with_split as base_request_product_name_structures_with_split,
)
from product_runtime import log
from product_workbook import (
    DATA_PRODUCT_NAME_COLUMN,
    DATA_REWRITTEN_NAME_COLUMN,
    DATA_SKU_COLUMN,
    PRODUCT_NAME_HEADER,
    REWRITTEN_PRODUCT_NAME_HEADER,
    SKU_HEADER,
    get_or_create_rewritten_name_column,
    load_workbook_checked,
    normalize_text,
    resolve_sheet_name,
    save_workbook_atomic,
    workbook_file_lock,
)

if BaseModel is not None:
    class RewrittenItemModel(BaseModel):
        index: int
        rewritten_descriptions: list[str]


    class RewrittenBatchModel(BaseModel):
        items: list[RewrittenItemModel]
else:
    RewrittenBatchModel = None

ATTRIBUTE_START_PATTERNS = [
    ("cao",),
    ("êm", "chân"),
    ("da", "mềm"),
    ("đính", "nơ"),
    ("dây", "cài"),
    ("quai", "cài"),
    ("may", "viền"),
    ("hot", "trend"),
    ("dễ", "phối"),
    ("tôn", "dáng"),
    ("phong", "cách"),
]

DESCRIPTION_CLEANUP_PATTERNS = [
    r"\bdễ phối(?: đồ| trang phục| outfit)?\b",
    r"\bde phoi(?: do| trang phuc| outfit)?\b",
    r"\bdễ dàng phối(?: đồ| trang phục| outfit)?\b",
    r"\bde dang phoi(?: do| trang phuc| outfit)?\b",
    r"\bhằng ngày\b",
    r"\bhang ngay\b",
    r"\bphong cách\b",
    r"\bphong cach\b",
    r"\bthanh lịch\b",
    r"\bthanh lich\b",
    r"\bnhẹ nhàng\b",
    r"\bnhe nhang\b",
    r"\bêm ái\b",
    r"\bem ai\b",
    r"\bkiểu dáng\b",
    r"\bkieu dang\b",
    r"\bdáng vẻ\b",
    r"\bdang ve\b",
    r"\blựa chọn tuyệt vời\b",
    r"\blua chon tuyet voi\b",
    r"\bhoàn hảo\b",
    r"\bhoan hao\b",
    r"\bmọi dịp\b",
    r"\bmoi dip\b",
    r"\bmang lại sự\b",
    r"\bmang lai su\b",
    r"\btiện dụng\b",
    r"\btien dung\b",
    r"\bdễ kết hợp\b",
    r"\bde ket hop\b",
]
MAX_KEYWORD_2_WORDS = 3

def normalize_dash(text):
    return re.sub(r"\s*[–—-]\s*", " - ", str(text).strip())


def split_name_code(product_name):
    match = re.search(r"\s+-\s+([A-Z]\d+)\s*$", normalize_dash(product_name))
    if not match:
        return normalize_dash(product_name), None
    body = normalize_dash(product_name)[: match.start()].strip()
    return body, match.group(1)


def starts_with_pattern(words, index, pattern):
    lowered_words = [word.lower().strip(" ,.;:") for word in words[index : index + len(pattern)]]
    return tuple(lowered_words) == pattern


def infer_locked_context(product_name):
    body, code = split_name_code(product_name)
    parts = body.split(" - ", 1)
    if len(parts) == 1:
        return body, code

    first_part, second_part = parts
    second_words = second_part.split()
    stop_index = len(second_words)
    for index in range(2, len(second_words)):
        if any(starts_with_pattern(second_words, index, pattern) for pattern in ATTRIBUTE_START_PATTERNS):
            stop_index = index
            break

    locked_second_part = " ".join(second_words[:stop_index]).strip()
    return f"{first_part.strip()} - {locked_second_part}".strip(), code


def infer_product_name_structure(product_name):
    locked_context, code = infer_locked_context(product_name)
    parts = locked_context.split(" - ", 1)
    if len(parts) == 2:
        keyword_1, keyword_2 = parts
    else:
        keyword_1 = locked_context
        keyword_2 = ""

    body, _ = split_name_code(product_name)
    description = body
    if keyword_2:
        prefix = f"{keyword_1} - {keyword_2}"
        if body.startswith(prefix):
            description = body[len(prefix):].strip()
    elif body.startswith(keyword_1):
        description = body[len(keyword_1):].strip()

    return {
        "keyword_1": keyword_1.strip(),
        "keyword_2": keyword_2.strip(),
        "description": description.strip(" -"),
        "product_code": code or "",
    }


def compose_product_name(structure, description=None):
    keyword_1 = str(structure.get("keyword_1", "")).strip()
    keyword_2 = str(structure.get("keyword_2", "")).strip()
    product_code = str(structure.get("product_code", "")).strip()
    final_description = str(description if description is not None else structure.get("description", "")).strip()
    parts = [keyword_1]
    if keyword_2:
        parts.append(keyword_2)
    body = " - ".join(part for part in parts if part).strip()
    if final_description:
        body = f"{body} {final_description}".strip()
    if product_code:
        return f"{body} - {product_code}"
    return body


def normalize_parsed_structure_for_rewrite(structure):
    normalized = {
        "keyword_1": str(structure.get("keyword_1", "")).strip(),
        "keyword_2": str(structure.get("keyword_2", "")).strip(),
        "description": str(structure.get("description", "")).strip(),
        "product_code": str(structure.get("product_code", "")).strip(),
    }

    keyword_2_words = normalized["keyword_2"].split()
    if len(keyword_2_words) <= MAX_KEYWORD_2_WORDS:
        return normalized

    short_keyword_2 = " ".join(keyword_2_words[:MAX_KEYWORD_2_WORDS]).strip()
    overflow_description = " ".join(keyword_2_words[MAX_KEYWORD_2_WORDS:]).strip()
    current_description = normalized["description"]

    if current_description and normalize_text(current_description) in normalize_text(overflow_description):
        merged_description = overflow_description
    elif overflow_description and current_description:
        merged_description = f"{overflow_description} {current_description}".strip()
    else:
        merged_description = overflow_description or current_description

    normalized["keyword_2"] = short_keyword_2
    normalized["description"] = re.sub(r"\s+", " ", merged_description).strip(" -")
    return normalized


def request_product_name_structures_with_split(product_names, model, api_key):
    parsed_structures = base_request_product_name_structures_with_split(product_names, model, api_key)
    return [normalize_parsed_structure_for_rewrite(structure) for structure in parsed_structures]


def calculate_description_char_budget(structure, sku, max_length=120):
    keyword_1 = str(structure.get("keyword_1", "")).strip()
    keyword_2 = str(structure.get("keyword_2", "")).strip()
    sku = str(sku or "").strip()

    prefix_parts = [keyword_1]
    if keyword_2:
        prefix_parts.append(keyword_2)
    prefix = " - ".join(part for part in prefix_parts if part).strip()

    fixed_length = len(prefix) + len(sku)
    if prefix and sku:
        fixed_length += 2
    elif prefix or sku:
        fixed_length += 1

    return max(0, max_length - fixed_length)


def build_rewrite_instructions(version_count):
    return (
        "Toi se gui danh sach san pham da duoc tach thanh keyword_1, keyword_2, description, product_code "
        "va max_description_chars. "
        "Hay su dung keyword_1 va keyword_2 de hieu dung ngu canh san pham, sau do chi viet lai phan description. "
        f"Voi moi san pham, tao dung {version_count} rewritten_description moi. "
        "Khong duoc doi keyword_1, keyword_2, product_code. "
        "Khong duoc tra ve full product_name, chi tra ve rewritten_description. "
        "Description moi bat buoc co do dai nho hon hoac bang max_description_chars cua tung item. "
        "Day la gioi han ky tu, khong phai so tu. "
        "Description moi can ngan gon, dang title san pham, uu tien cum tu dac diem truc tiep cua san pham. "
        "Duoc phep dua vao keyword_1 va keyword_2 de biet loai san pham chinh, nhung khong duoc lap lai nguyen van keyword_1 hoac keyword_2 trong description moi. "
        "Chi giu lai 2 den 5 dac diem noi bat nhat tu description goc, viet thanh cum tu moi gon hon. "
        "Bat buoc phai viet lai bang cach dien dat moi, khong duoc chi xoa bot tu tu description goc. "
        "Khong duoc giu nguyen cum tu dai lien tiep tu description goc. "
        "Uu tien doi thu tu cum tu, doi cach mo ta, hoac rut ve nhom dac diem cot loi. "
        "Khong bat dau description bang cac tu nhu giay, doi giay, dep, sandal, boots. "
        "Bam sat y nghia description goc, chi paraphrase ngan gon, khong them y moi. "
        "Khong dung cau quang cao hoac generic nhu de dang phoi, phu hop, ket hop trang phuc, hang ngay, "
        "thanh lich, nhe nhang, em ai, kieu dang, phong cach, hoan hao, lua chon tuyet voi, moi dip. "
        "Khong duoc dua product_code vao description. "
        "Khong dung dau phay hoac dau cham trong description. "
        "Vi du output hop le: rewritten_description='Ren luoi dinh da thoang khi nu tinh'. "
        "Vi du output khong hop le: 'Giay Bet Nu - Giay Bup Be ... - B91763'. "
        "Moi item output phai giu dung index cua item input tuong ung."
    )


def clean_rewritten_versions(parsed_products, items_by_index, version_count):
    missing_indexes = [index for index in range(len(parsed_products)) if index not in items_by_index]
    if missing_indexes or len(items_by_index) != len(parsed_products):
        raise ValueError(f"OpenAI returned mismatched indexes. Missing indexes: {missing_indexes}.")

    all_versions = []
    for index, parsed_product in enumerate(parsed_products):
        versions = items_by_index[index]["rewritten_descriptions"]
        if len(versions) != version_count:
            raise ValueError(
                f"OpenAI returned {len(versions)} versions for item {index}, expected {version_count}."
            )

        max_description_chars = int(parsed_product.get("max_description_chars") or 0)
        cleaned_versions = []
        for version in versions:
            description = str(version or "").strip()
            description = extract_description_from_composed_name(description, parsed_product)
            description = cleanup_description(description)
            normalized_description = normalize_text(description)
            keyword_1 = normalize_text(parsed_product.get("keyword_1", ""))
            keyword_2 = normalize_text(parsed_product.get("keyword_2", ""))
            product_code = normalize_text(parsed_product.get("product_code", ""))
            if keyword_1 and keyword_1 in normalized_description:
                description = str(parsed_product.get("description", "")).strip()
                normalized_description = normalize_text(description)
            if keyword_2 and keyword_2 in normalized_description:
                description = str(parsed_product.get("description", "")).strip()
                normalized_description = normalize_text(description)
            if product_code and product_code in normalized_description:
                description = re.sub(re.escape(str(parsed_product.get("product_code", ""))), " ", description, flags=re.IGNORECASE)
                description = cleanup_description(description)
            if len(description) > max_description_chars:
                words = description.split()
                while words and len(" ".join(words)) > max_description_chars:
                    words.pop()
                description = " ".join(words).strip()
            cleaned_versions.append(description)
        all_versions.append(cleaned_versions)
    return all_versions


def request_rewritten_name_versions_with_pydantic_ai(parsed_products, version_count, model, api_key):
    if Agent is None or RewrittenBatchModel is None:
        raise RuntimeError("pydantic-ai is not installed.")

    model_name = model if ":" in str(model) else f"openai:{model}"
    previous_api_key = os.environ.get("OPENAI_API_KEY")
    os.environ["OPENAI_API_KEY"] = str(api_key)
    try:
        agent = Agent(
            model_name,
            output_type=RewrittenBatchModel,
            instructions=build_rewrite_instructions(version_count),
        )
        result = agent.run_sync(
            json.dumps(
                {
                    "version_count": version_count,
                    "products": parsed_products,
                },
                ensure_ascii=False,
            )
        )
        output = result.output
    finally:
        if previous_api_key is None:
            os.environ.pop("OPENAI_API_KEY", None)
        else:
            os.environ["OPENAI_API_KEY"] = previous_api_key

    items_by_index = {
        item.index: {
            "index": item.index,
            "rewritten_descriptions": item.rewritten_descriptions,
        }
        for item in output.items
    }
    return clean_rewritten_versions(parsed_products, items_by_index, version_count)


def request_rewritten_name_versions_with_limits(parsed_products, version_count, model, api_key, max_retries=3):
    if os.environ.get("SHOPEE_USE_PYDANTIC_AI_REWRITE", "1") != "0":
        try:
            return request_rewritten_name_versions_with_pydantic_ai(parsed_products, version_count, model, api_key)
        except Exception as error:
            log(f"⚠️ PydanticAI rewrite failed, fallback Responses API: {error}")

    payload = {
        "model": model,
        "instructions": f"{build_rewrite_instructions(version_count)} Chi tra ve JSON dung schema.",
        "input": json.dumps(
            {
                "version_count": version_count,
                "products": parsed_products,
            },
            ensure_ascii=False,
        ),
        "text": {
            "format": {
                "type": "json_schema",
                "name": "rewritten_product_name_versions_with_limits",
                "strict": True,
                "schema": {
                    "type": "object",
                    "additionalProperties": False,
                    "properties": {
                        "items": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "additionalProperties": False,
                                "properties": {
                                    "index": {"type": "integer"},
                                    "rewritten_descriptions": {
                                        "type": "array",
                                        "items": {"type": "string"},
                                    },
                                },
                                "required": ["index", "rewritten_descriptions"],
                            },
                        }
                    },
                    "required": ["items"],
                },
            }
        },
    }

    request = urllib.request.Request(
        "https://api.openai.com/v1/responses",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )

    for attempt in range(1, max_retries + 1):
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                response_body = json.loads(response.read().decode("utf-8"))
            response_text = extract_response_text(response_body)
            parsed = json.loads(response_text)
            items_by_index = {item["index"]: item for item in parsed["items"]}
            return clean_rewritten_versions(parsed_products, items_by_index, version_count)
        except urllib.error.HTTPError as error:
            message = describe_http_error(error)
            if attempt == max_retries:
                raise RuntimeError(f"OpenAI request failed: {message}") from error
            time.sleep(2 * attempt)
        except (urllib.error.URLError, TimeoutError, ValueError, json.JSONDecodeError) as error:
            if attempt == max_retries:
                raise RuntimeError(f"OpenAI request failed: {error}") from error
            time.sleep(2 * attempt)

    return [[str(parsed_product.get("description", "")).strip()] * version_count for parsed_product in parsed_products]


def truncate_product_name_preserving_code(product_name, max_length=120):
    product_name = str(product_name or "").strip()
    if len(product_name) <= max_length:
        return product_name
    body, code = split_name_code(product_name)
    if not code:
        return product_name[:max_length].strip()
    suffix = f" - {code}"
    available_body_length = max_length - len(suffix)
    if available_body_length <= 0:
        return product_name[:max_length].strip()
    return f"{body[:available_body_length].rstrip()}{suffix}".strip()


def compose_update_product_name(original_name, sku):
    structure = infer_product_name_structure(original_name)
    keyword_1 = str(structure.get("keyword_1", "")).strip()
    keyword_2 = str(structure.get("keyword_2", "")).strip()
    description = str(structure.get("description", "")).strip()
    sku = str(sku or structure.get("product_code", "")).strip()

    if keyword_2:
        body = f"{keyword_1} - {keyword_2}".strip()
    else:
        body = keyword_1
    if description:
        body = f"{body} {description}".strip()
    if sku:
        body = f"{body} {sku}".strip()
    return body


def truncate_product_name_preserving_sku(product_name, sku, max_length=120):
    product_name = str(product_name or "").strip()
    sku = str(sku or "").strip()
    if len(product_name) <= max_length:
        return product_name

    if sku and product_name.endswith(sku):
        body = product_name[:-len(sku)].strip()
        words = body.split()
        while words:
            candidate = f"{' '.join(words)} {sku}".strip()
            if len(candidate) <= max_length:
                return candidate
            words.pop()
        return sku[:max_length].strip()

    words = product_name.split()
    while words:
        candidate = " ".join(words).strip()
        if len(candidate) <= max_length:
            return candidate
        words.pop()
    return ""


def build_update_product_name(original_name, sku=""):
    return truncate_product_name_preserving_sku(compose_update_product_name(original_name, sku), sku)


def extract_description_from_composed_name(product_name, structure):
    product_name = str(product_name or "").strip()
    keyword_1 = str(structure.get("keyword_1", "")).strip()
    keyword_2 = str(structure.get("keyword_2", "")).strip()

    prefixes = []
    if keyword_1 and keyword_2:
        prefixes.append(f"{keyword_1} - {keyword_2}")
    if keyword_1:
        prefixes.append(keyword_1)

    body, _ = split_name_code(product_name)
    for prefix in prefixes:
        if body.startswith(prefix):
            return body[len(prefix):].strip(" -")

    return body.strip(" -")


def is_bad_rewritten_description(description):
    normalized_description = normalize_text(description)
    words = [word for word in re.split(r"\s+", normalized_description) if word]
    if len(words) < 6:
        return True

    blocked_phrases = (
        ",",
        ".",
        " và ",
        "dễ dàng",
        "de dang",
        "phù hợp",
        "phu hop",
        "kết hợp",
        "ket hop",
        "trang phục",
        "trang phuc",
        "hàng ngày",
        "hang ngay",
        "kiểu dáng",
        "kieu dang",
        "phong cách",
        "phong cach",
        "thanh lịch",
        "thanh lich",
        "nhẹ nhàng",
        "nhe nhang",
        "êm ái",
        "em ai",
        "đáng yêu",
        "dang yeu",
        "hoàn hảo",
        "hoan hao",
        "lựa chọn",
        "lua chon",
        "tuyệt vời",
        "tuyet voi",
        "mọi dịp",
        "moi dip",
    )
    if any(phrase in normalized_description for phrase in blocked_phrases):
        return True

    if words and words[-1] in {"cho", "de", "để", "voi", "với", "cung", "cùng", "va", "và"}:
        return True

    generic_prefixes = (
        "giày ",
        "giay ",
        "dép ",
        "dep ",
        "sandal ",
        "boots ",
        "boot ",
    )
    return normalized_description.startswith(generic_prefixes)


def limit_text_by_chars_without_cutting_words(text, max_chars):
    text = str(text or "").strip()
    max_chars = int(max_chars or 0)
    if max_chars <= 0:
        return ""
    if len(text) <= max_chars:
        return text

    words = text.split()
    while words and len(" ".join(words)) > max_chars:
        words.pop()
    return " ".join(words).strip()


def cleanup_description(description):
    cleaned = str(description or "").strip()
    cleaned = re.sub(r"[,.]", " ", cleaned)
    for pattern in DESCRIPTION_CLEANUP_PATTERNS:
        cleaned = re.sub(pattern, " ", cleaned, flags=re.IGNORECASE)
    cleaned = re.sub(r"\s+", " ", cleaned).strip(" ,.-")
    return cleaned


def choose_rewritten_name(original_name, generated_name, parsed_structure, max_description_chars=None):
    generated_description = extract_description_from_composed_name(generated_name, parsed_structure)
    original_description = str(parsed_structure.get("description", "")).strip()
    max_description_chars = calculate_description_char_budget(
        parsed_structure,
        parsed_structure.get("product_code", ""),
    ) if max_description_chars is None else int(max_description_chars or 0)

    cleaned_generated_description = cleanup_description(generated_description)
    cleaned_original_description = cleanup_description(original_description)

    if not cleaned_generated_description or len(cleaned_generated_description.split()) < 4:
        safe_description = limit_text_by_chars_without_cutting_words(cleaned_original_description, max_description_chars)
        return compose_product_name(parsed_structure, safe_description)

    safe_description = limit_text_by_chars_without_cutting_words(cleaned_generated_description, max_description_chars)
    return compose_product_name(parsed_structure, safe_description)


def prepare_rewritten_product_names(
    workbook_path,
    sheet_name,
    start_row=2,
    model=DEFAULT_MODEL,
    api_key_file_path=DEFAULT_API_KEY_FILE_PATH,
    batch_size=40,
    end_row=None,
    overwrite=False,
):
    workbook_path = Path(workbook_path)
    if not workbook_path.exists():
        raise FileNotFoundError(f"Không tìm thấy workbook: {workbook_path}")

    with workbook_file_lock(workbook_path):
        workbook = load_workbook_checked(workbook_path)
        sheet = workbook[resolve_sheet_name(workbook, sheet_name)]
        product_name_column = DATA_PRODUCT_NAME_COLUMN
        sku_column = DATA_SKU_COLUMN
        rewritten_name_column = get_or_create_rewritten_name_column(sheet)

        resolved_sheet_name = sheet.title
        first_data_row = max(2, int(start_row or 2))
        last_data_row = min(sheet.max_row, int(end_row)) if end_row else sheet.max_row
        skipped_no_name_count = 0
        skipped_no_sku_count = 0
        skipped_existing_rewritten_count = 0
        rows_to_update = []
        unique_product_names = []
        seen_product_names = set()

        for row_index in range(first_data_row, last_data_row + 1):
            original_name = str(sheet.cell(row=row_index, column=product_name_column).value or "").strip()
            sku = str(sheet.cell(row=row_index, column=sku_column).value or "").strip()
            if not original_name:
                skipped_no_name_count += 1
                continue
            if not sku:
                skipped_no_sku_count += 1
                continue
            current_rewritten_name = str(sheet.cell(row=row_index, column=rewritten_name_column).value or "").strip()
            if current_rewritten_name and not overwrite:
                skipped_existing_rewritten_count += 1
                continue

            rows_to_update.append((row_index, original_name, sku))
            if original_name not in seen_product_names:
                seen_product_names.add(original_name)
                unique_product_names.append(original_name)

        if not rows_to_update:
            save_workbook_atomic(workbook, workbook_path)
            log(
                f"✅ Sheet '{sheet.title}' từ row {first_data_row}: không còn dòng cần rewrite. "
                f"Bỏ qua {skipped_no_name_count} dòng thiếu '{PRODUCT_NAME_HEADER}', "
                f"{skipped_no_sku_count} dòng thiếu '{SKU_HEADER}', "
                f"{skipped_existing_rewritten_count} dòng đã có '{REWRITTEN_PRODUCT_NAME_HEADER}'."
            )
            return 0

    api_key = get_openai_api_key(Path(api_key_file_path))
    if not api_key:
        raise RuntimeError(
            "Thiếu OpenAI API key. Set OPENAI_API_KEY hoặc đặt key trong "
            f"{DEFAULT_API_KEY_FILE_PATH}."
        )

    log(
        f"🤖 Rewrite tên theo logic process_sheet_data.py: "
        f"{len(unique_product_names)} tên unique / {len(rows_to_update)} dòng."
    )

    rows_by_original_name = {}
    for row_index, original_name, sku in rows_to_update:
        rows_by_original_name.setdefault(original_name, []).append((row_index, sku))

    updated_count = 0
    for start_index in range(0, len(unique_product_names), batch_size):
        batch = unique_product_names[start_index : start_index + batch_size]
        log(f"🤖 Parse/rewrite batch {start_index + 1}-{start_index + len(batch)}/{len(unique_product_names)}...")

        parsed_batch = request_product_name_structures_with_split(batch, model, api_key)
        sku_by_name = {}
        for _, original_name, sku in rows_to_update:
            sku_by_name.setdefault(original_name, sku)

        parsed_products = []
        for index, (original_name, parsed_structure) in enumerate(zip(batch, parsed_batch)):
            sku = sku_by_name.get(original_name, "")
            parsed_product = {
                "index": index,
                **parsed_structure,
                "product_code": "",
                "max_description_chars": calculate_description_char_budget(parsed_structure, sku),
            }
            parsed_products.append(parsed_product)

        try:
            version_batches = request_rewritten_name_versions_with_limits(parsed_products, 1, model, api_key)
        except Exception as error:
            log(f"Rewrite batch failed, retry item-by-item: {error}")
            version_batches = []
            for parsed_product in parsed_products:
                try:
                    item_versions = request_rewritten_name_versions_with_limits([parsed_product], 1, model, api_key)
                except Exception as item_error:
                    log(f"Rewrite item {parsed_product.get('index')} failed, use parsed description: {item_error}")
                    item_versions = [[str(parsed_product.get("description", "")).strip()]]
                version_batches.extend(item_versions)
        batch_updates = []
        batch_updated_count = 0
        for original_name, parsed_structure, parsed_product, versions in zip(batch, parsed_batch, parsed_products, version_batches):
            rewritten_base_name = choose_rewritten_name(
                original_name,
                versions[0],
                parsed_structure,
                parsed_product["max_description_chars"],
            )
            rewritten_base_name = rewritten_base_name or compose_update_product_name(original_name, "")
            rewritten_body, _ = split_name_code(rewritten_base_name)

            for row_index, sku in rows_by_original_name.get(original_name, []):
                rewritten_name = truncate_product_name_preserving_sku(f"{rewritten_body} {sku}".strip(), sku)
                batch_updates.append((row_index, rewritten_name))

        with workbook_file_lock(workbook_path):
            workbook = load_workbook_checked(workbook_path)
            sheet = workbook[resolved_sheet_name]
            product_name_column = DATA_PRODUCT_NAME_COLUMN
            rewritten_name_column = get_or_create_rewritten_name_column(sheet, product_name_column)

            for row_index, rewritten_name in batch_updates:
                current_value = str(sheet.cell(row=row_index, column=rewritten_name_column).value or "").strip()
                if not rewritten_name:
                    continue

                if current_value != rewritten_name:
                    sheet.cell(row=row_index, column=rewritten_name_column).value = rewritten_name
                    updated_count += 1
                    batch_updated_count += 1

            save_workbook_atomic(workbook, workbook_path)
        log(
            f"💾 Đã save batch {start_index + 1}-{start_index + len(batch)}/{len(unique_product_names)}: "
            f"{batch_updated_count} dòng đổi tên."
        )

    log(
        f"✅ Đã cập nhật cột '{REWRITTEN_PRODUCT_NAME_HEADER}' trong sheet '{sheet.title}' "
        f"từ row {first_data_row}: {updated_count} dòng thay đổi, "
        f"bỏ qua {skipped_no_name_count} dòng thiếu '{PRODUCT_NAME_HEADER}', "
        f"{skipped_no_sku_count} dòng thiếu '{SKU_HEADER}', "
        f"{skipped_existing_rewritten_count} dòng đã có '{REWRITTEN_PRODUCT_NAME_HEADER}'."
    )
    return updated_count
