"""Runtime helpers shared by the update-product modules."""
import sys
import time


def log(msg):
    try:
        print(msg, flush=True)
    except UnicodeEncodeError:
        encoding = getattr(sys.stdout, "encoding", None) or "utf-8"
        print(str(msg).encode(encoding, errors="replace").decode(encoding), flush=True)


def close_page_accepting_dialog(page, timeout_ms=5000):
    if page is None:
        return True
    try:
        if page.is_closed():
            return True
    except Exception:
        return True

    def _accept_dialog(dialog):
        try:
            log(f"?? Browser alert khi ??ng tab: {dialog.message}")
        except Exception:
            log("?? Browser alert khi ??ng tab edit.")
        try:
            dialog.accept()
        except Exception:
            try:
                dialog.dismiss()
            except Exception:
                pass

    try:
        page.once("dialog", _accept_dialog)
    except Exception:
        pass

    try:
        page.close(run_before_unload=True)
    except TypeError:
        try:
            page.close()
        except Exception:
            pass
    except Exception:
        pass

    deadline = time.time() + (timeout_ms / 1000)
    while time.time() < deadline:
        try:
            if page.is_closed():
                return True
        except Exception:
            return True
        time.sleep(0.2)

    try:
        page.close(run_before_unload=False)
    except TypeError:
        try:
            page.close()
        except Exception:
            pass
    except Exception:
        pass

    try:
        return page.is_closed()
    except Exception:
        return True
