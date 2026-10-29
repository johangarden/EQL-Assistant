"""Full refresh of data/item-stats.json from today's eqlwiki item pages (owner, 29 Oct 2026).

  python -I -X utf8 refresh-items-2026-10-29.py fetch   # every Category:Items page's wikitext -> all-wikitext.json (resumable)
  python -I -X utf8 refresh-items-2026-10-29.py build   # parse them all with the sweep parser, rebuild the table, report the drift

Build rules:
  * the wiki is the source for every page it still has; a record is parsed by sweep-items-2026-10-29.record;
  * a page that carries no stats block (stub) keeps its OLD record when one exists;
  * an old key the wiki no longer has keeps its old record (a dump name must still resolve);
  * icon: the old record's when the page no longer states lucy_img_ID (icons are embedded by id).
"""
import io, json, os, sys, time, urllib.request, urllib.parse, subprocess, importlib.util
from collections import OrderedDict, Counter

HERE = os.path.dirname(os.path.abspath(__file__))
TMP = r"C:\Users\JohanSehested\AppData\Local\Temp\eqlwiki-focus"
OUT = r"C:\Repos\EQL_Assistant\data\item-stats.json"
UA = "EQL-Assistant-data-sweep/1.0 (johan@thegardencph.com)"
ALL = TMP + r"\all-wikitext.json"

spec = importlib.util.spec_from_file_location("sw", os.path.join(HERE, "sweep-items.py"))
sw = importlib.util.module_from_spec(spec); spec.loader.exec_module(sw)

def get(params):
    url = "https://eqlwiki.com/api.php?" + urllib.parse.urlencode(params)
    for i in range(6):
        try:
            r = urllib.request.Request(url, headers={"User-Agent": UA})
            return json.load(urllib.request.urlopen(r, timeout=120))
        except Exception as e:
            print("retry", i, e, flush=True); time.sleep(4 * (i + 1))
    raise SystemExit("gave up")

def fetch():
    titles = json.load(open(TMP + r"\category-items.json", encoding="utf-8"))
    pages = json.load(open(ALL, encoding="utf-8")) if os.path.exists(ALL) else {}
    todo = [t for t in titles if t not in pages]
    print("pages", len(titles), "cached", len(pages), "to fetch", len(todo), flush=True)
    for i in range(0, len(todo), 50):
        chunk = todo[i:i+50]
        d = get({"action": "query", "prop": "revisions", "rvprop": "content", "rvslots": "main", "titles": "|".join(chunk), "format": "json"})
        for p in d["query"]["pages"].values():
            if "revisions" in p: pages[p["title"]] = p["revisions"][0]["slots"]["main"].get("*", "")
            else: pages[p["title"]] = ""  # missing page: remember we asked
        # titles the API normalised (e.g. underscores) come back under another name — mark the asked ones done
        for t in chunk: pages.setdefault(t, pages.get(t, ""))
        if (i // 50) % 10 == 0:
            json.dump(pages, open(ALL, "w", encoding="utf-8"))
            print(f"  {min(i+50, len(todo))}/{len(todo)}", flush=True)
        time.sleep(0.5)
    json.dump(pages, open(ALL, "w", encoding="utf-8"))
    print("fetched", len(pages), flush=True)

def build():
    pages = json.load(open(ALL, encoding="utf-8"))
    old = json.loads(subprocess.run(["git", "-C", r"C:\Repos\EQL_Assistant", "show", "HEAD:data/item-stats.json"], capture_output=True, check=True).stdout.decode("utf-8-sig"))["items"]
    new, stubs, aliases, nopage = {}, [], 0, 0
    for title, wt in sorted(pages.items()):
        if not wt: nopage += 1; continue
        rec, nm = sw.record(title, wt)
        k = sw.key(title)
        if rec is None:
            stubs.append(title)
            if k in old: new[k] = old[k]
            continue
        if "icon" not in rec and k in old and "icon" in old[k]: rec["icon"] = old[k]["icon"]
        if k in new and len(json.dumps(new[k])) >= len(json.dumps(rec)): continue  # case-variant duplicate pages: the richer wins
        new[k] = rec
        if nm and sw.key(nm) != k:
            ak = sw.key(nm)
            if ak not in new: new[ak] = rec; aliases += 1
    kept = 0
    for k, r in old.items():
        if k not in new: new[k] = r; kept += 1
    # the drift, field by field, against the committed table
    drift = Counter(); examples = {}
    for k, r in new.items():
        o = old.get(k)
        if o is None: drift["new key"] += 1; continue
        for f in set(r) | set(o):
            if r.get(f) != o.get(f):
                drift[f] += 1
                examples.setdefault(f, []).append((k, o.get(f), r.get(f)))
    table = OrderedDict([("source", "eqlwiki.com item pages (Category:Items, every page re-read 2026-10-29) — originally via jmoyers/everquest-companion items.json (MIT, (c) Josh Moyers)"), ("items", OrderedDict(sorted(new.items())))])
    io.open(OUT, "w", encoding="utf-8", newline="\n").write(json.dumps(table, ensure_ascii=False, separators=(",", ":")))
    print("pages", len(pages), "no page", nopage, "stubs", len(stubs), "aliases", aliases, "old keys kept", kept, "table", len(new))
    print("drift:", drift.most_common())
    for f, ex in examples.items():
        print(f"--- {f} ({len(ex)})")
        for k, a, b in ex[:6]: print("   ", k, "|", json.dumps(a, ensure_ascii=False)[:120], "=>", json.dumps(b, ensure_ascii=False)[:120])

if __name__ == "__main__":
    {"fetch": fetch, "build": build}[sys.argv[1]]()
