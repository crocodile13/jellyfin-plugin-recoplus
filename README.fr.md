<p align="center"><img src="assets/logo.svg" width="144" alt="Logo de Reco+"></p>

# Reco+ — recommandations personnelles pour Jellyfin

*[English version](README.md)*

Reco+ remplace les recommandations de Jellyfin par des listes calculées sur **tout** l'historique de chaque compte. Il a été construit et testé pour **Jellyfin 12.2** uniquement.

| Où | Jellyfin aujourd'hui | Avec Reco+ |
|---|---|---|
| `/Items/Suggestions` | du hasard | la liste classée du compte |
| `/Movies/Recommendations` (onglet Suggestions, Media Bar…) | proche des 7 derniers films et de quelques favoris au hasard | catégories « Parce que tu as aimé X » tirées de tout l'historique |
| « Plus comme ceci » (films) | recouvrement pondéré genres/mots-clés/personnes | ressemblance de contenu + un peu du goût du compte, volet suivant d'une saga d'abord |
| « Plus comme ceci » (séries) | des séries **au hasard** partageant un genre ou un mot-clé | ressemblance de contenu, comme pour les films |
| Home Screen Sections | — | rangée « Pour toi », et au choix « Nouveautés pour toi », jusqu'à trois « Parce que tu as aimé … », « Films pour toi », « Séries pour toi » — jamais un titre dans deux de ces rangées |
| Hors bibliothèque | — | facultatif : titres du catalogue Seerr/TMDb **mélangés dans la rangée « Pour toi »**, avec les cartes et le bouton *Demander* de Jellyfin Enhanced |

Tout est calculé en arrière-plan et gardé en cache, en mémoire et sur disque : aucune page n'attend un calcul. Un compte dont la liste n'est pas encore prête reçoit la réponse normale de Jellyfin.

## Ce que le moteur prend en compte

- **Tout l'historique, avec décroissance.** Un titre vu il y a longtemps compte encore, mais moins. La demi-vie est de 90 jours (valeur MovieLens, Larrain, Parra et al., RecSys 2015).
- **Les abandons comptent contre ce qui leur ressemble, avec une marge.**
  - Un film arrêté entre 5 % et 70 % et jamais repris pendant 30 jours est un abandon.
  - Un film regardé en deux fois n'en est pas un.
  - Une longue série lâchée après un ou deux épisodes est aussi un abandon.
- **Le temps regardé.** Une série pèse selon les heures réellement regardées (durée de chaque épisode ; 45 min si inconnue) : 150 épisodes de 15 min ne pèsent pas autant que 150 de 45 min.
- **Les dates inconnues.** Un titre marqué « vu » sans date, ou marqué en masse (au moins 4 titres « commencés » à moins de 2 minutes d'écart), compte comme un goût **ancien** — ni ignoré, ni pris pour un coup de cœur d'hier.
- **Les fichiers remplacés.** Quand Radarr ou Sonarr remplace un fichier par une meilleure version, Jellyfin crée un nouvel élément et Playback Reporting garde les lectures sous l'ancien : elles sont rattachées au titre actuel par son nom (s'il est unique), ce qui lui rend sa vraie date de premier lancement.
- **Les favoris** pèsent davantage, au niveau où ils ont été donnés : une série en favori compte pleinement, une saison en partie (0,6), des épisodes un peu (0,3, atteint à 3 épisodes) — aimer un épisode, ce n'est pas aimer la série.
- **Ce que le compte a dit explicitement** prime sur ce que le visionnage laisse deviner, dans les deux sens : avis notés de Jellyfin Enhanced (1 à 5 étoiles), note ou pouce Jellyfin. Un titre masqué partout dans Jellyfin Enhanced (« pas intéressé ») n'est plus jamais proposé et fait descendre ce qui lui ressemble ; un masquage limité à une rangée d'accueil ou à des épisodes ne compte pas.
- **La liste de suivi Seerr** compte comme une demande, plus faible (0,4) et aussi à court terme. Un titre seulement *voulu* (liste de suivi, demande ancienne, favori jamais regardé) reste proposable — c'est souvent le meilleur candidat.
- **Nouvelle saison** d'une série rattrapée et aimée : en tête pendant 45 jours après l'arrivée des nouveaux épisodes (2 séries au plus).
- **Les demandes Seerr** sont une intention déclarée *de court terme*.
  - Demi-vie de 21 jours, ignorée après 90 jours.
  - Dès que le titre est regardé, c'est ce visionnage qui compte.
  - Un titre demandé, arrivé et pas encore vu passe en tête pendant 30 jours.
