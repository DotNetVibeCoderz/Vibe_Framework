"""Boot the K210, collect the `T ` thumbnail lines, and write a PNG."""
import re
import struct
import sys
import zlib

import serial

PORT = sys.argv[1] if len(sys.argv) > 1 else "COM10"
OUT = sys.argv[2] if len(sys.argv) > 2 else "thumb.png"
SCALE = 6

s = serial.Serial(PORT, 115200, timeout=0.2)
s.dtr = False
s.rts = False
s.dtr = True
s.dtr = False

buf = b""
import time
end = time.time() + 40
while time.time() < end:
    buf += s.read(4096)
    if b"thumbnail end" in buf:
        break
s.close()

text = buf.decode("utf-8", "replace")
rows = []
for line in text.splitlines():
    m = re.match(r"^T ([0-9a-f]+)$", line.strip())
    if m:
        hexrow = m.group(1)
        rows.append([int(hexrow[i:i + 4], 16) for i in range(0, len(hexrow), 4)])

if not rows:
    print("no thumbnail rows captured")
    print(text[-2000:])
    sys.exit(1)

w, h = len(rows[0]), len(rows)
print(f"thumbnail {w}x{h}")

px = bytearray()
for row in rows:
    for _ in range(SCALE):
        px.append(0)  # PNG filter byte, once per output scanline
        del px[-1]
    line = bytearray()
    for v in row:
        r = ((v >> 11) & 0x1F) << 3
        g = ((v >> 5) & 0x3F) << 2
        b = (v & 0x1F) << 3
        line += bytes([r, g, b]) * SCALE
    for _ in range(SCALE):
        px += b"\x00" + line

raw = zlib.compress(bytes(px), 9)


def chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", struct.pack(">IIBBBBB", w * SCALE, h * SCALE, 8, 2, 0, 0, 0))
       + chunk(b"IDAT", raw)
       + chunk(b"IEND", b""))
open(OUT, "wb").write(png)
print("wrote", OUT)

# The statistics line, for context alongside the picture.
for line in text.splitlines():
    if "[camera]" in line:
        print(line.strip())
