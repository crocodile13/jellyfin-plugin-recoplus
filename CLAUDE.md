# Reco+ — notes de travail

Plugin Jellyfin 12.2 à 12.4 (.NET 10 ; compilé contre 12.2, la CI compile aussi contre les derniers paquets 12.x) : recommandations personnelles précalculées qui remplacent celles de Jellyfin, rangées
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
    `ScoreAll` (titres demandés ou en liste de suivi notés sans leur propre signal puis écartés selon `IntentFactor`),
    `Rank` (diversité MMR / personnalisée / calibrée, règle de saga).
  - `Pins.cs` : titres en tête (nouveautés de ce que le compte suit : nouveaux épisodes, volet suivant d'une saga ;
    jamais un titre demandé ; filtres des listes appliqués).
  - `HomeRows.cs` : répartition des rangées sans doublon (`Allocate`, rappel `fit`), placement des titres hors
    bibliothèque (`PlaceRow`, `FitRow`, `FreePlaces`, `CapPlaces`, `ForcePlaces`), `RowSource`, `MixOptions`.
  - `Regions.cs` : pays d'un compte (dates des nouvelles sorties, préférence de départ de l'origine des titres) : notation
    « » automatique, « * » international, « FR » ou « FR,BE,CH » liste (`ParseSpec`, `NormalizeSpec`, au plus 12 pays
    connus) ; `RegionGuess` (`Spec`, `Country`). Indices pondérés : fuseau du navigateur 3, langue du navigateur avec
    région 2 (seule 1), langue de l'interface Jellyfin 2 / 1, sous-titres 1, audio 0,5 (on regarde en VO), langue du
    serveur 0,5 ; les indices de langue d'un pays plafonnés ensemble à 2,5. Choix du compte ou de l'administrateur
    d'abord ; repli : `RegionFallback`, sinon pays des métadonnées du serveur, sinon US. `zones.txt` (fuseaux IANA → pays,
    tiré de tzdata, alias compris). Pas d'adresse IP (service extérieur ou base à tenir, faux derrière un VPN).
    `HomeOrigins` : langues (cultures .NET) et noms du pays (y compris ceux de TMDb, `TmdbNames`). `Releases.cs` : sorti
    récemment dans un pays (types TMDb 2, 3, 4, 6 ; sans date locale, la date principale ; international : date
    principale ; liste : la plus tôt des pays), `EnoughVotes` (seuil de votes qui monte sur 30 jours : relevé TMDb, un
    film de moins d'un mois a 9 votes en médiane), `ConfidentRating` (note bayésienne, 10 votes à 6,8). Les votes
    minimum ne sont plus envoyés à TMDb : filtrés après lecture.
  - `Origin.cs` : origine du titre (« l:fr » langue originale, « c:france » pays de production) ; familiarité du compte
    = ses titres aimés de cette origine + `OriginPrior` titres fictifs pour chaque origine de son pays (`Dataset.HomeOrigins`,
    aucune en international) ; ingrédient du goût `WOrigin` = écart à la moyenne des candidats. **Choix de Marien :
    actif par défaut avec un poids prudent**, exception à la règle « sans gain prouvé, coupé ». Banc hors ligne
    (`offline.run(w=…, home=…)`, comptes tous français) : 0,3 avec 5 titres fictifs fait baisser la part des goûts
    (0,63 → 0,58) et hit@10 ; d'où 0,1 et 2 (`OriginPrior`) : part des goûts inchangée, compte francophile bien
    mieux servi. À revoir sur de vrais historiques.
  - `Trending.cs` : listes TMDb du moment (intensité 1/√(1 + place), ordre tendance × goût), `KindFilter` (films,
    séries, les deux, séparées), `SplitLayout` (accueil HSS d'un compte quand une rangée est séparée ou réunie).
  - `ListPolicy.cs` (présentation : hasard du jour, minimums films/séries, filtres), `Exploration.cs` (mode étendu :
    sources, bruit du jour, note fiable ; `Top` compte les pistes par œuvre, une saga = une œuvre ; `SharesGenre` filtre
    les filmographies ; `IsThemeKeyword` écarte les mots-clés de fabrication, `IsReEdit` les remontages), `SimilarityIndex.cs` (produits scalaires précalculés), `Cpu.cs` (fils).
  - `Calibrator.cs` / `ChoiceModel.cs` / `NeuralNet.cs` : calibrage (voir plus bas). `Settings.cs` : `EngineSettings`
    (+ `Sanitized()`, bornes de chaque réglage).
  - `Texts.cs` + `english.json`, `german.json`, `spanish.json` : textes de l'interface (voir Conventions).
- `src/Jellyfin.Plugin.RecoPlus/` : intégration.
  - `Service/RecoService.cs` : calcul complet ou d'un compte (`ComputeCore`), données (`Assemble`), jeu de calibrage,
    aperçu. `RecoWorker.cs` : quand recalculer (activité du compte regroupée, écart minimal, au démarrage).
    `CalibrationService.cs` : tâche de calibrage. `RecoModels.cs` : `UserRecs` (listes, rangées, compositions).
    `RecoService.RegionOf` (pays d'un compte) et `ExtendedFor` (mode étendu coupé par le compte) : à utiliser partout à la
    place de `cfg.ExtendedEnabled` pour ce qui vaut pour un compte.
  - `Integration/` : HSS par réflexion (aucune dépendance de compilation), injection du script dans `index.html`,
    décorateur de « Plus comme ceci » (volet suivant : `Sagas.Next` sur les sagas du dernier calcul,
    `RecoService.SagaCollections`), filtre des suggestions, garde de compatibilité (coupe une partie qui ne colle
    plus à Jellyfin et prévient), notifications (webhook).
  - `Seerr/` : client et service (relevé des demandes et listes de suivi, fiches et listes TMDb en cache, rythme
    borné, disjoncteur après échecs répétés).
  - `Data/` : lecture de l'historique (Jellyfin, Playback Reporting, Jellyfin Enhanced), premiers lancements, titres
    montrés, secrets, journal (`LogTail`), export du jeu de calibrage (`CalibrationExport`).
  - `Api/RecoController.cs` : `RecoPlus/…` ; tout ce qui touche à la configuration exige les droits d'administration.
    Côté utilisateur (session seulement, identité du jeton) : `Extended`, `Impressions`, `Request`, `Client` (fuseau et
    langues du navigateur), `Me` (lire / écrire ses réglages, seulement ceux que l'administrateur ouvre), `UserPage`.
  - Page de chaque utilisateur : `Integration/PluginPagesIntegration.cs` (déclarée à Plugin Pages par réflexion,
    `PluginInterface.RegisterPage(JObject)`, `IsEnabled(string)` appelé à chaque menu), `Web/userPage.html` (fragment HTML
    que Plugin Pages charge avec la session et insère : scripts exécutés, rien d'inséré comme HTML), `Data/UserPrefsStore.cs`
    (`preferences.json` : pays choisi, mode étendu coupé, fuseau et langues signalés ; valeurs revérifiées à la lecture).
    Un réglage ouvert aux utilisateurs = un interrupteur `UserCanSet…` côté administrateur, coupé = le serveur décide.
  - `I18n.cs` : réglage de langue (`UiLanguage` : celle de Jellyfin, français, anglais), langue et pays des
    métadonnées du serveur (valeurs par défaut de la langue TMDb et du pays des classifications).
  - `Web/icon.svg` : icône de l'entrée « Reco+ » du menu du tableau de bord (`EnableInMainMenu`, icône Material
    « style » de secours ; `client.js` la remplace par le logo par CSS, sans toucher au DOM de l'interface).
  - `Web/configPage.html` (page de configuration) et `Web/client.js` (script de l'accueil : titres des rangées
    « Parce que … », titres montrés, titres hors bibliothèque ; sur la fiche d'un titre ouvert depuis une rangée Reco+,
    la raison de la proposition, `Reasons.For` côté moteur). Choix de Marien : la raison est discrète, sur la fiche
    seulement, jamais sur les cartes de l'accueil ; aucune raison plutôt qu'une raison vague ou fausse.
- Fichiers dans le dossier de données du plugin : `recommandations.json`, `premiers-lancements.json`, `secrets.json`
  (mode 600), titres montrés ; cache Seerr : `fiches.json`, `listes.json`. Tous écrits par fichier temporaire + renommage.
- `assets/` : `logo.svg` (en tête des README) et `logo.png` (même logo en 512 × 512, pour les sites qui refusent le
  SVG : aperçu social, forums, listes de plugins) ; `thumb.svg` est la source de `thumb.png` (image du plugin dans le
  catalogue et la liste des plugins installés) ; `screenshots/` est refait par le bot (voir Livraison).
- `tests/integration/offline.py` : banc hors ligne (moteur de référence sur la bibliothèque figée du banc et ses
  comptes), à lancer à la main pour mesurer vite une idée ; `run(extra=…)` ajoute des caractéristiques de contenu.
- `README.md` / `README.fr.md` : README classique (présentation, installation, prise en main) ; le détail (moteur,
  mesures, réglages, calibrage) est dans le **wiki** GitHub (dépôt `jellyfin-plugin-recoplus.wiki.git`, pages anglaises et
  pages « …-fr ») ; `docs/how-it-works*.md` n'y renvoient plus que. Un changement de comportement, de valeur par défaut
  ou de réglage met à jour les pages du wiki concernées (les deux langues) dans la même session.
- `tests/RecoPlus.Tests/` (xUnit), `tests/client/*.test.mjs` (jsdom), `prototype/` (moteur Python de référence,
  `golden.py` régénère le jeu synthétique de `golden.json`), `tools/reco-export/` (export lecture seule, pseudonymisé).
- `tests/integration/` + `integration.yml` : vrai Jellyfin (Docker, 12.2 et 12.4) avec Reco+ seul, avec HSS (+ File
  Transformation, Plugin Pages), avec Jellyfin Enhanced et Playback Reporting, et « eco » avec un vrai Seerr. Bibliothèque
  de vrais titres (fichiers factices, métadonnées figées `nfo/`), comptes simulés (`catalog.py`), tous les points
  d'accroche vérifiés, rapport de pertinence par compte (résumé du job, artifact) à lire en entier à chaque run. Sur les
  PR qui touchent `src/`, chaque lundi, à la main ; aucun secret (mots de passe aléatoires, jetons et clés masqués).
- CI (`.github/workflows/ci.yml`) : cohérence des versions, `golden.py`, `build.sh`, compilation contre les derniers paquets
  Jellyfin 12, tests jsdom (Node 24), tests de reco-export (un `ResourceWarning` est une erreur). Publication
  (`release.yml`) : seulement après une CI réussie sur un push vers `main` (`workflow_run`), pour le commit où `main` est
  encore, si la version de `manifest.json` n'a pas encore de release ; un commit du bot remplit ensuite `sourceUrl`,
  `checksum`, `timestamp` (il ne relance pas la CI). Un commit sans nouvelle version ne publie rien.

## Commandes

```
./build.sh                                                     # tests, compilation, paquet (SDK .NET 10)
dotnet test tests/RecoPlus.Tests -c Release
cd tests/client && npm install --no-save jsdom@24.1.3 && node client.test.mjs && node configpage.test.mjs && node userpage.test.mjs
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

- Tout passe par `main` (pas de branche de développement) : une branche de travail partant de `origin/main` à jour, PR,
  CI et test d'intégration verts (boucle `gh pr checks` sans `watch` ; échec : `gh run view --log-failed`), rapport
  relu, merge `--rebase`. Une correction après push = un commit de plus sur la même branche avant merge.
- **Une version publiée = un ensemble cohérent**, pas un changement : numéros `1.MINEUR.CORRECTIF.0` (série 1.x, on
  ne passe pas en 2) ; nouvelles fonctions et changements visibles → version mineure (1.2.0), correctifs → version
  corrective (1.1.1). Les changements intermédiaires se mergent **sans** toucher `manifest.json` (rien n'est publié) ;
  la version se monte une fois l'ensemble fini et mesuré. Un correctif qui gêne des utilisateurs sort tout de suite.
- Monter une version : `Version`, `AssemblyVersion`, `FileVersion` du csproj, `meta.json` (version, changelog) et une
  entrée en tête de `manifest.json` (changelog en anglais commençant par « 1.MINEUR.CORRECTIF » puis la fenêtre
  Jellyfin, `targetAbi` = `compat.json` from, `sourceUrl`/`checksum`/`timestamp` vides), et une rubrique en tête de
  `CHANGELOG.md`. La CI vérifie le format, l'ordre décroissant du catalogue et que toute entrée sous la première est
  réellement publiée (l'entrée 1.0.36, jamais publiée, est restée un mois dans le catalogue).
- Publication automatique (`release.yml`) : release « Reco+ 1.x.y — Jellyfin A to B » (fenêtre lue dans `compat.json`).
- Une version publiée ne se modifie pas : une correction est une nouvelle version.
- PR des contributeurs extérieurs : la CI et le test d'intégration les vérifient, **seul Marien** les valide et les
  merge (jamais moi, sauf demande explicite). Mes PR et celles de Marien : je merge moi-même une fois tout vert.
  Modèles : `.github/ISSUE_TEMPLATE/` (formulaires bug et idée), `.github/pull_request_template.md` (checklist),
  `CONTRIBUTING.md` ; à tenir à jour avec ces règles.
- Captures du README et du wiki (`assets/screenshots/`) : refaites par `integration.yml` après chaque publication (job
  `gate` : seulement si `assets/screenshots/VERSION` n'est pas la dernière release), commit direct du bot sur `main`.
  À la main : pousser une branche `sim-*` (`SIM_SHOTS`, artifact `integration-12.4-eco`, dossier `shots/`).
- Commits, titres et descriptions de PR : anglais. Auteur : `crocodile13 <134718087+crocodile13@users.noreply.github.com>`.

## Conventions

- Commentaires et docs XML : français, phrases complètes. Interface : français (langue source), anglais, allemand,
  espagnol.
- Par défaut, tout ce qui sort du plugin est en anglais : journaux du serveur, changelog, catalogue (`manifest.json`,
  `meta.json`), image du plugin, textes de repli du script de l'accueil, noms de fichiers exportés. Seule la langue de
  l'interface suit Jellyfin (réglage « celle de Jellyfin »).
- Traduction (`Texts`, `english.json`, `german.json`, `spanish.json` dans le moteur) : le texte français est la clé,
  chaque fichier sa traduction ; un texte absent en allemand ou en espagnol se rabat sur l'anglais (`Texts.T`, et
  `RecoPlus/I18n` envoie à la page le dictionnaire complété). Un nouveau texte se traduit dans les trois langues (test
  `GermanAndSpanishCoverEveryTextWithTheSamePlaceholders`). Page : virgule décimale et « 50 % » hors anglais (`comma()`),
  guillemets propres à chaque langue (`quote`). Ton : vouvoiement dans la page (Sie, usted), tutoiement dans les
  rangées de l'accueil (du, tú). Langues de `UiLanguage` ajoutées en fin d'énumération (valeurs enregistrées stables).
  - Un texte affiché passe par `Texts.Pack` (message avec paramètres `{0}`, rendu plus tard dans la langue du lecteur ;
    `Texts.Pack("texte")` sans paramètre marque un texte simple), `Texts.T` (rendu tout de suite), `Texts.Translatable`
    (paramètre à traduire lui aussi) ; jamais de `$"…"` ni de concaténation pour un texte affiché.
  - Un état ou une erreur gardé en mémoire reste emballé ; il est rendu à la frontière : `RecoController` (`L`, `Msg`,
    langue = réglage du plugin, sinon `?lang=`, sinon Accept-Language, sinon serveur), notifications et journal
    d'activité (langue du serveur), journaux du serveur (anglais, `Render(…, "en")`).
  - Côté page : `t('…')` / `tf('…', x)` pour chaque texte construit, `translateDom` pour le HTML statique ; chaque appel
    à l'API porte `lang`. Côté accueil : textes fournis par `RecoPlus/Extended` (`texts`).
  - Les tests vérifient que chaque texte (sources C#, page, libellés du moteur) a sa traduction, avec les mêmes
    paramètres. Une donnée (titre, genre TMDb) n'est jamais traduite. Ligne à ignorer : commentaire `i18n:skip`.
  - Pas de « .en. » dans le nom d'une ressource : MSBuild en ferait un assembly satellite (d'où `english.json`).
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
- Rangées d'accueil : `HomeRows.Allocate` remplit, dans l'ordre de `HomeRows.BaseRows` : « Pour
  toi », les listes TMDb du moment (Tendances, Populaires, Nouvelles sorties, À venir ; avec Seerr, `TmdbListsAsync`, ordre
  `Trending.Order` ; nouvelles sorties : découverte TMDb filtrée par dates, votes et note, puis date de sortie dans le pays
  de chaque compte, `TmdbList.NewIn` ; leurs minimums filtrent aussi la bibliothèque), Nouveautés, « Parce que … » 1-3, suite du classement (`HomeRows.Suite` : séparée par défaut, ses
  moitiés gardent les identifiants historiques `Films` et `Series` ; réunie : `Suite`). Chaque rangée de
  `HomeRows.KindRows` se règle en films / séries / les deux / séparée (titre propre à chaque moitié) (`KindFilter.Split` : identifiants suffixés `Films` et `Series`, servis par
  `HssSectionHandler.GetRow` via `additionalData`) ; à l'enregistrement, `ReconcileSplits` met à jour l'accueil HSS de
  chaque compte (`SplitLayout.Apply`). La tendance TMDb entre aussi dans la popularité (`Dataset.Trending`,
  `TrendingShare`, OU probabiliste avec la popularité du foyer, parité avec le prototype) ; avec des titres hors bibliothèque, `PlaceExternals` choisit leur place selon le contenu de chaque rangée
  (bibliothèque seule, Seerr seul, mélange) et les titres de la bibliothèque remplacés passent aux rangées suivantes.
  Le serveur compose (`UserRecs.Layouts`), le script applique ; chaque rangée garde sa taille.
- Listes du moment : « Les plus populaires de tous les temps » (`Populaires`) = découverte Seerr triée par `vote_count.desc`
  (la popularité du moment doublait Tendances). Renouvellement du jour par liste (`…Rotation`, `Trending.Daily` : clé en
  logarithme + Gumbel(compte, jour, rangée, titre) × renouvellement, même graine dans `Trending.Order` et `PlaceExternals`) ;
  réservoir lu plus large (× (1 + 2 r)). `ListsLongAgoWatched` / `ListsLongAgoYears` : un titre vu il y a très longtemps peut
  revenir dans les listes (coupé par défaut). **Piège Seerr** : sur `/discover/movies` et `/discover/tv`, le paramètre
  `language` est le filtre de langue ORIGINALE (la langue d'affichage est celle du compte Seerr) : toujours `language=all`
  (`SeerrClient.ListAsync`, exploration) ; avec la langue du serveur, un serveur français n'avait que des séries françaises.
- Demande arrivée (`Pins.Arrived`, `RequestArrivedDays` 3, `RequestArrivedWhere` : accueil, bandeau ou les deux) : en tête
  quelques jours puis classée comme les autres (`IntentFactor` = 1 une fois arrivée ; choix de Marien). Avant l'arrivée,
  une demande reste un signal de goût (0,6, demi-vie 21 j, coupée à 90 j) et n'est proposée nulle part pendant une demi-vie.
- Reprises entre rangées (`RowOptions.RepeatShare` / `RepeatMax`, réglages `RowRepeatPercent` 0 % / `RowRepeatMax` 3) :
  chaque source de rangée garde tous ses titres dans son ordre propre (ne pas filtrer `used` d'avance) ; `Take` prend un
  titre déjà montré seulement parmi les premiers de la rangée, jamais de la rangée juste au-dessus (`above`), jamais de
  `forbid` (la suite ne reprend pas « Pour toi », dont elle est la continuation), dans la limite de rangées par titre ;
  `Interleave` les éloigne de la première carte et les sépare. Coupé par défaut : sur le banc, 25 % ne change presque
  rien (accueil 33 % → 35 % dans les goûts). Piège : une première mesure à 37 % → 50 % venait de la suite qui répétait
  « Pour toi » ; relire les listes, pas seulement les chiffres.

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
  seulement si l'IC95 bootstrap par compte est au-dessus de zéro et le gain au-dessus du minimum. Gain annoncé et
  recommandation : `HonestSelection` (choix du pas et de la méthode refait sans chaque groupe, jugé sur lui, à partir
  des mesures hors échantillon `MethodResult.Oof`, sans réapprendre) : sinon « malédiction du gagnant ».
- Mémoire et durée : `SimCache` (index par jeu de vecteurs, borné), `ContextCache` (une version, budget en entrées :
  `Budget`, 6 millions ; au-delà, recalculé au lieu d'être gardé), `ChoiceModel.FeatureCache` (réglage de départ
  seulement, budget de 32 millions de valeurs), avancement pondéré par le coût mesuré de chaque étape. Le `Context` d'un
  cas ne garde que ce que lit le classement de son compte (`Context(…, onlyFor)` : lignes « vus ensemble » de ses titres
  aimés vers ce qu'il n'a pas vu) : 4,3 Go → 1,2 Go sur un serveur MovieLens, mêmes résultats (tests `CalibrationTests`).
- Le bouton « Exporter le jeu de calibrage » produit le format lu par `ParityTests.LoadDataset` (comptes u01, u02…).

## Page de configuration (`configPage.html`)

- Chaque réglage : une ligne du tableau `F` (clé, libellé, type, aide, pas, min, max, groupe, cible, défaut affiché).
  Le test `configpage.test.mjs` vérifie que chaque clé existe côté serveur.
- Aide au survol pour chaque réglage, valeur par défaut indiquée ; « 0 = automatique » quand il y a un mode auto.
- Un réglage sans effet dans la configuration actuelle : choisir le plus parlant pour la situation, valeur toujours
  gardée et enregistrée.
  - **Grisé et désactivé, avec la raison** (`WHY` / `reason`) quand il dépend d'un autre réglage que l'on peut changer
    (Seerr coupé, mode étendu coupé, facteur coupé, rangée en bibliothèque seule…) : l'utilisateur doit voir qu'il
    existe et ce qui le rallume. Une raison commune à toute une boîte est dite une fois en tête.
  - **Masqué** seulement quand il n'a aucun sens dans la forme choisie et réapparaît à côté du choix qui le fait
    exister (sous-lignes des moitiés d'une rangée séparée) : jamais ailleurs que juste sous ce choix.
- Avant d'ajouter un réglage, relire **toute** la page (tous les onglets) :
  - pas de doublon ni de chevauchement avec un réglage existant (même effet, ou l'un qui annule l'autre) : étendre
    l'existant, ou dériver la valeur automatiquement (« 0 = comme … ») ;
  - même vocabulaire, même unité, même sens de lecture que les réglages voisins et ceux des autres onglets (« goût
    minimal », « part des goûts », « votes TMDb minimum » veulent dire la même chose partout) ;
  - à sa place logique : avec ce qu'il règle (onglet de la fonction, boîte de la rangée), le courant visible, le fin
    au niveau expert dans la boîte de ce qu'il règle (une source d'exploration : ses réglages juste sous son
    interrupteur, `.rp-src-adv` ; une liste du moment : ses filtres sous elle), le diagnostic en mode développeur.
    Pas de menu dépliant « Réglages avancés » : il doublait le sélecteur de niveau ; chaque boîte a un titre ;
  - aide au survol qui dit l'effet concret, les bornes, la valeur par défaut, et ce qui le rend sans effet ;
  - compréhensible sans lire le code : si l'explication demande plus de deux phrases, revoir le réglage.
- Pas de réglage obsolète : un réglage retiré est migré (`Plugin.Migrate`) puis supprimé de la page.
- Onglet Accueil : une ligne de 4 cases par rangée (note, titre, sortes, contenu ; `blank` pour une case vide, `sep`
  pour un intertitre), dans l'ordre de la répartition ; ailleurs, `{ROW}` (rangée principale) ou `{T:Champ}` reprennent
  le titre saisi. Ce qui dépend de Seerr (`LIST_ROWS`, `NEEDS_SEERR`) ou du mode étendu est grisé avec la raison.
- Un `enum:` absent de la configuration reçue prend sa première option (sa valeur par défaut) : la mettre en premier.
- Le formulaire est en `novalidate` : un champ grisé (désactivé, donc non validé) qui redevient actif, ou une valeur écrite
  par le calibrage hors du pas, bloquait l'enregistrement sans message (ticket #44). Le serveur borne chaque valeur ;
  `configpage.test.mjs` vérifie que chaque défaut respecte min / max / pas, et soumet le formulaire pour de vrai
  (`requestSubmit`, pas un événement `submit` qui contourne la validation).
- Trois niveaux d'affichage (`data-level` sur onglets, panneaux, boîtes et sections ; sélecteur `#rpLevel`, retenu par
  navigateur) : 1 essentiels (par défaut : ce qu'il faut pour que tout marche), 2 avancés (Moteur, Calibrage, listes du
  moment, mélange, tête de liste, sources de découverte), 3 experts (réglages fins du moteur, de l'exploration, statistiques
  du calibrage). Un réglage caché garde sa valeur et reste enregistré. Mode développeur à part (outils, pas réglages :
  Journal, export, chemin du calibrage, apport des sources). Un nouveau réglage : choisir son niveau en se demandant
  « faut-il le toucher pour que ça marche ? » (1), « pour ajuster ? » (2), sinon 3.
- Aperçu : `RecoService.PreviewCards` compose chaque rangée comme le script de l'accueil (`Layouts` : « » = carte
  suivante de la bibliothèque), avec la raison de chaque titre ; la page la montre en affiches (`renderCards`, affiche
  TMDb seulement d'un chemin d'image valide). `items` / `rows` restent dans la réponse : le test d'intégration les lit.
- Onglet État : résumé de santé et mise en route (`renderHealth`) — chaque point dit ce qui manque et propose l'action
  (premier calcul, rangées dans HSS par `RecoPlus/HomeSections/Setup`, Seerr). Après Enregistrer : bandeau « recalculer
  et voir l'aperçu ».
- Les réglages du calibrage s'enregistrent dès qu'ils changent (eux seuls), comme la méthode.
- Audace des découvertes (`ExtendedBoldness`, type `slider`) : un préréglage de la page, pas un réglage lu par le calcul.
  Le curseur écrit `ExtendedMinVotes`, `ExtendedMinRating`, `ExtendedGemGenres`, `ExtendedNoise` (courbe `BOLD`, deux
  moitiés, 50 = leurs défauts) ; un de ces champs changé à la main affiche « réglée à la main ». Même courbe dans
  `tests/integration/sim.py` (`bold_values`, arrondi comme `Math.round`) ; expérience sur les branches `sim-*`
  (`SIM_BOLDNESS`).
- Relevé TMDb : `tests/integration/tmdb_stats.py` (branche `sim-*`, `SIM_TMDB_STATS`, eco 12.4) écrit les titres bruts de
  chaque liste dans l'artifact (`tmdb-stats.json`) pour choisir les seuils ; l'analyse se fait à part.
- Construction après le chargement de la langue (`__rpReady`) ; une variable locale ne s'appelle jamais `t`.
- Une option de liste `enum:` ne contient pas de virgule dans son libellé (séparateur des options).

## Faits utiles (vérifiés dans les sources des autres plugins)

- HSS : une section de plugin a une instance (Limit 1, Min/Max ignorés) ; les sections de même ordre sont mélangées ;
  une section sans élément est masquée ; rangées de plugin en paysage par défaut. Classes DOM `RecoPlus<Rangée>`.
- HSS, ce qui est affiché : les sections du tableau de l'administrateur (`SectionSettings` de sa configuration : ordre,
  `Enabled`, `AllowUserOverride`, affichage) croisées avec le choix du compte. Une section déclarée mais absente du
  tableau n'est jamais montrée (sa page ne l'ajoute qu'à l'enregistrement). Choix des comptes :
  `plugins/configurations/Jellyfin.Plugin.HomeScreenSections/ModularHomeSettings.json` ; un compte sans entrée suit les
  sections actives du tableau ; `AllowUserOverride` coupé impose l'état `Enabled` à tous.
- Un compte qui a enregistré son propre choix garde la liste de ce moment-là : une rangée ajoutée ensuite au tableau n'y
  est pas cochée et il ne la voit jamais (ticket #80). HSS ne retient que les sections cochées, pas celles proposées :
  Reco+ retient les rangées proposées à chaque compte (`rangees-proposees.json`, `OfferedRows`) et coche une fois celles
  jamais proposées (`EnableNewRows` : au démarrage, à chaque enregistrement, dans l'assistant) ; décochée, elle le reste.
  L'aperçu dit quelles rangées HSS ne montre pas au compte. Une section sans élément est créée masquée (`hide`) : le
  script de l'accueil la remplit et la démasque quand elle ne vit que hors bibliothèque.
- Rangée séparée ou réunie (`ReconcileSplits`) : le tableau de l'administrateur est mis à jour (la nouvelle forme
  reprend les réglages de l'ancienne, à sa place), puis seulement les comptes qui ont leur propre choix. Enregistrer
  le choix d'un compte qui suit le tableau le figerait.
- Les titres hors bibliothèque n'existent que dans le client web (script) ; les applications TV ne voient que la
  bibliothèque.
- Media Bar Enhanced : n'utilise Reco+ que si son option « Personalized Recommendations » est active
  (`/Movies/Recommendations`, `/Users/{id}/Suggestions`, `/Items/{id}/Similar`) ; bibliothèque seulement.
- Jellyfin Enhanced : cartes et fiche Seerr réutilisées s'il est présent et relié à Seerr ; sinon cartes Reco+. Depuis
  12.17, son module Seerr n'est chargé que sur ses pages : `client.js` le demande par son chargeur public
  (`JE.loader.ensure(['seerr-api', 'seerr'])`, une fois). Le test d'intégration le vérifie dans un vrai navigateur.

## Travail propre (avant chaque livraison)

- Clé TMDb « movie:123 » (ou « tmdb:movie:123 ») : toujours `Exploration.TryTmdbKey` (lecture invariante, sorte
  vérifiée, clé mal formée ignorée), jamais un `Split(':')` à la main. Bootstrap par compte : `BootstrapByAccount`.
- Une seule source de vérité : listes de rangées (`HomeRows.BaseRows`, `ListRows`, `KindRows`, `SourceRows`), titres
  (`HomeScreenSectionsIntegration.DisplayTitle`), réglages par rangée (`PluginConfiguration.SourceOf` / `KindOf` /
  `ListSettings`). Avant d'écrire une liste, un `switch` par rangée ou un petit utilitaire, chercher s'il existe déjà
  (`grep`) ; deux copies d'un même calcul = le factoriser.
- API Jellyfin : seulement des appels déjà utilisés dans le plugin (copier l'usage existant) ou vérifiés dans les
  sources de la plus ancienne version prise en charge. La compilation partielle ne voit pas un membre Jellyfin
  inexistant (`IUserManager.Users` n'existe pas : `GetUsers()`). Réflexion sur un autre plugin : vérifiée dans ses
  sources, et protégée (`try`/`catch` ciblé, rien si la forme n'est pas celle attendue).
- Vérifications, toutes, avant de livrer : tests du moteur (projet local, `RECOPLUS_ROOT` pointant le dépôt), tests
  jsdom (`client`, `configpage`), diagnostics Roslyn du plugin comparés à la base, `golden.py` régénéré puis tests de
  parité, cohérence des versions (csproj, `meta.json`, `manifest.json`), traductions, docs fr/en, ce fichier ; test
  d'intégration vert sur la PR, et son rapport relu (pertinence de chaque rangée, hors bibliothèque compris).
- Tout nouveau comportement a son test (moteur : xUnit ; page : jsdom), y compris le cas sans Seerr, sans HSS et la
  langue anglaise. Tests sans `[Theory]` (le substitut local de xUnit ne connaît que `[Fact]`).
- Ce fichier se met à jour dans le même commit qu'un changement d'organisation, de convention ou un nouveau piège.
- Ne jamais annoncer qu'une chose est faite, publiée ou testée sans l'avoir vérifiée.

## Versions de Jellyfin

- À chaque session, avant le reste : dernière version de Jellyfin (releases GitHub, paquets NuGet, images Docker),
  ses notes de version, et mise à jour de compatibilité si elle dépasse `TestedTo` ; de même pour les plugins et services
  utilisés (tableau ci-dessous) : notes de version, et test d'intégration (`integration.yml`) avec leur dernière version.
- Fenêtre de compatibilité : les 3 dernières versions mineures de Jellyfin, écrite une seule fois dans `compat.json`.
  `python tools/compat.py check` (CI) vérifie chaque endroit qui la répète (csproj, `meta.json`, `Plugin.TestedFrom/To`,
  README fr/en, matrice et exclusions de `integration.yml`, ce fichier, ABI de l'entrée en tête de `manifest.json`) ;
  `set 12.3 12.5` les réécrit tous ; `latest` (workflow `jellyfin-watch.yml`, chaque lundi) échoue quand une version
  mineure plus récente sort, avec la marche à suivre. Un nouvel endroit qui cite la fenêtre = une règle de plus dans
  `RULES`. Une nouvelle version entre, la plus ancienne sort : `JellyfinVersion` (donc `targetAbi`) remonte, ses
  chemins de repli et ses tests partent, le changelog dit « nécessite Jellyfin ≥ … ». Les anciennes entrées de `manifest.json` restent : un serveur plus ancien garde la dernière
  version de Reco+ qui lui convient. Une nouvelle majeure (ABI des plugins) ouvre une nouvelle ligne de versions.
- Suivis (dernière vérification 2026-11-23) :

  | Projet | Ce que Reco+ en utilise | Version |
  |---|---|---|
  | Jellyfin | API des plugins, base (`UserData`), contrôleurs | 12.4 |
  | Home Screen Sections (+ File Transformation, Plugin Pages) | réflexion : `RegisterSection`, `GetUserSettings`, `UpdateUserSettings`, `SectionSettings`, `ModularHomeSettings.json` | 3.0.4.0 (3.0.3.0, 3.0.3.0) |
  | Jellyfin Enhanced | `hidden-content.json`, `reviews.json`, cartes et proxy Seerr côté client | 12.17.0.0 |
  | Playback Reporting | base SQLite `PlaybackActivity` | v19 |
  | Media Bar Enhanced | appelle les routes remplacées | 3.0.2.0 |
  | Seerr | API v1 (demandes, listes de suivi, découverte, fiches) | v3.5.2 |
- `Plugin.TestedFrom` / `TestedTo` : intervalle testé (avertissement hors intervalle). `targetAbi` et les paquets
  restent sur la plus ancienne (`JellyfinVersion` du csproj) ; la CI compile aussi contre les derniers paquets 12.x.
- Nouvelle version de Jellyfin : lire ses notes de version (changements d'API, de base de données, de plugins), CI
  verte contre ses paquets, points d'accroche verts sur un serveur réel, puis élargir `TestedTo` (README, badge).

## Pièges rencontrés

- Une recherche qui garde un index par valeur essayée épuise la mémoire : tout cache par réglage doit être borné.
- Un test jsdom qui dépend d'une minuterie (journal toutes les 2 s) devient instable si la page ralentit : garder
  les recalculs de la page bon marché (éléments mis en cache, rien à refaire quand rien ne change).
- `pkill -f motif` tue aussi le shell dont la ligne de commande contient le motif.
- `UpdateUserSettings` de HSS appelé pour tous les comptes : ceux qui suivaient le tableau ont été figés. Seulement
  les comptes présents dans `ModularHomeSettings.json`.
- `<input type=number min=30 step=30>` avec un défaut de 365 : invalide pour le navigateur, invisible tant que le champ
  est désactivé (voir Page de configuration).
- `git stash` avec le lien `tests/client/node_modules` (anciennement suivi) supprimé : le lien revient à la place du
  dossier installé. Le lien n'est plus suivi ; `npm install --no-save` dans `tests/client` suffit.
- `i18n.en.json` comme ressource incorporée : MSBuild y voit la culture « en » et la sort de l'assembly (satellite).
- `QueueIfNotRunning` ignore une demande quand la tâche tourne déjà : un « Recalculer maintenant » pendant un calcul
  complet était perdu (réglages ou Seerr pris au calcul suivant). `RecoWorker` garde la demande (`_dueAll`) tant que
  `FullRunActive`. Côté test, attendre un calcul **démarré après** la demande (`sim.recompute`).
- jsdom n'exécute pas les scripts d'un fragment de `createContextualFragment` (un navigateur, si : Plugin Pages en dépend) :
  `userpage.test.mjs` les réinsère.
- `index.html` réécrite (Reco+, Jellyfin Enhanced) sans `ETag` ni date : un navigateur gardait l'ancienne page, donc
  l'ancien script, après une mise à jour (#50, réglé en vidant le cache). La page réécrite porte `Cache-Control: no-cache`.
