"""Build data/epic-quests.json — the Epics pack of the Quests window.

    python -I -X utf8 tools/data-sweeps/build-epics.py

The steps are HAND-TRANSCRIBED from eqlwiki's "<Class> Epic Quest" pages (their Checklist
sections, the long walkthrough where the checklist names nothing). Those pages sit under the
wiki's "Epics Era" banner: classic-era write-ups, unverified in EQ Legends until Kunark is live
(8 Dec 2026). Rules of the transcription:

  * a name goes into a MATCHED list (kill / killAny / loot / handin / make / say) only when the
    wiki spells it — a guessed name silently never fires. What the page leaves unnamed ("the
    combined sack", "his ring") stays out of those lists: the step is then proven by the trade
    line alone (trade=True, a started quest only) or ticked by hand;
  * a quest's PARTS run side by side, so a proven step vouches only for its own part;
  * OPTIONAL steps are never counted and never implied.

Reward stats are read from data/item-stats.json, so they follow the item table's refresh.
Re-run after editing; the app embeds the JSON.
"""
import io, json, os, re
from collections import OrderedDict

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
ITEMS = json.load(io.open(os.path.join(ROOT, "data", "item-stats.json"), encoding="utf-8-sig"))["items"]

def item_key(n): return n.replace("'", "").replace("`", "").lower()

def reward_stats(name):
    """The item table's record as the stats text the quest view reads (Skill … Atk Delay / DMG lines)."""
    r = ITEMS.get(item_key(name))
    assert r, "reward not in the item table: " + name
    L = []
    if r.get("flags"): L.append(r["flags"].replace(" · ", " ").upper())
    if r.get("slot"): L.append("Slot: " + r["slot"])
    if r.get("skill"): L.append(f"Skill: {r['skill']} Atk Delay: {r.get('delay', 0)}")
    if r.get("dmg") is not None: L.append(f"DMG: {r['dmg']}")
    if r.get("ac") is not None: L.append(f"AC: {r['ac']}")
    if r.get("stats"): L.append(" ".join(f"{k}: {v}" for k, v in r["stats"]))
    if r.get("saves"): L.append(" ".join(f"{k}: {v}" for k, v in r["saves"]))
    for e in (r.get("effects") or "").split("\n"):
        if e: L.append("Effect: " + e.split(": ", 1)[1])
    if r.get("weight"): L.append(f"WT: {r['weight']} Size: {r.get('size', '')}".strip())
    if r.get("classes"): L.append("Class: " + r["classes"])
    if r.get("races"): L.append("Race: " + r["races"])
    return "\n".join(L)

# ---- the step DSL ------------------------------------------------------------------------
_part = {"name": "", "optional": False}
_seen = set()

def part(name, optional=False):
    _part["name"], _part["optional"] = name, optional

def step(id, title, kind, npc="", zone="", **kw):
    assert id not in _seen, "duplicate step id " + id
    _seen.add(id)
    s = OrderedDict(id=id, title=title, kind=kind, npc=npc, zone=zone)
    if _part["name"]: s["part"] = _part["name"]
    if kw.pop("optional", _part["optional"]): s["optional"] = True
    for k in ("npcs", "kill", "killAny", "loot", "get", "make"):
        v = kw.pop(k, None)
        if v: s[k] = list(v)
    for k in ("handin", "bring"):
        v = kw.pop(k, None)
        if v: s[k] = [{"name": n, "count": c} for n, c in (x if isinstance(x, tuple) else (x, 1) for x in v)]
    for k in ("coins", "say", "reply", "faction", "note", "caution"):
        v = kw.pop(k, None)
        if v: s[k] = v
    if kw.pop("trade", False): s["trade"] = True
    assert not kw, f"unknown fields on {id}: {kw}"
    return s

def hail(id, npc, zone, **kw):   return step(id, f"Hail {npc}", "say", npc, zone, say=f"Hail, {npc}", **kw)
def say(id, title, npc, zone, words, **kw): return step(id, title, "say", npc, zone, say=words, **kw)
def give(id, title, npc, zone, items, **kw): return step(id, title, "handin", npc, zone, handin=items, **kw)
def kill(id, title, mobs, zone, loot=(), **kw): return step(id, title, "kill", mobs[0], zone, kill=mobs, loot=loot, **kw)
def kill_any(id, title, mobs, zone, loot=(), **kw): return step(id, title, "kill", "", zone, killAny=mobs, loot=loot, **kw)
def combine(id, title, where, items, makes=(), **kw): return step(id, title, "combine", "", where, bring=items, make=makes, **kw)
def by_hand(id, title, kind, npc="", zone="", **kw): return step(id, title, kind, npc, zone, **kw)   # buy · forage · ground · pickpocket · talk
def unnamed(id, title, npc, zone, **kw): return step(id, title, "handin", npc, zone, trade=True, **kw)

def quest(key, name, cls, reward, level, start, wiki, steps, warning="", note="", parallel=False):
    _seen.clear(); part("")
    q = OrderedDict(key=key, name=name, pack="epics", reward=reward, rewardStats=reward_stats(reward), classes=[cls],
                    minLevel=level, start=start, wiki="https://eqlwiki.com/" + wiki.replace(" ", "_"))
    if warning: q["warning"] = warning
    if note: q["note"] = note
    if parallel: q["parallel"] = True   # two rewards: no step vouches for the whole quest
    q["steps"] = steps()
    assert all(s["title"] for s in q["steps"])
    return q

HIDE = "Con indifferent first — put the items in the window, hide, then check the con."

# ---- Shadow Knight ---------------------------------------------------------------------
def shd():
    S = []
    part("The Letter to Duriek", optional=True)
    S += [hail("kurron-hail", "Kurron Ni", "The Overthere, the outpost"),
          give("kurron-armor", "Hand Kurron Ni the three Darkforge pieces and 900 platinum", "Kurron Ni", "The Overthere, the outpost",
               ["Darkforge Breastplate", "Darkforge Greaves", "Darkforge Helm"], coins="900 platinum",
               note="The armor comes from the Darkforge Armor quests."),
          kill("kurron-kill", "Kill Kurron Ni for the Letter to Duriek", ["Kurron Ni"], "The Overthere", ["Letter to Duriek"])]
    part("The Dusty Tome", optional=True)
    S += [give("duriek-letter", "Hand Duriek Bloodpool the letter", "Duriek Bloodpool", "Paineel", ["Letter to Duriek"], caution=HIDE),
          hail("smaka-hail", "Smaka", "Neriak Foreign Quarter"),
          step("smaka-elixir", "Pay Smaka 1000 platinum for a Cough Elixir", "handin", "Smaka", "Neriak Foreign Quarter",
               coins="1000 platinum", get=["Cough Elixir"]),
          give("duriek-elixir", "Hand Duriek the Cough Elixir", "Duriek Bloodpool", "Paineel", ["Cough Elixir"], caution=HIDE),
          kill("tome-kill", "Kill a ratman guard for a Dusty Tome", ["a ratman guard"], "The Hole", ["Dusty Tome"]),
          give("duriek-tome", "Hand Duriek the Dusty Tome", "Duriek Bloodpool", "Paineel", ["Dusty Tome"], caution=HIDE)]
    part("The Corrupted Ghoulbane")
    S += [kill("ghoulbane", "Kill the froglok shin lord for the Ghoulbane", ["The froglok shin lord"], "Ruins of Upper Guk", ["Ghoulbane"]),
          kill_any("soul-leech", "Kill a Fear golem for the Soul Leech, Dark Sword of Blood", ["Fright", "Dread", "Terror"], "Plane of Fear",
                   ["Soul Leech, Dark Sword of Blood"], note="Cazic-Thule carried it before the Fear revamp."),
          step("abrogation", "Loot a Blade of Abrogation in the Plane of Sky", "loot", "", "Plane of Sky", loot=["Blade of Abrogation"]),
          kill("drake-spine", "Kill Rharzar for a Drake Spine", ["Rharzar"], "Rathe Mountains", ["Drake Spine"]),
          kill("hide", "Kill an ashenbone drake for a Decrepit Hide", ["An ashenbone drake"], "Plane of Hate", ["Decrepit Hide"]),
          by_hand("bar", "Buy a Platinum Bar and have an enchanter enchant it", "buy", "Any jeweller", "an enchanter makes the bar",
                  get=["Enchanted Platinum Bar"]),
          give("sheath", "Hand Teydar the spine, the hide and the bar", "Teydar", "Qeynos Aqueducts, the evil guild area",
               ["Drake Spine", "Decrepit Hide", "Enchanted Platinum Bar"], get=["Decrepit Sheath"], caution=HIDE),
          give("corrupted", "Hand Duriek the three swords and the sheath", "Duriek Bloodpool", "Paineel",
               ["Ghoulbane", "Soul Leech, Dark Sword of Blood", "Blade of Abrogation", "Decrepit Sheath"], get=["Corrupted Ghoulbane"], caution=HIDE)]
    part("The Dark Shroud")
    S += [hail("lhranc-hail", "Lhranc", "City of Mist", optional=True),
          say("marl-seal", "Ask Marl Kastane about the prophecy for a Seal of Kastane", "Marl Kastane", "Kerra Isle", "What prophecy?",
              get=["Seal of Kastane"], note="Say \"I have the will of Innoruuk\" first.", optional=True),
          give("gerot-seal", "Hand Gerot Kastane the Seal of Kastane", "Gerot Kastane", "Paineel", ["Seal of Kastane"], get=["Note to Marl"], optional=True),
          give("marl-note", "Hand Marl Kastane the Note to Marl", "Marl Kastane", "Kerra Isle", ["Note to Marl"], optional=True),
          say("soulcase", "Ask Knarthenne Skurl about the heart of an innocent for a Soulcase", "Knarthenne Skurl", "Toxxulia Forest",
              "What heart of an innocent?", get=["Soulcase"]),
          kill("cell-key", "Kill a mimic for a Cell Key", ["a mimic"], "The Hole, the city bank", ["Cell Key"], note="It looks like a chest."),
          give("caradon", "Hand Caradon the Cell Key", "Caradon", "The Hole", ["Cell Key"], caution=HIDE, note="This spawns Kyrenna."),
          kill("kyrenna", "Kill Kyrenna for her blood and her heart", ["Kyrenna"], "The Hole", ["Blood of Kyrenna", "Heart of Kyrenna"],
               note="Kill Kyrenna, not Caradon, if it can be avoided."),
          give("shroud", "Hand Marl Kastane the Blood of Kyrenna", "Marl Kastane", "Kerra Isle", ["Blood of Kyrenna"], get=["Dark Shroud"],
               caution="Con indifferent first — hide, and never feign death on this hand-in.")]
    part("Lhranc's Coin")
    S += [give("glohnor", "Hand the Ghost of Glohnor the Dark Shroud", "Ghost of Glohnor", "The Hole", ["Dark Shroud"],
               caution="Con indifferent with invisibility to undead before you trade."),
          kill("mummy", "Kill the Mummy of Glohnor for his head and wrappings", ["Mummy of Glohnor"], "The Hole", ["Head of Glohnor", "Glohnor wrappings"]),
          give("valiant", "Hand Gerot Kastane the Head of Glohnor", "Gerot Kastane", "Paineel", ["Head of Glohnor"], get=["Head of the Valiant"], caution=HIDE),
          give("will", "Hand Marl Kastane the Glohnor wrappings", "Marl Kastane", "Kerra Isle", ["Glohnor wrappings"], get=["Will of Innoruuk"], caution=HIDE),
          combine("heart", "Combine the Heart of Kyrenna in the Soulcase", "In the Soulcase", ["Heart of Kyrenna", "Soulcase"], ["Heart of the Innocent"]),
          give("lhranc", "Hand Lhranc the Corrupted Ghoulbane, the heart, the head and the will", "Lhranc", "City of Mist",
               ["Corrupted Ghoulbane", "Heart of the Innocent", "Head of the Valiant", "Will of Innoruuk"],
               caution="Con indifferent first — have someone cast invisibility to undead on you. He returns with the sword: bring at least two groups."),
          kill("curse", "Kill Lhranc and loot Innoruuk's Curse", ["Lhranc"], "City of Mist", ["Innoruuk's Curse"],
               note="If you wipe, Lhranc does not despawn.")]
    return S

