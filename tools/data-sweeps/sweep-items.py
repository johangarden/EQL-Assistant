"""Sweep the eqlwiki item pages our item-stats table lacks (new zones, 29 Oct 2026)
and merge them in the SAME shape build-item-stats-v2.mjs wrote from Companion's scrape.

Input : %TEMP%/eqlwiki-focus/missing.json  (titles using Template:Itempage, not in the table)
Output: data/item-stats.json (merged; key = lowercased name, apostrophes/backticks dropped —
        FocusEffects.ItemKey; a page whose |itemname differs from its title gets both keys)
Run with: python -I -X utf8 sweep-items-2026-10-29.py
"""
import io, json, re, sys, time, urllib.request, urllib.parse
from collections import OrderedDict

UA = "EQL-Assistant-data-sweep/1.0 (johan@thegardencph.com)"
TMP = r"C:\Users\JohanSehested\AppData\Local\Temp\eqlwiki-focus"
OUT = r"C:\Repos\EQL_Assistant\data\item-stats.json"

def get(url):
    for i in range(6):
        try:
            r = urllib.request.Request(url, headers={"User-Agent": UA})
            return json.load(urllib.request.urlopen(r, timeout=120))
        except Exception as e:
            print("retry", i, e, file=sys.stderr); time.sleep(4 * (i + 1))
    raise SystemExit("gave up on " + url)

def key(n): return n.replace("'", "").replace("`", "").lower()

# ---- template fields --------------------------------------------------------
def fields(wt):
    """The |name = value pairs of the {{Itempage ...}} call (values may span lines)."""
    m = re.search(r"\{\{\s*Itempage\b(.*)", wt, re.S | re.I)
    if not m: return None
    body = m.group(1)
    # cut at the template's closing }} — the first "}}" at depth 0
    depth, i = 0, 0
    while i < len(body) - 1:
        if body[i:i+2] == "{{": depth += 1; i += 2; continue
        if body[i:i+2] == "}}":
            if depth == 0: body = body[:i]; break
            depth -= 1; i += 2; continue
        i += 1
    out = {}
    parts = re.split(r"\n\s*\|\s*(?=[A-Za-z_]+\s*=)", "\n" + body)
    for p in parts:
        mm = re.match(r"\s*([A-Za-z_]+)\s*=(.*)", p, re.S)
        if mm: out[mm.group(1).strip().lower()] = mm.group(2).strip()
    return out

# ---- the stats block --------------------------------------------------------
STATS = {"STR", "STA", "AGI", "DEX", "WIS", "INT", "CHA", "HP", "MANA", "END", "ENDUR", "ENDURANCE",
         "HASTE", "CHARGES", "ATTACK", "ATK", "ACCURACY", "HP REGEN", "MANA REGEN", "END REGEN",
         "ENDURANCE REGEN", "REQ LEVEL", "REQUIRED LEVEL", "REC LEVEL", "RECOMMENDED LEVEL",
         "COOLDOWN", "CAST TIME"}
STAT_NAME = {"ENDUR": "END", "ENDURANCE": "END", "ATK": "ATTACK", "HP REGEN": "HP Regen",
             "MANA REGEN": "Mana Regen", "END REGEN": "End Regen", "ENDURANCE REGEN": "End Regen"}
SAVES = {"SV MAGIC", "SV FIRE", "SV COLD", "SV DISEASE", "SV POISON", "SV VOID", "SV CORRUPTION"}
# the stats that are bonuses (a bare "3" means +3); charges, levels and timings are plain numbers
# (the regen trio stays bare — the Companion build wrote "HP Regen": "2" and the sheet adds the sign itself)
SIGNED = {"STR", "STA", "AGI", "DEX", "WIS", "INT", "CHA", "HP", "MANA", "END", "ENDUR", "ENDURANCE", "ATTACK", "ATK", "ACCURACY"}
SLOT_WORDS = {"PRIMARY", "SECONDARY", "RANGE", "AMMO", "HEAD", "FACE", "EAR", "EARS", "NECK", "SHOULDERS", "SHOULDER", "ARMS", "BACK",
              "WRIST", "WRISTS", "HANDS", "CHEST", "LEGS", "FEET", "WAIST", "FINGER", "FINGERS", "CHARM"}
