#!/usr/bin/env python3
"""Generate the placeholder add-on art: switchboard/icon.png and switchboard/logo.png.

Python standard library only (zlib + struct PNG writer) so the art carries no third-party
license and no font. The motif: three phase bars kept under an amber breaker-limit line.
Replace with real artwork before the public release.

Usage: python deploy/generate-placeholder-art.py
"""

import struct
import zlib
from pathlib import Path

SUPERSAMPLE = 4  # rendered at 4x and box-filtered down, for smooth edges

BLUE = (30, 90, 168)
WHITE = (255, 255, 255)
AMBER = (245, 166, 35)


def rounded_rect(x0, y0, x1, y1, r):
    """Return a hit test for a rounded rectangle in output-pixel coordinates."""

    def hit(x, y):
        if x < x0 or x >= x1 or y < y0 or y >= y1:
            return False
        cx = min(max(x, x0 + r), x1 - r)
        cy = min(max(y, y0 + r), y1 - r)
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

    return hit


def render(width, height, layers):
    """Rasterise (hit_test, rgb) layers, last on top, into RGBA bytes."""
    s = SUPERSAMPLE
    rows = []
    for py in range(height):
        row = bytearray()
        for px in range(width):
            acc_r = acc_g = acc_b = acc_a = 0
            for sy in range(s):
                for sx in range(s):
                    x = px + (sx + 0.5) / s
                    y = py + (sy + 0.5) / s
                    colour = None
                    for hit, rgb in layers:
                        if hit(x, y):
                            colour = rgb
                    if colour is not None:
                        acc_r += colour[0]
                        acc_g += colour[1]
                        acc_b += colour[2]
                        acc_a += 1
            n = s * s
            if acc_a:
                row += bytes((acc_r // acc_a, acc_g // acc_a, acc_b // acc_a, round(255 * acc_a / n)))
            else:
                row += bytes((0, 0, 0, 0))
        rows.append(bytes(row))
    return rows


def write_png(path, width, height, rows):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + row for row in rows)  # filter type 0 per scanline
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    path.write_bytes(png)


def phase_motif(width, height, bar_width, gap):
    """Background tile, three phase bars of different load, and the limit line above them."""
    margin = height * 0.19
    baseline = height - margin
    limit_y = margin + height * 0.06
    span = height - 2 * margin
    total = 3 * bar_width + 2 * gap
    left = (width - total) / 2
    loads = (0.62, 0.84, 0.48)  # fraction of the space under the limit line
    layers = [(rounded_rect(0, 0, width, height, height * 0.19), BLUE)]
    for i, load in enumerate(loads):
        x0 = left + i * (bar_width + gap)
        top = baseline - (baseline - limit_y - span * 0.08) * load
        layers.append((rounded_rect(x0, top, x0 + bar_width, baseline, bar_width * 0.2), WHITE))
    line_h = height * 0.055
    layers.append(
        (rounded_rect(left - gap, limit_y - line_h / 2, left + total + gap, limit_y + line_h / 2, line_h / 2), AMBER)
    )
    return layers


def main():
    out = Path(__file__).resolve().parent.parent / "switchboard"
    icon = (128, 128)
    logo = (250, 100)
    write_png(out / "icon.png", *icon, render(*icon, phase_motif(*icon, bar_width=20, gap=12)))
    write_png(out / "logo.png", *logo, render(*logo, phase_motif(*logo, bar_width=20, gap=14)))
    print(f"wrote {out / 'icon.png'} {icon} and {out / 'logo.png'} {logo}")


if __name__ == "__main__":
    main()