# ---- Enchanter -------------------------------------------------------------------------
def enc():
    S = []
    SACK = "Any master's Enchanters Sack does the combine."
    part("Jeb's Seal")
    S += [hail("stofo", "Stofo Olan", "Erudin", note="You must be level 50."),
          by_hand("vial", "Get an Empty Ink Vial from Reania Jukle", "talk", "Reania Jukle", "Qeynos Catacombs", get=["Empty Ink Vial"]),
          give("ink", "Charm a ghoul scribe and hand him the Empty Ink Vial", "a ghoul scribe", "Lower Guk", ["Empty Ink Vial"], get=["Ink of the Dark"]),
          kill("robes", "Kill the ghoul arch magus for the Shining Metallic Robes", ["the ghoul arch magus"], "Lower Guk", ["Shining Metallic Robes"]),
          give("pen", "Hand Rilgor Plegnog the Shining Metallic Robes", "Rilgor Plegnog", "Ak'Anon", ["Shining Metallic Robes"], get=["Mechanical Pen"]),
          by_hand("quill", "Buy a Quill and a Piece of Parchment", "buy", "Any merchant", "", get=["Quill", "Piece of Parchment"]),
          give("chrislin", "Hand Chrislin Baker the Quill and the Piece of Parchment", "Chrislin Baker", "Western Karana",
               ["Quill", "Piece of Parchment"], note="This spawns Thrackin Griften."),
          kill("thrackin", "Kill Thrackin Griften for a White Paper", ["Thrackin Griften"], "Western Karana", ["White Paper"]),
          give("notes", "Hand Stofo Olan the ink, the pen and the paper", "Stofo Olan", "Erudin",
               ["Ink of the Dark", "Mechanical Pen", "White Paper"], get=["Copy of Notes"]),
          give("seal", "Hand Jeb Lumsed the Copy of Notes", "a sarnak imitator", "Burning Woods", ["Copy of Notes"], get=["Jeb's Seal"],
               npcs=["a sarnak imitator", "Jeb Lumsed"], note="Jeb Lumsed stands there as a sarnak imitator.")]
    part("1st Piece of Staff — Test of Illusion")
    S += [kill("rod", "Kill Vessel Drozlin for the Xolion Rod", ["Vessel Drozlin"], "Cabilis East", ["Xolion Rod"]),
          kill("word", "Kill Verina Tomb for Innoruuk's Word", ["Verina Tomb"], "Neriak", ["Innoruuk's Word"]),
          kill("prince", "Kill Prince Selrach Di'zok for the Head of a Prince", ["Prince Selrach Di'zok"], "Chardok", ["Head of a Prince"]),
          give("chalice", "Hand Joren Nobleheart the Head of a Prince", "Joren Nobleheart", "Felwithe", ["Head of a Prince"], get=["Chalice of Kings"]),
          by_hand("sandals", "Pick up the Large Muddy Sandals", "ground", "", "Oggok", get=["Large Muddy Sandals"]),
          give("parchment", "Hand Bozlum Blossom the Large Muddy Sandals", "Bozlum Blossom", "Oggok", ["Large Muddy Sandals"], get=["Scribbled Parchment"]),
          give("gift", "Hand Brokk Boxtripper the Scribbled Parchment", "Brokk Boxtripper", "Oggok", ["Scribbled Parchment"], get=["Gift to Bozlum"]),
          give("blossoms", "Hand Bozlum Blossom the Gift to Bozlum", "Bozlum Blossom", "Oggok", ["Gift to Bozlum"], get=["Snow Blossoms"]),
          combine("sack1", "Combine the chalice, the rod, the blossoms and the word in an Enchanters Sack", "In an Enchanters Sack",
                  ["Chalice of Kings", "Xolion Rod", "Snow Blossoms", "Innoruuk's Word"], note=SACK),
          unnamed("piece1", "Hand Modani Qu`Loni the combined sack", "Modani Qu`Loni", "The Overthere", get=["1st Piece of Staff"])]
    part("2nd Piece of Staff — Test of Enlightenment")
    S += [kill("spoon", "Kill Cazel for a Spoon", ["Cazel"], "Oasis of Marr", ["Spoon"]),
          by_hand("key", "Pick up The One Key", "ground", "", "The Overthere", get=["The One Key"]),
          by_hand("scroll", "Pick up the Lost Scroll", "ground", "", "Dalnir", get=["Lost Scroll"]),
          by_hand("book", "Pick up the book Charm and Sacrifice", "ground", "", "Plane of Sky", get=["Charm and Sacrifice"]),
          combine("sack2", "Combine the spoon, the key, the scroll and the book in an Enchanters Sack", "In an Enchanters Sack",
                  ["Spoon", "The One Key", "Lost Scroll", "Charm and Sacrifice"], note=SACK),
          unnamed("piece2", "Hand Mizzle Gepple the combined sack", "Clockwork VIIX", "Ak'Anon", get=["2nd Piece of Staff"],
                  npcs=["Clockwork VIIX", "Mizzle Gepple"], note="Mizzle Gepple stands there as Clockwork VIIX.")]
    part("3rd Piece of Staff — Test of Charm")
    S += [by_hand("gems", "Get the four dull gems from Nadia Starfeast", "talk", "Nadia Starfeast", "Firiona Vie",
                  get=["Dull Diamond", "Dull Sapphire", "Dull Ruby", "Dull Emerald"]),
          give("diamond", "Charm a Spectral librarian and hand him the Dull Diamond", "Spectral librarian", "Kaesora", ["Dull Diamond"],
               get=["Enchanted Diamond"], caution="An enraged spectral librarian spawns on the hand-in — the wiki says not to kill it."),
          give("sapphire", "Charm Felia Goldenwing and hand her the Dull Sapphire", "Felia Goldenwing", "Skyfire Mountains", ["Dull Sapphire"], get=["Enchanted Sapphire"]),
          give("ruby", "Charm the Wraith of Jaxion and hand him the Dull Ruby", "Wraith of Jaxion", "City of Mist", ["Dull Ruby"], get=["Enchanted Ruby"]),
          give("emerald", "Charm Impaler Tzilug and hand him the Dull Emerald", "Impaler Tzilug", "The Overthere", ["Dull Emerald"], get=["Enchanted Emerald"]),
          combine("sack3", "Combine the four enchanted gems in an Enchanters Sack", "In an Enchanters Sack",
                  ["Enchanted Diamond", "Enchanted Sapphire", "Enchanted Ruby", "Enchanted Emerald"], note=SACK),
          unnamed("piece3", "Hand Nadia Starfeast the combined sack", "Nadia Starfeast", "Firiona Vie", get=["3rd Piece of Staff"])]
    part("4th Piece of Staff — Test of the Phantasm")
    S += [kill("serpent", "Kill the Wraith of a Shissir for the Head of the Serpent", ["Wraith of a Shissir"], "Plane of Fear", ["Head of the Serpent"]),
          kill("ghost", "Kill the Ghost of Kindle for the Essence of a Ghost", ["the Ghost of Kindle"], "The Hole", ["Essence of a Ghost"]),
          kill("vampire", "Kill a forsaken revenant for the Essence of a Vampire", ["a forsaken revenant"], "Plane of Hate", ["Essence of a Vampire"]),
          kill("sands", "Kill The Tangrin for the Sands of the Mystics", ["The Tangrin"], "Field of Bone", ["Sands of the Mystics"]),
          combine("sack4", "Combine the head, the two essences and the sands in an Enchanters Sack", "In an Enchanters Sack",
                  ["Head of the Serpent", "Essence of a Ghost", "Essence of a Vampire", "Sands of the Mystics"], note=SACK),
          unnamed("piece4", "Hand Polzin Mrid the combined sack", "Polzin Mrid", "The Hole", get=["4th Piece of Staff"],
                  note="In The Hole an elemental illusion plus Collaboration reads dubious to most of the zone.")]
    part("Staff of the Serpent")
    S += [combine("bundle", "Combine the four pieces of staff in an Enchanters Sack", "In an Enchanters Sack",
                  ["1st Piece of Staff", "2nd Piece of Staff", "3rd Piece of Staff", "4th Piece of Staff"], ["Bundle of Staves"]),
          give("staff", "Hand Jeb Lumsed the Bundle of Staves", "a sarnak imitator", "Burning Woods", ["Bundle of Staves"], get=["Staff of the Serpent"],
               npcs=["a sarnak imitator", "Jeb Lumsed"])]
    return S

# ---- Shaman ----------------------------------------------------------------------------
def shm():
    S = []
    part("Beginning the Quest")
    S += [kill_any("spirit", "Kill one of the four to call a lesser spirit",
                   ["Capt Surestout", "Blinde the Cutpurse", "Peg Leg", "an iksar manslayer"],
                   "Ocean of Tears · Rathe Mountains · Butcherblock Mountains · Field of Bone",
                   note="A lesser spirit appears where it dies. Do not kill the spirit."),
          say("tiny-gem", "Tell the lesser spirit you wish to know more", "a lesser spirit", "where the mob died", "I wish to know more",
              get=["Tiny Gem"])]
    part("The Drunken Shaman")
    S += [give("bondl", "Hand Bondl Felligan the Tiny Gem", "Bondl Felligan", "North Freeport, the sewers", ["Tiny Gem"],
               note="He leads you to the Jade Tiger, where the greater spirits appear."),
          say("opaque", "Question the greater spirit of your god until it gives the Opaque Gem", "a greater spirit", "North Freeport, the Jade Tiger",
              "What Heyokah?", get=["Opaque Gem"],
              caution="Say \"I will walk the path of the Heyokah\" only once you hold the gem — the spirit leaves at once.")]
    part("The Test of Patience")
    S += [give("ooglyn", "Hand Ooglyn the Opaque Gem", "Ooglyn", "Erud's Crossing, the island", ["Opaque Gem"], note="Her spawn takes two hours."),
          say("small-gem", "Hear out the drowned, then ask the greater spirit for the next path", "a greater spirit", "Erud's Crossing, under the water",
              "What is the next path?", get=["Small Gem"], note="Only one shaman can take the test at a time.")]
    part("The Test of Wisdom")
    S += [give("wandering", "Hand a wandering spirit the Small Gem", "a wandering spirit", "West Karana", ["Small Gem"],
               note="It looks like a brighter wisp and roams between Qeynos Hills and the bandit camp."),
          kill("twins", "Kill Glaron the Wicked and Tabien the Goodly", ["Glaron the Wicked", "Tabien the Goodly"], "Rathe Mountains",
               ["Envy", "Woe", "Marr's Promise"]),
          give("falsehood", "Hand the wandering spirit Envy, Woe and Marr's Promise", "a wandering spirit", "West Karana",
               ["Envy", "Woe", "Marr's Promise"], get=["Shield of Falsehood", "Sparkling Gem"],
               note="Ask \"What shield of falsehood?\" and \"What last path?\".")]
    part("The Test of Might")
    S += [give("sentinel", "Hand the Spirit Sentinel the Sparkling Gem", "Spirit Sentinel", "Emerald Jungle", ["Sparkling Gem"],
               faction="True Spirit ≥ Amiable (reported)"),
          kill("dire", "Kill Black Dire for his pelt", ["Black Dire"], "Castle Mistmoore", ["Black Dire Pelt"],
               note="Killing an advisor in the library calls him."),
          give("boots", "Hand the Spirit Sentinel the Black Dire Pelt", "Spirit Sentinel", "Emerald Jungle", ["Black Dire Pelt"],
               get=["Black Fur Boots"], note="He also hands you a six-slot booklet.")]
    part("The City of Mist Reports")
    S += [step("reports", "Loot the six reports in the City of Mist", "loot", "", "City of Mist",
               loot=["Personal Diary Page", "Crier's Scroll", "Merchant's Letter", "Written Announcement", "Priest's Diary Page", "Student's Log"],
               note="The Student's Log drops only from a spectral courier."),
          combine("booklet", "Combine the six reports in the booklet", "In the booklet",
                  ["Personal Diary Page", "Crier's Scroll", "Merchant's Letter", "Written Announcement", "Priest's Diary Page", "Student's Log"]),
          unnamed("report", "Hand the Spirit Sentinel the completed report", "Spirit Sentinel", "Emerald Jungle", faction="True Spirit ≥ Kindly")]
    part("The Three Books of Lord Ghiosk")
    S += [kill("ghiosk", "Kill Lord Ghiosk and loot his three books", ["Lord Ghiosk"], "City of Mist"),
          unnamed("books", "Hand the three books to the Spirit Sentinel in the pond", "Spirit Sentinel", "Emerald Jungle, the pond",
                  caution="Not the Spirit Sentinel near the City of Mist — the one in the pond."),
          by_hand("icon", "Pick up the Icon of the High Scale", "ground", "", "City of Mist, the temple", get=["Icon of the High Scale"]),
          give("kirn-icon", "Hand High Scale Kirn the icon", "High Scale Kirn", "The Hole", ["Icon of the High Scale"]),
          kill("kirn", "Kill High Scale Kirn and loot his ring", ["High Scale Kirn"], "The Hole"),
          unnamed("neh-ring", "Hand Neh`Ashiir the ring", "Neh`Ashiir", "City of Mist"),
          kill("neh", "Kill Neh`Ashiir and loot her diary", ["Neh`Ashiir"], "City of Mist"),
          unnamed("diary", "Hand the Spirit Sentinel the diary", "Spirit Sentinel", "Emerald Jungle"),
          kill("tear", "Kill the Iksar Broodling for a Child's Tear", ["Iksar Broodling"], "Plane of Fear", ["Child's Tear"],
               note="It appears at random when a golem dies — Dread, Fright or Terror."),
          give("rak", "Hand Lord Rak`Ashiir the Child's Tear", "Lord Rak`Ashiir", "City of Mist", ["Child's Tear"],
               faction="True Spirit ally", note="Ally is enough here; it need not be maxed."),
          kill("scale", "Kill Lord Rak`Ashiir for his Iksar Scale", ["Lord Rak`Ashiir"], "City of Mist", ["Iksar Scale"]),
          give("spear", "Hand the Spirit Sentinel in the pond the Iksar Scale", "Spirit Sentinel", "Emerald Jungle, the pond", ["Iksar Scale"],
               get=["Spear of Fate"],
               caution="MAX ally, not just ally, or the scale is eaten — wait for \"Your faction with True Spirit can not get any better\".")]
    return S

