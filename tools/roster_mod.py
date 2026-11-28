#!/usr/bin/env python3
"""roster_mod.py - build, check and package custom rosters for NHL 3on3 Arcade.

The game applies roster/roster.json and roster/headshots/ at startup
(src/nhl3on3_roster.cpp). This tool does the work around that:

  init       create a roster/ folder with a starter roster.json and version.txt
  slots      list the 43 arcade-star slots and which ones get claimed first
  check      check roster.json against the same rules the game uses, and show
             which slot each player would end up in
  headshot   crop and resize a photo into a portrait PNG the game will accept
  pack       bump version.txt and zip roster/ for sharing
  apply      write a roster into an extracted game now, without starting it
             (roster_apply.py, the same edit the loader makes)
  restore    put back the game files apply or the loader replaced

Everything here mirrors the loader in src/nhl3on3_roster.cpp. If the loader
changes, update the tables below to match.

Only the standard library is needed, except `headshot`, which needs Pillow
(pip install pillow). `apply` and `restore` use roster_apply.py, ea_big.py,
ea_tdb.py and xenos_texture.py from this folder.
"""

from __future__ import annotations

import argparse
import json
import re
import struct
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEFAULT_GAME = REPO / "game" / "extracted"
DEFAULT_ROSTER = REPO / "roster" / "roster.json"

# --- Tables copied from src/nhl3on3_roster.cpp --------------------------------

TEAMS = ["ANA", "ATL", "BOS", "BUF", "CGY", "CAR", "CHI", "COL", "CBJ", "DAL", "DET",
         "EDM", "FLA", "LA", "MIN", "MTL", "NSH", "NJ", "NYI", "NYR", "OTT", "PHI",
         "PHX", "PIT", "STL", "SJ", "TB", "TOR", "VAN", "WSH"]
POSITIONS = ["C", "LW", "RW", "D", "G"]
TYPES = ["all_around", "fast", "strong"]

# Stars the game never gives away unless a player names them in "replaces".
KEEP = ["Sidney Crosby", "Alexander Ovechkin", "Evgeni Malkin", "Patrick Kane",
        "Jonathan Toews", "Nicklas Lidstrom", "Zdeno Chara", "Henrik Lundqvist",
        "Martin Brodeur", "Pavel Datsyuk", "Jarome Iginla", "Joe Thornton",
        "Henrik Zetterberg", "Shea Weber", "Anze Kopitar", "Vincent Lecavalier",
        "Ilya Kovalchuk", "Chris Pronger"]
# Stars handed out, in this order, to players with no "replaces".
CLAIM = ["Nikolai Antropov", "Sean Avery", "Brad Boyes", "Kristian Huselius",
         "Alexander Frolov", "Andrew Cogliano", "Milan Lucic", "Jeremy Roenick",
         "Shane Doan", "Nathan Horton", "Alex Kovalev", "Daniel Briere",
         "Jean-Sebastien Giguere", "Rick DiPietro", "Roberto Luongo", "Mike Modano",
         "Joe Sakic", "Dion Phaneuf", "Mike Green", "Thomas Vanek", "Saku Koivu",
         "Jason Spezza", "Eric Staal", "Dany Heatley", "Marian Gaborik"]

# Slot group of each star (F forward, D defense, G goalie). The loader reads
# this from the game database; these are the stars' real positions, which
# add up to the 32 / 6 / 5 split the loader's warning quotes.
GOALIES = {"Henrik Lundqvist", "Martin Brodeur", "Jean-Sebastien Giguere",
           "Rick DiPietro", "Roberto Luongo"}
DEFENSE = {"Nicklas Lidstrom", "Zdeno Chara", "Shea Weber", "Chris Pronger",
           "Dion Phaneuf", "Mike Green"}

# Rating and body templates (TemplateFor in the loader). The loader looks
# these up by name, so once one of them is renamed, later players of that
# kind silently keep the old slot's ratings instead.
TEMPLATES = {("G", "all_around"): "Henrik Lundqvist", ("G", "fast"): "Henrik Lundqvist",
             ("G", "strong"): "Henrik Lundqvist",
             ("D", "fast"): "Mike Green", ("D", "strong"): "Zdeno Chara",
             ("D", "all_around"): "Nicklas Lidstrom",
             ("F", "fast"): "Patrick Kane", ("F", "strong"): "Jarome Iginla",
             ("F", "all_around"): "Sidney Crosby"}

