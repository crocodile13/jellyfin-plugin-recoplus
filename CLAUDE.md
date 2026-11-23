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
  - `Integration/` : HSS par réflexion (aucune dépendance de compilation), injection du script dans `index.html`,
    décorateur de « Plus comme ceci » (volet suivant : `Sagas.Next` sur les sagas du dernier calcul,
    `RecoService.SagaCollections`), filtre des suggestions, garde de compatibilité (coupe une partie qui ne colle
    plus à Jellyfin et prévient), notifications (webhook).
  - `Seerr/` : client et service (relevé des demandes et listes de suivi, fiches et listes TMDb en cache, rythme
    borné, disjoncteur après échecs répétés).
  - `Data/` : lecture de l'historique (Jellyfin, Playback Reporting, Jellyfin Enhanced), premiers lancements, titres
    montrés, secrets, journal (`LogTail`), export du jeu de calibrage (`CalibrationExport`).
  - `Api/RecoController.cs` : `RecoPlus/…` ; tout ce qui touche à la configuration exige les droits d'administration.
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
- `README.md` / `README.fr.md` : README classique (présentation, installation, prise en main) ; le détail (moteur,
  mesures, réglages, calibrage) est dans `docs/how-it-works.md` / `.fr.md`. Les deux langues restent alignées.
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

- Un commit par version, sur une branche `reco-1.0.N` partant de `origin/main` à jour ; livré en patch
  (`git format-patch`) avec son sha256, appliqué par Marien (`git am -3`), PR, CI, merge `--rebase`, publication
  automatique.
- Le bloc d'application ne suppose rien : il vérifie le sha256, vérifie que la base existe (`git rev-parse -q --verify`)
  et quelle version est sur `origin/main` (`meta.json`), applique seulement les patches plus récents, et travaille dans
  un `git worktree` temporaire (jamais dans la copie de travail de Marien, souvent modifiée). Plusieurs versions en
  attente : une série `format-patch` (une par version), pas des patches à appliquer sur des branches supposées.
- Merge seulement CI verte (boucle `gh pr checks` sans `watch`) ; en cas d'échec, le bloc affiche `gh run view
  --log-failed`. Une correction après push = un commit de plus sur la même branche avant merge.
- Une version publiée ne se modifie pas : une correction est une nouvelle version.
- Version : `Version`, `AssemblyVersion`, `FileVersion` du csproj, `meta.json` (version, changelog) et une entrée en tête
  de `manifest.json` (changelog en anglais commençant par « 1.0.N: », `sourceUrl`/`checksum`/`timestamp` vides).
  Un commit sans changement de `manifest.json` (documentation) ne publie rien.
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
  toi », les listes TMDb du moment (Tendances, Populaires, À venir ; avec Seerr, `TmdbListsAsync`, ordre
  `Trending.Order`), Nouveautés, « Parce que … » 1-3, suite du classement (`HomeRows.Suite` : séparée par défaut, ses
  moitiés gardent les identifiants historiques `Films` et `Series` ; réunie : `Suite`). Chaque rangée de
  `HomeRows.KindRows` se règle en films / séries / les deux / séparée (titre propre à chaque moitié) (`KindFilter.Split` : identifiants suffixés `Films` et `Series`, servis par
  `HssSectionHandler.GetRow` via `additionalData`) ; à l'enregistrement, `ReconcileSplits` met à jour l'accueil HSS de
  chaque compte (`SplitLayout.Apply`). La tendance TMDb entre aussi dans la popularité (`Dataset.Trending`,
  `TrendingShare`, OU probabiliste avec la popularité du foyer, parité avec le prototype) ; avec des titres hors bibliothèque, `PlaceExternals` choisit leur place selon le contenu de chaque rangée
  (bibliothèque seule, Seerr seul, mélange) et les titres de la bibliothèque remplacés passent aux rangées suivantes.
  Le serveur compose (`UserRecs.Layouts`), le script applique ; chaque rangée garde sa taille.
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
- Mémoire et durée : `SimCache` (index par jeu de vecteurs, borné), `ContextCache` (une version),
  `ChoiceModel.FeatureCache` (réglage de départ seulement), avancement pondéré par le coût mesuré de chaque étape.
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
    dans « Réglages avancés », le diagnostic en mode développeur ;
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
- Patch livré sur une branche supposée (`reco-1.0.N-1` jamais créée) : toujours vérifier la base (voir Livraison).
- `<input type=number min=30 step=30>` avec un défaut de 365 : invalide pour le navigateur, invisible tant que le champ
  est désactivé (voir Page de configuration).
- `git stash` avec le lien `tests/client/node_modules` (anciennement suivi) supprimé : le lien revient à la place du
  dossier installé. Le lien n'est plus suivi ; `npm install --no-save` dans `tests/client` suffit.
- `i18n.en.json` comme ressource incorporée : MSBuild y voit la culture « en » et la sort de l'assembly (satellite).
- `index.html` réécrite (Reco+, Jellyfin Enhanced) sans `ETag` ni date : un navigateur gardait l'ancienne page, donc
  l'ancien script, après une mise à jour (#50, réglé en vidant le cache). La page réécrite porte `Cache-Control: no-cache`.