# ---- Druid -----------------------------------------------------------------------------
def dru():
    S = []
    part("A Shiny Tin Bowl")
    S += [say("telin", "Tell Telin Darkforest you will take action", "Telin Darkforest", "Burning Woods", "I will take action", get=["Worn note"]),
          give("faelin", "Hand Faelin Bloodbriar the Worn note", "Faelin Bloodbriar", "Greater Faydark", ["Worn note"], get=["Faelin`s Ring"],
               note="She has to be spawned."),
          give("giz", "Hand Giz X`Tin Faelin`s Ring", "Giz X`Tin", "Kithicor Forest", ["Faelin`s Ring"], get=["Dark Metal Coin"]),
          give("telin-coin", "Hand Telin Darkforest the Dark Metal Coin", "Telin Darkforest", "Burning Woods", ["Dark Metal Coin"], get=["Worn Dark Metal Coin"]),
          give("althele", "Hand Althele the Worn Dark Metal Coin", "Althele", "East Karana", ["Worn Dark Metal Coin"], get=["Braided Grass Amulet"]),
          give("sionae", "Show Sionae the Braided Grass Amulet", "Sionae", "East Karana, -2300, -930", ["Braided Grass Amulet"], note="The amulet comes back."),
          give("nuien", "Show Nuien the Braided Grass Amulet", "Nuien", "East Karana, -3650, 300", ["Braided Grass Amulet"], note="The amulet comes back."),
          give("teloa", "Show Teloa the Braided Grass Amulet", "Teloa", "East Karana, -3800, -2860", ["Braided Grass Amulet"],
               caution="This spawns the Dark Elf Corruptor and two Dark Elf Reavers at -700, -1450. They run fast — be ready to intercept them."),
          kill("corruptor", "Kill the Dark Elf Corruptor for the Fleshbound Tome", ["Dark Elf Corruptor"], "East Karana", ["Fleshbound Tome"]),
          give("althele-tome", "Hand Althele the Fleshbound Tome", "Althele", "East Karana", ["Fleshbound Tome"], get=["Earth Stained Note"]),
          give("ella-note", "Hand Ella Foodcrafter the Earth Stained Note", "Ella Foodcrafter", "Misty Thicket", ["Earth Stained Note"], get=["Shiny Tin Bowl"])]
    part("The Hardened Mixture")
    S += [by_hand("forage", "Forage the four ingredients", "forage", "", "Everfrost · Greater Faydark · Innothule Swamp · Misty Thicket",
                  get=["Chilled Tundra Root", "Ripened Heartfruit", "Speckled Molded Mushroom", "Sweetened Mudroot"],
                  note="One from each zone, in that order."),
          combine("mixture", "Combine the four ingredients in the Shiny Tin Bowl", "In the Shiny Tin Bowl",
                  ["Chilled Tundra Root", "Ripened Heartfruit", "Speckled Molded Mushroom", "Sweetened Mudroot"], ["Hardened Mixture"])]
    part("The Runecrested Bowl")
    S += [say("alrik", "Tell Alrik Farsight you will take the artifact", "Alrik Farsight", "Timorous Deep", "I will take the artifact", get=["Crushed Pot"]),
          give("farios", "Hand Farios Elianos the Crushed Pot", "Farios Elianos", "Southern Felwithe", ["Crushed Pot"], get=["Grocery List"]),
          give("nora", "Hand Merchant Nora the Grocery List", "Merchant Nora", "Northern Felwithe", ["Grocery List"], get=["Bag of Provisions"]),
          give("farios-bag", "Hand Farios Elianos the Bag of Provisions", "Farios Elianos", "Southern Felwithe", ["Bag of Provisions"], get=["Receipt"]),
          give("pattern", "Hand Alrik Farsight the Receipt", "Alrik Farsight", "Timorous Deep", ["Receipt"], get=["Ancient Pattern"]),
          by_hand("rose", "Forage a Rose of Firiona", "forage", "", "Firiona Vie", get=["Rose of Firiona"]),
          give("merdan", "Hand Merdan Fleetfoot the Rose of Firiona", "Merdan Fleetfoot", "Surefall Glade", ["Rose of Firiona"], get=["Wood Painting"],
               note="Ask him \"Who is Niera?\" first."),
          give("skeleton", "Hand a human skeleton the Wood Painting", "a human skeleton", "Frontier Mountains, behind the giant fort", ["Wood Painting"],
               get=["Silver Chained Locket"]),
          give("niera", "Hand Niera Farbreeze the Silver Chained Locket", "Niera Farbreeze", "Surefall Glade", ["Silver Chained Locket"],
               get=["Platinum Speckled Powder"]),
          kill("reaver", "Kill a black reaver for a Jade Reaver", ["a black reaver"], "City of Mist", ["Jade Reaver"]),
          give("kinlo", "Hand Kinlo Strongarm the Jade Reaver", "Kinlo Strongarm", "North Kaladim", ["Jade Reaver"], get=["Enchanted Clay"]),
          combine("bowl", "Combine the pattern, the powder and the clay on a Pottery Wheel", "On a Pottery Wheel",
                  ["Ancient Pattern", "Platinum Speckled Powder", "Enchanted Clay"], ["Runecrested Bowl"])]
    part("The Elaborate Scimitar")
    S += [give("stone", "Hand Ella Foodcrafter the mixture and the bowl", "Ella Foodcrafter", "Misty Thicket", ["Hardened Mixture", "Runecrested Bowl"],
               get=["Softly Glowing Stone"]),
          by_hand("globe", "Cast a Summoned: Firefly Globe", "cast", "", "anywhere, at night", get=["Summoned: Firefly Globe"],
                  note="A level 1 druid spell, castable only at night (7pm to 4am); the globe does not last."),
          by_hand("res", "Buy a scroll of Resurrection", "buy", "A cleric spell vendor", "", get=["Spell: Resurrection"], note="The level 49 cleric spell."),
          give("remains", "Hand Venril Sathir Remains the Firefly Globe", "Venril Sathir Remains", "Karnor's Castle", ["Summoned: Firefly Globe"],
               note="This spawns the Spirit of Venril Sathir."),
          give("vs-spirit", "Hand the Spirit of Venril Sathir the Resurrection scroll", "Spirit of Venril Sathir", "Karnor's Castle", ["Spell: Resurrection"],
               note="This spawns Venril Sathir."),
          kill("vs", "Kill Venril Sathir for a Pulsing Green Stone", ["Venril Sathir"], "Karnor's Castle", ["Pulsing Green Stone"]),
          give("foloal", "Hand Foloal Stormforest the two stones", "Foloal Stormforest", "Firiona Vie", ["Pulsing Green Stone", "Softly Glowing Stone"],
               get=["Warmly Glowing Stone"]),
          give("scimitar", "Hand Ella Foodcrafter the Warmly Glowing Stone", "Ella Foodcrafter", "Misty Thicket", ["Warmly Glowing Stone"], get=["Elaborate Scimitar"])]
    part("The Cleansed Spirits")
    S += [kill("mammoth", "Kill a corrupted wooly mammoth for a Chunk of Tundra", ["corrupted wooly mammoth"], "Everfrost Peaks", ["Chunk of Tundra"]),
          kill("c-shaman", "Kill a corrupted shaman for Clean Lakewater", ["corrupted shaman"], "Lake Rathetear", ["Clean Lakewater"]),
          kill("c-giant", "Kill a corrupted hill giant for an Ancient Rock", ["corrupted hill giant"], "Rathe Mountains", ["Ancient Rock"]),
          give("treant", "Hand the Withered treant the tundra, the water and the rock", "Withered treant", "Northern Karana",
               ["Chunk of Tundra", "Clean Lakewater", "Ancient Rock"], get=["Warm Pulsing Treant Heart"]),
          give("yeka", "Hand Yeka Ias the Warm Pulsing Treant Heart", "Yeka Ias", "Southern Karana", ["Warm Pulsing Treant Heart"], get=["Cleansed Spirit of Antonica"]),
          kill("seahorse", "Kill the Corrupted Seahorse for Kedge Cave Crystals", ["Corrupted Seahorse"], "Kedge Keep", ["Kedge Cave Crystals"]),
          kill("cyclops", "Kill the Corrupted seafury cyclops for Ocean of Tears Seavines", ["Corrupted seafury cyclops"], "Ocean of Tears", ["Ocean of Tears Seavines"]),
          kill("brownie", "Kill the Corrupted brownie for a Green Heartwood Branch", ["Corrupted brownie"], "Lesser Faydark", ["Green Heartwood Branch"]),
          give("unicorn", "Hand the Pained Unicorn the crystals, the seavines and the branch", "Pained Unicorn", "Lesser Faydark",
               ["Kedge Cave Crystals", "Ocean of Tears Seavines", "Green Heartwood Branch"], get=["Gleaming Unicorn Horn"]),
          give("silox", "Hand Silox Azrix the Gleaming Unicorn Horn", "Silox Azrix", "Ak'Anon", ["Gleaming Unicorn Horn"], get=["Cleansed Spirit of Faydwer"]),
          kill("ulump", "Kill Ulump Pujluk for Froglok Essence", ["Ulump Pujluk"], "Swamp of No Hope", ["Froglok Essence"]),
          kill("gorilla", "Kill the Corrupted Gorilla for Green Tree Bark", ["Corrupted Gorilla"], "Emerald Jungle", ["Green Tree Bark"]),
          kill("barracuda", "Kill the Corrupted barracuda for Pure Lakewater", ["Corrupted barracuda"], "Lake of Ill Omen", ["Pure Lakewater"]),
          give("dolgin", "Show Dolgin Codslayer the Froglok Essence", "Dolgin Codslayer", "Timorous Deep", ["Froglok Essence"],
               note="He hands it back and leaves (a 14-hour respawn); this spawns Faydedar."),
          kill("faydedar", "Kill Faydedar for a Pod of Seawater", ["Faydedar"], "Timorous Deep", ["Pod of Seawater"]),
          give("nekexin", "Hand Nekexin Virulence the essence, the pod, the bark and the lakewater", "Nekexin Virulence", "The Overthere",
               ["Froglok Essence", "Pod of Seawater", "Green Tree Bark", "Pure Lakewater"], get=["Cleansed Spirit of Kunark"])]
    part("Nature Walkers Scimitar")
    S += [give("xanuusus", "Hand Xanuusus the Elaborate Scimitar and the three cleansed spirits", "Xanuusus", "North Karana",
               ["Elaborate Scimitar", "Cleansed Spirit of Antonica", "Cleansed Spirit of Faydwer", "Cleansed Spirit of Kunark"], get=["Nature Walkers Scimitar"])]
    return S

