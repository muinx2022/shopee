import sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

path = r"d:\Projects\shopee-27052026\open-multi-brave-v3\BraveInstanceSession.cs"
FFFD = b'\xef\xbf\xbd'

with open(path, 'rb') as f:
    data = f.read()

original = data

# Each replacement: (search_bytes, replace_bytes, description)
# Using enough context to be unique.  Vietnamese UTF-8:
#   đ = \xc4\x91   Đ = \xc4\x90   ã = \xc3\xa3   đã = \xc4\x91\xc3\xa3
#   ò = \xc3\xb2   ừ = \xe1\xbb\xab   ì = \xc3\xac
#   – = \xe2\x80\x93   … = \xe2\x80\xa6   · = \xc2\xb7
#   ô = \xc3\xb4   ố = \xe1\xbb\x91   ó = \xc3\xb3
#   â = \xc3\xa2   ầ = \xe1\xba\xa7   ắ = \xe1\xba\xaf
#   ừ = \xe1\xbb\xab   ợ = \xe1\xbb\xa3   ử = \xe1\xbb\xad

replacements = [

    # ------- L240 -------
    # 2×FFFD at start → "Đã"
    (b'Log($"\xef\xbf\xbd\xef\xbf\xbd c',
     b'Log($"\xc4\x90\xc3\xa3 c',
     'L240: [FFFD][FFFD] → Đã'),

    # d[FFFD]ng in row-range context → dòng
    (b'\", d\xef\xbf\xbdng {_config.StartRow}',
     b'", d\xc3\xb2ng {_config.StartRow}',
     'L240: d[FFFD]ng → dòng'),

    # [FFFD] between row numbers → –
    (b'StartRow}\xef\xbf\xbd{_config.EndRow',
     b'StartRow}\xe2\x80\x93{_config.EndRow',
     'L240: [FFFD] → –'),

    # ------- L336 -------
    # "Brave d[FFFD] tắt" → "Brave đã tắt"
    (b'Brave d\xef\xbf\xbd ',
     b'Brave \xc4\x91\xc3\xa3 ',
     'L336: d[FFFD] → đã'),

    # separator after "tắt" → "–"
    # tắt = t + ắ(\xe1\xba\xaf) + t
    (b'\xe1\xba\xaft \xef\xbf\xbd d',
     b'\xe1\xba\xaft \xe2\x80\x93 d',
     'L336: tắt [FFFD] → tắt –'),

    # trailing "…" after "lại" → lại…
    # lại = l + a + ̣(combining) + i  but actually lại = l + ạ + i
    # ạ = U+1EA1 = \xe1\xba\xa1
    (b'\xe1\xba\xa1i\xef\xbf\xbd")',
     b'\xe1\xba\xa1i\xe2\x80\xa6")',
     'L336: lại[FFFD] → lại…'),

    # ------- L427 -------
    # d[FFFD]ng in "dừng profile" → dừng  (ừ = \xe1\xbb\xab)
    (b'd\xef\xbf\xbdng profile',
     b'd\xe1\xbb\xabng profile',
     'L427: d[FFFD]ng → dừng'),

    # v[FFFD] in "vì đã" → vì
    (b'profile v\xef\xbf\xbd d',
     b'profile v\xc3\xac \xc4\x91\xc3\xa3',  # "vì đã" replacing "v[FFFD] d"
     'L427: v[FFFD] d → vì đã'),

    # NOTE: after the above, the 3rd FFFD in L427 (pos=49 "d[FFFD] ch?y") is now gone
    # because we replaced "v[FFFD] d" with "vì đã" - but we still need to check
    # Actually we replaced "v[FFFD] d" but the 3rd FFFD is AFTER this:
    # Original: "T? d[F1]ng profile v[F2] d[F3] ch?y xong."
    # After fix for F1: "T? dừng profile v[F2] d[F3] ch?y xong."
    # After fix for F2+F3 together: "T? dừng profile vì đã ch?y xong."
    # Wait - the replacement above handles both F2 and the "d" before F3 together.
    # Let me re-examine: original "v[F2] d[F3] ch?y" → we replace "v[F2] d" with "vì đã"
    # That leaves: "vì đã[F3] ch?y" - wait, we included the d in the search, and d is the
    # start of "đã", so after replacing "v[F2] d" with "vì đã", the F3 becomes orphaned "ã"?
    # No - let me look at the original bytes again:
    # pos=44: before='ng profile v', after=' d? ch?y x' → "v[F2] d"
    # pos=49: before='ofile v? d', after=' ch?y xong.' → "d[F3]"
    # The 'd' at before for F3 is the same 'd' in after for F2. So after replacing F2
    # "v[F2] d" → "vì đã", the string becomes "...profile vì đã ch?y xong."
    # The F3 (which was pos=49, i.e., d+F3+space) is included in the F2 replacement above!
    # Because F2 replacement: b'profile v\xef\xbf\xbd d' → b'profile v\xc3\xac \xc4\x91\xc3\xa3'
    # This replaces: "profile v" + FFFD(F2) + " d" → "profile vì đã"
    # Then what was after the "d" in F3? After pos=49 FFFD comes " ch?y xong."
    # So original: "profile v[F2] d[F3] ch?y xong." where F3 is BETWEEN d and space-ch
    # The replacement "profile v[F2] d" → "profile vì đã" removes F2 and the "d" but
    # leaves F3 still in the string! F3 was AFTER the "d" that we consumed.
    #
    # Let me re-examine positions:
    # "profile v" = 9 chars, then FFFD(F2) at pos=44, then " d" = 2 chars,
    # then FFFD(F3) at pos=44+3+2=49, then " ch?y xong."
    # So original: b'profile v\xef\xbf\xbd d\xef\xbf\xbd ch?y xong.'
    # My F2 replacement searches: b'profile v\xef\xbf\xbd d'  (9+3+2 = 14 bytes)
    # And replaces with: b'profile v\xc3\xac \xc4\x91\xc3\xa3'  (9+2+1+4 = 16 bytes)
    # So after this replacement: b'profile v\xc3\xac \xc4\x91\xc3\xa3\xef\xbf\xbd ch?y'
    # The F3 FFFD still remains! I need a separate replacement for it.

    # ------- L427 continued: the remaining FFFD after "đã" -------
    # After F2 replacement: "vì đã[F3] ch?y"
    # This F3 represents the "ã" of "đã"? No - we already have đã...
    # Wait, I'm confused. Let me look at L427 raw again:
    # "T? d[F1]ng profile v[F2] d[F3] ch?y xong."
    # - F1 is inside "dừng" - the ừ char
    # - F2 is inside "vì" - the ì char
    # - F3 is inside "đã" - but "đã" appears as "d[F3]" where d is ASCII
    # So the correct interpretation: ALL instances of "d[FFFD]" where FFFD is followed by space
    # represent "đã" where the đ is stored as "d" + FFFD
    #
    # For F3 at pos=49: before='ofile v? d', after=' ch?y xong.'
    # After fixing F1 (d[F1]ng → dừng) and F2 (v[F2] → vì),
    # the remaining "d[F3]" in context "đã ch?y" needs to become "đã"
    # But I INCLUDED the 'd' in my F2 search: 'profile v\xef\xbf\xbd d' → 'profile vì đã'
    # Wait no, I replaced "v[F2] d" not "v[F2] d[F3]". The replacement CONSUMED the 'd'
    # and replaced it with "đã". But F3 is AFTER that 'd' in the original:
    # Original bytes around F2 and F3: v + [F2=FFFD] + space + d + [F3=FFFD] + space + ch
    # My F2 search: v + [FFFD] + space + d  (consumes v, F2, space, d)
    # My F2 replace: v + ì + space + đ + ã  (= "vì đã")
    # After replacement: v + ì + space + đ + ã + [F3=FFFD] + space + ch
    # = "vì đã[F3] ch?y"
    # Now F3 is between "ã" and " ch" - this FFFD is leftover.
    # What could it be? If d + FFFD = "đã", then this remaining FFFD is extra...
    # Maybe "đã" = d + [FFFD representing stroke+accent], and the "ã" is already in "đã"
    # so no separate "ã" needed. Then F3 is something else.
    #
    # Actually let me look at L427 at position 49 context differently:
    # before='ofile v? d' - ends with 'd' which is the 'd' BEFORE F3
    # after=' ch?y xong.' - starts with ' ch'
    # So the byte sequence is: ...d + [F3=FFFD] + space + ch?y...
    # This is "d[F3] ch?y" where [F3] is between d and space-chạy.
    # In context "vì [something] chạy": "vì đã chạy" makes sense.
    # So "d[F3]" = "đã" meaning d (ASCII) + FFFD (representing full "đã"? No, that's 2 chars)
    #
    # I'll treat this as: d + FFFD = "đã" (the whole 2-char word, where d is ASCII base
    # and FFFD represents the combining stroke + following ã)
    # Replace: d + FFFD + space → đã + space

    # After F2 fix leaves FFFD from original F3: "đã[FFFD] ch"
    # = \xc4\x91\xc3\xa3 + FFFD + ' ch'
    # Hmm, this FFFD is between "ã" of "đã" and space-ch. What is it?
    # Most likely it's just a stray FFFD that should be removed, or it's "," or " "
    # Context: "vì đã ... chạy xong" - nothing between đã and chạy
    # So FFFD here = nothing/empty, just remove it.
    # But more carefully: maybe d+FFFD BOTH together = đã, and my F2 replacement is WRONG
    # because I split them: "v[F2] d" → "vì đã" consumed d but left F3.
    # The real sequence should be: v[F2] = vì, and d[F3] = đã (separately).
    # Let me redesign F2 and F3 separately:

    # F2: v[FFFD] space → vì space  (only replace v+FFFD, NOT including the 'd' after)
    # F3: d[FFFD] space (in this context) → đã space

    # ------- L438 + L522: 2×FFFD at start → "Đã" -------
    # "Đã dừng chạy." - the dừng and chạy are valid UTF-8
    # Search: b'       Log("' + 2×FFFD + b' d'  -- but need to be specific per line
    # L438: Log("[FFFD][FFFD] d?ng ch?y.")  context: pos 21, before='       Log("'
    # L522: : "[FFFD][FFFD] d?ng ch?y.")   context: pos 23, before='         : "'

]

