# Prototype (Python)

Reference implementation of the engine and the offline evaluation that was used to choose Reco+'s defaults. The C# engine in `src/RecoPlus.Engine` reproduces it exactly; `golden.py` regenerates the synthetic reference data the parity tests use.

| File | Role |
|---|---|
| `data.py` | loads an export produced by `tools/reco-export` |
| `engine.py` | signals, TF-IDF vectors, co-occurrence, scoring with the guard rail, MMR, saga rule; also a faithful replica of Jellyfin 12's own recommendations for comparison |
| `evaluate.py` | temporal "next adopted title" evaluation (`python evaluate.py export.json.gz`) |
| `fast.py` | numpy scorer, numerically identical to `engine.py`, for grid searches |
| `tune.py`, `grid_rank.py`, `grid2.py`, `listeval.py` | research scripts used to explore settings (2-fold cross-validation, bootstrap by account) |
| `golden.py` | builds a **synthetic** dataset (no real data) and the expected results: `python golden.py out.json` |

No real viewing data is included in this repository.