# ---- Bard ------------------------------------------------------------------------------
def brd():
    S = []
    part("Maestro's Symphony Page 24 Top")
    S += [by_hand("konia", "Talk to Konia Swiftfoot for a Torch of Misty", "talk", "Konia Swiftfoot", "Western Karana, guard tower 4", get=["Torch of Misty"]),
          give("fajio", "Hand Fajio Knejo the Torch of Misty", "Fajio Knejo", "Misty Thicket", ["Torch of Misty"], get=["Torch of Ro"]),
          give("andad", "Hand Andad Filla the Torch of Ro", "Andad Filla", "Southern Desert of Ro", ["Torch of Ro"], get=["Torch of Rathe"]),
          give("misty", "Hand Misty Tekcihta the Torch of Rathe", "Misty Tekcihta", "Lake Rathetear", ["Torch of Rathe"], get=["Proof of Speed"]),
          give("top", "Hand Konia Swiftfoot the Proof of Speed", "Konia Swiftfoot", "Western Karana, guard tower 4", ["Proof of Speed"],
               get=["Maestro's Symphony Page 24 Top"])]
    part("Maestro's Symphony Page 24 Bottom")
    S += [say("doll", "Ask Baenar Swiftsong about the doll", "Baenar Swiftsong", "South Karana", "What doll", get=["Solusek Mining Company Invoice"]),
          give("marfen", "Hand Marfen Binkdirple the invoice", "Marfen Binkdirple", "Solusek's Eye", ["Solusek Mining Company Invoice"], get=["Mechanical Doll"]),
          give("serra", "Hand Serra the Mechanical Doll", "Serra", "Estate of Unrest", ["Mechanical Doll"], get=["Note for Baenar"]),
          give("baenar-note", "Hand Baenar Swiftsong the Note for Baenar", "Baenar Swiftsong", "South Karana", ["Note for Baenar"], get=["Note to Maligar"]),
          give("maligar", "Hand Maligar the Note to Maligar", "Maligar", "Western Plains of Karana", ["Note to Maligar"]),
          kill("doppel", "Kill Maligar's Enraged Doppleganger for his head", ["Maligar's Enraged Doppleganger"], "Western Plains of Karana", ["Maligar's Head"]),
          give("bongos", "Hand Baenar Swiftsong Maligar's Head", "Baenar Swiftsong", "South Karana", ["Maligar's Head"], get=["Mahlin's Mystical Bongos"]),
          give("bottom", "Hand Konia Swiftfoot Mahlin's Mystical Bongos", "Konia Swiftfoot", "Western Karana, guard tower 4", ["Mahlin's Mystical Bongos"],
               get=["Maestro's Symphony Page 24 Bottom"])]
    part("Maestro's Symphony Page 25")
    S += [kill("onyx", "Kill Blackwing for an Onyx Drake Gut", ["Blackwing"], "Rathe Mountains", ["Onyx Drake Gut"]),
          kill_any("wurm", "Kill Nezekezena or Phurzikon for a Red Wurm Gut", ["Nezekezena", "Phurzikon"], "Burning Woods", ["Red Wurm Gut"]),
          kill("chromodrac", "Kill Eldrig the Old for a Chromodrac Gut", ["Eldrig the Old"], "Skyfire Mountains", ["Chromodrac Gut"]),
          give("page25", "Hand Kelkim Menkia the three guts", "Kelkim Menkia", "South Karana", ["Onyx Drake Gut", "Red Wurm Gut", "Chromodrac Gut"],
               get=["Maestro's Symphony Page 25"])]
    part("Mystical Lute Head")
    S += [kill("horn", "Kill Quag Maelstrom for the Alluring Horn", ["Quag Maelstrom"], "Ocean of Tears", ["Alluring Horn"], optional=True),
          give("vedico", "Hand Vedico Windwhisper the Alluring Horn", "Vedico Windwhisper", "Butcherblock Mountains", ["Alluring Horn"],
               get=["Note to Forpar Fizfla"], optional=True),
          give("forpar-note", "Hand Forpar Fizfla the note", "Forpar Fizfla", "Steamfont Mountains", ["Note to Forpar Fizfla"], optional=True),
          kill("backbone", "Kill Phinigel Autropos for a Kedge Backbone", ["Phinigel Autropos"], "Kedge Keep", ["Kedge Backbone"]),
          kill("tendril", "Kill an Amygdalan warrior for an Amygdalan Tendril", ["Amygdalan warrior"], "Plane of Fear", ["Amygdalan Tendril"]),
          kill("skull", "Kill the Drolvarg Warlord for a Petrified Werewolf Skull", ["Drolvarg Warlord"], "Karnor's Castle", ["Petrified Werewolf Skull"]),
          say("components", "Ask Forpar Fizfla about the components", "Forpar Fizfla", "Steamfont Mountains", "What components", get=["Forpar's Note to Himself"]),
          give("lute-head", "Hand Forpar his note, the backbone, the tendril and the skull", "Forpar Fizfla", "Steamfont Mountains",
               ["Forpar's Note to Himself", "Kedge Backbone", "Amygdalan Tendril", "Petrified Werewolf Skull"], get=["Mystical Lute Head"])]
    part("Mystical Lute Body")
    S += [kill_any("red-scales", "Kill a red dragon for Red Dragon Scales", ["Lord Nagafen", "Zordakalicus Ragefire", "Talendor", "Nortlav the Scalekeeper"],
                   "Nagafen's Lair · Skyfire Mountains · The Hole", ["Red Dragon Scales"]),
          kill_any("white-scales", "Kill a white dragon for White Dragon Scales", ["Lady Vox", "Gorenaire"], "Permafrost Caverns · Dreadlands", ["White Dragon Scales"]),
          give("lute-body", "Hand Forpar the metal bits and both sets of scales", "Forpar Fizfla", "Steamfont Mountains",
               ["Metal Bits", "Red Dragon Scales", "White Dragon Scales"], get=["Mystical Lute Body"], note="The Metal Bits are smithed.")]
    part("Undead Dragongut Strings")
    S += [give("undead-bard", "Hand An Undead Bard your Mystical Lute Body", "An Undead Bard", "", ["Mystical Lute Body"]),
          kill("trakanon", "Kill An Undead Bard, then the Trakanon he calls, for the strings", ["An Undead Bard", "Trakanon"], "", ["Undead Dragongut Strings"])]
    part("Mystical Lute")
    S += [give("lute", "Hand Forpar the head, the body and the strings", "Forpar Fizfla", "Steamfont Mountains",
               ["Mystical Lute Head", "Mystical Lute Body", "Undead Dragongut Strings"], get=["Mystical Lute"])]
    part("Singing Short Sword")
    S += [give("sword", "Hand Baldric Slezaf the three pages and the Mystical Lute", "Baldric Slezaf", "Dreadlands",
               ["Maestro's Symphony Page 24 Top", "Maestro's Symphony Page 24 Bottom", "Maestro's Symphony Page 25", "Mystical Lute"], get=["Singing Short Sword"])]
    return S

# ---- Wizard ----------------------------------------------------------------------------
def wiz():
    S = []
    part("Camin")
    S += [by_hand("solomen", "Talk to Solomen for his note", "talk", "Solomen", "Temple of Solusek Ro"),
          step("camin", "Hand Camin the note, hail him, then hand him 1000 platinum", "handin", "Camin", "Erudin, the Vasty Deep Inn",
               coins="1000 platinum", trade=True, note="Doing this twice is still not enough faction for indifferent.")]
    part("Arantir Karondor")
    S += [by_hand("potion", "Buy Ro's Breath from Dargon", "buy", "Dargon", "Halas, outside the shaman guild", get=["Ro's Breath"],
                  note="It costs 900 to 1100 platinum."),
          give("camin-potion", "Hand Camin the potion", "Camin", "Erudin, the Vasty Deep Inn", ["Ro's Breath"], note="It comes back with its charge spent."),
          give("dargon-potion", "Hand Dargon the spent potion, then hail Arantir Karondor", "Dargon", "Halas", ["Ro's Breath"], get=["Arantir's Ring"],
               npcs=["Dargon", "Arantir Karondor"], note="Dargon turns into Arantir Karondor; any wizard may hail him for a ring."),
          give("challice", "Hand Challice the ring", "Challice", "Felwithe, under the paladin guild", ["Arantir's Ring"], note="She hands back another ring."),
          unnamed("arantir-ring", "Hand Arantir Karondor the second ring and hear him out", "Dargon", "Halas", get=["Note from Arantir"],
                  npcs=["Dargon", "Arantir Karondor"], note="\"Who is the gnome?\" skips to the end of his story."),
          give("kandin-note", "Hand Kandin Firepot the Note from Arantir and ask about the oil", "Kandin Firepot", "Butcherblock Mountains", ["Note from Arantir"],
               get=["Golem Sprocket"], note="Say \"what oil?\".")]
    part("The Three Staves")
    S += [kill("blue", "Kill Phinigel Autropos for the Blue Crystal Staff", ["Phinigel Autropos"], "Kedge Keep", ["Blue Crystal Staff"]),
          kill("gnarled", "Kill Venril Sathir for the Gnarled Staff", ["Venril Sathir"], "Karnor's Castle", ["Gnarled Staff"]),
          give("sprocket", "Hand a broken golem the Golem Sprocket", "a broken golem", "Plane of Fear", ["Golem Sprocket"], note="This spawns an enraged golem."),
          kill("oil", "Kill the enraged golem for Green Oil", ["an enraged golem"], "Plane of Fear", ["Green Oil"]),
          give("gabstik", "Hand Kandin Firepot the Green Oil", "Kandin Firepot", "Butcherblock Mountains", ["Green Oil"], get=["Staff of Gabstik", "Note to Arantir"])]
    part("Staff of the Four")
    S += [give("arantir-note", "Hand Dargon the Note to Arantir, then hail Arantir Karondor", "Dargon", "Halas", ["Note to Arantir"], npcs=["Dargon", "Arantir Karondor"]),
          give("staves", "Hand Arantir Karondor the three staves", "Arantir Karondor", "Halas", ["Blue Crystal Staff", "Gnarled Staff", "Staff of Gabstik"],
               npcs=["Arantir Karondor", "Dargon"], note="He hands you a bag."),
          unnamed("staff", "Hand Solomen the bag", "Solomen", "Temple of Solusek Ro", get=["Staff of the Four"])]
    return S

# ---- Warrior ---------------------------------------------------------------------------
def war():
    S = []
    part("Jeweled Dragon Head Hilt")
    S += [hail("kargek", "Kargek Redblade", "East Freeport", note="Hail Wenden Blackhammer as well."),
          by_hand("unjeweled", "Fetch the Unjeweled Dragon Head Hilt from the lake bottom", "ground", "", "Lake Rathetear", get=["Unjeweled Dragon Head Hilt"]),
          give("jeweled", "Hand Wenden Blackhammer the hilt and the three gems", "Wenden Blackhammer", "East Freeport",
               ["Unjeweled Dragon Head Hilt", "Diamond", "Jacinth", "Black Sapphire"], get=["Jeweled Dragon Head Hilt"])]
    part("Finely Crafted Dragon Head Hilt")
    S += [by_hand("damaged", "Fetch the Severely Damaged Dragon Head Hilt from the chessboard", "ground", "", "Timorous Deep", get=["Severely Damaged Dragon Head Hilt"]),
          kill("monocle", "Kill a mountain giant patriarch for a Giant Sized Monocle", ["A mountain giant patriarch"], "Dreadlands", ["Giant Sized Monocle"]),
          give("ore", "Hand Mentrax Mountainbone the Giant Sized Monocle", "Mentrax Mountainbone", "Frontier Mountains", ["Giant Sized Monocle"], get=["Rejesiam Ore"]),
          kill_any("golem", "Kill a Fear golem for a Ball of Everliving Golem", ["Fright", "Dread", "Terror"], "Plane of Fear", ["Ball of Everliving Golem"]),
          give("crafted", "Hand Wenden Blackhammer the damaged hilt, the ore and the ball", "Wenden Blackhammer", "East Freeport",
               ["Severely Damaged Dragon Head Hilt", "Rejesiam Ore", "Ball of Everliving Golem"], get=["Finely Crafted Dragon Head Hilt"])]
    part("The Blades")
    S += [by_hand("ale", "Buy a Keg of Vox Tail Ale", "buy", "Various merchants", "", get=["Keg of Vox Tail Ale"]),
          by_hand("rebreathers", "Get two Rebreathers", "buy", "Other players, or a gnome tinker", "", get=["Rebreather"], note="Tinkering, trivial at 175."),
          step("permafrost", "Loot a Block of Permafrost from the ice giants", "loot", "", "Permafrost Caverns", loot=["Block of Permafrost"]),
          give("denken", "Hand Denken Strongpick the block, the keg and the two rebreathers", "Denken Strongpick", "Ocean of Tears",
               ["Block of Permafrost", "Keg of Vox Tail Ale", ("Rebreather", 2)], get=["Ancient Sword Blade"]),
          kill("velazul", "Kill Queen Velazul Di`zok for the Ancient Blade", ["Queen Velazul Di`zok"], "Chardok", ["Ancient Blade"])]
    part("The Red Scabbard")
    S += [say("favor", "Ask Kargek Redblade about the favor", "Kargek Redblade", "East Freeport", "What favor", get=["Wax Sealed Note"]),
          give("oknoggin", "Hand Oknoggin Stonesmacker the Wax Sealed Note", "Oknoggin Stonesmacker", "The Feerrott", ["Wax Sealed Note"], get=["Tiny Lute"]),
          give("legacy", "Hand Kargek Redblade the Tiny Lute", "Kargek Redblade", "East Freeport", ["Tiny Lute"], get=["Redblade's Legacy"]),
          give("tenal-book", "Hand Tenal Redblade the book", "Tenal Redblade", "East Karana", ["Redblade's Legacy"], get=["Totem of the Freezing War"]),
          kill("frost", "Kill goblin wizards for a Heart of Frost", ["a goblin wizard"], "Permafrost Caverns", ["Heart of Frost"]),
          give("fiery-war", "Hand Tenal the totem and the Heart of Frost", "Tenal Redblade", "East Karana", ["Totem of the Freezing War", "Heart of Frost"],
               get=["Totem of Fiery War"]),
          kill_any("red-scales", "Kill a red dragon for Red Dragon Scales", ["Lord Nagafen", "Ragefire", "Zordakalicus Ragefire", "Nortlav the Scalekeeper", "Talendor"],
                   "Nagafen's Lair · The Hole · Skyfire Mountains", ["Red Dragon Scales"]),
          kill_any("green-scales", "Kill a green dragon for Green Dragon Scales", ["Severilous", "Hoshkar"], "Emerald Jungle · Veeshan's Peak", ["Green Dragon Scales"]),
          give("mark", "Hand Tenal the fiery totem and both sets of scales", "Tenal Redblade", "East Karana",
               ["Totem of Fiery War", "Red Dragon Scales", "Green Dragon Scales"], get=["Mark of the Sword"]),
          kill("maestro", "Kill the Maestro for his hand", ["Maestro"], "Plane of Hate", ["Hand of the Maestro"]),
          give("tenal-note", "Hand Tenal the mark and the hand", "Tenal Redblade", "East Karana", ["Mark of the Sword", "Hand of the Maestro"],
               get=["Tenal`s note to Kargek"]),
          kill("wingblade", "Kill the Spiroc Lord for a Spiroc Wingblade", ["Spiroc Lord"], "Plane of Sky", ["Spiroc Wingblade"]),
          give("scabbard", "Hand Kargek Redblade Tenal's note and the wingblade", "Kargek Redblade", "East Freeport",
               ["Tenal`s note to Kargek", "Spiroc Wingblade"], get=["Red Scabbard"])]
    part("Jagged Blade of War")
    S += [combine("blade", "Combine both hilts and both blades in the Red Scabbard", "In the Red Scabbard",
                  ["Jeweled Dragon Head Hilt", "Finely Crafted Dragon Head Hilt", "Ancient Sword Blade", "Ancient Blade"], ["Jagged Blade of War"])]
    return S