# Portrait sizes the loader writes (texture is cropped to the drawn area).
LARGE = (467, 253)
SMALL = (202, 128)
# Recommended source size: twice the large portrait, same aspect ratio.
HEADSHOT_SIZE = (934, 506)

# Today's teams the 2009 game has no slot for, and the nearest fit.
MODERN_TEAMS = {
    "WPG": "ATL",  # the Jets moved from Atlanta in 2011
    "UTA": "PHX",  # Utah took over the Arizona (Phoenix) franchise in 2024
    "ARI": "PHX",
    "LAK": "LA", "NJD": "NJ", "SJS": "SJ", "TBL": "TB",
    "MON": "MTL", "CLB": "CBJ", "WAS": "WSH", "NAS": "NSH", "CAL": "CGY",
    "SEA": None,  # no slot; pick a team yourself
    "VGK": None,
}

FIELDS = ["first_name", "last_name", "team", "position", "type", "jersey_number",
          "shoots", "birth_date", "birthplace", "headshot", "replaces"]
# Read by this tool only (the game skips them): who took the headshot and under which
# licence, e.g. "Connor Mah, CC BY-SA 3.0, https://commons.wikimedia.org/wiki/File:...".
# `check --release` requires one for every headshot, and `pack` collects them into
# roster/CREDITS.txt. headshot_source is where a candidate photo was found, kept for
# reference until its licence is confirmed and copied into headshot_credit.
TOOL_FIELDS = ["headshot_credit", "headshot_source"]


def norm(text: str) -> str:
    return text.strip().lower()


def group_of(position: str) -> str:
    return "G" if position == "G" else "D" if position == "D" else "F"


def star_group(name: str) -> str:
    return "G" if name in GOALIES else "D" if name in DEFENSE else "F"


ALL_STARS = KEEP + CLAIM
STAR_BY_NORM = {norm(n): n for n in ALL_STARS}


# --- PNG header ----------------------------------------------------------------

def png_size(path: Path) -> tuple[int, int] | None:
    with open(path, "rb") as f:
        head = f.read(24)
    if len(head) < 24 or head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", head[16:24])


# --- Commands ------------------------------------------------------------------

def cmd_init(args: argparse.Namespace) -> int:
    root = Path(args.dir) / "roster"
    if (root / "roster.json").exists() and not args.force:
        print(f"{root / 'roster.json'} already exists (use --force to overwrite)")
        return 1
    (root / "headshots").mkdir(parents=True, exist_ok=True)
    example = {
        "players": [{
            "first_name": "Connor", "last_name": "McDavid", "team": "EDM",
            "position": "C", "type": "fast", "jersey_number": 97, "shoots": "left",
            "birth_date": "1997-01-13", "birthplace": "Richmond Hill, ON",
            "headshot": "headshots/mcdavid.png", "replaces": "Joe Sakic",
        }]
    }
    (root / "roster.json").write_text(json.dumps(example, indent=2) + "\n", encoding="utf-8")
    (root / "version.txt").write_text("1\n", encoding="utf-8")
    print(f"Created {root}")
    print("  roster.json   one example player; add the rest")
    print("  version.txt   bump this when you share a new version (pack does it)")
    print("  headshots/    put portraits here (see: roster_mod.py headshot)")
    return 0


def cmd_slots(args: argparse.Namespace) -> int:
    print("Kept unless a player names them in \"replaces\":")
    for name in KEEP:
        tag = "  (rating template)" if name in TEMPLATES.values() else ""
        print(f"  {star_group(name)}  {name}{tag}")
    print("\nGiven out in this order to players with no \"replaces\":")
    for i, name in enumerate(CLAIM, 1):
        tag = "  (rating template)" if name in TEMPLATES.values() else ""
        print(f"  {i:2}. {star_group(name)}  {name}{tag}")
    counts = {g: sum(star_group(n) == g for n in ALL_STARS) for g in "FDG"}
    print(f"\n{len(ALL_STARS)} slots: {counts['F']} forward, {counts['D']} defense, "
          f"{counts['G']} goalie")
    return 0


