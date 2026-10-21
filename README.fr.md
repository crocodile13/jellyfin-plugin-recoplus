<p align="center"><img src="assets/logo.svg" width="144" alt="Logo de Reco+"></p>

<h1 align="center">Reco+</h1>

<p align="center"><b>Des recommandations personnelles pour Jellyfin, calculées sur tout l'historique de chaque compte.</b></p>

<p align="center">
  <a href="https://github.com/crocodile13/jellyfin-plugin-recoplus/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/crocodile13/jellyfin-plugin-recoplus?sort=semver"></a>
  <a href="https://github.com/crocodile13/jellyfin-plugin-recoplus/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/crocodile13/jellyfin-plugin-recoplus/ci.yml?branch=main&label=CI"></a>
  <img alt="Jellyfin 12.2" src="https://img.shields.io/badge/Jellyfin-12.2-00a4dc">
  <a href="LICENSE"><img alt="Licence : GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
</p>

<p align="center"><i><a href="README.md">English version</a></i></p>

Reco+ remplace les recommandations de Jellyfin (Suggestions, « Parce que vous avez regardé », « Plus comme ceci ») par des listes classées pour chaque compte, ajoute des rangées d'accueil via Home Screen Sections, et peut proposer des titres que vous n'avez pas encore via Seerr. Tout est calculé en arrière-plan et gardé en cache : aucune page n'attend.

## Fonctionnalités

- **De meilleures recommandations partout où Jellyfin en montre** : `/Items/Suggestions` (du hasard chez Jellyfin), `/Movies/Recommendations` (« Parce que tu as aimé X » tiré de tout l'historique, pas seulement des 7 derniers films), « Plus comme ceci » pour les films et les séries (des séries au hasard chez Jellyfin).
- **Rangées d'accueil** avec [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) : « Pour toi », « Nouveautés pour toi », jusqu'à trois « Parce que tu as aimé … », « Films pour toi », « Séries pour toi » — jamais deux fois le même titre.
- **Tout ce que le compte a exprimé compte** : historique complet avec oubli progressif, abandons, favoris (séries, saisons, épisodes), notes et avis de Jellyfin Enhanced et titres masqués, demandes et liste de suivi Seerr, titres montrés sans être ouverts.
- **Mode étendu** (facultatif, nécessite Seerr) : des titres hors bibliothèque dans les rangées d'accueil de votre choix — bibliothèque seule, Seerr seul ou mélange, rangée par rangée — avec les cartes et le bouton *Demander* de Jellyfin Enhanced.
- **Sûr par conception** : un garde-fou empêche de pousser des titres récents ou populaires à des comptes qu'ils ne concernent pas ; les droits d'accès et le contrôle parental de Jellyfin s'appliquent à chaque liste ; rien n'est jamais demandé automatiquement.
- **Calibrage** sur votre propre historique, avec validation croisée par compte : un réglage n'est proposé que s'il se généralise vraiment, et rien n'est appliqué sans votre clic.
- **Se protège seul** : chaque point d'accroche est vérifié au démarrage ; une partie qui ne colle plus se coupe et Jellyfin reprend son comportement normal, avec une alerte (journal d'activité, bannière, webhook facultatif).
- **Interface en français et en anglais**, selon la langue Jellyfin de chacun.

## Captures d'écran

<p align="center">
  <img src="assets/screenshots/accueil.jpg" alt="Accueil avec quatre rangées Reco+ : Pour toi (avec des titres hors bibliothèque), Nouveautés, Films, Séries" width="100%">
  <br><sub>Accueil avec Home Screen Sections : quatre rangées Reco+, jamais un titre deux fois. La première mélange des titres hors bibliothèque (cartes Seerr de Jellyfin Enhanced).</sub>
</p>

