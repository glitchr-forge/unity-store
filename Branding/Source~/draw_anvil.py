"""Pixel-art anvil in the key's palette: black outline, steel greys lit from the top left."""
import struct, sys, zlib
W, H = 36, 22
def body(x, y):
    if 3 <= y <= 7 and 10 <= x <= 31: return True                      # face and heel
    if 3 <= y <= 6 and 2 + 2 * (y - 3) <= x <= 10: return True          # horn, tapering left
    if y == 8 and 11 <= x <= 29: return True                            # under the face
    if 9 <= y <= 12 and 14 <= x <= 25: return True                      # waist
    if y == 13 and 12 <= x <= 27: return True
    if 14 <= y <= 16 and 9 <= x <= 30: return True                      # base
    if 17 <= y <= 18 and (8 <= x <= 13 or 26 <= x <= 31): return True  # feet
    return False
inside = lambda x, y: 0 <= x < W and 0 <= y < H and body(x, y)
OUT, FACE, LIGHT, MID, DARK, SHINE = (10, 10, 12), (196, 198, 206), (150, 152, 160), (104, 106, 114), (58, 60, 66), (236, 238, 244)
px = []
for y in range(H):
    row = []
    for x in range(W):
        if inside(x, y):
            if y == 3: c = FACE                                          # polished top face
            elif not inside(x, y - 1) or not inside(x - 1, y): c = LIGHT
            elif not inside(x, y + 1) or not inside(x + 1, y): c = DARK
            elif y >= 14 and x >= 24: c = DARK                           # shaded right of the base
            else: c = MID
            row.append(c + (255,))
        elif any(inside(x + dx, y + dy) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
            row.append(OUT + (255,))
        else:
            row.append((0, 0, 0, 0))
    px.append(row)
for x, y in ((13, 3), (14, 3), (15, 4), (6, 4), (11, 14)):              # glints
    if inside(x, y): px[y][x] = SHINE + (255,)
raw = b"".join(b"\0" + bytes(v for p in row for v in p) for row in px)
chunk = lambda t, d: struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
open(sys.argv[1], "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
     + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
