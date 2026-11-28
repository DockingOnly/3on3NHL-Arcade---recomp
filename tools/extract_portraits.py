#!/usr/bin/env python3
"""extract_portraits.py - dump the game's arcade-star portraits to PNG.

    python extract_portraits.py <game folder> <output folder>

<game folder> is the extracted game (the one holding data0.big and db/nhlng.db).
Writes <id>_<Name>_large.png (467x253) and <id>_<Name>_small.png (202x128) for each
portrait in fe/ion/artassets/playerheads[small]/p0_4000/, plus contact_sheet.png.

Reverses what src/nhl3on3_roster.cpp does when it writes a portrait: RefPack unpack,
nested BIG, RW4 texture at +4096, byte-swapped Xbox 360 tiled DXT5.

These are EA's images from your own copy of the game. Use them as a style reference and
keep them out of the repository. Needs Pillow.
"""

import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SPECS = [("playerheads", 512, 256, 467, 253, "large"),
         ("playerheadssmall", 256, 128, 202, 128, "small")]


def be32(b, o):
    return struct.unpack_from(">I", b, o)[0]


def big_entries(data):
    if data[:4] not in (b"BIGF", b"BIG4"):
        raise ValueError("not a BIG archive")
    n, p, out = be32(data, 8), 16, []
    for _ in range(n):
        off, size = struct.unpack_from(">II", data, p)
        p += 8
        end = data.index(b"\0", p)
        out.append((data[p:end].decode("latin1"), off, size))
        p = end + 1
    return out


def unpack(src):
    """EA RefPack. Returns the input unchanged if it isn't compressed."""
    if len(src) < 2 or src[1] != 0xFB or (src[0] & 0xFE) not in (0x10, 0x90):
        return bytes(src)
    size_len = 4 if src[0] & 0x80 else 3
    p = 2 + (size_len if src[0] & 1 else 0)
    out_size = int.from_bytes(src[p:p + size_len], "big")
    p += size_len
    out = bytearray()
    n = len(src)
    while p < n:
        b0 = src[p]
        if b0 < 0x80:
            b1 = src[p + 1]; p += 2
            lit = b0 & 3; out += src[p:p + lit]; p += lit
            length = ((b0 >> 2) & 7) + 3; offset = ((b0 & 0x60) << 3) + b1 + 1
        elif b0 < 0xC0:
            b1, b2 = src[p + 1], src[p + 2]; p += 3
            lit = b1 >> 6; out += src[p:p + lit]; p += lit
            length = (b0 & 0x3F) + 4; offset = ((b1 & 0x3F) << 8) + b2 + 1
        elif b0 < 0xE0:
            b1, b2, b3 = src[p + 1], src[p + 2], src[p + 3]; p += 4
            lit = b0 & 3; out += src[p:p + lit]; p += lit
            length = ((b0 & 0x0C) << 6) + b3 + 5
            offset = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1
        elif b0 < 0xFC:
            p += 1; lit = ((b0 & 0x1F) << 2) + 4; out += src[p:p + lit]; p += lit
            continue
        else:
            p += 1; lit = b0 & 3; out += src[p:p + lit]; p += lit
            break
        start = len(out) - offset
        for i in range(length):
            out.append(out[start + i])
    if len(out) != out_size:
        raise ValueError("RefPack size mismatch")
    return bytes(out)


def tiled_block(x, y, width_blocks):
    log_bpp = 4
    aligned = (width_blocks + 31) & ~31
    macro = ((x >> 5) + (y >> 5) * (aligned >> 5)) << (log_bpp + 7)
    micro = ((x & 7) + ((y & 0xE) << 2)) << log_bpp
    off = macro + ((micro & ~0xF) << 1) + (micro & 0xF) + ((y & 1) << 4)
    return (((off & ~0x1FF) << 3) + ((y & 16) << 7) + ((off & 0x1C0) << 2)
            + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6) + (off & 0x3F)) >> log_bpp