KEYS = sorted(STATS | SAVES | {"SLOT", "AC", "DMG", "ATK DELAY", "DELAY", "DMG BONUS", "DMG BON", "DAMAGE BONUS", "BACKSTAB",
                               "SKILL", "RANGE", "WT", "SIZE", "CLASS", "RACE", "MAGIC DMG", "BANE DMG", "ELEM DMG", "CAPACITY",
                               "SIZE CAPACITY", "WR", "WEIGHT REDUCTION", "LIGHT", "DEITY", "RECAST", "RECAST DELAY", "REC LEVEL", "REQ LEVEL", "TYPE"},
              key=len, reverse=True)
KEY_RX = re.compile(r"\b(" + "|".join(re.escape(k) for k in KEYS) + r")\s*[:：]", re.I)

def pairs_of(line):
    """Known "Key: value" pairs on one line — pairs are split by the NEXT known key, one space or
    two ("Size: TINY WT: 0.1" and "Size: MEDIUM  WT: 3.0" both occur). Text before the first key
    is returned as the leftover."""
    ms = list(KEY_RX.finditer(line))
    if not ms: return [], line.strip()
    out = []
    for i, m in enumerate(ms):
        end = ms[i + 1].start() if i + 1 < len(ms) else len(line)
        out.append((m.group(1), line[m.end():end].strip()))
    return out, line[:ms[0].start()].strip()

def clean(s):
    s = re.sub(r"\[\[([^\]|]+)\|([^\]]+)\]\]", r"\2", s)
    s = re.sub(r"\[\[([^\]]+)\]\]", r"\1", s)
    s = re.sub(r"'{2,}", "", s)
    s = re.sub(r"<[^>]+>", " ", s)
    return s.replace("&nbsp;", " ").strip()

