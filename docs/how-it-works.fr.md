# Comment fonctionne Reco+

*[English version](how-it-works.md) · [Retour au README](../README.fr.md)*

## Ce que le moteur prend en compte

- **Tout l'historique, avec décroissance.** Un titre vu il y a longtemps compte encore, mais moins. La demi-vie est de 90 jours (valeur MovieLens, Larrain, Parra et al., RecSys 2015).
- **Les abandons comptent contre ce qui leur ressemble, avec une marge.**
  - Un film arrêté entre 5 % et 70 % et jamais repris pendant 30 jours est un abandon.
  - Un film regardé en deux fois n'en est pas un.
  - Une longue série lâchée après un ou deux épisodes est aussi un abandon.
- **Le temps regardé.** Une série pèse selon les heures réellement regardées (durée de chaque épisode ; 45 min si inconnue) : 150 épisodes de 15 min ne pèsent pas autant que 150 de 45 min.
- **Les dates inconnues.** Un titre marqué « vu » sans date, ou marqué en masse (au moins 4 titres « commencés » — ou « finis » — à moins de 2 minutes d'écart), compte comme un goût **ancien** — ni ignoré, ni pris pour un coup de cœur d'hier. Un titre commencé bien avant puis marqué fini en masse garde sa vraie date de début. Avec Playback Reporting, la date d'un titre est sa dernière lecture *réelle*, que « marquer comme vu » ne réécrit pas.
- **Les fichiers remplacés.** Quand Radarr ou Sonarr remplace un fichier par une meilleure version, Jellyfin crée un nouvel élément et Playback Reporting garde les lectures sous l'ancien : elles sont rattachées au titre actuel par son nom (s'il est unique), ce qui lui rend sa vraie date de premier lancement.
- **Les favoris** pèsent davantage, au niveau où ils ont été donnés : une série en favori compte pleinement, une saison en partie (0,6), des épisodes un peu (0,3, atteint à 3 épisodes) — aimer un épisode, ce n'est pas aimer la série.
- **Ce que le compte a dit explicitement** prime sur ce que le visionnage laisse deviner, dans les deux sens : avis notés de Jellyfin Enhanced (1 à 5 étoiles), note ou pouce Jellyfin. Un titre masqué partout dans Jellyfin Enhanced (« pas intéressé ») n'est plus jamais proposé et fait descendre ce qui lui ressemble ; un masquage limité à une rangée d'accueil ou à des épisodes ne compte pas.
- **Aimé sans être vu ici** (pouce ou favori sur un titre jamais lu sur ce serveur — vu ailleurs, ou clic par erreur) : compte dans les goûts à moitié (réglable), pour guider sans dominer.
- **Les demandes Seerr** sont une intention déclarée *de court terme*, et **la liste de suivi Seerr** compte comme une demande, plus faible (0,4).
  - Demi-vie de 21 jours, ignorée après 90 jours.
  - Dès que le titre est regardé, c'est ce visionnage qui compte.
  - Elles nourrissent les goûts, mais ne poussent jamais **leur propre** titre : un titre demandé est classé sans sa propre demande, écarté juste après la demande ou son arrivée — montré dans aucune rangée pendant une demi-vie (le compte sait déjà qu'il le veut, pas besoin de le lui faire découvrir) — puis revient peu à peu s'il reste non vu (même demi-vie de 21 jours) : une vieille demande oubliée qui colle aux goûts du compte peut remonter.
- Un titre noté (avis, pouce) ou en favori n'est pas proposé : c'est un titre déjà vu (réglable : « Proposer aussi ce que le compte a déjà marqué »).
- **En tête de liste : les nouveautés de ce que le compte suit**, pendant 45 jours après leur arrivée (3 titres au plus), jamais un titre qu'il a demandé :
  - nouveaux épisodes ou nouvelle saison d'une série vue sur ce serveur et presque rattrapée (pas besoin de l'avoir aimée, juste un poids positif : ni abandonnée, ni masquée) ;
  - pour une série aimée sans être vue ici (on ignore jusqu'où il l'a vue), seulement les épisodes diffusés récemment ;
  - le volet suivant d'une saga dont les volets précédents sont vus (ou aimés) — pour une saga seulement aimée, seulement une sortie récente. Une saga vue en entier n'a plus rien à placer.
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
- **Les nouveaux comptes.** Sans goût connu, tous les titres ont le même score : on ajoute un a priori, ce que le foyer regarde puis la note de la communauté. Son poids diminue continûment avec les goûts du compte (moitié vers deux titres aimés, négligeable ensuite).
- **Les doublons.** Un titre présent deux fois dans la bibliothèque (même identifiant TMDb, par exemple dans deux bibliothèques) n'est proposé qu'une fois, et pas du tout si l'un des deux a été vu.
- **La rangée « Nouveautés »** : seulement les titres arrivés récemment qui figurent dans le premier quart du classement par goût ; un mois calme ne la remplit pas avec n'importe quoi.
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
  - Un seul calcul tourne à la fois. Le recalcul complet est une tâche planifiée, avec sa progression visible, lancée après des ajouts à la bibliothèque (au plus une fois par heure), au démarrage seulement si aucun compte n'a de liste (un compte sans liste est calculé seul), ou à la main (« Recalculer maintenant »). Pas de recalcul nocturne par défaut.
  - Un compte est recalculé une fois son activité calmée (2 minutes sans nouvelle fin de lecture, marque vu, favori ou note), et au plus une fois toutes les 15 minutes.
  - Enregistrer les réglages ou appliquer un calibrage ne relance rien : « Recalculer maintenant », sinon chaque compte en profite à sa prochaine activité.
  - La bibliothèque est réindexée après des ajouts, en regroupant les changements rapprochés.