# ---- Cleric ----------------------------------------------------------------------------
def clr():
    S = []
    part("Orb of Frozen Water")
    S += [kill("crown", "Kill Lord Bergurgle for his crown", ["Lord Bergurgle"], "Lake Rathetear", ["Lord Bergurgle's Crown"]),
          give("shmendrik", "Hand Shmendrik Lavawalker the crown", "Shmendrik Lavawalker", "Lake Rathetear", ["Lord Bergurgle's Crown"], get=["Oil of Fennin Ro"],
               note="Natasha Whitewater appears nearby and leaves after five minutes unless she is fighting."),
          kill("flame", "Kill Shmendrik Lavawalker, then the Spirit of Flame he becomes", ["Shmendrik Lavawalker", "A Spirit of Flame"], "Lake Rathetear",
               ["Damaged Goblin Crown"], caution="Do not let Natasha Whitewater land the last hit — the corpse vanishes."),
          give("natasha-crown", "Hand Natasha Whitewater the Damaged Goblin Crown", "Natasha Whitewater", "Lake Rathetear", ["Damaged Goblin Crown"], get=["Ornate Sea Shell"]),
          give("omat-shell", "Hand Omat Vastsea the Ornate Sea Shell", "Omat Vastsea", "Timorous Deep", ["Ornate Sea Shell"], get=["Coral Statue of Tarew"]),
          give("seeker", "Hand a Seeker the Coral Statue of Tarew", "A Seeker", "Temple of Solusek Ro", ["Coral Statue of Tarew"],
               note="The seeker becomes A Plasmatic Priest."),
          kill("priest", "Kill A Plasmatic Priest for his robe", ["A Plasmatic Priest"], "Temple of Solusek Ro", ["Blood Soaked Plasmatic Priest Robe"]),
          give("frozen", "Hand Omat Vastsea the priest's robe", "Omat Vastsea", "Timorous Deep", ["Blood Soaked Plasmatic Priest Robe"], get=["Orb of Frozen Water"],
               note="This puts Natasha inside the hut — the wiki suggests waiting until you hold Lord Gimblox's Signet Ring.")]
    part("Orb of Clear Water")
    S += [kill("gimblox", "Kill Lord Gimblox for his signet ring", ["Lord Gimblox"], "Solusek's Eye", ["Lord Gimblox's Signet Ring"]),
          give("natasha-ring", "Hand Natasha Whitewater the signet ring", "Natasha Whitewater", "Timorous Deep", ["Lord Gimblox's Signet Ring"],
               get=["Ornate Sea Shell"], note="A second shell; Natasha leaves."),
          give("naxot", "Hand Naxot Deepwater the second Ornate Sea Shell", "Naxot Deepwater", "Burning Woods", ["Ornate Sea Shell"], get=["Message to Natasha"],
               note="This spawns Ixiblat Fer."),
          kill("ixiblat", "Kill Ixiblat Fer for his sceptre", ["Ixiblat Fer"], "Burning Woods", ["Sceptre of Ixiblat Fer"]),
          kill("scroll", "Kill Overking Bathezid for a Singed Scroll", ["Overking Bathezid"], "Chardok", ["Singed Scroll"]),
          give("clear", "Hand Omat Vastsea the sceptre and the scroll", "Omat Vastsea", "Timorous Deep", ["Sceptre of Ixiblat Fer", "Singed Scroll"],
               get=["Orb of Clear Water"])]
    part("Orb of Vapor")
    S += [give("message", "Hand Natasha Whitewater the Message to Natasha", "Natasha Whitewater", "Timorous Deep", ["Message to Natasha"], get=["Shimmering Pearl"]),
          give("zordak", "Hand Zordak Ragefire the Shimmering Pearl", "Zordak Ragefire", "Nagafen's Lair", ["Shimmering Pearl"],
               note="He returns as Zordakalicus Ragefire and attacks."),
          kill("heart", "Kill Zordakalicus Ragefire for his heart", ["Zordakalicus Ragefire"], "Nagafen's Lair", ["Heart of Zordak Ragefire"]),
          give("vapor", "Hand Omat Vastsea the Heart of Zordak Ragefire", "Omat Vastsea", "Timorous Deep", ["Heart of Zordak Ragefire"], get=["Orb of Vapor"],
               caution="Only once all three orbs are ready — this spawns Jhassad for the final hand-in.")]
    part("Water Sprinkler of Nem Ankh")
    S += [give("jhassad", "Hand Jhassad Oceanson the three orbs", "Jhassad Oceanson", "Timorous Deep",
               ["Orb of Frozen Water", "Orb of Clear Water", "Orb of Vapor"], get=["Orb of Triumvirate"], note="This spawns the Avatar of Water in the lake."),
          give("avatar", "Hand the Avatar of Water the Orb of Triumvirate", "Avatar of Water", "Timorous Deep", ["Orb of Triumvirate"],
               get=["Water Sprinkler of Nem Ankh"])]
    return S

# ---- Monk ------------------------------------------------------------------------------
def mnk():
    S = []
    part("Robe of the Lost Circle")
    S += [kill_any("lost-circle", "Kill Brother Zephyl or Brother Qwinn for the Robe of the Lost Circle", ["Brother Zephyl", "Brother Qwinn"],
                   "Rathe Mountains · Southern Karana", ["Robe of the Lost Circle"],
                   note="Or make it: the wiki lists a sub-quest — headband and Code of Zan Fi to Qwinn, sash and The Idol to Zephyl, then two sewing-kit combines.")]
    part("Robe of the Whistling Fists")
    S += [kill("pipe-fi", "Kill an iksar betrayer for A Metal Pipe (Fi)", ["an iksar betrayer"], "Chardok", ["A Metal Pipe"]),
          kill("pipe-zan", "Kill a drolvarg pawbuster for A Metal Pipe (Zan)", ["a drolvarg pawbuster"], "Karnor's Castle", ["A Metal Pipe"]),
          give("balatin", "Hand Brother Balatin the two pipes and the robe", "Brother Balatin", "Dreadlands", [("A Metal Pipe", 2), "Robe of the Lost Circle"],
               get=["Robe of the Whistling Fists"], caution="He must not be aggressive: charm or sneak work, pacify does not.")]
    part("The Book")
    S += [step("immortals", "Loot the book Immortals from a named mob", "loot", "", "Skyfire Mountains", loot=["Immortals"]),
          give("danl", "Hand Tomekeeper Danl the Immortals", "Tomekeeper Danl", "Erudin, the library", ["Immortals"], get=["Danl's Reference"],
               caution="Never hand in while feigned — he eats the book."),
          give("lheao", "Hand Lheao the reference and the Robe of the Whistling Fists", "Lheao", "Timorous Deep, the hidden cove",
               ["Danl's Reference", "Robe of the Whistling Fists"], get=["Celestial Fists"],
               note="The book is called Celestial Fists too. Leave this until late if you want to keep wearing the robe.")]
    part("The Four Fists")
    S += [say("eejag-call", "Challenge Eejag through a fire sprite", "a fire sprite", "Lavastorm Mountains", "I challenge Eejag"),
          kill("eejag", "Kill Eejag for a Charred Scale", ["Eejag"], "Lavastorm Mountains", ["Charred Scale"]),
          give("presence", "Hand a presence the Charred Scale", "a presence", "Plane of Sky, Dojorn's island", ["Charred Scale"], note="This spawns Gwan."),
          kill("gwan", "Kill Gwan for the Breath of Gwan", ["Gwan"], "Plane of Sky", ["Breath of Gwan"]),
          give("ogre", "Hand a sleeping ogre the Breath of Gwan", "a sleeping ogre", "Mines of Nurga", ["Breath of Gwan"],
               caution="He must con indifferent — sneak up from behind. This spawns Trunt."),
          kill("trunt", "Kill Trunt for his head", ["Trunt"], "Mines of Nurga", ["Trunt's Head"]),
          give("deep", "Show Deep the head", "Deep", "Lake Rathetear, the underwater caverns", ["Trunt's Head"], note="She gives it back and leaves.", optional=True),
          give("eye", "Hand the Astral Projection Trunt's Head", "Astral Projection", "The Overthere", ["Trunt's Head"], get=["Eye of Kaiaren"]),
          give("vorash-call", "Hand the Astral Projection by the lake the Eye of Kaiaren", "Astral Projection", "Lake of Ill Omen, the shore at -1900, -950",
               ["Eye of Kaiaren"], note="This spawns Vorash and Deep; Xenevorash appears when Vorash dies."),
          kill("fangs", "Kill Vorash, then Xenevorash, for the Demon Fangs", ["Vorash", "Xenevorash"], "Lake of Ill Omen", ["Demon Fangs"])]
    part("Kaiaren")
    S += [give("kaiaren-book", "Hand the book to the mad Kaiaren, then to the sane one", "Kaiaren", "Trakanon's Teeth", ["Celestial Fists"],
               get=["Book of Celestial Fists"],
               note="The mad one, at the ruins near Sebilis, hands it back and hits you; the sane one then appears in an empty hut by the lake.",
               caution="Have the Demon Fangs in hand first — the sane Kaiaren leaves within 30 minutes, and the first book is spent."),
          give("fists", "Hand the sane Kaiaren the Book of Celestial Fists and the Demon Fangs", "Kaiaren", "Trakanon's Teeth",
               ["Book of Celestial Fists", "Demon Fangs"], get=["Celestial Fists"])]
    return S

