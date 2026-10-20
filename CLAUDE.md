# Reco+ — notes de travail

Plugin Jellyfin 12.2 (.NET 10) : recommandations personnelles précalculées, rangées Home Screen Sections (HSS), mode
étendu (titres hors bibliothèque via Seerr). Utilisateur et mainteneur : Marien.

## Organisation

- `src/RecoPlus.Engine/` : moteur pur (aucune dépendance Jellyfin), testable seul. `Recommender` (score, classement),
  `Core` (vecteurs, signaux), `HomeRows` (répartition des rangées, placement des titres hors bibliothèque),
  `Calibrator` / `ChoiceModel` / `NeuralNet` (calibrage), `Exploration` (mode étendu), `Cpu` (fils d'exécution).
- `src/Jellyfin.Plugin.RecoPlus/` : intégration. `Service/RecoService.cs` (calcul complet ou d'un compte),
  `Integration/` (HSS par réflexion, injection du script, garde de compatibilité), `Seerr/`, `Api/RecoController.cs`,
  `Web/configPage.html` (page de configuration) et `Web/client.js` (script de l'accueil).
- `tests/RecoPlus.Tests/` (xUnit), `tests/client/*.test.mjs` (jsdom), `prototype/golden.py` (parité Python ↔ C#).
- `build.sh` : tests, compilation, paquet. La CI publie la version et remplit `sourceUrl`, `checksum`, `timestamp` du
  manifeste.

## Commandes

```
./build.sh                                                     # tout (SDK .NET 10)
dotnet test tests/RecoPlus.Tests -c Release
cd tests/client && npm install --no-save jsdom@24.1.3 && node client.test.mjs && node configpage.test.mjs
cd prototype && python golden.py ../tests/RecoPlus.Tests/golden.json
```

Sans accès NuGet (bac à sable) : le moteur et les tests se compilent avec un projet local qui référence
`RecoPlus.Engine.csproj` et inclut les fichiers de test ; le code du plugin se vérifie par compilation Roslyn
partielle en comparant les diagnostics avant/après le changement (seuls les nouveaux comptent).

## Conventions

- Texte de l'interface, commentaires et docs XML : français. Commits, titres et descriptions de PR : anglais.
- Auteur des commits : `crocodile13 <134718087+crocodile13@users.noreply.github.com>`.
- Aucun secret dans le code ni dans la configuration XML : clés et jetons dans le magasin de secrets à part.
- Version : `Version`, `AssemblyVersion`, `FileVersion` du csproj, `meta.json` (version et changelog) et une entrée en
  tête de `manifest.json` (changelog en français, `sourceUrl`/`checksum`/`timestamp` vides). Le changelog commence par
  « 1.0.N : ».
- `TreatWarningsAsErrors` : un avertissement casse la CI.
- Dans l'espace de noms `Jellyfin.Plugin.RecoPlus`, `RecoPlus.Engine.X` se résout mal (CS0234) : utiliser
  `using RecoPlus.Engine;`.
- Le moteur reste déterministe (mêmes entrées → mêmes listes) ; `golden.json` vérifie la parité au 1e-9.
- Calcul parallèle : un `Context` par fil (`ctx.Fork()`, seule la tranche horaire est propre au fil) ; rien de
  modifiable partagé entre comptes ; les appels Jellyfin/HSS se font avant la boucle parallèle.

## Page de configuration (`configPage.html`)

- Chaque réglage : une ligne du tableau `F` (clé, libellé, type, aide, pas, min, max, groupe, cible, défaut affiché).
  Le test `configpage.test.mjs` vérifie que chaque clé existe côté serveur.
- Aide au survol pour chaque réglage, valeur par défaut indiquée.
- Un réglage sans effet dans la configuration actuelle est **grisé et désactivé, avec la raison** (`WHY` / `reason`
  dans le script) ; une raison commune à toute une boîte est dite une fois en tête. Jamais masqué silencieusement.
  Sa valeur est gardée et enregistrée.
- Pas de réglage obsolète : un réglage retiré est migré (`Plugin.Migrate`) puis supprimé de la page.
- Titres des rangées : tous dans l'onglet Général ; ailleurs, `{ROW}` (rangée principale) ou `{T:Champ}` reprennent le
  titre saisi.
- Onglets Moteur, Calibrage, Journal : mode développeur seulement.

## Faits utiles (vérifiés dans les sources)

- HSS : une section de plugin a une instance (Limit 1, Min/Max ignorés) ; les sections de même ordre sont mélangées ;
  une section sans élément est masquée ; rangées de plugin en paysage par défaut. Classes DOM `RecoPlus<Rangée>`.
- Les titres hors bibliothèque n'existent que dans le client web (script) ; les applications TV ne voient que la
  bibliothèque. Le serveur compose chaque rangée (`UserRecs.Layouts`), le script applique.
- Media Bar Enhanced : n'utilise Reco+ que si son option « Personalized Recommendations » est active
  (`/Movies/Recommendations`, `/Users/{id}/Suggestions`, `/Items/{id}/Similar`) ; bibliothèque seulement.
- Jellyfin Enhanced : cartes et fiche Seerr réutilisées si présent et relié à Seerr ; sinon cartes Reco+.
