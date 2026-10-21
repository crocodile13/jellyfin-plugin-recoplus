# Reco+ — notes de travail

Plugin Jellyfin 12.2 (.NET 10) : recommandations personnelles précalculées qui remplacent celles de Jellyfin, rangées
Home Screen Sections (HSS), mode étendu (titres hors bibliothèque via Seerr), calibrage sur l'historique. Mainteneur :
Marien (crocodile13). Dépôt public : jamais de donnée réelle de visionnage, d'adresse, de nom de machine ni de secret.

## Organisation

- `src/RecoPlus.Engine/` : moteur pur, sans dépendance Jellyfin, testable seul.
  - `Model.cs` : `Title`, `Interaction` (ce qu'un compte a fait d'un titre, avec ses dates), `Dataset`, `Exposure`.
  - `Core.cs` : signal d'une interaction (`Signal` : vu, abandon, favori, revu, note, masqué, demande), vecteurs de
    contenu (TF-IDF / BM25, `SparseVector`), « vus ensemble » (`ItemCooc`).
  - `Recommender.cs` : `Context` (signaux de tous les comptes à un instant), `Compute` (ingrédients : goût d'ensemble,
    titre aimé le plus proche, vus ensemble, écart aux abandons, goût du moment, heure de la journée, nouveauté,
    popularité, note, sortie, avis du foyer), `Score` (goût + bonus réservés aux N premiers par goût : le garde-fou),
    `Rank` (diversité MMR / personnalisée / calibrée, règle de saga).
  - `HomeRows.cs` : répartition des rangées sans doublon (`Allocate`, rappel `fit`), placement des titres hors
    bibliothèque (`PlaceRow`, `FitRow`, `FreePlaces`, `CapPlaces`, `ForcePlaces`), `RowSource`, `MixOptions`.
  - `ListPolicy.cs` (présentation : hasard du jour, minimums films/séries, filtres), `Exploration.cs` (mode étendu :
    sources, bruit du jour, note fiable), `SimilarityIndex.cs` (produits scalaires précalculés), `Cpu.cs` (fils).
  - `Calibrator.cs` / `ChoiceModel.cs` / `NeuralNet.cs` : calibrage (voir plus bas). `Settings.cs` : `EngineSettings`
    (+ `Sanitized()`, bornes de chaque réglage).
- `src/Jellyfin.Plugin.RecoPlus/` : intégration.
  - `Service/RecoService.cs` : calcul complet ou d'un compte (`ComputeCore`), données (`Assemble`), jeu de calibrage,
    aperçu. `RecoWorker.cs` : quand recalculer (activité du compte regroupée, écart minimal, au démarrage).
    `CalibrationService.cs` : tâche de calibrage. `RecoModels.cs` : `UserRecs` (listes, rangées, compositions).
  - `Integration/` : HSS par réflexion (aucune dépendance de compilation), injection du script dans `index.html`,
    décorateur de « Plus comme ceci », filtre des suggestions, garde de compatibilité (coupe une partie qui ne colle
    plus à Jellyfin et prévient), notifications (webhook).
  - `Seerr/` : client et service (relevé des demandes et listes de suivi, fiches et listes TMDb en cache, rythme
    borné, disjoncteur après échecs répétés).
  - `Data/` : lecture de l'historique (Jellyfin, Playback Reporting, Jellyfin Enhanced), premiers lancements, titres
    montrés, secrets, journal (`LogTail`), export du jeu de calibrage (`CalibrationExport`).
  - `Api/RecoController.cs` : `RecoPlus/…` ; tout ce qui touche à la configuration exige les droits d'administration.
  - `Web/configPage.html` (page de configuration) et `Web/client.js` (script de l'accueil : titres des rangées
    « Parce que … », titres montrés, titres hors bibliothèque).
- Fichiers dans le dossier de données du plugin : `recommandations.json`, `premiers-lancements.json`, `secrets.json`
  (mode 600), titres montrés ; cache Seerr : `fiches.json`, `listes.json`. Tous écrits par fichier temporaire + renommage.
- `tests/RecoPlus.Tests/` (xUnit), `tests/client/*.test.mjs` (jsdom), `prototype/` (moteur Python de référence,
  `golden.py` régénère le jeu synthétique de `golden.json`), `tools/reco-export/` (export lecture seule, pseudonymisé).
