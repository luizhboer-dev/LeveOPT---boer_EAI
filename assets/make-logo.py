"""Gera o logo e o ícone do LeveOPT (fonte de pixels 5x7, estilo terminal). Uso: python make-logo.py"""
from PIL import Image, ImageDraw, ImageFilter

G = {
    'L': ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    'E': ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    'V': ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
    'O': ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    'P': ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    'T': ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    'B': ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    'y': ["00000", "00000", "10001", "10001", "01111", "00001", "01110"],
    ':': ["00000", "00000", "00100", "00000", "00000", "00100", "00000"],
    'F': ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    'i': ["00100", "00000", "01100", "00100", "00100", "00100", "01110"],
    'z': ["00000", "00000", "11111", "00010", "00100", "01000", "11111"],
    '>': ["10000", "01000", "00100", "00010", "00100", "01000", "10000"],
    'b': ["10000", "10000", "11110", "10001", "10001", "10001", "11110"],
    'o': ["00000", "00000", "01110", "10001", "10001", "10001", "01110"],
    'e': ["00000", "00000", "01110", "10001", "11111", "10000", "01110"],
    'r': ["00000", "00000", "10110", "11001", "10000", "10000", "10000"],
    '_': ["00000", "00000", "00000", "00000", "00000", "00000", "11111"],
    'A': ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    'I': ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
    ' ': ["00000"] * 7,
}
BG, PANEL, LINE, LINE2 = "#0D1110", "#121917", "#1F2A26", "#2C3A35"
GREEN, BRIGHT, MUTED = "#5DCAA5", "#E6ECE8", "#6B7A74"


def text_cells(s, x0, y0, cell, gap=1):
    cells, x = [], x0
    for ch in s:
        for r, row in enumerate(G[ch]):
            for c, v in enumerate(row):
                if v == '1':
                    cells.append((x + c * cell, y0 + r * cell))
        x += (5 + gap) * cell
    return cells, x


def hexc(h, a=255):
    h = h.lstrip('#')
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


W, H = 1200, 420
small, cell, bycell, y0 = 4, 18, 7, 120
header, _ = text_cells("LEVEOPT", 62, 17, small)
prompt, xp = text_cells(">", 70, y0, cell)
leve, xl = text_cells("LEVE", xp + cell, y0, cell)
opt, xo = text_cells("OPT", xl + cell // 2, y0, cell)
cursor_x = xo + cell
by, _ = text_cells("By: boer_eAI", 70 + 7 * cell, y0 + 7 * cell + 44, bycell)
bx, byy, segs = 70 + 7 * cell, H - 62, 16

# ── SVG ──
svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img" aria-label="LeveOPT by boer_eAI">',
       '<title>LeveOPT — by boer_eAI</title>',
       '<defs><filter id="glow" x="-20%" y="-40%" width="140%" height="180%"><feGaussianBlur stdDeviation="9" result="b"/>'
       '<feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter></defs>',
       f'<rect x="1" y="1" width="{W-2}" height="{H-2}" rx="22" fill="{BG}" stroke="{LINE2}" stroke-width="2"/>',
       f'<path d="M1 60 V23 Q1 1 23 1 H{W-23} Q{W-1} 1 {W-1} 23 V60 Z" fill="{PANEL}"/>',
       f'<line x1="1" y1="60" x2="{W-1}" y2="60" stroke="{LINE}" stroke-width="2"/>',
       f'<circle cx="40" cy="31" r="7" fill="{GREEN}"/>',
       f'<g fill="{MUTED}">' + ''.join(f'<rect x="{x}" y="{y}" width="{small}" height="{small}"/>' for x, y in header) + '</g>']


def svg_blocks(cells, color, glow=False):
    f = ' filter="url(#glow)"' if glow else ''
    return f'<g fill="{color}"{f}>' + ''.join(f'<rect x="{x}" y="{y}" width="{cell-2}" height="{cell-2}" rx="2"/>' for x, y in cells) + '</g>'


svg += [svg_blocks(prompt, GREEN, True), svg_blocks(leve, BRIGHT), svg_blocks(opt, GREEN, True),
        f'<rect x="{cursor_x}" y="{y0+6*cell}" width="{cell*4}" height="{cell-2}" rx="2" fill="{GREEN}" filter="url(#glow)">'
        '<animate attributeName="opacity" values="1;1;0;0" dur="1.1s" repeatCount="indefinite"/></rect>',
        f'<g fill="{MUTED}">' + ''.join(f'<rect x="{x}" y="{y}" width="{bycell-1}" height="{bycell-1}"/>' for x, y in by) + '</g>']
