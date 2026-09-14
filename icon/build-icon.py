#!/usr/bin/env python3
"""Rebuilds ../mashed.ico from artwork.jpg.

    python3 icon/build-icon.py

The artwork is a cartoon on a sheet of graph paper, landscape, far too detailed for
a 16px tray icon. This keys the paper out, crops it square twice - once wide, once
tight - and box-filters each icon size down from whichever crop it can carry.

Pure standard library, so there is nothing to install. The one thing Python cannot
do here is decode a JPEG, which powershell.exe is asked to do instead - the same
Windows-from-WSL bargain build.sh makes for the compiler.
"""
import os, struct, subprocess, sys, tempfile, zlib
from collections import deque

HERE = os.path.dirname(os.path.abspath(__file__))

def jpeg_to_bmp(jpg, bmp):
    """.NET via PowerShell: Python has no JPEG decoder in the standard library."""
    subprocess.run(['powershell.exe', '-NoProfile', '-Command', f"""
        Add-Type -AssemblyName System.Drawing
        $src = [System.Drawing.Image]::FromFile('{jpg}')
        $out = New-Object System.Drawing.Bitmap $src.Width, $src.Height, (
            [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.DrawImage($src, 0, 0, $src.Width, $src.Height)
        $out.Save('{bmp}', [System.Drawing.Imaging.ImageFormat]::Bmp)
        $g.Dispose(); $out.Dispose(); $src.Dispose()"""],
        check=True, stdout=subprocess.DEVNULL)