- **Le contenu.** Vecteurs TF-IDF sur genres, mots-clés, studios, acteurs principaux, réalisateurs, scénaristes, créateurs, décennie et type — et, en option, langue originale et pays de production. Le goût du compte combine son profil d'ensemble et le titre aimé le plus proche.
- **En option, coupés par défaut** (aucun gain mesurable sur l'historique de référence ; le Calibrage peut les proposer sur le vôtre) : revisionnages, sortie *récente* (et non ajout récent), langue originale et pays. Le bonus de qualité peut utiliser la note de la communauté, celle des critiques, la meilleure des deux ou leur moyenne.
- **Confiance dans les notes.** La note de la communauté est tirée vers 6,5 quand elle repose sur peu de votes (moyenne bayésienne, 150 votes fictifs ; nombre de votes TMDb lu via Seerr) : 9/10 sur 12 votes ≈ 6,7, sur 20 000 votes = 9. **Les avis du foyer** (Jellyfin Enhanced, autres comptes seulement) donnent un bonus réservé selon la même idée : 2 avis neutres fictifs, donc un seul 5 étoiles pèse peu et cinq avis concordants beaucoup.
- **Les « vus ensemble » dans le foyer.** Co-occurrences entre titres avec rétrécissement (Ferrari Dacrema et al., 2019), pour qu'une seule personne qui regarde tout ne relie pas tous les titres entre eux.
- **Le garde-fou.**
  - La nouveauté, la popularité dans le foyer et la note de la communauté sont des *bonus multiplicatifs réservés aux titres qui figurent déjà parmi les 60 premiers par goût*.
  - Un titre tout neuf sans aucun rapport avec le compte ne reçoit aucun bonus.
  - Sans cette règle, les nouveautés étaient poussées à tout le monde : par exemple des animes ecchi récents à un compte qui ne regarde que du Ghibli.
  - La règle n'est pas désactivable, et la nouveauté est plafonnée.
- **La diversité, au choix.**
  - *Personnalisée* (par défaut) : la liste est aussi variée que ce que le compte aime. Plusieurs forces de Maximal Marginal Relevance (Carbonell & Goldstein, 1998) sont essayées ; on garde la liste dont la ressemblance interne est la plus proche de celle de ses titres aimés. Un éclectique reçoit une liste variée, le fan d'un seul genre une liste resserrée.
  - *Calibrée* (Steck, RecSys 2018) : la part de chaque genre dans la liste suit celle de l'historique (70 % de thrillers vus → environ 70 % proposés, pas 100 %).
  - *Fixe* : la même pénalité de redondance pour tous (comportement d'avant la 1.0.5).
- **Montré sans être ouvert.** Un titre resté plusieurs jours à l'écran d'un compte sans qu'il l'ouvre descend progressivement (au plus 40 % de son score), puis remonte avec le temps (demi-vie de 30 jours). Ne compte que ce que le script de l'accueil a vu **réellement affiché au compte connecté** : carte d'une rangée Reco+ visible au moins une seconde, onglet au premier plan, une fois par jour. Ouvrir le titre remet son compteur à zéro.
- **Goût du moment, en option** (coupé par défaut) : ressemblance avec les 5 derniers titres aimés, en plus des goûts de toujours.
- **Heure de la journée, en option** (coupée par défaut) : ressemblance avec les titres aimés regardés dans la même tranche horaire (nuit, matin, après-midi, soirée) ; une liste est alors calculée par tranche et servie selon l'heure de la demande (fuseau réglable).
- **Les sagas.**
  - Une seule entrée par saga, et c'est le premier volet pas encore vu, pas le 4ᵉ.
  - Les grands regroupements thématiques ont au plus deux entrées.

## Mesuré sur un vrai serveur (prudence avec ces chiffres)

Le test : chaque fois qu'un compte a commencé un nouveau titre, ce titre figurait-il dans le top 10 calculé juste avant, avec seulement ce qui était connu à ce moment-là ? On compte 126 cas sur 11 comptes.

| Méthode | Titre adopté dans le top 10 |
|---|---|
| Hasard (`/Items/Suggestions` de Jellyfin) | 2 % |
| Recommandations de Jellyfin 12 (réplique fidèle) | 11 % |
| **Reco+ (réglages par défaut)** | **18–19 %** |

- Reco+ trouve le titre 13 fois où Jellyfin le rate, et le rate 4 fois où Jellyfin le trouve.
- L'intervalle de confiance à 95 % du gain, calculé par tirage de comptes, va de **0 à +13 points**. Reco+ ne fait pas moins bien, mais avec si peu d'historique l'ampleur du gain reste incertaine.
- Une variante sans garde-fou atteignait 32 %, uniquement en recommandant les nouveautés à tout le monde. Elle a été écartée.
- La règle d'abandon des films n'a pas pu être validée : l'historique ne contenait que 4 films en cours.

## Sécurité et robustesse

- **Garde-fou de version.**
  - Au démarrage, Reco+ vérifie la version de Jellyfin et chaque point d'accroche qu'il utilise : enregistrement des services, actions et paramètres des contrôleurs, plugins facultatifs.
  - Une partie qui ne colle plus se coupe seule, et Jellyfin reprend son comportement normal pour elle. C'est aussi le cas d'une partie qui fait 5 erreurs en 10 minutes.
  - Vous êtes prévenu par le journal d'activité de Jellyfin, par une bannière rouge sur la page du plugin et, en option, par un webhook (ntfy, Gotify, Discord ou JSON).
- **Droits.**
  - Toutes les listes passent par les règles d'accès de Jellyfin pour le compte (bibliothèques, contrôle parental).
  - Un titre hors bibliothèque doit avoir une classification connue, au plus égale à la limite du compte. C'est plus strict que Jellyfin.
- **Secrets.**
  - La clé d'API Seerr, ainsi que l'URL et le jeton du webhook, sont stockés à part, dans un fichier en droits 600.
  - Ils ne sont jamais renvoyés par l'API : la page affiche seulement « défini (…1234) ».
- **Demandes à Seerr.**
  - Elles sont faites *au nom de l'utilisateur*, avec ses droits, ses quotas et son approbation automatique.
  - Rien n'est jamais demandé automatiquement.
- **Léger pour le serveur.**
  - L'historique est lu en une seule requête, en lecture seule.
  - Un seul calcul tourne à la fois. Le recalcul complet est une tâche planifiée, avec sa progression visible.
  - Un compte est recalculé 2 minutes après une lecture.
  - La bibliothèque est réindexée après des ajouts, en regroupant les changements rapprochés.
- **Cohabitation avec les autres plugins.**
  - Le filtre des suggestions s'exécute au plus près de l'action, donc le filtre de contenu masqué de Jellyfin Enhanced s'applique toujours à sa réponse.
  - Le script web est ajouté au moment de la requête, comme le fait Jellyfin Enhanced, sans écrire dans le dossier web.

## Installation

### Par le dépôt de plugins

Tableau de bord → Extensions → Dépôts, ajouter :

```
https://raw.githubusercontent.com/crocodile13/jellyfin-plugin-recoplus/main/manifest.json
```

Puis installer **Reco+** depuis le catalogue et redémarrer Jellyfin.

### À la main

1. Télécharger `RecoPlus_<version>.zip` depuis les [releases](https://github.com/crocodile13/jellyfin-plugin-recoplus/releases).
2. Vérifier son empreinte avec le fichier `.sha256` publié à côté.
3. L'extraire dans `<config>/plugins/RecoPlus_<version>/`, avec l'utilisateur de Jellyfin comme propriétaire.
4. Redémarrer Jellyfin.

## Configuration

Tableau de bord → Extensions → Reco+. La page est organisée en onglets :

| Onglet | Contenu |
|---|---|
| État | vérifications de compatibilité, dernier calcul, comptes prêts |
| Général | ce qui est remplacé, rangées Home Screen Sections, sources (dont les titres montrés sans être ouverts), comptes ignorés |
| Listes | ce qui est montré, appliqué après le classement : part de hasard du jour, minimums de films et de séries, genres exclus, note et année minimales, diversité (personnalisée, calibrée ou fixe), sagas, demandes en tête |
| Moteur | chaque facteur de classement (favoris, demandes, abandons, ressemblances, vus ensemble, nouveauté, popularité, note, accord TMDb, caractéristiques de contenu) se coupe ou se règle en force (×0,25 à ×3) ; durées ; valeurs par défaut évaluées |
| Seerr | URL et clé, avec un bouton **Tester** qui utilise les valeurs saisies sans les enregistrer |
| Mode étendu | titres hors bibliothèque : activation ; mélangés dans la rangée d'accueil un toutes les N cartes, ou librement selon le classement commun du moteur (sur ce que les deux ont : goût, note, sortie récente), avec un minimum de titres hors bibliothèque montrés (3 par défaut) |
| Notifications | webhook, avec envoi de test |
| Calibrage | méthode (automatique, modèle de choix appris, recherche par curseurs) avec un avertissement si l'historique ne suffit pas ; voir ci-dessous |
| Aperçu | ce que voit actuellement un compte, rangée par rangée |

Chaque onglet montre l'essentiel ; les réglages avancés sont repliés. Survolez (ou touchez) le **?** à côté d'un réglage pour son aide et sa valeur par défaut. Un seul bouton **Enregistrer** sauvegarde à la fois les réglages et les secrets saisis.

### Calibrage

L'onglet Calibrage rejoue l'historique de votre serveur : chaque fois qu'un compte a commencé un nouveau titre, **à quel rang** ce titre figurait-il dans la liste calculée juste avant, avec seulement ce qui était connu alors ?

- **Mesure.** Gain de rang 1/log2(1 + rang) sur *tous* les candidats, pas seulement « dans le top 10 ou non » : bien plus de puissance statistique à historique égal.
- **Cas.** Chaque titre commencé (date connue, hors marquage en masse) alors que le compte en avait déjà adopté au moins 2 ; un titre commencé sans être adopté (film arrêté, série à 1 ou 2 épisodes) compte à demi-poids. Chaque cas pèse selon l'historique déjà connu du compte (n/(n+3)), et les comptes sont équilibrés (1/√cas) pour qu'un gros spectateur ne décide pas seul.
- **Méthodes.**
  - *Modèle de choix appris* : à chaque cas, le compte a choisi un titre parmi tous ceux proposables ; on apprend d'un coup les 10 poids du score qui rendent ces choix les plus probables (logit conditionnel, Newton projeté, poids positifs et bornés). Régularisation L2 **vers les réglages actuels** — sans preuve, rien ne bouge — dont la force est choisie par validation croisée par comptes. Peu de paramètres : utilisable avec peu d'historique (environ 10 cas par poids), une minute de calcul.
  - *Recherche par curseurs* : un paramètre à la fois, dans des bornes sûres, plus de 30 réglages (contenu et signaux compris) : grille de 5 points (échelle log pour les poids, zéro essayé pour les facteurs facultatifs), puis affinage par section dorée ; choix discrets énumérés (dont la diversité) ; deux passes. Un changement n'est gardé que s'il améliore l'ensemble *et* ne perd sur aucune des deux moitiés des comptes. Il faut beaucoup plus d'historique (environ 20 cas par réglage).
  - *Réseau de neurones* (expérimental) : petite correction non linéaire (10 → 8 → 1, tanh, 96 paramètres) apprise sur les ingrédients du score et ajoutée au moteur ; elle part de zéro et la régularisation l'y ramène sans preuve. Il faut beaucoup d'historique (environ 10 cas par paramètre).
  - *Automatique* (par défaut) : toutes, et garde celle qui se généralise le mieux.
  - La page indique, pour chaque méthode, si l'historique est **suffisant, limite ou insuffisant**.
- **Validation.** Chaque méthode est refaite sur la moitié des comptes et mesurée sur l'autre (validation croisée à deux plis, par compte). Un réglage n'est **recommandé** que si son gain se retrouve chez des comptes qui n'ont pas servi à le choisir (intervalle de confiance à 95 % au-dessus de zéro, par bootstrap) et atteint au moins 1 % : en dessous, rien ne se verrait. Un changement minime d'un poids (moins de 0,02 ou de 3 %) n'est pas proposé : ce n'est que du bruit.

Rien n'est appliqué sans clic sur **Appliquer** ; le réglage précédent est gardé pour revenir en arrière. Il refuse de proposer quoi que ce soit avec moins de 50 cas exploitables. Il tourne comme une tâche planifiée, sur la moitié des cœurs.

Sur le foyer de référence (144 cas sur 13 comptes, une fois les 22 faux cas d'un marquage en masse écartés et les titres commencés ajoutés à demi-poids) :

| Méthode | Sur l'historique | Validation croisée (IC 95 %) | Historique |
|---|---|---|---|
| Recherche par curseurs | +17,8 % | −3,1 % [−11,5 ; +8,0] | insuffisant |
| Modèle de choix appris | −0,4 % | +0,7 % [−0,9 ; +4,5] | suffisant |
| Réseau de neurones | 0 % | −2,1 % [−6,0 ; +2,5] | insuffisant |

La recherche par curseurs est l'exemple type du surapprentissage : +18 % sur les cas qui ont servi à régler, −3 % sur des comptes neufs. Aucune méthode n'est recommandée et les valeurs par défaut restent. C'est le garde-fou qui fonctionne, pas un échec. Durée en automatique : environ 23 minutes sur un cœur (modèle de choix : une minute ; réseau : six).

Les nouveautés ont été mesurées de la même façon avant de fixer leurs valeurs par défaut : le poids des séries selon le temps regardé fait aussi bien que le compte d'épisodes (et le laisser dépasser 1 pour les très grosses séries fait moins bien, d'où le plafond à 1) ; l'heure de la journée fait moins bien (−0,7 à −3,8 % : trop peu de visionnages par tranche et par compte) et reste coupée ; la diversité personnalisée fait +0,8 % de qualité de classement par rapport à la diversité fixe (IC95 [−0,1 % ; +2,4 %]) et devient la valeur par défaut ; la diversité calibrée est neutre à force modérée et perd à force élevée ; le goût du moment n'apporte rien sur cet historique (la demi-vie suit déjà les envies récentes) et reste coupé. Les titres montrés sans être ouverts ne peuvent pas être mesurés ainsi (aucun historique d'affichage avant la 1.0.5) : la pénalité par défaut est volontairement modérée.

## Intégrations facultatives

| Plugin / service | Présent | Absent |
|---|---|---|
| Home Screen Sections | toutes les rangées Reco+ figurent dans sa liste de sections : c'est lui qui décide lesquelles afficher, dans quel ordre et pour quels comptes (Reco+ ne règle que leurs titres, sans redémarrage, et ne remplit que les rangées que HSS montre à chaque compte) ; les titres « Parce que tu as aimé X » sont propres à chaque compte dans l'interface web (« Parce que tu as aimé… » ailleurs) ; HSS affiche les rangées de plugins en paysage, et les titres hors bibliothèque prennent le même format | pas de rangée d'accueil, le reste marche |
| Jellyfin Enhanced | le mode étendu utilise ses cartes, sa fiche et son bouton *Demander*, avec l'état Seerr du moment (s'il est relié à Seerr) ; Reco+ l'attend quelques secondes au chargement et remplace ses propres cartes dès qu'il est prêt | cartes Reco+, qui ouvrent la page Seerr, et bouton *Demander* de Reco+ |
| Media Bar Enhanced | en profite automatiquement | — |
| Playback Reporting | dates de premier lancement précises pour le calibrage | calibrage moins précis ; Reco+ relève lui-même les premiers lancements à partir de son installation |
| Seerr (ex-Jellyseerr) | demandes comme signal, arrivées en tête, mode étendu | tout cela est coupé, le reste marche |
| LocalRecs | signalé comme doublon | — |

## Vie privée

Les titres montrés sans être ouverts restent sur le serveur (fichier du plugin, un an au plus) et ne servent qu'au classement du compte concerné ; l'interrupteur « Titres montrés sans être ouverts » les coupe.

Rien ne sort du serveur, à deux exceptions près :
- les appels à **votre** Seerr, si vous le configurez ;
- en mode étendu, le client web charge les affiches depuis `image.tmdb.org`.

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

- Construit pour Jellyfin 12.2. Les autres versions sont détectées et chaque point d'accroche est vérifié, mais elles ne sont pas testées.
- Les valeurs par défaut ont été choisies sur un seul petit foyer (environ 16 comptes actifs). Utilisez le calibrage sur le vôtre.
- Les titres hors bibliothèque sont ajoutés par un script du client web (mise en page Modern testée) : mélangés dans la rangée « Pour toi » de Home Screen Sections, ou en rangée à part sans HSS. Les autres clients (TV, applications mobiles) ne reçoivent que les recommandations de la bibliothèque.

## Licence

[GPL-3.0](LICENSE).