<p align="center">
  <img src="assets/screenshots/etat.png" alt="Onglet État : dernier calcul, comptes prêts, Playback Reporting, Seerr, Jellyfin Enhanced, vérifications de compatibilité" width="100%">
  <br><sub>Onglet État : ce qui a été lu, et les vérifications de compatibilité faites au démarrage.</sub>
</p>

<table>
  <tr>
    <td width="50%" valign="top"><img src="assets/screenshots/general.png" alt="Onglet Général : ce que Reco+ remplace, titres des rangées, retours explicites, comptes ignorés"><br><sub><b>Général</b> — ce que Reco+ remplace, titres des rangées, retours explicites, comptes ignorés.</sub></td>
    <td width="50%" valign="top"><img src="assets/screenshots/listes.png" alt="Onglet Listes : composition du haut de liste, filtres, genres exclus, diversité et sagas, titres en tête"><br><sub><b>Listes</b> — ce qui est montré : hasard, minimums, filtres, diversité, sagas, titres en tête.</sub></td>
  </tr>
  <tr>
    <td width="50%" valign="top"><img src="assets/screenshots/seerr.png" alt="Onglet Seerr : connexion, URL interne et publique, clé stockée à part"><br><sub><b>Seerr</b> — test de connexion ; la clé est stockée à part et jamais renvoyée.</sub></td>
    <td width="50%" valign="top"><img src="assets/screenshots/notifications.png" alt="Onglet Notifications : type de webhook, URL et jeton stockés comme secrets"><br><sub><b>Notifications</b> — webhook quand une partie se coupe d'elle-même.</sub></td>
  </tr>
</table>

<sub>Interface en français et en anglais (captures en français). Captures de la version 1.0.20 sur un vrai serveur : noms des comptes remplacés, affiches floutées, titres inventés.</sub>

<sub>PS : la pluie n'est pas une fonctionnalité de Reco+. C'est un délire perso de l'instance de « prod » de l'auteur, sur laquelle ces captures ont été prises. Le plugin recommande des films, il ne fait pas la météo.</sub>

## Prérequis