# --- Secret players (planned feature; see edition-2026-27-plan.md) -------------------
# They live in a separate top-level "secret_players" list, which the current loader
# never reads (it only parses "players"), so adding them is safe today. Each one names
# the active player whose slot it takes once unlocked ("swaps_with") and how it's
# unlocked.
UNLOCK_METHODS = ["code", "achievement", "wins"]
CODE_BUTTONS = ["UP", "DOWN", "LEFT", "RIGHT", "A", "B", "X", "Y", "LB", "RB", "LT", "RT",
                "START", "BACK"]
# The game's 12 achievements (named after the icons in the game folder).
ACHIEVEMENTS = ["brickwall", "doubleup", "iceman", "juggernaut", "laserbeam", "letsplay",
                "overandout", "racetovictory", "scorebig", "superspeed", "teamplayer",
                "trickster"]
SECRET_FIELDS = FIELDS + ["id", "swaps_with", "unlock", "hint"] + ["headshot_credit",
                                                                    "headshot_source"]


def load_secrets(path: Path) -> list[dict]:
    data = json.loads(path.read_text(encoding="utf-8"))
    secrets = data.get("secret_players", []) if isinstance(data, dict) else []
    if not isinstance(secrets, list):
        raise ValueError("\"secret_players\" must be a list")
    return secrets


def check_secrets(secrets, players, root, errors, warnings):
    """Validate secret players against the active roster and print them."""
    active = {norm(f"{p.get('first_name', '')} {p.get('last_name', '')}"):
              (str(p.get("position") or ""), None) for p in players}
    seen_ids, seen_swaps = set(), {}
    rows = []
    for i, s in enumerate(secrets, 1):
        name = f"{s.get('first_name', '')} {s.get('last_name', '')}".strip() or f"secret {i}"
        who = f"secret {name}"
        for key in s:
            if key not in SECRET_FIELDS:
                warnings.append(f"{who}: \"{key}\" is not a known field")
        sid = str(s.get("id") or "")
        if not re.fullmatch(r"[a-z0-9_]{2,24}", sid):
            errors.append(f"{who}: id must be 2-24 lowercase letters, digits or _ "
                          f"(it's stored in the unlock list)")
        elif sid in seen_ids:
            errors.append(f"{who}: id {sid} is used twice")
        seen_ids.add(sid)
        team, pos, ptype = str(s.get("team") or ""), str(s.get("position") or ""), \
            str(s.get("type") or "")
        if team not in TEAMS:
            errors.append(f"{who}: unknown team {team or '(none)'} (secret players still need "
                          f"a game team code, e.g. a favourite team)")
        if pos not in POSITIONS:
            errors.append(f"{who}: unknown position {pos or '(none)'}")
        if pos != "G" and ptype not in TYPES:
            errors.append(f"{who}: type must be all_around, fast or strong")
        num = s.get("jersey_number", 0)
        if not isinstance(num, int) or not 0 <= num <= 99:
            errors.append(f"{who}: jersey_number must be 0-99")
        swap = str(s.get("swaps_with") or "")
        if norm(swap) not in active:
            errors.append(f"{who}: swaps_with must name a player in \"players\" "
                          f"({swap or 'none given'} isn't one)")
        else:
            other_pos, slot = active[norm(swap)]
            if group_of(other_pos) != group_of(pos):
                warnings.append(f"{who}: {pos} swapping with {swap} ({other_pos}); "
                                f"the slot changes position group")
            if norm(swap) in seen_swaps:
                warnings.append(f"{who}: {swap} is also the swap for {seen_swaps[norm(swap)]}; "
                                f"only one can be unlocked into that slot at a time")
            seen_swaps[norm(swap)] = name
        unlock = s.get("unlock")
        how = "?"
        if not isinstance(unlock, dict) or unlock.get("method") not in UNLOCK_METHODS:
            errors.append(f"{who}: unlock must be {{\"method\": one of {', '.join(UNLOCK_METHODS)}, "
                          f"\"value\": ...}}")
        else:
            m, v = unlock["method"], unlock.get("value")
            if m == "code":
                buttons = str(v or "").upper().split()
                if not 4 <= len(buttons) <= 16 or any(b not in CODE_BUTTONS for b in buttons):
                    errors.append(f"{who}: a code is 4-16 buttons separated by spaces, from "
                                  f"{' '.join(CODE_BUTTONS)}")
                how = "code " + " ".join(buttons)
            elif m == "achievement":
                if v not in ACHIEVEMENTS:
                    errors.append(f"{who}: achievement must be one of {', '.join(ACHIEVEMENTS)}")
                how = f"achievement {v}"
            else:
                if not isinstance(v, int) or not 1 <= v <= 1000:
                    errors.append(f"{who}: wins must be a whole number 1-1000")
                how = f"{v} wins"
        shot = str(s.get("headshot") or "")
        if shot and not (root / shot).is_file():
            warnings.append(f"{who}: headshot {shot} not found yet")
        rows.append((name, pos, ptype, swap, how))
    if rows:
        print("\nSecret players (not read by the current loader; planned feature):")
        for name, pos, ptype, swap, how in rows:
            print(f"  {name:<24} {pos:<2} {ptype or '-':<10} swaps with {swap:<22} unlock: {how}")
    codes = [s["unlock"]["value"].upper() for s in secrets if isinstance(s.get("unlock"), dict)
             and s["unlock"].get("method") == "code" and isinstance(s["unlock"].get("value"), str)]
    for c in set(codes):
        if codes.count(c) > 1:
            errors.append(f"two secret players share the code {c}")


