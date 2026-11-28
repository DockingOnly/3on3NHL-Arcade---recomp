#!/usr/bin/env python3
"""portrait_style.py - give mixed photos one consistent portrait look.

Photos from different sources differ in lighting, colour and sharpness. Each style
here throws that variation away and rebuilds the image from tone alone, in the
player's team colours, with the same outline, so 43 portraits from 43 sources
read as one set.

    python portrait_style.py IN.png OUT.png --team EDM --style duotone
    python portrait_style.py --sheet OUT.png --team EDM IN1.png IN2.png ...   # compare styles

IN.png should be a cutout (background removed), ideally already framed by
`roster_mod.py headshot`. Needs Pillow.
"""

import argparse
import sys

from PIL import Image, ImageChops, ImageFilter, ImageOps, ImageDraw

# Primary and secondary colours per game team code (2026-27 identities: ATL is the
# Winnipeg slot, PHX the Utah slot), plus SEA and VGK for later.
TEAM_COLORS = {
    "ANA": ("#F47A38", "#B9975B"), "ATL": ("#041E42", "#7B303E"), "BOS": ("#FFB81C", "#000000"),
    "BUF": ("#003087", "#FFB81C"), "CGY": ("#C8102E", "#F1BE48"), "CAR": ("#CE1126", "#000000"),
    "CHI": ("#CF0A2C", "#000000"), "COL": ("#6F263D", "#236192"), "CBJ": ("#002654", "#CE1126"),
    "DAL": ("#006847", "#8F8F8C"), "DET": ("#CE1126", "#FFFFFF"), "EDM": ("#041E42", "#FF4C00"),
    "FLA": ("#041E42", "#C8102E"), "LA": ("#111111", "#A2AAAD"), "MIN": ("#154734", "#A6192E"),
    "MTL": ("#AF1E2D", "#192168"), "NSH": ("#FFB81C", "#041E42"), "NJ": ("#CE1126", "#000000"),
    "NYI": ("#00539B", "#F47D30"), "NYR": ("#0038A8", "#CE1126"), "OTT": ("#C52032", "#C2912C"),
    "PHI": ("#F74902", "#000000"), "PHX": ("#71AFE5", "#090909"), "PIT": ("#FCB514", "#000000"),
    "STL": ("#002F87", "#FCB514"), "SJ": ("#006D75", "#EA7200"), "TB": ("#002868", "#FFFFFF"),
    "TOR": ("#00205B", "#FFFFFF"), "VAN": ("#00205B", "#00843D"), "WSH": ("#C8102E", "#041E42"),
    "SEA": ("#001628", "#99D9D9"), "VGK": ("#B4975A", "#333F42"),
}
STYLES = ["duotone", "poster", "ink"]


def rgb(hex_colour):
    h = hex_colour.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def mix(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def luminance(c):
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]


def team_palette(team):
    """Shadow, mid and highlight colours for a team: dark navy-ish shadows, the
    team's main colour in the mids, near-white highlights. Very light or very dark
    team colours are pulled toward the middle so faces stay readable."""
    primary, secondary = (rgb(c) for c in TEAM_COLORS.get(team, ("#3A4A5C", "#A0A8B0")))
    mid = primary
    if luminance(mid) < 60:          # navy/black teams: lift the mid tone
        mid = mix(mid, (255, 255, 255), 0.35)
    if luminance(mid) > 190:         # gold/very light teams: deepen it
        mid = mix(mid, (0, 0, 0), 0.25)
    shadow = mix(primary, (8, 10, 16), 0.75)
    highlight = mix(mid, (255, 255, 255), 0.88)
    return shadow, mid, highlight, primary, secondary


def normalised_tone(img):
    """Greyscale with the exposure evened out, measured on the subject's own pixels.

    Only the white point moves: the brightest 1% (eye whites, teeth, jersey trim)
    is set to near-white, and the black point and midtones are left alone. That
    evens out dim and bright photos without flattening skin tones: a darker-skinned
    player stays darker than a fair-skinned one, as in the photo."""
    alpha = img.getchannel("A")
    gray = img.convert("L")
    mask = alpha.point(lambda v: 255 if v > 128 else 0)
    hist = gray.histogram(mask)
    total = sum(hist)
    if not total:
        return gray
    run, hi = 0, 255
    for i in range(255, -1, -1):
        run += hist[i]
        if run >= total * 0.01:
            hi = i
            break
    gain = min(1.6, 245 / max(hi, 1))   # never brighten more than 60%
    return gray.point(lambda v: min(255, round(v * gain)))