- **Jellyfin 12.2** (les autres versions sont détectées et chaque point d'accroche est vérifié, mais elles ne sont pas testées).
- Facultatif : [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) pour les rangées d'accueil, [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) pour les avis, titres masqués et cartes Seerr, [Playback Reporting](https://github.com/jellyfin/jellyfin-plugin-playbackreporting) pour dater précisément les premières lectures, et une instance [Seerr](https://github.com/seerr-team/seerr) pour les demandes et le mode étendu.

## Installation

### Par le dépôt de plugins (recommandé)

1. Dans **Tableau de bord → Extensions → Dépôts**, ajoutez :
   ```
   https://raw.githubusercontent.com/crocodile13/jellyfin-plugin-recoplus/main/manifest.json
   ```
2. Installez **Reco+** depuis le catalogue.
3. Redémarrez Jellyfin.

### À la main

1. Téléchargez `RecoPlus_<version>.zip` depuis les [releases](https://github.com/crocodile13/jellyfin-plugin-recoplus/releases) et vérifiez-le avec le `.sha256` publié.
2. Décompressez-le dans `<config>/plugins/RecoPlus_<version>/`, propriété de l'utilisateur de Jellyfin.
3. Redémarrez Jellyfin.

## Prise en main

1. Ouvrez **Tableau de bord → Extensions → Reco+**. Les valeurs par défaut sont celles qui ont été évaluées : Reco+ fonctionne dès l'installation.
2. Cliquez sur **Recalculer maintenant** dans l'onglet État (sinon chaque compte est calculé à sa prochaine activité).
3. Avec Home Screen Sections : ajoutez les rangées Reco+ voulues dans ses réglages (« Add Section ») ; leurs titres se règlent dans l'onglet Général.
4. Avec Seerr : saisissez son URL et sa clé d'API dans l'onglet Seerr, cliquez sur **Tester**, puis activez le mode étendu si vous voulez des titres hors bibliothèque.
5. Vérifiez ce que reçoit un compte dans l'onglet **Aperçu**.

Les réglages de base sont visibles d'emblée ; les avancés sont repliés ou dans les onglets **Moteur** et **Calibrage** ; les outils de diagnostic (journal, export du jeu de calibrage) sont en **mode développeur**. Survolez le **?** d'un réglage pour son aide.

## Intégrations facultatives

| Plugin / service | Présent | Absent |
|---|---|---|
| Home Screen Sections | toutes les rangées Reco+ figurent dans sa liste de sections : c'est lui qui décide lesquelles afficher, dans quel ordre et pour quels comptes (Reco+ ne règle que leurs titres, sans redémarrage, et ne remplit que les rangées que HSS montre à chaque compte) ; les titres « Parce que tu as aimé X » sont propres à chaque compte dans l'interface web (« Parce que tu as aimé… » ailleurs) ; HSS affiche les rangées de plugins en paysage, et les titres hors bibliothèque prennent le même format | pas de rangée d'accueil, le reste marche |
| Jellyfin Enhanced | le mode étendu utilise ses cartes, sa fiche et son bouton *Demander*, avec l'état Seerr du moment (s'il est relié à Seerr) ; Reco+ l'attend quelques secondes au chargement et remplace ses propres cartes dès qu'il est prêt | cartes Reco+, qui ouvrent la page Seerr, et bouton *Demander* de Reco+ |
| Media Bar Enhanced | avec son option « Personalized Recommendations » activée, montre les recommandations Reco+ (il appelle les points d'entrée remplacés ; bibliothèque seulement) | — |
| Playback Reporting | dates de premier lancement précises pour le calibrage | calibrage moins précis ; Reco+ relève lui-même les premiers lancements à partir de son installation |
| Seerr (ex-Jellyseerr) | demandes comme signal, arrivées en tête, mode étendu | tout cela est coupé, le reste marche |
| LocalRecs | signalé comme doublon | — |

## Vie privée

Rien ne quitte le serveur, sauf les appels à **votre** instance Seerr quand vous la configurez et, en mode étendu, les affiches chargées par le client web depuis `image.tmdb.org`. Les titres montrés sans être ouverts restent sur le serveur (un an au plus) et ne jouent que sur le classement de ce compte.

## Documentation

- [Comment fonctionne Reco+](docs/how-it-works.fr.md) : ce que le moteur prend en compte, mesures, sécurité, chaque réglage, calibrage.

## Développement

```
src/RecoPlus.Engine/          le moteur (.NET pur, sans dépendance à Jellyfin)
src/Jellyfin.Plugin.RecoPlus/ intégration Jellyfin, Seerr, API, page et script web
tests/RecoPlus.Tests/         tests unitaires + tests de parité avec le prototype Python
prototype/                    moteur de référence en Python et évaluation hors ligne qui a servi à fixer les réglages
tools/reco-export/            export pseudonymisé, en lecture seule, de l'historique d'un serveur, pour l'évaluation
```

Pour compiler et tester :

```bash
./build.sh
# ou, sans SDK .NET local :
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 ./build.sh
```

Le moteur C# est vérifié pour reproduire exactement le prototype Python :
- scores à 1e-9 près et listes identiques, sur un jeu synthétique que la CI régénère à chaque exécution (`prototype/golden.py`) ;
- pendant le développement, 0 écart sur les 126 cas d'évaluation des données réelles.

La CI compile et teste à chaque push. Quand une nouvelle version apparaît dans `manifest.json`, une release est publiée et le manifeste est complété automatiquement.

## Limites

- Construit et testé pour Jellyfin 12.2 uniquement.
- Les valeurs par défaut ont été choisies sur un seul petit foyer : utilisez le Calibrage sur le vôtre.
- Les titres hors bibliothèque sont ajoutés par un script du client web ; les applications TV et mobiles ne reçoivent que les recommandations de la bibliothèque.

## Licence

[GPL-3.0](LICENSE).
