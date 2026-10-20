"""Tests de reco-export : faux serveur Jellyfin + fausse base Playback Reporting."""
import gzip, importlib.util, json, os, sqlite3, sys, tempfile, threading, unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("rx", HERE / "reco-export.py")
rx = importlib.util.module_from_spec(spec); sys.modules["rx"] = rx; spec.loader.exec_module(rx)
KEY = "k" * 32
U1, U2 = "a" * 32, "b" * 32
MOVIES = [{"Id": f"{i:032x}", "Type": "Movie", "Name": f"Film {i}", "ProductionYear": 2000 + i,
           "Genres": ["Drame"], "Tags": ["t"], "Studios": [{"Name": "S"}],
           "People": [{"Name": f"Acteur{j}", "Type": "Actor"} for j in range(20)] + [{"Name": "Réal", "Type": "Director"}],
           "ProviderIds": {"Tmdb": str(i)}, "CommunityRating": 7.0} for i in range(1, 951)]
SERIES = [{"Id": "f" * 31 + "1", "Type": "Series", "Name": "Série", "Genres": ["Anime"], "People": []}]
BOX = {"Id": "c" * 32, "Type": "BoxSet", "Name": "Saga"}
EPS = [{"Id": f"e{i:031x}", "Type": "Episode", "SeriesId": "f" * 31 + "1", "IndexNumber": i, "ParentIndexNumber": 1} for i in range(30)]

def ud(it, **k):
    d = dict(it); d["UserData"] = k; return d

HIST = {U1: {"IsPlayed": [ud(MOVIES[0], Played=True, PlayCount=2, LastPlayedDate="2026-10-01T20:00:00Z"),
                          ud(EPS[0], Played=True, PlayCount=1, LastPlayedDate="2026-10-02T20:00:00Z")],
             "IsResumable": [ud(MOVIES[1], PlaybackPositionTicks=10**10, PlayedPercentage=30.0)],
             "IsFavorite": [ud(MOVIES[0], IsFavorite=True, Played=True)]},
        U2: {"IsPlayed": [], "IsResumable": [], "IsFavorite": []}}

EMPTY_BOX = False

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        u = urlsplit(self.path); q = {k: v[0] for k, v in parse_qs(u.query).items()}
        if self.headers.get("Authorization") != f'MediaBrowser Token="{KEY}"':   # X-Emby-Token refusé (Jellyfin 12)
            self.send_response(401); self.end_headers(); return
        if u.path == "/System/Info": body = {"Version": "12.0.0"}
        elif u.path == "/Users":
            body = [{"Id": U1, "Name": "alice", "Policy": {"IsAdministrator": True}, "LastActivityDate": "2026-10-19"},
                    {"Id": U2, "Name": "invite", "Policy": {"IsDisabled": True}, "LastActivityDate": "2025-01-01"}]
        elif u.path == "/Items":
            collapse = q.get("CollapseBoxSetItems") != "false"            # comportement par défaut de Jellyfin
            if "ParentId" in q:                                            # enfants filtrés par utilisateur (Jellyfin)
                allx = MOVIES[:5] if q["ParentId"] == BOX["Id"] and q.get("userId") == U1 and not EMPTY_BOX else []
            elif "Filters" in q: allx = HIST[q["userId"]][q["Filters"]]
            elif q["IncludeItemTypes"] == "Episode": allx = EPS
            elif q["IncludeItemTypes"] == "BoxSet": allx = [BOX]
            else: allx = ([BOX] + MOVIES[5:] if collapse else MOVIES) + SERIES
            s, l = int(q["StartIndex"]), int(q["Limit"])
            body = {"Items": allx[s:s + l], "TotalRecordCount": len(allx)}
        else:
            self.send_response(404); self.end_headers(); return
        b = json.dumps(body).encode(); self.send_response(200); self.end_headers(); self.wfile.write(b)