def rgb565(c):
    return ((c >> 11) * 255 // 31, ((c >> 5) & 63) * 255 // 63, (c & 31) * 255 // 31)


def decode_dxt5_tiled(tex, w, h):
    bw, bh = w // 4, h // 4
    px = bytearray(w * h * 4)
    for by in range(bh):
        for bx in range(bw):
            at = tiled_block(bx, by, bw) * 16
            raw = bytearray(tex[at:at + 16])
            for i in range(0, 16, 2):           # undo the Xbox 16-bit byte swap
                raw[i], raw[i + 1] = raw[i + 1], raw[i]
            a0, a1 = raw[0], raw[1]
            if a0 > a1:
                apal = [a0, a1] + [((6 - k) * a0 + (k + 1) * a1) // 7 for k in range(6)]
            else:
                apal = [a0, a1] + [((4 - k) * a0 + (k + 1) * a1) // 5 for k in range(4)] + [0, 255]
            abits = int.from_bytes(raw[2:8], "little")
            c0 = raw[8] | raw[9] << 8
            c1 = raw[10] | raw[11] << 8
            p0, p1 = rgb565(c0), rgb565(c1)
            # DXT5 colour blocks always use the 4-colour mode.
            cpal = [p0, p1, tuple((2 * a + b) // 3 for a, b in zip(p0, p1)),
                    tuple((a + 2 * b) // 3 for a, b in zip(p0, p1))]
            cbits = int.from_bytes(raw[12:16], "little")
            for k in range(16):
                x, y = bx * 4 + k % 4, by * 4 + k // 4
                r, g, b = cpal[(cbits >> (2 * k)) & 3]
                o = (y * w + x) * 4
                px[o:o + 4] = bytes((r, g, b, apal[(abits >> (3 * k)) & 7]))
    return Image.frombytes("RGBA", (w, h), bytes(px))


def player_names(db_path):
    """Map arcade id (Nzao) -> 'First Last' from the cPbu player table."""
    d = db_path.read_bytes()
    n = be32(d, 0x10)
    ie = 0x18 + 8 * n
    for i in range(n):
        if d[0x18 + 8 * i:0x1C + 8 * i] != b"cPbu":
            continue
        off = ie + be32(d, 0x1C + 8 * i)
        rs, cnt, nf = be32(d, off + 8), (d[off + 22] << 8) | d[off + 23], d[off + 28]
        p, fields = off + 40, {}
        for _ in range(nf):
            fields[d[p + 8:p + 12].decode("latin1")] = (be32(d, p), be32(d, p + 4), be32(d, p + 12))
            p += 16

        def get(row, name):
            typ, bo, bits = fields[name]
            rec = p + row * rs
            if typ == 0:
                return d[rec + bo // 8:rec + (bo + bits) // 8].split(b"\0")[0].decode("latin1")
            v = 0
            for k in range(bits):
                b = bo + k
                v = (v << 1) | ((d[rec + b // 8] >> (7 - b % 8)) & 1)
            return v

        return {get(r, "Nzao"): f"{get(r, 'PedH')} {get(r, 'RMbQ')}".strip() for r in range(cnt)}
    return {}


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 1
    game, out = Path(sys.argv[1]), Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    big = (game / "data0.big").read_bytes()
    entries = {name: (off, size) for name, off, size in big_entries(big)}
    names = player_names(game / "db" / "nhlng.db")
    done = []
    for folder, tw, th, dw, dh, tag in SPECS:
        prefix = f"fe/ion/artassets/{folder}/p0_4000/p"
        for name in sorted(n for n in entries if n.startswith(prefix)):
            pid = int(name[len(prefix):-4])
            off, size = entries[name]
            nested = unpack(big[off:off + size])
            tex = None
            for _, o, s in big_entries(nested):
                if s == 4096 + tw * th and nested[o:o + 4] == b"\x89RW4":
                    tex = nested[o + 4096:o + 4096 + tw * th]
                    break
            if tex is None:
                print(f"skip {name}: no {tw}x{th} texture")
                continue
            img = decode_dxt5_tiled(tex, tw, th).crop((0, 0, dw, dh))
            who = names.get(pid, "unknown").replace(" ", "_").replace("/", "-")
            path = out / f"{pid}_{who}_{tag}.png"
            img.save(path)
            if tag == "large":
                done.append((pid, names.get(pid, "?"), img))
    # Contact sheet of the large portraits, labelled, on a mid-grey background.
    cols, tw_, th_ = 6, 234, 127
    rows = (len(done) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * tw_, rows * (th_ + 16)), (90, 90, 90, 255))
    draw = ImageDraw.Draw(sheet)
    for i, (pid, who, img) in enumerate(done):
        x, y = (i % cols) * tw_, (i // cols) * (th_ + 16)
        thumb = img.resize((tw_, th_))
        sheet.alpha_composite(thumb, (x, y))
        draw.text((x + 3, y + th_ + 2), f"{pid} {who}", fill=(255, 255, 255, 255))
    sheet.save(out / "contact_sheet.png")
    print(f"Wrote {len(done)} large and small portraits and contact_sheet.png to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