# ---- Rogue -----------------------------------------------------------------------------
def rog():
    S = []
    part("Stanos' Pouch")
    S += [say("pouch", "Tell Malka Rale you can help", "Malka Rale", "Qeynos Aqueducts, +380, -210", "I can help", get=["Stanos' Pouch"],
              note="She appears at 8pm game time.", optional=True)]
    part("Pickpocketing and Travel")
    S += [by_hand("p-top", "Pickpocket the Stained Parchment Top from Founy Jestands", "pickpocket", "Founy Jestands", "North Kaladim", get=["Stained Parchment Top"]),
          by_hand("p-bottom", "Pickpocket the Stained Parchment Bottom from Tani N`Mar", "pickpocket", "Tani N`Mar", "Neriak Third Gate", get=["Stained Parchment Bottom"]),
          give("anson", "Hand Anson Mcbale the pouch and ask to see Stanos", "Anson Mcbale", "Highpass Hold", ["Stanos' Pouch"],
               note="Say \"I need to see Stanos\" — Stanos Herkanor appears."),
          give("combined", "Hand Stanos Herkanor both halves of the parchment", "Stanos Herkanor", "Highpass Hold",
               ["Stained Parchment Top", "Stained Parchment Bottom"], get=["Combined Parchment"]),
          give("eldreth", "Hand Eldreth the parchment, 100 platinum and two bottles of milk", "Eldreth", "Lake Rathetear",
               ["Combined Parchment", ("Bottle of Milk", 2)], coins="100 platinum", get=["Scribbled Parchment"], note="The two bottles must be unstacked."),
          give("yendar", "Hand Yendar Starpyre the Scribbled Parchment", "Yendar Starpyre", "Steamfont Mountains", ["Scribbled Parchment"], get=["Tattered Parchment"])]
    part("Book of Souls")
    S += [by_hand("book", "Pick up the Book of Souls", "ground", "", "Plane of Hate, -60, +325", get=["Book of Souls"], note="A ten-hour ground spawn.")]
    part("Renux")
    S += [give("renux-call", "Hand Yendar Starpyre the Tattered Parchment and the Book of Souls", "Yendar Starpyre", "Steamfont Mountains",
               ["Tattered Parchment", "Book of Souls"], note="This spawns Renux."),
          kill("renux", "Kill Renux Herkanor for the Translated Parchment", ["Renux Herkanor"], "Steamfont Mountains", ["Translated Parchment"],
               note="She may also drop the Jagged Diamond Dagger.")]
    part("General V`Ghera")
    S += [give("box", "Hand Stanos Herkanor the Translated Parchment", "Stanos Herkanor", "Highpass Hold", ["Translated Parchment"], get=["Sealed Box"]),
          by_hand("general-call", "Hand the Sealed Box to one of the dark elves", "give", "any of the dark elves", "Kithicor Forest", bring=["Sealed Box"],
                  note="This spawns General V`ghera.",
                  caution="Sneak, but do not hide or go invisible — and check for an indifferent con before the hand-in, or the progress is lost."),
          kill("general", "Kill General V`ghera for the General's Pouch", ["General V`ghera"], "Kithicor Forest", ["General's Pouch"],
               note="He may also drop the Cazic Quill.")]
    part("Cazic Quill — if it did not drop", optional=True)
    S += [step("robes", "Collect the four robes", "loot", "", "Kedge Keep · Splitpaw · Lower Guk · Ocean of Tears",
               loot=["Robe of the Kedge", "Robe of the Ishva", "Shining Metallic Robes", "Robe of the Oracle"],
               note="Phinigel Autropos, The Ishva Mal, the ghoul arch magus and the Oracle of K`Arnon carry them."),
          give("quill", "Hand Vilnius the Small the four robes", "Vilnius the Small", "Western Plains of Karana",
               ["Robe of the Kedge", "Robe of the Ishva", "Shining Metallic Robes", "Robe of the Oracle"], get=["Cazic Quill"])]
    part("Jagged Diamond Dagger — if it did not drop", optional=True)
    S += [step("blades", "Collect the four blades", "loot", "", "Nagafen's Lair · Lower Guk · Everfrost Peaks",
               loot=["Fleshripper", "Painbringer", "Mithril Two-Handed Sword", "Gigantic Zweihander"],
               note="The Solusek kobold king, the Kobold champion, the froglok king and Karg Icebear carry them."),
          give("dagger", "Hand Vilnius the Small the four blades", "Vilnius the Small", "Western Plains of Karana",
               ["Fleshripper", "Painbringer", "Mithril Two-Handed Sword", "Gigantic Zweihander"], get=["Jagged Diamond Dagger"])]
    part("Ragebringer")
    S += [give("rage", "Hand Stanos Herkanor the pouch, the dagger and the quill", "Stanos Herkanor", "Highpass Hold",
               ["General's Pouch", "Jagged Diamond Dagger", "Cazic Quill"], get=["Ragebringer"])]
    return S

# ---- Paladin ---------------------------------------------------------------------------
def pal():
    S = []
    part("Fiery Avenger")
    S += [by_hand("avenger", "Finish The Fiery Avenger", "quest", "", "", get=["Fiery Avenger"],
                  note="It stands in the Notable quests tab, with SoulFire before it.")]
    part("Gleaming Crested Breastplate")
    S += [kill("t-breastplate", "Kill a thought destroyer for the Tainted Darksteel Breastplate", ["thought destroyer"], "Plane of Hate", ["Tainted Darksteel Breastplate"]),
          say("dinner", "Tell Jark you will get his dinner", "Jark", "North Kaladim", "I will get your dinner", get=["Cold Plate of Beef and Bread"],
              note="Nella Stonebraids appears in the temple area — ask her \"Do you have Jark's Dinner?\"."),
          give("crystal", "Hand Jark the Cold Plate of Beef and Bread", "Jark", "North Kaladim", ["Cold Plate of Beef and Bread"], get=["Pure Crystal"]),
          give("g-breastplate", "Hand Reklon Gnallen the tainted breastplate and the crystal", "Reklon Gnallen", "Erudin, the temple of Quellious",
               ["Tainted Darksteel Breastplate", "Pure Crystal"], get=["Gleaming Crested Breastplate"])]
    part("Gleaming Crested Sword")
    S += [kill("t-sword", "Kill the Keeper of the Tombs for the Tainted Darksteel Sword", ["Keeper of the Tombs"], "The Hole", ["Tainted Darksteel Sword"]),
          say("bucket", "Tell a peasant woman you will take it to him", "a peasant woman", "West Freeport, -190, -725", "I will take it to him", get=["Bucket of Water"]),
          give("joshua", "Hand Joshua the Bucket of Water", "Joshua", "West Freeport, the back of the bakery", ["Bucket of Water"], get=["Bucket of Pure Water"]),
          give("g-sword", "Hand Reklon Gnallen the tainted sword and the pure water", "Reklon Gnallen", "Erudin, the temple of Quellious",
               ["Tainted Darksteel Sword", "Bucket of Pure Water"], get=["Gleaming Crested Sword"])]
    part("Gleaming Crested Shield")
    S += [kill("t-shield", "Kill Kirak Vil for the Tainted Darksteel Shield", ["Kirak Vil"], "Nektulos Forest", ["Tainted Darksteel Shield"]),
          give("g-shield", "Hand Elia the Pure the tainted shield", "Elia the Pure", "Felwithe", ["Tainted Darksteel Shield"], get=["Gleaming Crested Shield"])]
    part("Mark of Atonement")
    S += [give("mark", "Hand Reklon Gnallen the three gleaming pieces", "Reklon Gnallen", "Erudin, the temple of Quellious",
               ["Gleaming Crested Shield", "Gleaming Crested Breastplate", "Gleaming Crested Sword"], get=["Mark of Atonement"])]
    part("Fiery Defender")
    S += [give("defender", "Hand Irak Altil the Mark of Atonement and the Fiery Avenger", "Irak Altil", "Plane of Fear", ["Mark of Atonement", "Fiery Avenger"],
               get=["Fiery Defender"], note="A wandering skeleton.")]
    return S

# ---- Necromancer -----------------------------------------------------------------------
def nec():
    S = []
    part("Symbol of the Apprentice")
    S += [hail("venenzi", "Venenzi Oberzendi", "Nektulos Forest", optional=True),
          hail("kazen", "Kazen Fecae", "Lake Rathetear", optional=True),
          kill("edwin", "Kill Sir Edwin Motte and loot his head", ["Sir Edwin Motte"], ""),
          unnamed("apprentice", "Hand Kazen Fecae the head", "Kazen Fecae", "Lake Rathetear", get=["Symbol of the Apprentice"])]
    part("Symbol of the Serpent")
    S += [give("twisted", "Hand Venenzi Oberzendi the Symbol of the Apprentice", "Venenzi Oberzendi", "Nektulos Forest", ["Symbol of the Apprentice"],
               get=["Twisted Symbol of the Apprentice"]),
          kill("najena", "Kill Najena for the Flowing Black Robe", ["Najena"], "Najena", ["Flowing Black Robe"]),
          give("moss", "Hand Venenzi the Flowing Black Robe", "Venenzi Oberzendi", "Nektulos Forest", ["Flowing Black Robe"], get=["Rolling Stone Moss"]),
          give("emkel", "Hand Emkel Kabae the moss and the twisted symbol", "Emkel Kabae", "Lake Rathetear",
               ["Rolling Stone Moss", "Twisted Symbol of the Apprentice"], get=["Symbol of the Serpent"])]
    part("Symbol of Testing")
    S += [give("ssess", "Hand Ssessthrass the Symbol of the Serpent", "Ssessthrass", "Swamp of No Hope", ["Symbol of the Serpent"], get=["Scaled Symbol of the Serpent"]),
          kill_any("herb", "Kill the sarnak herbalist for the Manisi Herb", ["Grand Herbalist Mak`ha", "Royal Sarnak Herbalist"], "Chardok", ["Manisi Herb"]),
          give("refined", "Hand Ssessthrass the herb and the scaled symbol", "Ssessthrass", "Swamp of No Hope", ["Manisi Herb", "Scaled Symbol of the Serpent"],
               note="He returns the refined herb."),
          unnamed("testing", "Hand Emkel Kabae the refined herb", "Emkel Kabae", "Lake Rathetear", get=["Symbol of Testing"])]
    part("Symbol of Insanity")
    S += [give("kazen-testing", "Hand Kazen Fecae the Symbol of Testing", "Kazen Fecae", "Lake Rathetear", ["Symbol of Testing"],
               note="Or kill Kazen Fecae instead — his wiki page describes the fight.", optional=True),
          kill("golem", "Kill a bone golem", ["a bone golem"], "Lake Rathetear, the small inlet east of Emkel"),
          kill("failed", "Kill a failed apprentice", ["a failed apprentice"], "Lake Rathetear"),
          kill("soul", "Kill A tortured soul for the Symbol of Insanity", ["A tortured soul"], "Lake Rathetear", ["Symbol of Insanity"])]
    part("Gkzzallk in a Box")
    S += [give("journal", "Hand Drendico Metalbones the Symbol of Insanity", "Drendico Metalbones", "Timorous Deep", ["Symbol of Insanity"],
               note="He gives you the journal."),
          by_hand("jzil", "Speak to Jzil GSix", "talk", "Jzil GSix", "Plane of Sky"),
          step("disc", "Loot a Silver Disc on the second island", "loot", "", "Plane of Sky", loot=["Silver Disc"], note="An azarack and the other second-island mobs."),
          step("feathers", "Loot Spiroc Feathers on the third island", "loot", "", "Plane of Sky", loot=["Spiroc Feathers"],
               note="A gorgalask and the other third-island mobs."),
          kill("cape", "Kill the Keeper of Souls for the Black Silk Cape", ["Keeper of Souls"], "Plane of Sky", ["Black Silk Cape"]),
          give("cloak", "Hand Jzil GSix the disc, the feathers and the cape", "Jzil GSix", "Plane of Sky", ["Silver Disc", "Spiroc Feathers", "Black Silk Cape"],
               get=["Cloak of Spiroc Feathers"]),
          step("eye", "Loot the Eye of Innoruuk from the Hate mini-bosses", "loot", "", "Plane of Hate", loot=["Eye of Innoruuk"]),
          step("slime", "Loot the Slime Blood of Cazic Thule", "loot", "", "Plane of Fear", loot=["Slime Blood of Cazic Thule"],
               note="From the golems, or Cazic Thule himself."),
          give("reagents", "Hand Drendico the cloak, the eye, the slime blood and the journal", "Drendico Metalbones", "Timorous Deep",
               ["Cloak of Spiroc Feathers", "Eye of Innoruuk", "Slime Blood of Cazic Thule"], note="He gives you the prepared reagents box."),
          unnamed("tome", "Hand Kazen Fecae the reagents box", "Kazen Fecae", "Lake Rathetear", get=["Tome of Instruction"]),
          give("gkzzallk", "Hand Gkzzallk the Tome of Instruction", "Gkzzallk", "Plane of Sky", ["Tome of Instruction"], get=["Gkzzallk in a Box"],
               caution="This despawns the first island's boss and blocks a normal Sky raid until respawn — never do it on another guild's slot unasked.")]
    part("Scythe of the Shadowed Soul")
    S += [give("scythe", "Hand Kazen Fecae Gkzzallk in a Box", "Kazen Fecae", "Lake Rathetear", ["Gkzzallk in a Box"], get=["Scythe of the Shadowed Soul"])]
    return S