def load_roster(path: Path) -> list[dict]:
    data = json.loads(path.read_text(encoding="utf-8"))
    players = data.get("players") if isinstance(data, dict) else None
    if not isinstance(players, list):
        raise ValueError("roster.json needs a top-level \"players\" list")
    return players


def cmd_check(args: argparse.Namespace) -> int:
    path = Path(args.roster)
    if path.is_dir():
        path = path / "roster.json"
    root = path.parent
    try:
        players = load_roster(path)
    except (OSError, ValueError) as e:
        print(f"ERROR {e}")
        return 1

    errors: list[str] = []
    warnings: list[str] = []
    seen_names: set[str] = set()
    seen_replaces: dict[str, str] = {}

    ready = []
    for i, p in enumerate(players, 1):
        first = str(p.get("first_name") or "").strip()
        last = str(p.get("last_name") or "").strip()
        name = f"{first} {last}".strip() or f"player {i}"
        who = f"{name} (#{i})"
        for key in p:
            if key not in FIELDS and key not in TOOL_FIELDS:
                warnings.append(f"{who}: \"{key}\" is ignored by the game")
        team = str(p.get("team") or "")
        pos = str(p.get("position") or "")
        if not first or not team or not pos:
            errors.append(f"{who}: missing a name, team or position")
        if team and team not in TEAMS:
            if team in MODERN_TEAMS:
                fit = MODERN_TEAMS[team]
                hint = f"use \"{fit}\"" if fit else "the game has no slot for this team; pick another"
                errors.append(f"{who}: team {team} isn't in the 2009 game ({hint})")
            else:
                errors.append(f"{who}: unknown team {team} (one of {', '.join(TEAMS)})")
        if pos and pos not in POSITIONS:
            errors.append(f"{who}: unknown position {pos} (C, LW, RW, D or G)")
        ptype = str(p.get("type") or "")
        if pos != "G" and ptype not in TYPES:
            errors.append(f"{who}: type must be all_around, fast or strong")
        number = p.get("jersey_number", 0)
        if not isinstance(number, int) or not 0 <= number <= 99:
            errors.append(f"{who}: jersey_number must be a whole number 0-99")
        shoots = str(p.get("shoots") or "")
        if shoots and shoots.lower() not in ("left", "right", "l", "r"):
            errors.append(f"{who}: shoots must be left or right")
        birth = str(p.get("birth_date") or "")
        if birth and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", birth):
            errors.append(f"{who}: birth_date must look like 1997-01-13")
        elif birth and not 1900 <= int(birth[:4]) <= 2155:
            errors.append(f"{who}: birth_date year out of range")
        if norm(name) in seen_names:
            errors.append(f"{who}: listed twice")
        seen_names.add(norm(name))

        replaces = str(p.get("replaces") or "")
        if replaces:
            star = STAR_BY_NORM.get(norm(replaces))
            if not star:
                errors.append(f"{who}: replaces {replaces}, who is not an arcade star "
                              f"(see: roster_mod.py slots)")
            else:
                if star in seen_replaces:
                    errors.append(f"{who}: {star}'s slot is already taken by "
                                  f"{seen_replaces[star]}")
                seen_replaces[star] = name
                if pos and star_group(star) != group_of(pos):
                    warnings.append(f"{who}: {pos} replacing {star} "
                                    f"({star_group(star)} slot); the game allows it, "
                                    f"but check it looks right")

        shot = str(p.get("headshot") or "")
        has_shot = False
        if shot:
            shot_path = root / shot
            if not shot_path.is_file():
                warnings.append(f"{who}: headshot {shot} not found yet")
            else:
                size = png_size(shot_path)
                if not size:
                    errors.append(f"{who}: {shot} is not a PNG")
                else:
                    has_shot = True
                    if getattr(args, "release", False) and not str(p.get("headshot_credit") or ""):
                        errors.append(f"{who}: {shot} has no headshot_credit (needed for a "
                                      f"release: photographer, licence, source link)")
                    w, h = size
                    if h < LARGE[1]:
                        warnings.append(f"{who}: {shot} is {w}x{h}; under {LARGE[1]} px "
                                        f"tall it will look soft")
                    if abs(w / h - LARGE[0] / LARGE[1]) > 0.15:
                        warnings.append(f"{who}: {shot} is {w}x{h}; the game crops it to "
                                        f"{LARGE[0]}:{LARGE[1]} from the centre "
                                        f"(run headshot to control the crop)")
        is_star = norm(name) in STAR_BY_NORM
        if has_shot or is_star:
            ready.append((p, name, pos, ptype, replaces))
        elif first:
            warnings.append(f"{who}: no headshot yet, so the game skips this player")

    # Slot assignment, same order and rules as ApplyRoster.
    keep = {norm(n) for n in KEEP}
    for p, name, pos, ptype, replaces in ready:
        keep.add(norm(name))
        if replaces:
            keep.add(norm(replaces))
    claimable = [n for n in CLAIM if norm(n) not in keep]
    renamed: set[str] = set()
    plan = []
    for p, name, pos, ptype, replaces in ready:
        if pos not in POSITIONS:
            continue
        g = group_of(pos)
        if replaces and norm(replaces) in STAR_BY_NORM:
            slot, how = STAR_BY_NORM[norm(replaces)], "replaces"
        elif norm(name) in STAR_BY_NORM:
            slot, how = STAR_BY_NORM[norm(name)], "keeps"
        else:
            slot = next((n for n in claimable if star_group(n) == g), None)
            if not slot:
                errors.append(f"{name}: no {g} slot left (the game has 32 forward, 6 defense "
                              f"and 5 goalie slots); add \"replaces\" to free one")
                continue
            claimable.remove(slot)
            how = "takes"
        template = TEMPLATES[(g, ptype if ptype in TYPES else "all_around")]
        if template in renamed:
            warnings.append(f"{name}: the {ptype or 'goalie'} template ({template}) was already "
                            f"renamed by an earlier player, so {name} keeps {slot}'s old "
                            f"ratings. Move {name} above whoever takes {template}'s slot, "
                            f"or leave {template} alone")
        plan.append((name, pos, ptype, how, slot))
        if norm(slot) != norm(name):
            renamed.add(slot)

    if plan:
        print("Slot plan:")
        for name, pos, ptype, how, slot in plan:
            print(f"  {name:<28} {pos:<2} {ptype or '-':<10} {how} {slot}")
    try:
        secrets = load_secrets(path)
    except (OSError, ValueError) as e:
        secrets = []
        errors.append(str(e))
    check_secrets(secrets, players, root, errors, warnings)
    left = {g: sum(star_group(n) == g for n in claimable) for g in "FDG"}
    print(f"\nFree slots left: {left['F']} F, {left['D']} D, {left['G']} G "
          f"(more by naming kept stars in \"replaces\")")
    for w in warnings:
        print(f"WARN  {w}")
    for e in errors:
        print(f"ERROR {e}")
    print(f"\n{len(players)} players, {len(ready)} ready, {len(secrets)} secret, "
          f"{len(errors)} errors, "
          f"{len(warnings)} warnings")
    return 1 if errors else 0


