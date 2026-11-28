#!/usr/bin/env python3
"""fetch_roster_facts.py - fill roster.json from the NHL's public stats API.

    python fetch_roster_facts.py roster/roster.json            # show what would change
    python fetch_roster_facts.py roster/roster.json --write    # apply (keeps roster.json.bak)

Looks every player in roster.json up in all 32 current NHL rosters
(https://api-web.nhle.com/v1/roster/<TEAM>/current), then fills or corrects
jersey_number, shoots, birth_date and birthplace. It also reports players whose
real team differs from the game team in roster.json (after the WPG->ATL and
UTA->PHX mapping), and players it can't find.

It never touches type, replaces or headshot, and never downloads photos.
Names are folded to plain ASCII by default (Stutzle, not Stützle), since the game's
fonts are from 2009; pass --keep-accents to keep them.

Standard library only. Needs internet access, so run it at home, not from the
cloud workspace.
"""

import argparse
import json
import shutil
import sys
import unicodedata
import urllib.request
from pathlib import Path

API = "https://api-web.nhle.com/v1/roster/{team}/current"
NHL_TEAMS = ["ANA", "BOS", "BUF", "CGY", "CAR", "CHI", "COL", "CBJ", "DAL", "DET", "EDM",
             "FLA", "LAK", "MIN", "MTL", "NSH", "NJD", "NYI", "NYR", "OTT", "PHI", "PIT",
             "SJS", "SEA", "STL", "TBL", "TOR", "UTA", "VAN", "VGK", "WSH", "WPG"]
# Today's team -> the 2009 game's team code (None: no slot in the game).
TO_GAME = {t: t for t in NHL_TEAMS}
TO_GAME.update({"LAK": "LA", "NJD": "NJ", "SJS": "SJ", "TBL": "TB", "WPG": "ATL",
                "UTA": "PHX", "SEA": None, "VGK": None})


def fold(text: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFKD", text) if not unicodedata.combining(c))


def key(first: str, last: str) -> str:
    return fold(f"{first} {last}").lower().replace("-", " ").replace(".", "").strip()


def fetch(team: str) -> dict:
    req = urllib.request.Request(API.format(team=team), headers={"User-Agent": "nhl3on3-roster"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.load(r)


def index_league(fetcher=fetch) -> dict:
    """name key -> facts, for every rostered NHL player."""
    league = {}
    for team in NHL_TEAMS:
        try:
            data = fetcher(team)
        except Exception as e:  # keep going; report at the end
            print(f"  could not read {team}: {e}", file=sys.stderr)
            continue
        for group in ("forwards", "defensemen", "goalies"):
            for p in data.get(group, []):
                first = p.get("firstName", {}).get("default", "")
                last = p.get("lastName", {}).get("default", "")
                city = p.get("birthCity", {}).get("default", "")
                region = p.get("birthStateProvince", {}).get("default") or p.get("birthCountry", "")
                league[key(first, last)] = {
                    "team": team, "first": first, "last": last,
                    "number": p.get("sweaterNumber"),
                    "position": p.get("positionCode"),
                    "shoots": {"L": "left", "R": "right"}.get(p.get("shootsCatches", ""), ""),
                    "birth_date": p.get("birthDate", ""),
                    "birthplace": ", ".join(x for x in (city, region) if x),
                }
    return league


def update(players: list, league: dict, keep_accents: bool) -> tuple[list, list, list]:
    changes, team_notes, missing = [], [], []
    for p in players:
        name = f"{p.get('first_name', '')} {p.get('last_name', '')}".strip()
        facts = league.get(key(p.get("first_name", ""), p.get("last_name", "")))
        if not facts:
            missing.append(name)
            continue
        game_team = TO_GAME.get(facts["team"])
        if game_team is None:
            team_notes.append(f"{name}: now on {facts['team']}, which has no slot in the game")
        elif game_team != p.get("team"):
            team_notes.append(f"{name}: roster.json says {p.get('team')}, "
                              f"NHL says {facts['team']} (game code {game_team})")
        new = {
            "jersey_number": facts["number"],
            "shoots": facts["shoots"],
            "birth_date": facts["birth_date"],
            "birthplace": facts["birthplace"] if keep_accents else fold(facts["birthplace"]),
        }
        for field, value in new.items():
            if value in (None, "") or p.get(field) == value:
                continue
            changes.append(f"{name}: {field} {p.get(field, '(none)')!r} -> {value!r}")
            p[field] = value
        if not keep_accents:
            for field in ("first_name", "last_name"):
                if p.get(field) and fold(p[field]) != p[field]:
                    changes.append(f"{name}: {field} {p[field]!r} -> {fold(p[field])!r}")
                    p[field] = fold(p[field])
    return changes, team_notes, missing


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("roster", help="path to roster.json")
    ap.add_argument("--write", action="store_true", help="save the changes")
    ap.add_argument("--keep-accents", action="store_true")
    ap.add_argument("--from-file", help="use a saved {TEAM: roster json} file instead of the API "
                                        "(for testing)")
    args = ap.parse_args()

    path = Path(args.roster)
    data = json.loads(path.read_text(encoding="utf-8"))
    if args.from_file:
        saved = json.loads(Path(args.from_file).read_text(encoding="utf-8"))
        league = index_league(lambda team: saved.get(team, {}))
    else:
        print("Reading 32 NHL rosters...")
        league = index_league()
    changes, team_notes, missing = update(data["players"], league, args.keep_accents)

    for line in changes:
        print("  " + line)
    for line in team_notes:
        print("TEAM  " + line)
    for name in missing:
        print("MISS  " + name + " (not on any current NHL roster: injured reserve, minors or retired?)")
    print(f"\n{len(changes)} changes, {len(team_notes)} team differences, {len(missing)} not found")
    if args.write and changes:
        shutil.copy2(path, path.with_name(path.name + ".bak"))
        path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Saved {path} (previous version in {path.name}.bak)")
    elif changes:
        print("Nothing saved; run again with --write to apply.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