svg += [f'<rect x="{bx+k*30}" y="{byy}" width="24" height="12" rx="2" fill="{GREEN if k < 12 else LINE2}"/>' for k in range(segs)]
svg.append('</svg>')
open('leveopt-logo.svg', 'w', encoding='utf-8').write('\n'.join(svg))

# ── PNG ──
img = Image.new('RGBA', (W, H), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([1, 1, W - 2, H - 2], radius=22, fill=hexc(BG), outline=hexc(LINE2), width=2)
d.rounded_rectangle([1, 1, W - 2, 60], radius=22, fill=hexc(PANEL))
d.rectangle([1, 40, W - 2, 60], fill=hexc(PANEL))
d.line([1, 60, W - 2, 60], fill=hexc(LINE), width=2)
d.ellipse([33, 24, 47, 38], fill=hexc(GREEN))
for x, y in header:
    d.rectangle([x, y, x + small - 1, y + small - 1], fill=hexc(MUTED))


def blocks(dr, cells, color, size):
    for x, y in cells:
        dr.rounded_rectangle([x, y, x + size - 3, y + size - 3], radius=2, fill=color)


glow = Image.new('RGBA', (W, H), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
blocks(gd, prompt, hexc(GREEN, 200), cell)
blocks(gd, opt, hexc(GREEN, 200), cell)
gd.rounded_rectangle([cursor_x, y0 + 6 * cell, cursor_x + cell * 4, y0 + 7 * cell - 3], radius=2, fill=hexc(GREEN, 200))
img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(9)))
d = ImageDraw.Draw(img)
blocks(d, prompt, hexc(GREEN), cell)
blocks(d, leve, hexc(BRIGHT), cell)
blocks(d, opt, hexc(GREEN), cell)
d.rounded_rectangle([cursor_x, y0 + 6 * cell, cursor_x + cell * 4, y0 + 7 * cell - 3], radius=2, fill=hexc(GREEN))
for x, y in by:
    d.rectangle([x, y, x + bycell - 2, y + bycell - 2], fill=hexc(MUTED))
for k in range(segs):
    d.rounded_rectangle([bx + k * 30, byy, bx + k * 30 + 24, byy + 12], radius=2, fill=hexc(GREEN if k < 12 else LINE2))
img.save('leveopt-logo.png')

# ── Ícone ">_" (pixel art 16x16, nítido em todos os tamanhos) ──
ICON = [(3, 4), (4, 4), (4, 5), (5, 5), (5, 6), (6, 6), (6, 7), (7, 7), (5, 8), (6, 8), (4, 9), (5, 9), (3, 10), (4, 10)]
CURSOR = [(c, r) for r in (10, 11) for c in range(9, 14)]


def icon(size):
    im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    dr = ImageDraw.Draw(im)
    dr.rounded_rectangle([0, 0, size - 1, size - 1], radius=max(2, size // 6), fill=hexc(BG), outline=hexc(LINE2), width=max(1, size // 32))
    s = size / 16

    def box(c, r):
        return [round(c * s), round(r * s), round((c + 1) * s) - 1, round((r + 1) * s) - 1]

    if size >= 48:
        gl = Image.new('RGBA', (size, size), (0, 0, 0, 0))
        gdr = ImageDraw.Draw(gl)
        for c, r in ICON + CURSOR:
            gdr.rectangle(box(c, r), fill=hexc(GREEN, 180))
        im = Image.alpha_composite(im, gl.filter(ImageFilter.GaussianBlur(size / 40)))
        dr = ImageDraw.Draw(im)
    for c, r in ICON + CURSOR:
        dr.rectangle(box(c, r), fill=hexc(GREEN))
    return im


sizes = [16, 24, 32, 48, 64, 128, 256]
icons = [icon(s) for s in sizes]
icons[-1].save('leveopt.ico', format='ICO', sizes=[(s, s) for s in sizes], append_images=icons[:-1])
icons[-1].save('leveopt-icon-256.png')
print('logo e ícone gerados')
