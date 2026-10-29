"""Merge the 29 Oct 2026 Category:Focus_Effects sweep into data/focus-effects.json.

Rules kept from build-focus-json.ps1:
  * existing items (wiki Aug sweep + Companion) stay — the wiki dropping a
    Companion-only carrier is not evidence against it;
  * Minor Improved Damage I stays off the board (owner's call);
  * tier ORDER inside a family = level cap / the number in the name;
  * items keep the wiki's slot + classes when the effect page shows them,
    name-only otherwise (Wine Thief, Broken Minotaur Lord's Horn have no page).
Left out on purpose:
  * Heritage of Mistmoore — an exaltation-only regen/−MR effect (Mistmoore
    Heirloom Ring), not a tiered spell focus;
  * Lifebite — a weapon proc (Cherista's Fangs) the wiki filed under the category.
"""
import io, json
from collections import OrderedDict

P = r"C:\Repos\EQL_Assistant\data\focus-effects.json"
s = io.open(P, encoding="utf-8-sig").read()
d = json.loads(s, object_pairs_hook=OrderedDict)

def item(name, slot="", classes=""):
    return OrderedDict([("name", name), ("slot", slot), ("classes", classes)])

def tier(effect, description, cap, items):
    t = OrderedDict([("effect", effect), ("tier", 0), ("description", description), ("levelCap", cap), ("items", items)])
    return t

def fam(name):
    f = next((f for f in d["families"] if f["family"] == name), None)
    assert f is not None, name
    return f

def add_items(effect, new):
    for f in d["families"]:
        for t in f["tiers"]:
            if t["effect"] == effect:
                have = {i["name"].lower() for i in t["items"]}
                for it in new:
                    assert it["name"].lower() not in have, it["name"]
                    t["items"].append(it)
                return
    raise AssertionError("no tier " + effect)

def renumber(f):
    for i, t in enumerate(f["tiers"]):
        t["tier"] = i + 1

# 1. Brass Resonance 15 — Denon's Horn of Disaster (new zones).
f = fam("Brass Resonance")
assert [t["effect"] for t in f["tiers"]] == ["Brass Resonance 14"]
f["tiers"].append(tier("Brass Resonance 15", "Increases the power of brass instrument based bard songs by up to 140%.", None,
                       [item("Denon's Horn of Disaster", "PRIMARY SECONDARY", "ALL")]))
renumber(f)

# 2. Improved Damage III gains two carriers.
add_items("Improved Damage III", [
    item("Warstaff of Hope's Demise", "PRIMARY", "ROG SHM NEC WIZ MAG ENC"),
    item("Checkmate", "EAR", "WAR MNK ROG BER"),
])

# 3. Mana Preservation III — a new top tier.
f = fam("Mana Preservation")
assert [t["effect"] for t in f["tiers"]] == ["Mana Preservation I", "Mana Preservation II"]
f["tiers"].append(tier("Mana Preservation III", "Reduces the mana cost of spells by up to 10%. This bonus will decay on spells over level 60.", 60, [
    item("Signet Ring of Lord Everling", "FINGER", "SHD NEC WIZ MAG ENC"),
    item("The Unbreakable Word", "PRIMARY", "CLR PAL"),
]))
renumber(f)

# 4. Wind Resonance 8 and 11 slot in by their number: 8 < 10 < 11 < 14.
f = fam("Wind Resonance")
assert [t["effect"] for t in f["tiers"]] == ["Wind Resonance 10", "Wind Resonance 14"]
t10, t14 = f["tiers"]
f["tiers"] = [
    tier("Wind Resonance 8", "Increases the power of wind instrument based bard songs by up to 80%.", None, [item("Broken Minotaur Lord's Horn")]),
    t10,
    tier("Wind Resonance 11", "Increases the power of wind instrument based bard songs by up to 110%.", None, [item("Brahhms Horn", "SECONDARY", "BRD")]),
    t14,
]
renumber(f)

# 5. Improved Vampirism — a new family (lifetap damage); the wiki has no tier I.
assert not any(f["family"] == "Improved Vampirism" for f in d["families"])
iv = OrderedDict([
    ("family", "Improved Vampirism"), ("group", "spell"), ("kind", "lifetap damage"),
    ("tiers", [
        tier("Improved Vampirism II", "Increases the damage of lifetaps by up to 25%. This bonus will decay on spells over level 44.", 44, [item("Wine Thief")]),
        tier("Improved Vampirism III", "Increases the damage of lifetaps by up to 25%. This bonus will decay on spells over level 55.", 55, [
            item("Bloodmoon", "PRIMARY", "SHD"),
            item("Dark Ironbound Tome", "PRIMARY", "SHD NEC"),
        ]),
    ]),
])
renumber(iv)
# keep the families alphabetical, as the original build wrote them
idx = next(i for i, f in enumerate(d["families"]) if f["family"] == "Jedah's Conservation")
d["families"].insert(idx, iv)

d["source"] = ("eqlwiki.com Category:Focus_Effects pages (fetched 2026-08-17, new-zone gear re-swept 2026-10-29) "
               "+ jmoyers/everquest-companion items.json (MIT, (c) Josh Moyers)")

out = json.dumps(d, indent=2, ensure_ascii=False)
io.open(P, "w", encoding="utf-8-sig", newline="\n").write(out + "\n")
n = sum(len(t["items"]) for f in d["families"] for t in f["tiers"])
print("families", len(d["families"]), "tiers", sum(len(f["tiers"]) for f in d["families"]), "items", n)
