"""Build data/reincarnation.json — what each class and race gains from its three reincarnation ranks.

Source: eqlwiki's Alternate Advancement page, the "Special AAs" table. One row per class and per
race, all named "Class Reincarnation: <name>" (the races too):

    | Class Reincarnation: Shadow Knight || 3 || 6 || 9 || <the passive, worded for rank I> || <the activated ability>

Columns: the passive's value at rank I / II / III, its description, and (classes only) the activated
ability the THIRD rank grants. The table's last header says the activated abilities share one timer
("HK Share CoolDown on 90sec timer"). "Reincarnation: Completionist" is the extra passive for finishing
every class.

Read as written: the cells are not uniform ("3%" · "3" · "+2 Mana gen" · "10%" then "20"), so they are
kept as the wiki has them, whitespace tidied — the description beside them carries the unit. Parts of
the table are datamined; the page that shows it says so until Kunark (8 Dec 2026) is live.

Run:  python tools/data-sweeps/build-reincarnation.py          (fetches the page)
      python tools/data-sweeps/build-reincarnation.py <file>   (a saved copy of the wikitext)
"""
import io, json, re, sys, urllib.request, urllib.parse
from collections import OrderedDict

OUT = r"C:\Repos\EQL_Assistant\data\reincarnation.json"
PAGE = "Alternate_Advancement"
API = "https://eqlwiki.com/api.php?" + urllib.parse.urlencode({"action": "parse", "page": PAGE, "prop": "wikitext", "format": "json"})
UA = "EQL-Assistant-data-sweep/1.0"

CLASSES = OrderedDict([
    ("Warrior", "WAR"), ("Paladin", "PAL"), ("Shadow Knight", "SHD"), ("Monk", "MNK"), ("Rogue", "ROG"), ("Ranger", "RNG"),
    ("Bard", "BRD"), ("Beastlord", "BST"), ("Berserker", "BER"), ("Cleric", "CLR"), ("Druid", "DRU"), ("Shaman", "SHM"),
    ("Enchanter", "ENC"), ("Wizard", "WIZ"), ("Magician", "MAG"), ("Necromancer", "NEC"),
])
RACES = OrderedDict([
    ("Barbarian", "BAR"), ("Dark Elf", "DEF"), ("Dwarf", "DWF"), ("Erudite", "ERU"), ("Froglok", "FRG"), ("Gnome", "GNM"),
    ("Half Dark Elf", "HDE"), ("Half Elf", "HEF"), ("Halfling", "HFL"), ("High Elf", "HIE"), ("Human", "HUM"), ("Iksar", "IKS"),
    ("Kerran", "KRN"), ("Ogre", "OGR"), ("Troll", "TRL"), ("Wood Elf", "WEF"),
])

def tidy(s):
    s = re.sub(r"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", r"\1", s)   # [[Page|text]] -> text
    s = s.replace("\u2212", "-")                                # the wiki's minus sign
    return re.sub(r"\s+", " ", s).strip()

def wikitext():
    if len(sys.argv) > 1:
        return io.open(sys.argv[1], encoding="utf-8").read()
    req = urllib.request.Request(API, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))["parse"]["wikitext"]["*"]

text = wikitext()
start = text.index("== Special AAs ==")
table = text[start:]
m = re.search(r"CoolDown on (\d+)\s*sec", table)
shared = int(m.group(1)) if m else 0

entries, completionist = [], None
for line in table.splitlines():
    if not line.startswith("|") or line.startswith("|-") or line.startswith("|}"):
        continue
    cells = [tidy(c.lstrip("|")) for c in line[1:].split("||")]
    name = cells[0]
    if name.startswith("Reincarnation: Completionist") and len(cells) >= 5:
        completionist = OrderedDict([("ranks", cells[1:4]), ("passive", cells[4])])
        continue
    if not name.startswith("Class Reincarnation: ") or len(cells) < 5:
        continue
    who = name.split(": ", 1)[1]
    kind = "class" if who in CLASSES else "race" if who in RACES else None
    assert kind, "a reincarnation row this script does not know: " + who
    e = OrderedDict([("code", (CLASSES if kind == "class" else RACES)[who]), ("kind", kind), ("name", who),
                     ("ranks", cells[1:4]), ("passive", cells[4])])
    if len(cells) > 5 and cells[5]:
        active, _, what = cells[5].partition(": ")
        e["active"] = active
        e["activeText"] = what[:1].upper() + what[1:] if what else ""
    entries.append(e)

have = {e["name"] for e in entries}
missing = [n for n in list(CLASSES) + list(RACES) if n not in have]
assert not missing, "rows missing from the wiki table: " + ", ".join(missing)
assert all("active" in e for e in entries if e["kind"] == "class"), "a class without its activated ability"

order = list(CLASSES) + list(RACES)
entries.sort(key=lambda e: order.index(e["name"]))
doc = OrderedDict([
    ("source", "https://eqlwiki.com/" + PAGE),
    ("credit", "eqlwiki — Alternate Advancement, Special AAs"),
    ("notice", "From eqlwiki's Special AAs table; parts are datamined and unverified until Kunark is live."),
    ("sharedCooldownSec", shared),
    ("completionist", completionist),
    ("entries", entries),
])
io.open(OUT, "w", encoding="utf-8", newline="\n").write(json.dumps(doc, ensure_ascii=False, indent=2) + "\n")
print("wrote", OUT, "-", sum(e["kind"] == "class" for e in entries), "classes,", sum(e["kind"] == "race" for e in entries), "races, shared timer", shared, "s")
