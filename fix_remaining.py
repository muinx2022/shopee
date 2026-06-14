import sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

path = r"d:\Projects\shopee-27052026\open-multi-brave-v3\BraveInstanceSession.cs"
FFFD = b'\xef\xbf\xbd'

with open(path, 'rb') as f:
    data = f.read()

original_size = len(data)

# L336 pos=40: separator after "tắt" → "–"
# Context: "[FFFD] dang" (only FFFD before " dang" in this line)
data = data.replace(b'\xef\xbf\xbd d\x61\x6e\x67', b'\xe2\x80\x93 d\x61\x6e\x67')  # [FFFD]dang → –dang

# L336 pos=62: trailing "…" after lại → lại…
# Context: i[FFFD]") — searching for FFFD immediately before ")
data = data.replace(b'i\xef\xbf\xbd");', b'i\xe2\x80\xa6");')

# L914 pos=36: t[FFFD]m → tìm (ì = \xc3\xac)
data = data.replace(b't\xef\xbf\xbdm', b't\xc3\xacm')

# Verify
remaining = []
lines = data.split(b'\n')
for i, line in enumerate(lines):
    if FFFD in line:
        remaining.append((i+1, line.decode('utf-8', errors='replace').strip()))

if remaining:
    print("Still remaining FFFD:")
    for lineno, txt in remaining:
        print(f"  L{lineno}: {txt}")
else:
    print("All FFFD cleared!")

print(f"Size: {original_size} → {len(data)} bytes")

with open(path, 'wb') as f:
    f.write(data)
print("Written.")
