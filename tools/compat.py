#!/usr/bin/env python3
"""Fenêtre de compatibilité de Jellyfin : les 3 dernières versions mineures, une seule source de vérité (compat.json).

    python tools/compat.py check            chaque endroit qui la répète est-il d'accord avec compat.json ? (CI)
    python tools/compat.py set 12.3 12.5    réécrit compat.json et tous ces endroits (nouvelle version de Jellyfin)
    python tools/compat.py latest           une version mineure plus récente que la fenêtre est-elle sortie ? (chaque lundi)

Une nouvelle version mineure entre, la plus ancienne sort : la version la plus ancienne est celle contre laquelle le
plugin est compilé (JellyfinVersion, targetAbi). Avant « set » : lire les notes de version de Jellyfin (API des plugins,
base de données), puis CI et test d'intégration verts sur la nouvelle version (voir CLAUDE.md, « Versions de Jellyfin »).
"""
from __future__ import annotations

import json
import os
import re
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "compat.json"
V = r"(\d+\.\d+)"

# (fichier, motif à deux groupes : version la plus ancienne, puis la plus récente ; None si le motif n'en a qu'une,
#  forme attendue) ; {f} et {t} = « 12.2 » et « 12.4 »
RULES = [
    ("src/Jellyfin.Plugin.RecoPlus/Jellyfin.Plugin.RecoPlus.csproj",
     r"<JellyfinVersion Condition=\"'\$\(JellyfinVersion\)' == ''\">" + V + r"\.0</JellyfinVersion>",
     "<JellyfinVersion Condition=\"'$(JellyfinVersion)' == ''\">{f}.0</JellyfinVersion>"),
    ("meta.json", r"\"targetAbi\": \"" + V + r"\.0\.0\"", "\"targetAbi\": \"{f}.0.0\""),
    ("src/Jellyfin.Plugin.RecoPlus/Plugin.cs",
     r"TestedFrom = new\((\d+), (\d+)\), TestedTo = new\((\d+), (\d+)\);",
     "TestedFrom = new({fM}, {fm}), TestedTo = new({tM}, {tm});"),
    ("README.md", r"alt=\"Jellyfin " + V + " to " + V + r"\" src=\"https://img\.shields\.io/badge/Jellyfin-" + V + "%E2%80%93" + V + "-00a4dc\"",
     "alt=\"Jellyfin {f} to {t}\" src=\"https://img.shields.io/badge/Jellyfin-{f}%E2%80%93{t}-00a4dc\""),
    ("README.md", r"\*\*Jellyfin " + V + " to " + V + r"\*\*", "**Jellyfin {f} to {t}**"),
    ("README.md", r"Built and tested for Jellyfin " + V + " to " + V + " only", "Built and tested for Jellyfin {f} to {t} only"),
    ("README.fr.md", r"alt=\"Jellyfin " + V + " à " + V + r"\" src=\"https://img\.shields\.io/badge/Jellyfin-" + V + "%E2%80%93" + V + "-00a4dc\"",
     "alt=\"Jellyfin {f} à {t}\" src=\"https://img.shields.io/badge/Jellyfin-{f}%E2%80%93{t}-00a4dc\""),
    ("README.fr.md", r"\*\*Jellyfin " + V + " à " + V + r"\*\*", "**Jellyfin {f} à {t}**"),
    ("README.fr.md", r"Construit et testé pour Jellyfin " + V + " à " + V + " uniquement", "Construit et testé pour Jellyfin {f} à {t} uniquement"),
    (".github/workflows/integration.yml", r"jellyfin: \['" + V + "', '" + V + r"'\]", "jellyfin: ['{f}', '{t}']"),
    ("CLAUDE.md", r"Plugin Jellyfin " + V + " à " + V + r" \(\.NET 10 ; compilé contre " + V,
     "Plugin Jellyfin {f} à {t} (.NET 10 ; compilé contre {f}"),
    ("CLAUDE.md", r"vrai Jellyfin \(Docker, " + V + " et " + V, "vrai Jellyfin (Docker, {f} et {t}"),
]


def window() -> tuple[str, str]:
    c = json.loads(SOURCE.read_text(encoding="utf-8"))
    return c["from"], c["to"]


def expected(tpl: str, f: str, t: str) -> str:
    (fM, fm), (tM, tm) = f.split("."), t.split(".")
    return tpl.format(f=f, t=t, fM=fM, fm=fm, tM=tM, tm=tm)