# I'll redo this more carefully with a step-by-step approach
# Let me do line-by-line replacements using the actual line content

with open(path, 'rb') as f:
    data = f.read()

lines = data.split(b'\n')
fixed_lines = []

def fix_line(lineno, line):
    """Apply fixes to a specific line, return fixed bytes."""

    if lineno == 240:
        # "Đã cập nhật extension: sheet ..., dòng ..–.."
        line = line.replace(
            b'\xef\xbf\xbd\xef\xbf\xbd c',  # 2×FFFD + " c"
            b'\xc4\x90\xc3\xa3 c')           # Đã + " c"
        # dòng: d + [FFFD] + ng
        line = line.replace(
            b'd\xef\xbf\xbdng {_config.StartRow}',
            b'd\xc3\xb2ng {_config.StartRow}')
        # – between row numbers
        line = line.replace(
            b'StartRow}\xef\xbf\xbd{_config.EndRow',
            b'StartRow}\xe2\x80\x93{_config.EndRow')

    elif lineno == 336:
        # "Brave đã tắt – đang khởi động lại…"
        # d[FFFD] → đã  (d + FFFD → đã, but keeping the space after)
        line = line.replace(b'd\xef\xbf\xbd ', b'\xc4\x91\xc3\xa3 ')
        # separator after tắt: tắt[FFFD]đang → tắt – đang
        # tắt = \x74\xe1\xba\xaf\x74, [FFFD], đang has đ=\xc4\x91
        line = line.replace(
            b'\xe1\xba\xaft \xef\xbf\xbd d',
            b'\xe1\xba\xaft \xe2\x80\x93 d')
        # trailing FFFD after "lại" → "…"
        # lại = \x6c\xe1\xba\xa1\x69
        line = line.replace(
            b'\xe1\xba\xa1i\xef\xbf\xbd")',
            b'\xe1\xba\xa1i\xe2\x80\xa6")')

    elif lineno == 427:
        # "Tự dừng profile vì đã chạy xong."
        # d[FFFD]ng → dừng (ừ = \xe1\xbb\xab)
        line = line.replace(
            b'd\xef\xbf\xbdng profile',
            b'd\xe1\xbb\xabng profile')
        # v[FFFD] → vì (ì = \xc3\xac)
        line = line.replace(b'v\xef\xbf\xbd ', b'v\xc3\xac ')
        # d[FFFD] followed by space (in "đã chạy") → đã
        line = line.replace(b'd\xef\xbf\xbd ', b'\xc4\x91\xc3\xa3 ')

    elif lineno == 438:
        # "Đã dừng chạy."  (2×FFFD → Đã)
        line = line.replace(
            b'\xef\xbf\xbd\xef\xbf\xbd d',
            b'\xc4\x90\xc3\xa3 d')

    elif lineno == 521:
        # "Trạng thái cuối: sheet ..., xong dòng {last}, chạy tiếp từ {resume}."
        # th[FFFD]i → thái (á = \xc3\xa1? or ái with á=\xc3\xa1)
        # Actually "thái" = th + á + i, where á=\xc3\xa1 (valid UTF-8)
        # But dump shows th[FFFD]i → meaning á became FFFD
        line = line.replace(b'th\xef\xbf\xbdi', b'th\xc3\xa1i')
        # d[FFFD]ng in row context → dòng
        line = line.replace(b'd\xef\xbf\xbdng {last}', b'd\xc3\xb2ng {last}')

    elif lineno == 522:
        # "Đã dừng chạy."  (2×FFFD → Đã)
        line = line.replace(
            b'\xef\xbf\xbd\xef\xbf\xbd d',
            b'\xc4\x90\xc3\xa3 d')

    elif lineno == 684:
        # "/// <summary>Đóng nhanh (khi thoát app) – không chờ CDP.</summary>"
        # [FFFD][FFFD]ng → Đóng  (Đ = \xc4\x90, ó = \xc3\xb3)
        line = line.replace(
            b'summary>\xef\xbf\xbd\xef\xbf\xbdng',
            b'summary>\xc4\x90\xc3\xb3ng')
        # tho[FFFD]t → thoát (á = \xc3\xa1)
        line = line.replace(b'tho\xef\xbf\xbdt', b'tho\xc3\xa1t')
        # separator [FFFD] → –
        line = line.replace(b'app) \xef\xbf\xbd k', b'app) \xe2\x80\x93 k')
        # kh[FFFD]ng → không (ô = \xc3\xb4 + combining? no, ô=\xc3\xb4 and không=k+h+ô+n+g)
        # Actually "không" = k + h + ô + n + g. ô = U+00F4 = \xc3\xb4 (valid)
        # dump shows kh[FFFD]ng → k+h+FFFD+n+g meaning ô was replaced
        line = line.replace(b'kh\xef\xbf\xbdng', b'kh\xc3\xb4ng')

    elif lineno == 818:
        # "Đã tạo profile mới từ User Data mẫu."
        line = line.replace(
            b'\xef\xbf\xbd\xef\xbf\xbd t',
            b'\xc4\x90\xc3\xa3 t')

    elif lineno == 823:
        # "Tôi sẽ dùng profile hiện có." or "Tiếp tục dùng profile hiện có."
        # T[FFFD]i → Tôi (ô = \xc3\xb4)
        line = line.replace(b'Log("T\xef\xbf\xbdi', b'Log("T\xc3\xb4i')
        # c[FFFD] at end → có (ó = \xc3\xb3)
        line = line.replace(b'hi\xef\xbf\xbdn c\xef\xbf\xbd.', b'hi\xe1\xbb\x87n c\xc3\xb3.')
        # Actually "hiện" has ệ not just "hi?n":
        # "hiện" = h + i + ệ + n where ệ = U+1EC7 = \xe1\xbb\x87
        # But the dump shows pos=18 x1 and pos=45 x1
        # pos=18: before='Log("T', after='i s? d?ng pr' → T[FFFD]i = "Tôi"
        # pos=45: before='ofile hi?n c', after='.");' → c[FFFD] before .")
        # "hiện có" where "có" = c + ó, and ó = \xc3\xb3
        # After fixing T[FFFD]i already done above
        # For c[FFFD]: c + FFFD before .")  → "có"
        line = line.replace(b'n c\xef\xbf\xbd.', b'n c\xc3\xb3.')

    elif lineno == 914:
        # "Cảnh báo: không tìm thấy thư mục ext đầy đủ (thiếu background.js) – Shopee Data Runner có thể không load."
        # b[FFFD]o → báo (á = \xc3\xa1)
        line = line.replace(b'b\xef\xbf\xbdo:', b'b\xc3\xa1o:')
        # kh[FFFD]ng → không
        line = line.replace(b'kh\xef\xbf\xbdng', b'kh\xc3\xb4ng')
        # separator [FFFD] → –
        line = line.replace(b'.js) \xef\xbf\xbd S', b'.js) \xe2\x80\x93 S')
        # c[FFFD] th? → có thể (ó = \xc3\xb3)
        line = line.replace(b'c\xef\xbf\xbd th', b'c\xc3\xb3 th')

    elif lineno == 1211:
        # "Lỗi không xác định."
        # kh[FFFD]ng → không
        line = line.replace(b'kh\xef\xbf\xbdng', b'kh\xc3\xb4ng')
        # x[FFFD]c → xác (á = \xc3\xa1)
        line = line.replace(b'x\xef\xbf\xbdc', b'x\xc3\xa1c')

    elif lineno == 1242:
        # "Phát hiện ERR_PROXY/No internet – tự khởi động lại profile…"
        # Ph[FFFD]t → Phát (á = \xc3\xa1)
        line = line.replace(b'Ph\xef\xbf\xbdt hi', b'Ph\xc3\xa1t hi')
        # separator [FFFD] → –
        line = line.replace(b'internet \xef\xbf\xbd t', b'internet \xe2\x80\x93 t')
        # trailing FFFD → …
        line = line.replace(b'profile\xef\xbf\xbd"', b'profile\xe2\x80\xa6"')

    elif lineno == 1304:
        # comment: "Proxy API báo OK nhưng..."
        # b[FFFD]o → báo
        line = line.replace(b'b\xef\xbf\xbdo OK', b'b\xc3\xa1o OK')

    elif lineno == 1315:
        # "Phát hiện ERR_PROXY/No internet trên tab – tự khởi động lại profile…"
        line = line.replace(b'Ph\xef\xbf\xbdt hi', b'Ph\xc3\xa1t hi')
        # tr[FFFD]n → trên (ê = \xc3\xaa? or ê with tone?)
        # "trên" = tr + ê + n where ê = U+00EA = \xc3\xaa
        line = line.replace(b'tr\xef\xbf\xbdn tab', b'tr\xc3\xaan tab')
        # separator [FFFD] → –
        line = line.replace(b'tab \xef\xbf\xbd t', b'tab \xe2\x80\x93 t')
        # trailing FFFD → …
        line = line.replace(b'profile\xef\xbf\xbd"', b'profile\xe2\x80\xa6"')

    elif lineno == 1463:
        # "Đã xuất N cookie ở ..."
        line = line.replace(
            b'Log($"\xef\xbf\xbd\xef\xbf\xbd xu',
            b'Log($"\xc4\x90\xc3\xa3 xu')

    elif lineno == 1473:
        # "Đã lưu N cookie ở ..."
        line = line.replace(
            b'Log($"\xef\xbf\xbd\xef\xbf\xbd l',
            b'Log($"\xc4\x90\xc3\xa3 l')

    elif lineno == 1516:
        # "Đã nhập N cookie."
        line = line.replace(
            b'Log($"\xef\xbf\xbd\xef\xbf\xbd n',
            b'Log($"\xc4\x90\xc3\xa3 n')

    elif lineno == 1583:
        # "Shopee login: đã mở trang đăng nhập và điền tài khoản..."
        # d[FFFD] → đã  (d + FFFD space → đã space)
        line = line.replace(b'd\xef\xbf\xbd m', b'\xc4\x91\xc3\xa3 m')
        # v[FFFD] → và (à = \xc3\xa0)
        line = line.replace(b'p v\xef\xbf\xbd d', b'p v\xc3\xa0 \xc4\x91')
        # t[FFFD]i → tài  (à = \xc3\xa0)  -- "tài khoản" where tài = t+à+i
        # dump pos=67: before=' v? di?n t', after='i kho?n'
        # So t[FFFD]i = "tài" where FFFD = à
        line = line.replace(b't\xef\xbf\xbdi kho', b't\xc3\xa0i kho')

    elif lineno == 1800:
        # "Đã tự nhập N cookie BigSeller."
        line = line.replace(
            b'Log($"\xef\xbf\xbd\xef\xbf\xbd t',
            b'Log($"\xc4\x90\xc3\xa3 t')

    return line

# Process
FFFD = b'\xef\xbf\xbd'
for i, line in enumerate(lines):
    if FFFD in line:
        lines[i] = fix_line(i + 1, line)

fixed = b'\n'.join(lines)

# Report remaining FFFD
remaining = []
for i, line in enumerate(fixed.split(b'\n')):
    if FFFD in line:
        remaining.append((i+1, line.decode('utf-8', errors='replace').strip()))

if remaining:
    print("Remaining FFFD after fix:")
    for lineno, txt in remaining:
        print(f"  L{lineno}: {txt}")
else:
    print("All FFFD fixed!")

print(f"Original size: {len(original)} bytes")
print(f"Fixed size:    {len(fixed)} bytes")

with open(path, 'wb') as f:
    f.write(fixed)
print("Written.")