def cmd_headshot(args: argparse.Namespace) -> int:
    try:
        from PIL import Image, ImageChops
    except ImportError:
        print("headshot needs Pillow: pip install pillow")
        return 1
    src = Image.open(args.image).convert("RGBA")
    tw, th = HEADSHOT_SIZE
    w, h = src.size
    alpha = src.getchannel("A")
    is_cutout = alpha.getextrema()[0] < 250 and not args.photo
    if is_cutout:
        # Match the original portraits (measured from all 43 in the game): a centred
        # head-and-shoulders cutout about 260x230 px on the 467x253 card, hair about
        # 13 px from the top, shoulders fading out at the bottom. Doubled here.
        box = alpha.point(lambda v: 255 if v > 128 else 0).getbbox()
        if not box:
            print(f"{args.image} is fully transparent")
            return 1
        subject = src.crop(box)
        sw, sh = subject.size
        scale = min(460 / sh, 600 / sw)
        subject = subject.resize((max(1, round(sw * scale)), max(1, round(sh * scale))),
                                 Image.LANCZOS)
        out = Image.new("RGBA", (tw, th), (0, 0, 0, 0))
        out.alpha_composite(subject, ((tw - subject.width) // 2, 26))
        # Bottom fade, as in the originals.
        a = out.getchannel("A")
        fade = Image.linear_gradient("L").resize((tw, 36)).transpose(Image.FLIP_TOP_BOTTOM)
        ramp = Image.new("L", (tw, th), 255)
        ramp.paste(fade, (0, th - 36))
        out.putalpha(ImageChops.multiply(a, ramp))
    else:
        # Opaque photo: crop to the portrait's shape. --top sets where the crop sits
        # vertically (0 = top of the photo, 0.5 = centre); faces sit high. The game's
        # own portraits are cutouts, so remove the background first for a matching look.
        target = tw / th
        if w / h > target:
            cw, ch = int(round(h * target)), h
        else:
            cw, ch = w, int(round(w / target))
        x = int(round((w - cw) * args.left))
        y = int(round((h - ch) * args.top))
        out = src.crop((x, y, x + cw, y + ch)).resize((tw, th), Image.LANCZOS)
        print("Note: no transparency, so this won't match the game's cutout portraits")
    if args.style:
        # One shared look for photos from mixed sources (portrait_style.py, same folder).
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import portrait_style
        out = portrait_style.stylise(out, args.team, args.style, scale=tw / LARGE[0])
    dest = Path(args.out)
    dest.parent.mkdir(parents=True, exist_ok=True)
    out.save(dest, "PNG")
    print(f"Wrote {dest} ({tw}x{th}) from {args.image} ({w}x{h})")
    if args.preview:
        prev = dest.with_name(dest.stem + "_preview.png")
        sheet = Image.new("RGBA", (LARGE[0] + SMALL[0] + 30, LARGE[1] + 20), (40, 40, 40, 255))
        sheet.alpha_composite(out.resize(LARGE, Image.LANCZOS), (10, 10))
        # The loader scales to 128 tall, then centre-crops to 202 wide.
        sw = max(SMALL[0], round(tw * SMALL[1] / th))
        small = out.resize((sw, SMALL[1]), Image.LANCZOS)
        cx = (sw - SMALL[0]) // 2
        sheet.alpha_composite(small.crop((cx, 0, cx + SMALL[0], SMALL[1])), (LARGE[0] + 20, 10))
        sheet.save(prev, "PNG")
        print(f"Wrote {prev} (large and small portraits as the game draws them)")
    return 0


def cmd_pack(args: argparse.Namespace) -> int:
    root = Path(args.dir) / "roster"
    if not (root / "roster.json").is_file():
        print(f"No roster.json in {root}")
        return 1
    if cmd_check(argparse.Namespace(roster=str(root), release=args.release)) != 0 and not args.force:
        print("Fix the errors first (or use --force)")
        return 1
    vfile = root / "version.txt"
    try:
        version = int(vfile.read_text(encoding="utf-8").strip() or "0")
    except (OSError, ValueError):
        version = 0
    if not args.keep_version:
        version += 1
        vfile.write_text(f"{version}\n", encoding="utf-8")
    credits = []
    for p in load_roster(root / "roster.json"):
        shot, credit = str(p.get("headshot") or ""), str(p.get("headshot_credit") or "")
        if shot and credit and (root / shot).is_file():
            name = f"{p.get('first_name', '')} {p.get('last_name', '')}".strip()
            credits.append(f"{name} ({shot}): {credit}")
    if credits:
        (root / "CREDITS.txt").write_text(
            "Player portraits, adapted (cropped, cut out and resized) from the photos below.\n"
            "Each keeps its original licence.\n\n" + "\n".join(credits) + "\n", encoding="utf-8")
        print(f"Wrote {root / 'CREDITS.txt'} ({len(credits)} credits)")
    dest = Path(args.out or f"roster-v{version}.zip")
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(root.rglob("*")):
            if f.is_file() and not f.name.endswith("_preview.png"):
                z.write(f, Path("roster") / f.relative_to(root))
    print(f"Wrote {dest} (version {version}). Unzip it next to nhl3on3.exe.")
    print("Everyone in an online lobby needs the same zip, or matches will desync.")
    return 0


def cmd_apply(args: argparse.Namespace) -> int:
    import roster_apply
    roster_apply.build(args.game_dir, args.roster, args.dry_run)
    return 0


def cmd_restore(args: argparse.Namespace) -> int:
    import roster_apply
    roster_apply.restore(args.game_dir)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("init", help="create a roster/ folder")
    p.add_argument("dir", nargs="?", default=".", help="folder to create roster/ in")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_init)

    p = sub.add_parser("slots", help="list the 43 arcade-star slots")
    p.set_defaults(fn=cmd_slots)

    p = sub.add_parser("check", help="check roster.json and show the slot plan")
    p.add_argument("roster", nargs="?", default="roster", help="roster.json or its folder")
    p.add_argument("--release", action="store_true",
                   help="also require a headshot_credit for every headshot")
    p.set_defaults(fn=cmd_check)

    p = sub.add_parser("headshot", help="make a portrait PNG from a photo")
    p.add_argument("image")
    p.add_argument("out", help="e.g. roster/headshots/mcdavid.png")
    p.add_argument("--top", type=float, default=0.2,
                   help="vertical crop position, 0 top to 1 bottom (default 0.2)")
    p.add_argument("--left", type=float, default=0.5,
                   help="horizontal crop position, 0 left to 1 right (default 0.5)")
    p.add_argument("--photo", action="store_true",
                   help="treat the image as an opaque photo even if it has transparency")
    p.add_argument("--preview", action="store_true",
                   help="also write a preview of both in-game sizes")
    p.add_argument("--style", choices=["ink", "duotone", "poster"],
                   help="apply a shared portrait style (see portrait_style.py); ink is "
                        "black and white with a team-colour outline")
    p.add_argument("--team", default="",
                   help="game team code for --style colours, e.g. EDM, ATL (Winnipeg), PHX (Utah)")
    p.set_defaults(fn=cmd_headshot)

    p = sub.add_parser("pack", help="bump version.txt and zip roster/")
    p.add_argument("dir", nargs="?", default=".", help="folder that holds roster/")
    p.add_argument("--out")
    p.add_argument("--keep-version", action="store_true")
    p.add_argument("--release", action="store_true",
                   help="refuse to pack headshots without a headshot_credit")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_pack)

    p = sub.add_parser("apply", help="write a roster into an extracted game now")
    p.add_argument("game_dir", nargs="?", default=str(DEFAULT_GAME),
                   help="the extracted game (default game/extracted)")
    p.add_argument("--roster", default=str(DEFAULT_ROSTER), help="roster.json to apply")
    p.add_argument("--dry-run", action="store_true", help="show what would change")
    p.set_defaults(fn=cmd_apply)

    p = sub.add_parser("restore", help="put back the game files apply replaced")
    p.add_argument("game_dir", nargs="?", default=str(DEFAULT_GAME))
    p.set_defaults(fn=cmd_restore)

    args = ap.parse_args()
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
