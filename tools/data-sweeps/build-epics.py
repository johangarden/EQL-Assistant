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

def quest(key, name, cls, reward, level, start, wiki, steps, warning="", note=""):
    _seen.clear(); part("")
    q = OrderedDict(key=key, name=name, pack="epics", reward=reward, rewardStats=reward_stats(reward), classes=[cls],
                    minLevel=level, start=start, wiki="https://eqlwiki.com/" + wiki.replace(" ", "_"))
    if warning: q["warning"] = warning
    if note: q["note"] = note
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

QUESTS = [
    quest("epic-shd", "Shadow Knight Epic", "SHD", "Innoruuk's Curse", 46, "Kurron Ni · The Overthere", "Shadow Knight Epic Quest", shd,
          warning="Con INDIFFERENT to every hand-in NPC before you press Trade: items in the window, then Hide or invisibility, then check the con. A hand-in at a worse con is eaten.",
          note="The Letter to Duriek and The Dusty Tome are not required."),
    quest("epic-shm", "Shaman Epic", "SHM", "Spear of Fate", 46, "a lesser spirit · four zones", "Shaman Epic Quest", shm,
          warning="True Spirit faction carries this quest: about amiable to move along, and MAX ally for the final scale — below that the scale is eaten.",
          note="The wiki's page does not name the three books, Kirn's ring or the diary; those steps tick on the trade or by hand."),
    quest("epic-enc", "Enchanter Epic", "ENC", "Staff of the Serpent", 50, "Stofo Olan · Erudin", "Enchanter Epic Quest", enc,
          warning="One Enchanters Sack does every combine: hand any of the four masters Jeb's Seal (it comes back), then say \"I need a sack\"."),
]

DOC = OrderedDict(
    credit="Epic quest steps transcribed from eqlwiki.com's class epic pages (Shadow Knight, Shaman, Enchanter); step-tracking design EQL Assistant.",
    pack="epics",
    notice="Before Kunark (8 Dec): these steps come from eqlwiki's classic-era walkthroughs and are unverified in EQ Legends. "
           "A name the game spells differently will not tick by itself — tick the step by hand; the steps get corrected once the epics are live.",
    footer="3 of 15 class epics so far: Shadow Knight, Shaman, Enchanter. Still to come: Bard, Berserker, Cleric, Druid, Magician, Monk, "
           "Necromancer, Paladin, Ranger, Rogue, Warrior, Wizard. eqlwiki has no Beastlord epic page yet.",
    quests=QUESTS)

OUT = os.path.join(ROOT, "data", "epic-quests.json")
io.open(OUT, "w", encoding="utf-8", newline="\n").write(json.dumps(DOC, ensure_ascii=False, indent=1) + "\n")
for q in QUESTS:
    req = [s for s in q["steps"] if not s.get("optional")]
    parts = list(OrderedDict.fromkeys(s.get("part", "") for s in q["steps"]))
    print(f"{q['name']:20s} steps {len(q['steps']):3d}  required {len(req):3d}  parts {len(parts)}  reward {q['reward']}")