def parse_block(block, rec):
    stats, saves, flags, extras, effects = [], [], [], [], []
    lines = [clean(l) for l in re.split(r"<br\s*/?>|\n", block)]
    for raw in lines:
        line = raw.strip()
        if not line: continue
        # Effects first — their values carry their own colons/parentheses.
        if re.fullmatch(r"Item Information\s*:?", line, re.I): continue
        m = re.match(r"^(Focus Effect|Click Effect|Worn Effect|Combat Effect|Proc Effect|Effect|Proc)\s*:\s*(.+)$", line, re.I)
        if m:
            what, val = m.group(1).lower(), re.sub(r"\s+", " ", m.group(2)).strip()
            lvl = re.search(r"\s+at Level (\d+)\s*$", val, re.I)  # "… (Combat, Casting Time: Instant) at Level 30"
            if lvl: val = val[:lvl.start()].strip()
            tail = ""
            dash = re.search(r"\)\s*-\s*(.*)$", val)  # "(Must Equip) - Cast Time: 1.0 seconds, Required Level: 46"
            if dash: tail, val = dash.group(1).strip(), val[:dash.start() + 1]
            val = val.rstrip(" -")
            paren = re.search(r"\(([^)]*)\)\s*$", val)
            detail = paren.group(1) if paren else ""
            name = val[:paren.start()].strip() if paren else val
            if tail: detail = (detail + ", " if detail else "") + tail
            if lvl: detail = (detail + ", " if detail else "") + f"Req Level {lvl.group(1)}"
            d = detail.lower()
            if what.startswith("focus"): kind = "focus"
            elif "combat" in d or "proc" in d or what.startswith("combat") or what.startswith("proc"): kind = "combat"
            elif "worn" in d or what.startswith("worn"): kind = "worn"
            elif what.startswith("click") or "any slot" in d or "must equip" in d or "can equip" in d or "casting time" in d or "click" in d: kind = "click"
            elif name.lower().startswith("illusion"): kind = "click"
            else: kind = "effect"
            effects.append(f"{kind}: {name}" + (f" ({detail})" if detail else ""))
            continue
        if re.match(r"^(Wind|Brass|String|Percussion|Singing) Resonance\s*:", line, re.I):
            flags.append(line); continue
        if ":" not in line and "：" not in line:
            if line.upper() in SLOT_WORDS and "slot" not in rec: rec["slot"] = line.upper(); continue  # a bare "Wrist" line
            # a flags line: "Lore Equipped, No Trade, Placeable" / "Attuneable" / "This is a meal!"
            for f in re.split(r",\s*|\s{2,}|(?<=Attunable) (?=Lore)|(?<=Attuneable) (?=Lore)", line):
                f = f.strip()
                if not f: continue
                if f.lower() in ("attuneable", "attunable"): f = "Attunable"
                elif f.upper() == "NODROP": f = "No Drop"
                elif f.isupper(): f = f.title()  # "MAGIC ITEM" / "NO DROP" — the table's spelling is title case
                flags.append(f)
            continue
        # "Key: value   Key: value" pairs — two+ spaces separate pairs on a line.
        pairs, left = pairs_of(line.replace("：", ":"))
        if left: extras.append(left)
        if not pairs: continue
        for k, v in pairs:
            K, v = k.strip().upper(), v.strip()
            if re.fullmatch(r"\d+", v) and (K in SIGNED or K in SAVES): v = "+" + v  # "WIS: 3" is +3; the table says so
            num = re.search(r"-?\d+", v)  # "DMG: 13 / 12 / 11" (a tiered line) reads its first number
            if K == "SLOT": rec["slot"] = re.sub(r"\s+", " ", v.upper().replace("/", " ")).strip()
            elif K == "AC" and num: rec["ac"] = int(num.group())
            elif K == "DMG" and num: rec["dmg"] = int(num.group())
            elif K in ("ATK DELAY", "DELAY") and num: rec["delay"] = int(num.group())
            elif K in ("DMG BONUS", "DMG BON", "DAMAGE BONUS") and num: rec["dmgBon"] = int(num.group())
            elif K == "BACKSTAB" and num: rec["backstab"] = int(num.group())
            elif K == "DEITY": flags.append(f"Deity: {v}")
            # a bag's line rides the FLAGS, as the Companion build had it — ItemStats.IsContainer reads "Capacity:" there
            elif K in ("CAPACITY", "SIZE CAPACITY", "WEIGHT REDUCTION", "WR"): flags.append(f"{k.strip().title()}: {v}")
            elif K == "SKILL": rec["skill"] = v
            elif K == "RANGE": rec["range"] = v
            elif K == "WT": rec["weight"] = v
            elif K == "SIZE": rec["size"] = v.upper()
            elif K in ("CLASS", "RACE"):
                cv = re.sub(r"\s+", " ", re.sub(r"\s*,\s*", " ", v)).strip()
                if cv.lower() in ("all", "none"): cv = cv.upper()
                rec["classes" if K == "CLASS" else "races"] = cv
            elif K in SAVES: saves.append([K, v])
            elif K in STATS: stats.append([STAT_NAME.get(K, K), v])
            else: extras.append(f"{k.strip()}: {v}")
    if stats: rec["stats"] = stats
    if saves: rec["saves"] = saves
    if flags: rec["flags"] = " · ".join(OrderedDict.fromkeys(flags))
    if effects: rec["effects"] = "\n".join(effects)
    if extras: rec["extras"] = " · ".join(extras)

def parse_drops(block):
    drops, zone = [], ""
    for raw in block.split("\n"):
        line = raw.strip()
        if not line: continue
        bullet = line.startswith("*")
        names = re.findall(r"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]", line)
        if not names:
            if not bullet and ("{{" not in line) and len(line) < 60: zone = clean(line)
            continue
        if bullet: drops.append([clean(names[0]), zone])
        else: zone = clean(names[0])
    return drops