# ---- Magician --------------------------------------------------------------------------
def mag():
    S = []
    part("The Beginning", optional=True)
    S += [by_hand("rykas", "Find Rykas for a Token of Mastery", "talk", "Rykas", "Lake Rathetear", get=["Token of Mastery"]),
          give("jahsohn-token", "Hand Jahsohn Aksot the Token of Mastery", "Jahsohn Aksot", "West Commonlands", ["Token of Mastery"])]
    part("Words of Magi`kot")
    S += [kill("page1", "Kill an enraged dread wolf for page 1", ["enraged dread wolf"], "Kithicor Forest", ["Torn Page of Magi`kot pg. 1"]),
          kill("page2", "Kill tentacle terrors for page 2", ["a tentacle terror"], "Estate of Unrest", ["Torn Page of Magi`kot pg. 2"]),
          kill("page3", "Kill a bloodthirsty ghoul for page 3", ["a bloodthirsty ghoul"], "Lower Guk", ["Torn Page of Magi`kot pg. 3"]),
          give("words", "Hand Jahsohn Aksot the three torn pages", "Jahsohn Aksot", "West Commonlands",
               ["Torn Page of Magi`kot pg. 1", "Torn Page of Magi`kot pg. 2", "Torn Page of Magi`kot pg. 3"], get=["Words of Magi`kot"])]
    part("Power of the Elements")
    S += [kill("p-wind", "Kill a gypsy dancer for the Power of Wind", ["a gypsy dancer"], "Castle Mistmoore", ["Power of Wind"]),
          kill_any("p-fire", "Kill a lava or blazing elemental for the Power of Fire", ["Lava elemental", "Blazing Elemental"], "Solusek's Eye", ["Power of Fire"]),
          kill_any("p-water", "Kill a ravener for the Power of Water", ["famished ravener", "Hungered Ravener", "Gorged Ravener"], "Kaesora", ["Power of Water"]),
          kill_any("p-earth", "Kill a fairy guard for the Power of Earth", ["A Fairy Guard", "a faerie guard"], "Lesser Faydark", ["Power of Earth"]),
          give("elements", "Hand Walnan the four powers", "Walnan", "Butcherblock Mountains", ["Power of Wind", "Power of Fire", "Power of Water", "Power of Earth"],
               get=["Power of the Elements"])]
    part("Words of Mastery")
    S += [step("m-pages", "Loot the four Torn Pages of Mastery", "loot", "", "Najena · Cazic Thule",
               loot=["Torn Page of Mastery Earth", "Torn Page of Mastery Fire", "Torn Page of Mastery Water", "Torn Page of Mastery Wind"],
               note="A Magician and A Goblin Magician in Najena, or an alligator in Cazic Thule."),
          give("mastery", "Hand Akksstaff the four pages", "Akksstaff", "Najena",
               ["Torn Page of Mastery Earth", "Torn Page of Mastery Fire", "Torn Page of Mastery Water", "Torn Page of Mastery Wind"], get=["Words of Mastery"])]
    part("Power of the Orb")
    S += [give("orb-power", "Hand Rykas the two Words and the Power of the Elements", "Rykas", "Lake Rathetear",
               ["Words of Magi`kot", "Power of the Elements", "Words of Mastery"], get=["Power of the Orb"])]
    part("Element of Fire")
    S += [kill("torch", "Kill Neh'Ashiir for the Torch of the Elements", ["Neh'Ashiir"], "City of Mist", ["Torch of the Elements"]),
          kill_any("embers", "Kill Gylton, Phurzikon or Nezekezena for Burning Embers", ["Gylton", "Phurzikon", "Nezekezena"], "Burning Woods", ["Burning Embers"],
                   note="Listed from easiest to hardest."),
          kill("wand", "Kill Undertow for the Blazing Wand", ["Undertow"], "Kedge Keep", ["Blazing Wand"]),
          give("e-fire", "Hand Jennus Lyklobar the Power of the Orb and the three fire items", "Jennus Lyklobar", "Skyfire Mountains",
               ["Power of the Orb", "Torch of the Elements", "Burning Embers", "Blazing Wand"], get=["Element of Fire"])]
    part("Element of Earth")
    S += [kill("s-earth", "Kill Magi P`Tasa for the earth staff", ["Magi P`Tasa"], "Plane of Hate", ["Staff of Elemental Mastery: Earth"]),
          kill("dirt", "Kill Slixin Klex for the Dirt of Underfoot", ["Slixin Klex"], "Burning Woods", ["Dirt of Underfoot"]),
          by_hand("shovel", "Quest for the Shovel of Ponz", "quest", "", "", get=["Shovel of Ponz"]),
          by_hand("broom", "Quest for the Broom of Trilon", "quest", "", "", get=["Broom of Trilon"]),
          give("e-earth", "Hand Tiblner Milnik the four earth items", "Tiblner Milnik", "Firiona Vie",
               ["Staff of Elemental Mastery: Earth", "Dirt of Underfoot", "Shovel of Ponz", "Broom of Trilon"], get=["Element of Earth"])]
    part("Element of Water")
    S += [kill("s-water", "Kill Phinigel Autropos for the water staff", ["Phinigel Autropos"], "Kedge Keep", ["Staff of Elemental Mastery: Water"]),
          kill("tears", "Kill Captain Rottgrime for the Tears of Erollisi", ["Captain Rottgrime"], "The Overthere", ["Tears of Erollisi"]),
          kill("rain", "Kill Tarbul Earthstrider for the Rain of Karana", ["Tarbul Earthstrider"], "East Karana", ["Rain of Karana"]),
          give("e-water", "Hand Jinalis Andir the three water items", "Jinalis Andir", "Dagnor's Cauldron",
               ["Staff of Elemental Mastery: Water", "Tears of Erollisi", "Rain of Karana"], get=["Element of Water"])]
    part("Element of Wind")
    S += [step("crown", "Loot the Crown of Elemental Mastery on the seventh island", "loot", "", "Plane of Sky", loot=["Crown of Elemental Mastery"]),
          step("binder", "Loot the Elemental Binder", "loot", "", "The Hole", loot=["Elemental Binder"]),
          kill("cloak", "Kill Quillmane for the Pegasus Feather Cloak", ["Quillmane"], "South Karana", ["Pegasus Feather Cloak"]),
          give("e-wind", "Hand Kihun Solstin the three wind items", "Kihun Solstin", "Plane of Sky",
               ["Crown of Elemental Mastery", "Elemental Binder", "Pegasus Feather Cloak"], get=["Element of Wind"])]
    part("Orb of Mastery")
    S += [give("master", "Hand the Master of Elements the four Elements, then scribe and cast Summon Orb", "Master of Elements", "Plane of Sky",
               ["Element of Fire", "Element of Earth", "Element of Water", "Element of Wind"], get=["Spell: Summon Orb", "Orb of Mastery"],
               note="If he is not up, hail Kihun Solstin and say \"I want to see the master\".")]
    return S

# ---- Ranger ----------------------------------------------------------------------------
def rng():
    S = []
    part("The Shiny Tin Bowl")
    S += [say("telin", "Ask Telin Darkforest what action", "Telin Darkforest", "Burning Woods", "What action", get=["Worn note"]),
          give("faelin", "Hand Faelin Bloodbriar the Worn note", "Faelin Bloodbriar", "Greater Faydark", ["Worn note"], get=["Faelin`s Ring"],
               note="She has to be spawned."),
          give("giz", "Hand Giz X`Tin Faelin`s Ring", "Giz X`Tin", "Kithicor Forest", ["Faelin`s Ring"], get=["Dark Metal Coin"]),
          give("telin-coin", "Hand Telin Darkforest the Dark Metal Coin", "Telin Darkforest", "Burning Woods", ["Dark Metal Coin"], get=["Worn Dark Metal Coin"]),
          give("althele", "Hand Althele the Worn Dark Metal Coin", "Althele", "East Karana", ["Worn Dark Metal Coin"], get=["Braided Grass Amulet"],
               note="She sends you to Sionae, who shows on track."),
          give("sionae", "Show Sionae the Braided Grass Amulet", "Sionae", "East Karana", ["Braided Grass Amulet"], get=["Frayed Braided Grass Amulet"]),
          give("nuien", "Show Nuien the Frayed Braided Grass Amulet", "Nuien", "East Karana", ["Frayed Braided Grass Amulet"], note="It comes back."),
          give("teloa", "Show Teloa the Frayed Braided Grass Amulet", "Teloa", "East Karana", ["Frayed Braided Grass Amulet"],
               caution="This spawns the Dark Elf Corruptor and two Dark Elf Reavers, and they run almost as fast as Spirit of Wolf."),
          kill("corruptor", "Kill the Dark Elf Corruptor for the Fleshbound Tome", ["Dark Elf Corruptor"], "East Karana", ["Fleshbound Tome"]),
          give("althele-tome", "Hand Althele the Fleshbound Tome", "Althele", "East Karana", ["Fleshbound Tome"], get=["Earth Stained Note"]),
          give("ella-note", "Hand Ella Foodcrafter the Earth Stained Note", "Ella Foodcrafter", "Misty Thicket", ["Earth Stained Note"], get=["Shiny Tin Bowl"])]
    part("The Hardened Mixture")
    S += [by_hand("forage", "Forage the four ingredients", "forage", "", "Everfrost · Greater Faydark · Innothule Swamp · Misty Thicket",
                  get=["Chilled Tundra Root", "Ripened Heartfruit", "Speckled Molded Mushroom", "Sweetened Mudroot"], note="One from each zone, in that order."),
          combine("mixture", "Combine the four ingredients in the Shiny Tin Bowl", "In the Shiny Tin Bowl",
                  ["Chilled Tundra Root", "Ripened Heartfruit", "Speckled Molded Mushroom", "Sweetened Mudroot"], ["Hardened Mixture"],
                  caution="Keep the Shiny Tin Bowl — the Refined Mithril Blade needs it again.")]
    part("The Runecrested Bowl")
    S += [by_hand("alrik", "Talk to Alrik Farsight for a Crushed Pot", "talk", "Alrik Farsight", "Timorous Deep", get=["Crushed Pot"]),
          give("farios", "Hand Farios Elianos the Crushed Pot", "Farios Elianos", "Felwithe", ["Crushed Pot"], get=["Grocery List"]),
          give("nora", "Hand Merchant Nora the Grocery List", "Merchant Nora", "Northern Felwithe", ["Grocery List"], get=["Bag of Provisions"]),
          give("farios-bag", "Hand Farios Elianos the Bag of Provisions", "Farios Elianos", "Felwithe", ["Bag of Provisions"], get=["Receipt"]),
          give("pattern", "Hand Alrik Farsight the Receipt", "Alrik Farsight", "Timorous Deep", ["Receipt"], get=["Ancient Pattern"]),
          by_hand("rose", "Forage a Rose of Firiona", "forage", "", "Firiona Vie", get=["Rose of Firiona"]),
          give("merdan", "Hand Merdan Fleetfoot the Rose of Firiona", "Merdan Fleetfoot", "Surefall Glade", ["Rose of Firiona"], get=["Wood Painting"],
               note="Ask him \"Who is Niera?\" first."),
          give("skeleton", "Hand a human skeleton the Wood Painting", "a human skeleton", "Frontier Mountains", ["Wood Painting"], get=["Silver Chained Locket"]),
          give("niera", "Hand Niera Farbreeze the Silver Chained Locket", "Niera Farbreeze", "Surefall Glade", ["Silver Chained Locket"], get=["Platinum Speckled Powder"]),
          kill("reaver", "Kill a black reaver for a Jade Reaver", ["a black reaver"], "City of Mist", ["Jade Reaver"]),
          give("kinlo", "Hand Kinlo Strongarm the Jade Reaver", "Kinlo Strongarm", "North Kaladim", ["Jade Reaver"], get=["Enchanted Clay"]),
          combine("bowl", "Combine the pattern, the powder and the clay on a pottery wheel", "On a pottery wheel",
                  ["Ancient Pattern", "Platinum Speckled Powder", "Enchanted Clay"], ["Runecrested Bowl"])]
    part("Swiftwind")
    S += [give("stone", "Hand Ella Foodcrafter the mixture and the bowl", "Ella Foodcrafter", "Misty Thicket", ["Hardened Mixture", "Runecrested Bowl"],
               get=["Softly Glowing Stone"]),
          by_hand("globe", "Cast a Summoned: Firefly Globe", "cast", "", "anywhere, at night", get=["Summoned: Firefly Globe"],
                  note="A level 1 druid spell, castable only at night (7pm to 4am); the globe does not last."),
          by_hand("res", "Buy a scroll of Resurrection", "buy", "A cleric spell vendor", "", get=["Spell: Resurrection"], note="The level 49 cleric spell."),
          give("remains", "Hand Venril Sathir Remains the Firefly Globe", "Venril Sathir Remains", "Karnor's Castle", ["Summoned: Firefly Globe"],
               note="This spawns the Spirit of Venril Sathir."),
          give("vs-spirit", "Hand the Spirit of Venril Sathir the Resurrection scroll", "Spirit of Venril Sathir", "Karnor's Castle", ["Spell: Resurrection"],
               note="This spawns Venril Sathir."),
          kill("vs", "Kill Venril Sathir for a Pulsing Green Stone", ["Venril Sathir"], "Karnor's Castle", ["Pulsing Green Stone"]),
          give("foloal", "Hand Foloal Stormforest the two stones", "Foloal Stormforest", "Firiona Vie", ["Pulsing Green Stone", "Softly Glowing Stone"],
               get=["Warmly Glowing Stone"]),
          give("longsword", "Hand Telin Darkforest the Warmly Glowing Stone", "Telin Darkforest", "Burning Woods", ["Warmly Glowing Stone"], get=["Ancient Longsword"]),
          give("usbak", "Hand Usbak the Old the Ancient Longsword", "Usbak the Old", "North Kaladim", ["Ancient Longsword"], get=["Refined Ancient Sword"],
               note="Kinlo Strongarm calls Usbak when he takes the Hammer of the Ancients — see the Refined Mithril Blade part."),
          give("swiftwind", "Hand the Refined Ancient Sword to Telin, then to Faelin Bloodbriar", "Telin Darkforest", "Burning Woods", ["Refined Ancient Sword"],
               get=["Swiftwind"], npcs=["Telin Darkforest", "Faelin Bloodbriar"], note="Telin hands it back and Faelin appears; she gives you Swiftwind.")]
    part("Refined Mithril Blade")
    S += [kill("sphere", "Kill essence tamers for a Swirling Sphere of Color", ["an essence tamer"], "Plane of Sky", ["Swirling Sphere of Color"]),
          give("jaeil-call", "Hand Jaeil the Wretched the Shiny Tin Bowl", "Jaeil the Wretched", "The Hole", ["Shiny Tin Bowl"], note="This spawns Jaeil the Insane."),
          kill("jaeil", "Kill Jaeil the Insane for the Soulbound Hammer", ["Jaeil the Insane"], "The Hole", ["Soulbound Hammer"]),
          give("mairee", "Hand Mairee Silentone the Soulbound Hammer", "Mairee Silentone", "Erudin Palace", ["Soulbound Hammer"], get=["Dwarven Smiths Hammer"]),
          give("ancients", "Hand Mairee the smith's hammer and the sphere", "Mairee Silentone", "Erudin Palace", ["Dwarven Smiths Hammer", "Swirling Sphere of Color"],
               get=["Hammer of the Ancients"]),
          give("ore", "Hand Kinlo Strongarm the Hammer of the Ancients", "Kinlo Strongarm", "North Kaladim", ["Hammer of the Ancients"], get=["Small bit of Mithril Ore"],
               caution="This calls Usbak the Old, and he leaves again — hold the Ancient Longsword for him before you do it."),
          give("blade", "Hand Kinlo Strongarm the Small bit of Mithril Ore", "Kinlo Strongarm", "North Kaladim", ["Small bit of Mithril Ore"], get=["Refined Mithril Blade"])]
    part("Earthcaller")
    S += [step("emerald", "Loot a Shattered Emerald of Corruption", "loot", "", "Plane of Hate", loot=["Shattered Emerald of Corruption"],
               note="From the mini-bosses (a 72-hour respawn) or Innoruuk."),
          give("earthcaller", "Hand Xanuusus the emerald and the Refined Mithril Blade", "Xanuusus", "North Karana",
               ["Shattered Emerald of Corruption", "Refined Mithril Blade"], get=["Earthcaller"])]
    return S