# ---------- read a 24bpp BMP ----------
def read_bmp(path):
    d = open(path, 'rb').read()
    off = struct.unpack_from('<I', d, 10)[0]
    w, h, bits = struct.unpack_from('<ii', d, 18) + struct.unpack_from('<H', d, 28)
    assert bits == 24, bits
    stride = ((w * 3 + 3) // 4) * 4
    px = []
    for r in range(h - 1, -1, -1):                 # stored bottom-up
        base = off + r * stride
        px.append([(d[base + c*3 + 2], d[base + c*3 + 1], d[base + c*3]) for c in range(w)])
    return w, h, px

# ---------- key the paper out ----------
def paper(p):
    """Near-white and unsaturated: the sheet and its printed grid, not the drawing."""
    return min(p) > 200 and max(p) - min(p) < 28

def background_mask(w, h, px, grow):
    """Flood from the borders, so white *inside* the drawing (the smoke puffs) stays."""
    bg = bytearray(w * h)
    q = deque()
    for c in range(w):
        for r in (0, h - 1):
            if not bg[r*w + c] and paper(px[r][c]): bg[r*w + c] = 1; q.append((r, c))
    for r in range(h):
        for c in (0, w - 1):
            if not bg[r*w + c] and paper(px[r][c]): bg[r*w + c] = 1; q.append((r, c))
    while q:
        r, c = q.popleft()
        for nr, nc in ((r-1, c), (r+1, c), (r, c-1), (r, c+1)):
            if 0 <= nr < h and 0 <= nc < w and not bg[nr*w + nc] and paper(px[nr][nc]):
                bg[nr*w + nc] = 1; q.append((nr, nc))
    # Grow the background a little: the pixel ring just inside an edge is JPEG-pale
    # and reads as a white halo once the icon is on a dark taskbar.
    for _ in range(grow):
        edge = [(r, c) for r in range(h) for c in range(w) if not bg[r*w + c] and (
                (r and bg[(r-1)*w + c]) or (r < h-1 and bg[(r+1)*w + c]) or
                (c and bg[r*w + c-1]) or (c < w-1 and bg[r*w + c+1]))]
        for r, c in edge: bg[r*w + c] = 1
    return bg

def bbox(w, h, bg):
    rows = [r for r in range(h) if any(not bg[r*w + c] for c in range(w))]
    cols = [c for c in range(w) if any(not bg[r*w + c] for r in range(h))]
    return min(cols), min(rows), max(cols), max(rows)

# ---------- resample ----------
def crop_square(w, h, px, bg, box, pad):
    """A square window around `box`, padded, clamped to the image."""
    x0, y0, x1, y1 = box
    side = max(x1 - x0, y1 - y0) + 2 * pad
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    left, top = round(cx - side / 2), round(cy - side / 2)
    out = []
    for r in range(side):
        row = []
        for c in range(side):
            sr, sc = top + r, left + c
            if 0 <= sr < h and 0 <= sc < w and not bg[sr*w + sc]:
                row.append(px[sr][sc] + (255,))
            else:
                row.append((0, 0, 0, 0))
        out.append(row)
    return side, out

def downsample(side, img, size):
    """Box filter over premultiplied alpha, so transparent pixels cannot bleed
    their (black) colour into the edge."""
    out = [None] * (size * size)
    for r in range(size):
        r0, r1 = r * side // size, max((r + 1) * side // size, r * side // size + 1)
        for c in range(size):
            c0, c1 = c * side // size, max((c + 1) * side // size, c * side // size + 1)
            tr = tg = tb = ta = n = 0
            for y in range(r0, r1):
                for x in range(c0, c1):
                    p = img[y][x]
                    a = p[3] / 255
                    tr += p[0] * a; tg += p[1] * a; tb += p[2] * a; ta += p[3]; n += 1
            a = ta / n
            if a < 0.5:
                out[r * size + c] = (0, 0, 0, 0)
            else:
                k = 255 / (a * n)
                out[r * size + c] = (min(255, round(tr * k)), min(255, round(tg * k)),
                                     min(255, round(tb * k)), round(a))
    return out

# ---------- file formats ----------
def png(size, px):
    raw = b''.join(b'\x00' + b''.join(bytes(px[r * size + c]) for c in range(size))
                   for r in range(size))
    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data
                + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

def dib(size, px):
    """A DIB as an .ico holds one: no file header, doubled height, rows bottom-up,
    and a 1bpp AND mask after the pixels that 32bpp icons leave blank."""
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    body = b''.join(bytes(b for c in range(size)
                          for b in (lambda p: (p[2], p[1], p[0], p[3]))(px[r * size + c]))
                    for r in reversed(range(size)))
    return header + body + b'\x00' * (((size + 31) // 32) * 4) * size

def ico(path, images):
    entries, blobs, offset = b'', b'', 6 + 16 * len(images)
    for size, blob in images:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32,
                               len(blob), offset)
        offset += len(blob); blobs += blob
    open(path, 'wb').write(struct.pack('<HHH', 0, 1, len(images)) + entries + blobs)

# ---------- the icon itself ----------
# Two crops, because one cannot serve both ends of the range. The wide one keeps the
# whole scene - chunks, handle, the "SMASH!" on the head - and is legible from 48px
# up. Below that the scene turns to noise, so the small sizes get a crop tight enough
# that the two things that matter, a gold potato and a dark hammer head, still read.
WIDE  = (330,  90, 660)
TIGHT = (420, 130, 560)
SIZES = [(16, TIGHT), (20, TIGHT), (24, TIGHT), (32, TIGHT),
         (40, WIDE), (48, WIDE), (64, WIDE), (128, WIDE), (256, WIDE)]

def cut(w, h, px, bg, crop):
    x0, y0, side = crop
    return side, [[px[y0+r][x0+c] + (255,)
                   if 0 <= y0+r < h and 0 <= x0+c < w and not bg[(y0+r)*w + x0+c]
                   else (0, 0, 0, 0)
                   for c in range(side)] for r in range(side)]

def build(bmp_path, out_path):
    w, h, px = read_bmp(bmp_path)
    bg = background_mask(w, h, px, 2)
    cache, images = {}, []
    for size, crop in SIZES:
        if crop not in cache: cache[crop] = cut(w, h, px, bg, crop)
        side, img = cache[crop]
        small = downsample(side, img, size)
        images.append((size, png(size, small) if size >= 128 else dib(size, small)))
        print(f'  {size}x{size}', file=sys.stderr)
    ico(out_path, images)

def win_path(p):
    return subprocess.run(['wslpath', '-w', p], capture_output=True, text=True,
                          check=True).stdout.strip()

if __name__ == '__main__':
    scratch = tempfile.mkdtemp()
    # Both paths cross into Windows, so both have to be spelled the Windows way -
    # and under /mnt/c, because powershell.exe cannot write to a \\wsl.localhost path.
    bmp = os.path.join(os.environ.get('TEMP_WIN', '/mnt/c/Windows/Temp'), 'mashed-art.bmp')
    jpeg_to_bmp(win_path(os.path.join(HERE, 'artwork.jpg')), win_path(bmp))
    build(bmp, os.path.join(HERE, os.pardir, 'mashed.ico'))
    os.remove(bmp)
    print('wrote mashed.ico', file=sys.stderr)