- CI (`.github/workflows/ci.yml`) : cohérence des versions, `golden.py`, `build.sh`, tests jsdom, tests de
  reco-export. Publication (`release.yml`) : à chaque changement de `manifest.json` sur `main` dont la version n'a pas
  encore de release ; un commit du bot remplit ensuite `sourceUrl`, `checksum`, `timestamp`.

## Commandes

```
./build.sh                                                     # tests, compilation, paquet (SDK .NET 10)
dotnet test tests/RecoPlus.Tests -c Release
cd tests/client && npm install --no-save jsdom@24.1.3 && node client.test.mjs && node configpage.test.mjs
cd prototype && python golden.py ../tests/RecoPlus.Tests/golden.json
cd tools/reco-export && python test_reco_export.py
```

Sans accès NuGet : le moteur et les tests se compilent avec un projet local qui référence `RecoPlus.Engine.csproj`,
inclut les fichiers de `tests/RecoPlus.Tests/` et les fichiers du plugin sans dépendance Jellyfin, avec un petit
substitut de xUnit (`[Fact]`, `Assert`) ; un mode « jeu de données » y lance le calibrage sur un export. Le code du
plugin se vérifie par compilation Roslyn partielle : comparer les diagnostics avant et après le changement (les types
Jellyfin manquants produisent des erreurs en cascade ; seules les nouvelles comptent). La vraie compilation est celle
de la CI.

## Livraison

- Un commit par version, sur une branche `reco-1.0.N` partant de `main` à jour ; livré en patch (`git format-patch`)
  avec son sha256, appliqué par Marien (`git am -3`), PR, CI, merge `--rebase`, publication automatique.
- Une version publiée ne se modifie pas : une correction est une nouvelle version.
- Version : `Version`, `AssemblyVersion`, `FileVersion` du csproj, `meta.json` (version, changelog) et une entrée en tête
  de `manifest.json` (changelog en français commençant par « 1.0.N : », `sourceUrl`/`checksum`/`timestamp` vides).
  Un commit sans changement de `manifest.json` (documentation) ne publie rien.
- Commits, titres et descriptions de PR : anglais. Auteur : `crocodile13 <134718087+crocodile13@users.noreply.github.com>`.

## Conventions

- Texte de l'interface, commentaires et docs XML : français, phrases complètes.
- `TreatWarningsAsErrors` : un avertissement casse la CI (attention aux `cref` ambigus entre surcharges).
- Dans l'espace de noms `Jellyfin.Plugin.RecoPlus`, `RecoPlus.Engine.X` se résout mal (CS0234) : `using RecoPlus.Engine;`.
  Une méthode ne porte pas le nom d'un type qu'elle renvoie (`BuildCalibrationOptions`, pas `CalibrationOptions`).
- Aucun secret dans le code, la configuration XML ou les journaux : magasin de secrets à part ; un secret enregistré
  n'est envoyé qu'à l'URL enregistrée ; le journal masque ce qui ressemble à un secret ; l'interface n'en montre que
  les 4 derniers caractères.
- Valeurs automatiques : des fonctions continues (√n, log, interpolation…) plutôt que des paliers (« 5 à partir de 10 »).
- Toute nouveauté du moteur est mesurée (calibrage, validation croisée par comptes) avant de fixer sa valeur par
  défaut ; sans gain prouvé, elle reste coupée ou neutre. Ne jamais annoncer « plus pertinent » sans mesure.
- Le moteur reste déterministe (mêmes entrées → mêmes listes) ; `golden.json` vérifie la parité avec le prototype au
  1e-9 : un changement de calcul se fait des deux côtés.
- Calcul parallèle : un `Context` par fil (`ctx.Fork()`, seule la tranche horaire est propre au fil) ; rien de
  modifiable partagé entre comptes ; les appels Jellyfin et HSS se font avant la boucle parallèle.