def outline(alpha, width, colour):
    """A solid outline around the cutout, the same on every portrait."""
    grown = alpha.point(lambda v: 255 if v > 64 else 0).filter(ImageFilter.MaxFilter(width * 2 + 1))
    ring = ImageChops.subtract(grown, alpha.point(lambda v: 255 if v > 64 else 0))
    layer = Image.new("RGBA", alpha.size, colour + (0,))
    layer.putalpha(ring)
    return layer, grown


def stylise(img, team, style, scale=1.0):
    """img: RGBA cutout. Returns an RGBA image of the same size."""
    img = img.convert("RGBA")
    alpha = img.getchannel("A")
    shadow, mid, highlight, primary, secondary = team_palette(team)
    tone = normalised_tone(img)

    if style == "duotone":
        body = ImageOps.colorize(tone, black=shadow, mid=mid, white=highlight, midpoint=120)
    elif style == "poster":
        # Smooth first so the tone bands are clean shapes, not speckle.
        smooth = tone.filter(ImageFilter.MedianFilter(5)).filter(ImageFilter.SMOOTH_MORE)
        levels = smooth.point(lambda v: [0, 70, 150, 225][min(3, v // 64)])
        body = ImageOps.colorize(levels, black=shadow, mid=mid, white=highlight, midpoint=128)
        # Ink lines only where the smoothed image has strong edges.
        edges = smooth.filter(ImageFilter.FIND_EDGES).point(lambda v: 255 if v > 60 else 0)
        edges = edges.filter(ImageFilter.MedianFilter(3))
        body = Image.composite(Image.new("RGB", img.size, shadow), body, edges)
    elif style == "ink":
        paper = mix(highlight, (255, 255, 255), 0.5)
        ink = mix(shadow, (0, 0, 0), 0.4)
        body = ImageOps.colorize(tone.filter(ImageFilter.UnsharpMask(2, 80)), black=ink, white=paper)
    else:
        raise ValueError(f"unknown style {style}")

    out = body.convert("RGBA")
    out.putalpha(alpha)
    ring, grown = outline(alpha, max(2, round(3 * scale)), secondary if style != "ink" else primary)
    framed = Image.new("RGBA", img.size, (0, 0, 0, 0))
    framed.alpha_composite(ring)
    framed.alpha_composite(out)
    # Keep the original bottom fade on the outline too.
    fade = alpha.point(lambda v: v)
    bottom = Image.new("L", img.size, 255)
    h = img.size[1]
    ramp = Image.linear_gradient("L").resize((img.size[0], max(1, h // 14))).transpose(Image.FLIP_TOP_BOTTOM)
    bottom.paste(ramp, (0, h - ramp.size[1]))
    framed.putalpha(ImageChops.multiply(framed.getchannel("A"), bottom))
    return framed


def sheet(paths, team, dest):
    """Rows: inputs. Columns: original, then each style. Dark menu-grey background."""
    imgs = [Image.open(p).convert("RGBA") for p in paths]
    w, h = imgs[0].size
    pad = 12
    labels = ["original"] + STYLES
    out = Image.new("RGBA", ((w + pad) * len(labels) + pad, (h + pad) * len(imgs) + pad + 22),
                    (52, 56, 64, 255))
    d = ImageDraw.Draw(out)
    for c, label in enumerate(labels):
        d.text((pad + c * (w + pad) + 4, 6), label, fill=(230, 230, 230, 255))
    for r, img in enumerate(imgs):
        teams = team if isinstance(team, list) else [team] * len(imgs)
        cells = [img] + [stylise(img, teams[r], s, scale=w / 467) for s in STYLES]
        for c, cell in enumerate(cells):
            out.alpha_composite(cell.resize((w, h)), (pad + c * (w + pad), 22 + pad + r * (h + pad)))
    out.save(dest)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("paths", nargs="+", help="IN.png OUT.png, or with --sheet the inputs")
    ap.add_argument("--team", default="", help="game team code (e.g. EDM); with --sheet, "
                                               "comma-separated, one per input")
    ap.add_argument("--style", choices=STYLES, default="duotone")
    ap.add_argument("--sheet", help="write a comparison sheet of every style to this path")
    args = ap.parse_args()
    if args.sheet:
        teams = args.team.split(",") if "," in args.team else args.team
        sheet(args.paths, teams, args.sheet)
        print(f"Wrote {args.sheet}")
        return 0
    if len(args.paths) != 2:
        ap.error("give IN.png OUT.png")
    src = Image.open(args.paths[0])
    stylise(src, args.team, args.style, scale=src.size[0] / 467).save(args.paths[1])
    print(f"Wrote {args.paths[1]} ({args.style}, {args.team or 'neutral'})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