- **Cohabitation avec les autres plugins.**
  - Le filtre des suggestions s'exécute au plus près de l'action, donc le filtre de contenu masqué de Jellyfin Enhanced s'applique toujours à sa réponse.
  - Le script web est ajouté au moment de la requête, comme le fait Jellyfin Enhanced, sans écrire dans le dossier web.

## Configuration

Tableau de bord → Extensions → Reco+. La page est organisée en onglets :

| Onglet | Contenu |
|---|---|
| État | vérifications de compatibilité, dernier calcul, comptes prêts |
| Général | ce qui est remplacé, titres des rangées Home Screen Sections, sources (dont les titres montrés sans être ouverts), comptes ignorés, langue de l'interface |
| Listes | ce qui est montré, appliqué après le classement : part de hasard du jour, minimums de films et de séries, genres exclus, note et année minimales, diversité (personnalisée, calibrée ou fixe), sagas, nouveautés de ce que le compte suit |
| Moteur *(avancé)* | chaque facteur de classement (favoris, demandes, abandons, ressemblances, vus ensemble, nouveauté, popularité, note, accord TMDb, caractéristiques de contenu) se coupe ou se règle en force (×0,25 à ×3) ; durées ; valeurs par défaut évaluées |
| Seerr | URL et clé, avec un bouton **Tester** qui utilise les valeurs saisies sans les enregistrer |
| Mode étendu | titres hors bibliothèque : activation ; contenu de chaque rangée d'accueil (bibliothèque seule, Seerr seul ou mélange — un titre hors bibliothèque ne va que dans une rangée, les titres de la bibliothèque qu'il remplace passent aux rangées suivantes, chaque rangée garde sa taille) ; mélange un toutes les N cartes, ou libre selon le classement commun du moteur (sur ce que les deux ont : goût, note, sortie récente), avec en option un minimum de titres hors bibliothèque montrés et des places « découverte » pour les mieux notés ; sources (chacune se coupe) : recommandations et titres similaires des titres aimés, volets manquants des sagas, filmographies des réalisateurs, créateurs et scénaristes récurrents, les mieux notés de TMDb tous genres, des genres, thèmes (mots-clés), studios et chaînes, langues d'origine préférés et de leurs croisements (genre × thème, genre × langue), et des pépites peu connues (très bien notées, peu de votes), lus à des pages tirées au hasard chaque jour ; hasard du jour réglable ; l'aperçu montre l'apport de chaque source ; note, nombre de votes et durée d'un film minimaux (courts métrages écartés) ; au recalcul complet, l'exploration Seerr n'est refaite que pour les comptes actifs récemment |
| Notifications | webhook, avec envoi de test |
| Calibrage *(avancé)* | méthode (automatique, modèle de choix appris, recherche par curseurs, réseau de neurones) avec un avertissement si l'historique ne suffit pas, résultat, application et retour en arrière ; voir ci-dessous |
| Aperçu | ce que voit actuellement un compte, rangée par rangée (et, en mode développeur, l'apport de chaque source du mode étendu) |
| Journal | mode développeur : le journal de Reco+ en temps réel (ou de tout Jellyfin), filtrable, secrets masqués |

Trois niveaux :

- **De base** : les onglets ci-dessus, sauf Moteur, Calibrage et Journal.
- **Avancé** : les onglets Moteur et Calibrage (toujours visibles, marqués *avancé*), et les sections repliées « Réglages avancés » de chaque onglet.
- **Mode développeur** (case à droite des onglets, retenue par navigateur) : l'onglet Journal, l'export du jeu de calibrage, les réglages statistiques du calibrage et le chemin étape par étape de chaque méthode, et l'apport de chaque source du mode étendu dans l'Aperçu.

**Langue.** Le plugin est en français et en anglais : page, rangées et cartes de l'accueil, notifications. *Celle de Jellyfin* (par défaut) suit la langue de l'interface Jellyfin de chacun (sinon celle du serveur) ; français ou anglais l'imposent à tous. Un titre de rangée laissé vide prend le titre par défaut dans la langue de celui qui le lit.

Chaque onglet montre l'essentiel ; les réglages avancés sont repliés. Survolez (ou touchez) le **?** à côté d'un réglage pour son aide et sa valeur par défaut. Un seul bouton **Enregistrer** sauvegarde à la fois les réglages et les secrets saisis.

### Calibrage

L'onglet Calibrage rejoue l'historique de votre serveur : chaque fois qu'un compte a commencé un nouveau titre, **à quel rang** ce titre figurait-il dans la liste calculée juste avant, avec seulement ce qui était connu alors ?

- **Données.** Exactement celles des recommandations : historique de lecture, demandes et listes de suivi Seerr, nombre de votes TMDb, avis et titres masqués de Jellyfin Enhanced ; comptes ignorés exclus. Rien n'est demandé à Seerr pendant un calibrage (dernier relevé et cache seulement).
- **Sans connaître la suite.** Chaque cas ne voit que ce qui existait à ce moment : un titre commencé avant mais fini après est vu en cours, une demande compte dès sa date. Un titre demandé ou mis en liste de suivi avant d'être regardé n'est pas un cas : le compte l'avait trouvé seul (en vrai, il est écarté à son arrivée, pas proposé).
- **Mesure.** Gain de rang 1/log2(1 + rang) sur *tous* les candidats, pas seulement « dans le top 10 ou non » : bien plus de puissance statistique à historique égal.
- **Cas.** Chaque titre commencé (date connue, hors marquage en masse) alors que le compte en avait déjà adopté au moins 2 ; un titre commencé sans être adopté (film arrêté, série à 1 ou 2 épisodes) compte à demi-poids. Chaque cas pèse selon l'historique déjà connu du compte (n/(n+3)), et les comptes sont équilibrés (1/√cas) pour qu'un gros spectateur ne décide pas seul.
- **Méthodes.**
  - *Modèle de choix appris* (par défaut) : à chaque cas, le compte a choisi un titre parmi tous ceux proposables ; on apprend d'un coup les 10 poids du score qui rendent ces choix les plus probables (logit conditionnel, Newton projeté, poids positifs et bornés). Régularisation L2 **vers les réglages actuels** — sans preuve, rien ne bouge — dont la force est choisie par validation croisée par comptes. Peu de paramètres : utilisable avec peu d'historique (environ 10 cas par poids), une minute de calcul.
  - *Recherche par curseurs* : un paramètre à la fois, dans des bornes sûres, plus de 30 réglages (contenu et signaux compris) : grille de 5 points (échelle log pour les poids, zéro essayé pour les facteurs facultatifs), puis affinage par section dorée ; choix discrets énumérés (dont la diversité) ; deux passes. Un changement n'est gardé que s'il améliore l'ensemble *et* ne perd sur aucune des deux moitiés des comptes. Il faut beaucoup plus d'historique (environ 20 cas par réglage).
  - *Réseau de neurones* (expérimental) : petite correction non linéaire (10 → 8 → 1, tanh, 96 paramètres) apprise sur les ingrédients du score et ajoutée au moteur ; elle part de zéro et la régularisation l'y ramène sans preuve. Il faut beaucoup d'historique (environ 10 cas par paramètre).
  - *Automatique* : toutes, et garde celle qui se généralise le mieux ; long et gourmand (jusqu'à une heure).
  - La page indique, pour chaque méthode, si l'historique est **suffisant, limite ou insuffisant**.
- **Validation.** Chaque méthode est refaite sans un groupe de comptes et mesurée sur ce groupe, pour chaque groupe (validation croisée par compte ; par défaut √(nombre de comptes) groupes, arrondi, entre 2 et 10 : autant de groupes que de comptes par groupe). La force de régularisation des modèles appris est choisie de la même façon.
- **Jusqu'où bouger.** Chaque méthode donne un chemin qui s'éloigne du réglage actuel : chaque changement gardé par la recherche par curseurs, ou un quart, la moitié et tout le chemin vers un modèle appris. La validation croisée mesure chaque point du chemin, et on garde le plus court qui fait à moins d'une erreur type du meilleur (règle « 1-SE » : sans preuve nette, on bouge moins ; le début du chemin, c'est garder l'actuel). C'est ce qui empêche la recherche par curseurs de sur-apprendre.
- **Recommandation.** Un réglage n'est **recommandé** que si son gain se retrouve chez des comptes qui n'ont pas servi à le choisir (intervalle de confiance à 95 % au-dessus de zéro, par bootstrap) et atteint au moins 1 % : en dessous, rien ne se verrait.
- **Réglages du calibrage lui-même** (onglet Calibrage, « Réglages du calibrage », mode développeur, enregistrés aussitôt ; 0 = automatique) : nombre de groupes de la validation croisée, des méthodes longues et du choix imbriqué de la régularisation, jusqu'où bouger (prudent = une erreur type, très prudent = deux, ou la meilleure étape), passes et affinages de la recherche par curseurs, gain minimal pour recommander. Les réglages sans objet pour la méthode choisie sont grisés. Un changement minime d'un poids (moins de 0,02 ou de 3 %) n'est pas proposé : ce n'est que du bruit.

Le jeu de calibrage peut être exporté, pseudonymisé (comptes renommés u01, u02…, ni nom ni identifiant), pour l'évaluer ailleurs : bouton **Exporter le jeu de calibrage** (mode développeur).

Rien n'est appliqué sans clic sur **Appliquer** ; le réglage précédent est gardé pour revenir en arrière. Il refuse de proposer quoi que ce soit avec moins de 50 cas exploitables. Il tourne comme une tâche planifiée, sur la moitié des cœurs par défaut (réglage « Fils d'exécution », qui fixe aussi le nombre de comptes calculés en même temps).

Sur le foyer de référence (144 cas sur 13 comptes, une fois les 22 faux cas d'un marquage en masse écartés et les titres commencés ajoutés à demi-poids) :

Chemin de chaque méthode (1.0.23, validation croisée à 5 groupes de comptes, sans connaître la suite ; écart avec les valeurs par défaut) :

| Méthode | Étapes | Sur l'historique, le long du chemin | Validation croisée, le long du chemin | Retenue |
|---|---|---|---|---|
| Recherche par curseurs | 18 | +0,1 % → +16,0 % | −0,7 % → −3,9 % (jamais au-dessus de 0) | aucune |
| Modèle de choix appris | ¼, ½, tout | 0 % → −0,3 % | −0,3 % → −1,3 % | aucune |
| Réseau de neurones | ¼, ½, tout | 0 % | ±0,1 % | aucune (aucun gain sur l'historique) |

La recherche par curseurs est l'exemple type du surapprentissage : chaque changement qu'elle garde améliore les cas qui ont servi à régler (+16 % après 18 changements) et dégrade des comptes neufs. Chaque point du chemin est maintenant mesuré : c'est visible, et rien n'est proposé ; les valeurs par défaut restent. C'est le garde-fou qui fonctionne, pas un échec. Durée en automatique : environ 27 minutes sur deux cœurs.

Les nouveautés ont été mesurées de la même façon avant de fixer leurs valeurs par défaut : le poids des séries selon le temps regardé fait aussi bien que le compte d'épisodes (et le laisser dépasser 1 pour les très grosses séries fait moins bien, d'où le plafond à 1) ; l'heure de la journée fait moins bien (−0,7 à −3,8 % : trop peu de visionnages par tranche et par compte) et reste coupée ; la diversité personnalisée fait +0,8 % de qualité de classement par rapport à la diversité fixe (IC95 [−0,1 % ; +2,4 %]) et devient la valeur par défaut ; la diversité calibrée est neutre à force modérée et perd à force élevée ; le goût du moment n'apporte rien sur cet historique (la demi-vie suit déjà les envies récentes) et reste coupé. Les titres montrés sans être ouverts ne peuvent pas être mesurés ainsi (aucun historique d'affichage avant la 1.0.5) : la pénalité par défaut est volontairement modérée.