- Mémoire bornée partout (index, caches) : un serveur Jellyfin modeste ne doit pas tomber à cause d'un calcul.
- Ce qui vient de l'extérieur (Seerr, TMDb, autres plugins) passe par des garde-fous : délais, disjoncteur, données
  validées (chemins d'images TMDb, identifiants), jamais de HTML inséré tel quel côté client (`textContent`).

## Calcul

- `ComputeCore` : bibliothèque, historiques (tous les comptes non ignorés), relevé Seerr, `Assemble` (titres, votes
  TMDb du cache, demandes et listes de suivi, avis et masquages de Jellyfin Enhanced), exploration Seerr (comptes
  actifs), vecteurs, puis chaque compte en parallèle : candidats (ni vus ni déjà connus), tranches horaires si
  l'heure de la journée est active, rangées, titres hors bibliothèque et composition des rangées.
- Rangées d'accueil : `HomeRows.Allocate` remplit « Pour toi », Nouveautés, « Parce que … » 1-3, Films, Séries sans
  doublon ; avec des titres hors bibliothèque, `PlaceExternals` choisit leur place selon le contenu de chaque rangée
  (bibliothèque seule, Seerr seul, mélange) et les titres de la bibliothèque remplacés passent aux rangées suivantes.
  Le serveur compose (`UserRecs.Layouts`), le script applique ; chaque rangée garde sa taille.

## Calibrage

- Données : exactement celles du calcul (`BuildCalibrationDatasetAsync`, Seerr en cache seulement, comptes ignorés
  exclus). `Calibrator.Snapshot` ne laisse rien passer du futur (titre en cours à la date du cas, demande seulement
  après sa date). Un titre voulu avant d'être regardé (demandé, en liste de suivi) n'est pas un cas.
- Mesure : gain de rang 1/log2(1 + rang) sur tous les candidats, cas pondérés (historique déjà connu, comptes
  équilibrés).
- Méthodes : recherche par curseurs (grille + section dorée, deux moitiés de comptes en garde-fou), modèle de choix
  (logit conditionnel régularisé vers l'actuel), réseau (correction non linéaire, expérimental) ; automatique = toutes.
- Validation croisée par comptes à k groupes (`FoldCount` : √comptes ; méthodes longues `SlowFoldCount` : √(2k)) ;
  chaque méthode donne un chemin, l'étape retenue suit la règle 1-SE (réglable : `CalibrationOptions`) ; recommandé
  seulement si l'IC95 bootstrap par compte est au-dessus de zéro et le gain au-dessus du minimum.
- Mémoire et durée : `SimCache` (index par jeu de vecteurs, borné), `ContextCache` (une version),
  `ChoiceModel.FeatureCache` (réglage de départ seulement), avancement pondéré par le coût mesuré de chaque étape.
- Le bouton « Exporter le jeu de calibrage » produit le format lu par `ParityTests.LoadDataset` (comptes u01, u02…).

## Page de configuration (`configPage.html`)

- Chaque réglage : une ligne du tableau `F` (clé, libellé, type, aide, pas, min, max, groupe, cible, défaut affiché).
  Le test `configpage.test.mjs` vérifie que chaque clé existe côté serveur.
- Aide au survol pour chaque réglage, valeur par défaut indiquée ; « 0 = automatique » quand il y a un mode auto.
- Un réglage sans effet dans la configuration actuelle est **grisé et désactivé, avec la raison** (`WHY` / `reason`) ;
  une raison commune à toute une boîte est dite une fois en tête. Jamais masqué silencieusement ; valeur gardée.
- Pas de réglage obsolète : un réglage retiré est migré (`Plugin.Migrate`) puis supprimé de la page.
- Titres des rangées : tous dans l'onglet Général ; ailleurs, `{ROW}` (rangée principale) ou `{T:Champ}` reprennent le
  titre saisi.
- Onglets Moteur, Calibrage, Journal : mode développeur seulement. Les réglages du calibrage s'enregistrent dès qu'ils
  changent (eux seuls), comme la méthode.

## Faits utiles (vérifiés dans les sources des autres plugins)

- HSS : une section de plugin a une instance (Limit 1, Min/Max ignorés) ; les sections de même ordre sont mélangées ;
  une section sans élément est masquée ; rangées de plugin en paysage par défaut. Classes DOM `RecoPlus<Rangée>`.
- Les titres hors bibliothèque n'existent que dans le client web (script) ; les applications TV ne voient que la
  bibliothèque.
- Media Bar Enhanced : n'utilise Reco+ que si son option « Personalized Recommendations » est active
  (`/Movies/Recommendations`, `/Users/{id}/Suggestions`, `/Items/{id}/Similar`) ; bibliothèque seulement.
- Jellyfin Enhanced : cartes et fiche Seerr réutilisées s'il est présent et relié à Seerr ; sinon cartes Reco+.

## Pièges rencontrés

- Une recherche qui garde un index par valeur essayée épuise la mémoire : tout cache par réglage doit être borné.
- Un test jsdom qui dépend d'une minuterie (journal toutes les 2 s) devient instable si la page ralentit : garder
  les recalculs de la page bon marché (éléments mis en cache, rien à refaire quand rien ne change).
- `pkill -f motif` tue aussi le shell dont la ligne de commande contient le motif.
