import re, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

path = r"d:\Projects\shopee-27052026\open-multi-brave-v3\BraveInstanceSession.cs"
FFFD = b'\xef\xbf\xbd'

with open(path, 'rb') as f:
    data = f.read()

lines = data.split(b'\n')

results = []
for i, line in enumerate(lines):
    if FFFD in line:
        results.append((i+1, line))

for lineno, line in results:
    display = line.replace(FFFD, b'[?]')
    txt = display.decode('utf-8', errors='replace').strip()
    print(f"L{lineno}: {txt}")

    i = 0
    while True:
        pos = line.find(FFFD, i)
        if pos == -1:
            break
        count = 0
        p2 = pos
        while line[p2:p2+3] == FFFD:
            count += 1
            p2 += 3
        before_bytes = line[max(0,pos-12):pos]
        after_bytes = line[p2:p2+12]
        before = before_bytes.decode('utf-8', errors='replace')
        after = after_bytes.decode('utf-8', errors='replace')
        hex_seg = ' '.join(f'{b:02x}' for b in line[pos:p2])
        print(f"  pos={pos} x{count} [{hex_seg}] before='{before}' after='{after}'")
        i = p2
    print()