class T(unittest.TestCase):
    def setUp(self):
        self.srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.tmp = Path(tempfile.mkdtemp())
        self.env = self.tmp / "env"; self.env.write_text(f"JF_URL=http://127.0.0.1:{self.srv.server_address[1]}\nJF_KEY={KEY}\n"); os.chmod(self.env, 0o600)
        db = sqlite3.connect(self.tmp / "pr.db")
        db.execute("CREATE TABLE PlaybackActivity (DateCreated TEXT, UserId TEXT, ItemId TEXT, ItemType TEXT, ItemName TEXT, PlaybackMethod TEXT, ClientName TEXT, DeviceName TEXT, PlayDuration INT)")
        db.execute("INSERT INTO PlaybackActivity VALUES ('2026-10-01 20:00', ?, ?, 'Movie', 'Film 1', 'DirectPlay', 'Firefox', 'PC d’Alice', 5400)",
                   ("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", MOVIES[0]["Id"]))
        db.execute("INSERT INTO PlaybackActivity VALUES ('2026-09-01 20:00', ?, 'ffffffffffffffffffffffffffffffff', 'Movie', 'Film remplacé', 'DirectPlay', 'Firefox', 'PC d’Alice', 4000)",
                   ("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",))
        db.commit(); db.close()
    def tearDown(self): self.srv.shutdown()

    def run_main(self, *extra):
        return rx.main(["--env", str(self.env), "--out", str(self.tmp / "out"), *extra])

    def test_export(self):
        self.assertEqual(self.run_main("--playback-db", str(self.tmp / "pr.db")), 0)
        f = next((self.tmp / "out").glob("reco-export-*.json.gz"))
        raw = gzip.open(f).read(); d = json.loads(raw)
        self.assertEqual(oct(f.stat().st_mode & 0o777), "0o600")
        self.assertEqual(len([i for i in d["items"] if i["type"] == "Movie"]), 950)   # pagination
        self.assertEqual(len(d["episodes"]), 30)
        self.assertNotIn("BoxSet", {i["type"] for i in d["items"]})                    # films hors collections
        self.assertEqual(d["collections"], [{"id": BOX["Id"], "name": "Saga", "items": [m["Id"] for m in MOVIES[:5]]}])
        self.assertEqual(len(d["items"][0]["people"]["Actor"]), 15)                    # acteurs plafonnés
        a1 = d["accounts"][0]
        self.assertEqual(a1["user"], "u01"); self.assertTrue(a1["admin"])
        self.assertEqual({i["id"] for i in a1["items"]}, {MOVIES[0]["Id"], MOVIES[1]["Id"], EPS[0]["Id"]})
        m0 = next(i for i in a1["items"] if i["id"] == MOVIES[0]["Id"])
        self.assertTrue(m0["fav"])                                                     # dernier filtre lu l'emporte
        self.assertEqual(d["accounts"][1]["user"], "u02"); self.assertTrue(d["accounts"][1]["disabled"])
        self.assertEqual(d["sessions"], [{"at": "2026-10-01 20:00", "user": "u01", "id": MOVIES[0]["Id"], "type": "Movie", "secs": 5400},
                                         {"at": "2026-09-01 20:00", "user": "u01", "id": "f" * 32, "type": "Movie", "secs": 4000, "name": "Film remplacé"}])
        for bad in (KEY, U1, U2, "alice", "PC d’Alice", "Firefox"):
            self.assertNotIn(bad.encode(), raw, bad)
        mp = json.loads((self.tmp / "out" / "correspondance-comptes.json").read_text())
        self.assertEqual(mp["u01"]["name"], "alice")
        self.assertEqual(oct((self.tmp / "out" / "correspondance-comptes.json").stat().st_mode & 0o777), "0o600")

    def test_empty_collections_fail(self):
        global EMPTY_BOX
        EMPTY_BOX = True
        try:
            self.assertEqual(self.run_main(), 1)                                      # pas d'export silencieusement incomplet
            self.assertFalse(list((self.tmp / "out").glob("reco-export-*.json.gz")))
        finally:
            EMPTY_BOX = False

    def test_bad_key_and_perms(self):
        self.env.write_text(f"JF_URL=http://127.0.0.1:{self.srv.server_address[1]}\nJF_KEY=mauvaise\n")
        self.assertEqual(self.run_main(), 1)
        os.chmod(self.env, 0o644)
        with self.assertRaises(SystemExit): self.run_main()

if __name__ == "__main__":
    unittest.main()