def check() -> int:
    f, t = window()
    bad = []
    for path, pattern, tpl in RULES:
        text = (ROOT / path).read_text(encoding="utf-8")
        found = re.findall(pattern, text)
        want = expected(tpl, f, t)
        if not found:
            bad.append(f"{path}: motif introuvable ({tpl})")
        elif want not in text:
            bad.append(f"{path}: {found} au lieu de « {want} »")
    # les exclusions de la matrice du test d'intégration visent l'une des deux bornes de la fenêtre
    integ = (ROOT / ".github/workflows/integration.yml").read_text(encoding="utf-8")
    ex = set(re.findall(r'"jellyfin":"' + V + '"', integ))
    if not ex <= {f, t}:
        bad.append(f".github/workflows/integration.yml: exclusions sur {sorted(ex)}, hors de la fenêtre {f} → {t}")
    # l'ABI de la prochaine entrée du catalogue vient de meta.json (règle ci-dessus) : la publication l'y copie ; les
    # entrées déjà publiées ne se modifient pas
    print(f"fenêtre {f} → {t} (compat.json)")
    for b in bad:
        print("  ✗ " + b)
    if bad:
        print(f"  {len(bad)} endroit(s) à réaligner : python tools/compat.py set {f} {t}")
    else:
        print("  ✓ tout est aligné")
    return 1 if bad else 0


def set_window(f: str, t: str) -> int:
    for v in (f, t):
        if not re.fullmatch(r"\d+\.\d+", v):
            print(f"version invalide : {v} (attendu : 12.3)")
            return 2
    of, ot = window()
    for path, pattern, tpl in RULES:
        p = ROOT / path
        text = p.read_text(encoding="utf-8")
        new, n = re.subn(pattern, lambda _m: expected(tpl, f, t), text)
        if n == 0:
            print(f"  ✗ {path}: motif introuvable")
            return 1
        p.write_text(new, encoding="utf-8")
    # exclusions : chaque borne passe à sa nouvelle valeur (en deux temps : l'ancienne borne haute peut être la nouvelle basse)
    p = ROOT / ".github/workflows/integration.yml"
    text = p.read_text(encoding="utf-8").replace(f'"jellyfin":"{of}"', '"jellyfin":"\x00F"').replace(f'"jellyfin":"{ot}"', '"jellyfin":"\x00T"')
    p.write_text(text.replace('"jellyfin":"\x00F"', f'"jellyfin":"{f}"').replace('"jellyfin":"\x00T"', f'"jellyfin":"{t}"'), encoding="utf-8")
    SOURCE.write_text(json.dumps({"from": f, "to": t}, indent=2) + "\n", encoding="utf-8")
    print(f"fenêtre {f} → {t} écrite ; meta.json vise {f}.0.0 : la prochaine version publiée l'ajoutera ainsi au catalogue")
    return check()


def latest() -> int:
    f, t = window()
    req = urllib.request.Request("https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=30",
                                 headers={"Accept": "application/vnd.github+json", "User-Agent": "recoplus-compat"})
    if os.environ.get("GH_TOKEN"):
        req.add_header("Authorization", "Bearer " + os.environ["GH_TOKEN"])
    with urllib.request.urlopen(req, timeout=30) as r:
        tags = [x["tag_name"] for x in json.load(r) if not x.get("prerelease") and not x.get("draft")]
    minors = sorted({tuple(int(p) for p in m.groups()) for tg in tags for m in [re.match(r"v?(\d+)\.(\d+)", tg)] if m})
    newest = minors[-1] if minors else None
    cur = tuple(int(p) for p in t.split("."))
    print(f"fenêtre {f} → {t} ; dernière version de Jellyfin : {'.'.join(map(str, newest)) if newest else '?'}")
    if newest and newest > cur:
        n = f"{newest[0]}.{newest[1]}"
        if newest[0] != cur[0]:
            print(f"::error::Jellyfin {n} : nouvelle version MAJEURE (nouvelle ABI des plugins) — nouvelle ligne de versions de Reco+, voir CLAUDE.md.")
        else:
            nf = f"{newest[0]}.{max(0, newest[1] - 2)}"
            print(f"::error::Jellyfin {n} est sortie. Lire ses notes de version, puis : python tools/compat.py set {nf} {n} "
                  f"(la fenêtre passe à {nf} → {n}) ; CI et test d'intégration verts avant de publier.")
        return 1
    return 0


if __name__ == "__main__":
    a = sys.argv[1:]
    if a == ["check"]:
        sys.exit(check())
    if len(a) == 3 and a[0] == "set":
        sys.exit(set_window(a[1], a[2]))
    if a == ["latest"]:
        sys.exit(latest())
    print(__doc__)
    sys.exit(2)