# ---- Berserker -------------------------------------------------------------------------
def ber():
    S = []
    KERAS = "Lake of Ill Omen, -110, -3400"
    part("Axe Me No Questions")
    S += [say("keras", "Tell Keras McArik you wish to take this great stride", "Keras McArik", KERAS, "I wish to take this great stride",
              get=["Throwing Axe of the Spirit", "Medal of Blood"], note="Say \"I wish to master the fires\" first.")]
    part("Trial of Blood")
    S += [give("axefall", "Hand Lingering Axefall the Throwing Axe of the Spirit", "Lingering Axefall", "Lake of Ill Omen, +65, +2490",
               ["Throwing Axe of the Spirit"], note="He does not answer hails. He hands you three Bore Axes; ask what he was sent to defeat."),
          kill("bloodbeast", "Kill the Impervious Bloodbeast for a tooth", ["Impervious Bloodbeast"], "Lake of Ill Omen", ["Bloodbeast Tooth"],
               note="Stay at range and throw the Bore Axes; hail Axefall for three more."),
          give("blood", "Hand Keras the tooth and the Medal of Blood", "Keras McArik", KERAS, ["Bloodbeast Tooth", "Medal of Blood"], get=["Medal of Mirages"])]
    part("Trial of Mirages")
    S += [give("mardic", "Hand Mardic Crimsonsteel the sealed note", "Mardic Crimsonsteel", "Field of Bone, +2115, +3625", ["A Sealed Note for Mardic"],
               note="Keras gives you the note when you say you are ready for this challenge. Mardic sends you to a sealed room."),
          kill("image", "Kill the Berserker's Image, alone", ["Berserker's Image"], "a sealed room", ["Axe of Lost Souls"],
               note="It pauses at intervals so you can bandage. Mardic gives you the Image Essence afterwards."),
          give("mirages", "Hand Keras the Image Essence and the Medal of Mirages", "Keras McArik", KERAS, ["Image Essence", "Medal of Mirages"], get=["Medal of Strategy"])]
    part("Trial of Strategy")
    S += [give("treanik", "Hand Treanik Ireblade the note", "Treanik Ireblade", "Dagnor's Cauldron, -1585, +210", ["Note for Treanik"]),
          kill("praklion", "Kill Praklion of the Cauldron for Treanik's axe", ["Praklion of the Cauldron"], "Dagnor's Cauldron, -525, -1400", ["Treanik's Tarnished Axe"]),
          give("trinket", "Hand Treanik her Tarnished Axe", "Treanik Ireblade", "Dagnor's Cauldron", ["Treanik's Tarnished Axe"], get=["A Bone Trinket"]),
          give("strategy", "Hand Keras the trinket and the Medal of Strategy", "Keras McArik", KERAS, ["A Bone Trinket", "Medal of Strategy"], get=["Medal of Fervid Rage"])]
    part("Trial of Fervid Rage")
    S += [kill("imp", "Kill the Ireblind Imp for the Burning Essence of Rage", ["Ireblind Imp"], "Plane of Fear", ["Burning Essence of Rage"],
               note="Kill scarelings until the Enraged Imp appears; its death calls the Ireblind Imp.",
               caution="The Ireblind Imp has near-zonewide aggro and an area mesmerize."),
          give("rage", "Hand Keras the essence and the Medal of Fervid Rage", "Keras McArik", KERAS, ["Medal of Fervid Rage", "Burning Essence of Rage"],
               get=["McArik Reagent Satchel"])]
    part("Trial of Hunting")
    S += [kill("liver", "Kill the Disease-Ridden Gorilla for a Decaying Liver", ["Disease-Ridden Gorilla"], "Emerald Jungle", ["Decaying Liver"],
               note="Its placeholder is a tottering ape near -1525, -90."),
          kill("paw", "Kill the Gnashing Kobold for its paw", ["Gnashing Kobold"], "Stonebrunt Mountains", ["Gnashing Kobold Paw"],
               note="Its placeholder is A Kobold at +1885, +2340."),
          kill("gland", "Kill the Reeking Skunk for a Scent Gland", ["Reeking Skunk"], "Toxxulia Forest", ["Scent Gland"]),
          combine("dust", "Combine the three with a Philter of Major Translocation in the satchel", "In the McArik Reagent Satchel",
                  ["Decaying Liver", "Gnashing Kobold Paw", "Scent Gland", "Philter of Major Translocation"], ["Bag of Image Essence Dust"],
                  note="A shaman makes the philter."),
          give("hunting", "Hand Keras the bag and your medal", "Keras McArik", KERAS, ["Bag of Image Essence Dust"], get=["Medal of Mastery"])]
    part("Trial of Mastery")
    S += [step("hopeful", "Pay the Lurking Hopeful 1000 platinum", "handin", "Lurking Hopeful", "Warsliks Woods, +175, +630", coins="1000 platinum",
               note="She is there from 11pm to 7am game time, then leaves."),
          step("tome", "Kill the goblin up the river and loot A Maniac's Garbled Tome", "loot", "", "Warsliks Woods", loot=["A Maniac's Garbled Tome"],
               note="Three waves of frantic goblins, then ferocious goblins, a minute a wave. Bring melee help and levitation."),
          give("axe", "Hand Keras the tome and the Medal of Mastery", "Keras McArik", KERAS, ["Medal of Mastery", "A Maniac's Garbled Tome"], get=["Kerasian Axe of Ire"])]
    return S

QUESTS = [
    quest("epic-shd", "Shadow Knight Epic", "SHD", "Innoruuk's Curse", 46, "Kurron Ni · The Overthere", "Shadow Knight Epic Quest", shd,
          warning="Con INDIFFERENT to every hand-in NPC before you press Trade: items in the window, then Hide or invisibility, then check the con. A hand-in at a worse con is eaten.",
          note="The Letter to Duriek and The Dusty Tome are not required."),
    quest("epic-shm", "Shaman Epic", "SHM", "Spear of Fate", 46, "a lesser spirit · four zones", "Shaman Epic Quest", shm,
          warning="True Spirit faction carries this quest: about amiable to move along, and MAX ally for the final scale — below that the scale is eaten.",
          note="The wiki's page does not name the three books, Kirn's ring or the diary; those steps tick on the trade or by hand."),
    quest("epic-enc", "Enchanter Epic", "ENC", "Staff of the Serpent", 50, "Stofo Olan · Erudin", "Enchanter Epic Quest", enc,
          warning="One Enchanters Sack does every combine: hand any of the four masters Jeb's Seal (it comes back), then say \"I need a sack\"."),
    quest("epic-dru", "Druid Epic", "DRU", "Nature Walkers Scimitar", 46, "Telin Darkforest · Burning Woods", "Druid Epic Quest", dru,
          warning="The East Karana steps end in a fight: Nuien triggers Teloa, and Teloa calls the dark elves. Be ready for them before you start."),
    quest("epic-brd", "Bard Epic", "BRD", "Singing Short Sword", 46, "Baldric Slezaf · Dreadlands", "Bard Epic Quest", brd,
          note="The Alluring Horn chain under the lute head is optional."),
    quest("epic-wiz", "Wizard Epic", "WIZ", "Staff of the Four", 46, "Solomen · Temple of Solusek Ro", "Wizard Epic Quest", wiz,
          warning="An Ornate Velium Pendant lets you skip straight to the Golem Sprocket — but then it must be used on EVERY hand-in after that, or the hand-in is eaten.",
          note="The wiki's page does not name Solomen's note, Challice's ring or Arantir's bag; those steps tick on the trade or by hand."),
    quest("epic-war", "Warrior Epic", "WAR", "Jagged Blade of War", 46, "Kargek Redblade · East Freeport", "Warrior Epic Quest", war,
          note="The same combine can make the Blade of Strategy and the Blade of Tactics instead."),
    quest("epic-clr", "Cleric Epic", "CLR", "Water Sprinkler of Nem Ankh", 46, "Shmendrik Lavawalker · Lake Rathetear", "Cleric Epic Quest", clr,
          warning="Natasha Whitewater leaves five minutes after she appears in Lake Rathetear unless she is fighting — have each hand-in ready."),
    quest("epic-mnk", "Monk Epic", "MNK", "Celestial Fists", 46, "Tomekeeper Danl · Erudin", "Monk Epic Quest", mnk,
          note="The book from Lheao and the final reward are both called Celestial Fists."),
    quest("epic-rog", "Rogue Epic", "ROG", "Ragebringer", 46, "Malka Rale · Qeynos Aqueducts", "Rogue Epic Quest", rog,
          note="The quill and the dagger can drop from General V`ghera and Renux; the two optional parts are the long way round."),
    quest("epic-pal", "Paladin Epic", "PAL", "Fiery Defender", 46, "Irak Altil · Plane of Fear", "Paladin Epic Quest", pal),
    quest("epic-nec", "Necromancer Epic", "NEC", "Scythe of the Shadowed Soul", 46, "Venenzi Oberzendi · Nektulos Forest", "Necromancer Epic Quest", nec,
          note="The wiki's page does not link Sir Edwin's head, the refined herb, the journal or the reagents box; those steps tick on the trade or by hand."),
    quest("epic-mag", "Magician Epic", "MAG", "Orb of Mastery", 46, "Rykas · Lake Rathetear", "Magician Epic Quest", mag),
    quest("epic-rng", "Ranger Epic", "RNG", "Swiftwind", 46, "Telin Darkforest · Burning Woods", "Ranger Epic Quest", rng, parallel=True,
          warning="Usbak the Old appears only when Kinlo Strongarm takes the Hammer of the Ancients, and leaves again — hold the Ancient Longsword for him first.",
          note="Two swords: Swiftwind and Earthcaller. They share the bowl and Usbak, and finish apart."),
    quest("epic-ber", "Berserker Epic", "BER", "Kerasian Axe of Ire", 46, "Keras McArik · Lake of Ill Omen", "Berserker Epic Quest", ber,
          note="Transcribed from the wiki's walkthrough; it has no checklist."),
]

DOC = OrderedDict(
    credit="Epic quest steps transcribed from eqlwiki.com's class epic pages; step-tracking design EQL Assistant.",
    pack="epics",
    notice="Before Kunark (8 Dec): these steps come from eqlwiki's classic-era walkthroughs and are unverified in EQ Legends. "
           "A name the game spells differently will not tick by itself — tick the step by hand; the steps get corrected once the epics are live.",
    footer="All 15 class epics eqlwiki has written up. It has no Beastlord epic page yet.",
    quests=QUESTS)

OUT = os.path.join(ROOT, "data", "epic-quests.json")
io.open(OUT, "w", encoding="utf-8", newline="\n").write(json.dumps(DOC, ensure_ascii=False, indent=1) + "\n")
for q in QUESTS:
    req = [s for s in q["steps"] if not s.get("optional")]
    parts = list(OrderedDict.fromkeys(s.get("part", "") for s in q["steps"]))
    print(f"{q['name']:20s} steps {len(q['steps']):3d}  required {len(req):3d}  parts {len(parts)}  reward {q['reward']}")