def record(title, wt):
    f = fields(wt)
    if f is None: return None, None
    block = f.get("statsblock", "")
    if not block.strip(): return None, None
    rec = OrderedDict(); rec["name"] = title
    nm = re.sub(r"\s+", " ", f.get("itemname", "")).strip()
    if nm and not re.search(r"[{}\[\]|<>]", nm) and len(nm) <= 80: rec["name"] = nm
    parse_block(block, rec)
    foc = clean(f.get("focus_effect", "")).strip()
    if foc and f"focus: {foc}" not in rec.get("effects", ""):
        rec["effects"] = (rec.get("effects", "") + "\n" if rec.get("effects") else "") + f"focus: {foc}"
    icon = re.sub(r"[^\d]", "", f.get("lucy_img_id", ""))
    if icon: rec["icon"] = int(icon)
    drops = parse_drops(f.get("dropsfrom", ""))
    if drops: rec["drops"] = drops
    # the builder's field order, for a diff that reads like the rest of the table
    order = ["name", "slot", "ac", "dmg", "delay", "dmgBon", "backstab", "skill", "range", "stats", "saves", "flags", "weight", "size", "classes", "races", "effects", "extras", "icon", "drops"]
    return OrderedDict((k, rec[k]) for k in order if k in rec), nm

def main():
    missing = json.load(open(TMP + r"\missing.json", encoding="utf-8"))
    print("pages to fetch", len(missing))
    pages = {}
    import os
    if os.path.exists(TMP + r"\missing-wikitext.json"):
        pages = json.load(open(TMP + r"\missing-wikitext.json", encoding="utf-8"))
        missing = [t for t in missing if t not in pages]
        print("cached pages", len(pages), "still to fetch", len(missing))
    for i in range(0, len(missing), 40):
        chunk = missing[i:i+40]
        q = urllib.parse.quote("|".join(chunk))
        d = get(f"https://eqlwiki.com/api.php?action=query&prop=revisions&rvprop=content&rvslots=main&titles={q}&format=json")
        for p in d["query"]["pages"].values():
            if "revisions" in p: pages[p["title"]] = p["revisions"][0]["slots"]["main"].get("*", "")
        print(f"  {min(i+40, len(missing))}/{len(missing)}", file=sys.stderr); time.sleep(0.6)
    json.dump(pages, open(TMP + r"\missing-wikitext.json", "w", encoding="utf-8"))
    # Always merge onto the COMMITTED table, so a re-run replaces this sweep's records instead of skipping them.
    import subprocess
    table = json.loads(subprocess.run(["git", "-C", r"C:\Repos\EQL_Assistant", "show", "HEAD:data/item-stats.json"], capture_output=True, check=True).stdout.decode("utf-8-sig"))
    items = table["items"]
    added, stubs, aliases, sample = 0, [], 0, []
    for title, wt in sorted(pages.items()):
        rec, nm = record(title, wt)
        if rec is None: stubs.append(title); continue
        k = key(title)
        if k in items: continue
        items[k] = rec; added += 1
        if nm and key(nm) != k and key(nm) not in items: items[key(nm)] = rec; aliases += 1
        if len(sample) < 12 and rec.get("slot"): sample.append(rec)
    table["items"] = OrderedDict(sorted(items.items()))
    table["source"] = ("eqlwiki.com item pages via jmoyers/everquest-companion items.json (MIT, (c) Josh Moyers); "
                       "pages the scrape lacked swept from eqlwiki.com Template:Itempage on 2026-10-29")
    io.open(OUT, "w", encoding="utf-8", newline="\n").write(json.dumps(table, ensure_ascii=False, separators=(",", ":")))
    print("added", added, "aliases", aliases, "stubs (no stats block)", len(stubs), "table now", len(table["items"]))
    print("stubs:", stubs[:30])
    for r in sample: print(json.dumps(r, ensure_ascii=False)[:300])
    for probe in ["signet ring of lord everling", "warstaff of hopes demise", "checkmate", "denons horn of disaster", "wine thief", "pyrosilk robe"]:
        print(probe, "=>", json.dumps(items.get(probe), ensure_ascii=False)[:400])

if __name__ == "__main__":
    main()
