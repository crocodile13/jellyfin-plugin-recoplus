"""Bibliothèque et comptes simulés du test d'intégration (données fictives : aucun historique réel).

Les titres sont de vrais films et séries, pour que Jellyfin trouve leurs métadonnées sur TMDb (genres, mots-clés,
studios, personnes, collections) : c'est sur elles que le moteur travaille. Les fichiers vidéo sont factices.
"""

# (titre, année) — noms tels que Jellyfin les reconnaît (« Titre (Année) »)
MOVIES = [
    # Ghibli et animation japonaise
    ("Spirited Away", 2001), ("My Neighbor Totoro", 1988), ("Princess Mononoke", 1997), ("Howl's Moving Castle", 2004),
    ("Kiki's Delivery Service", 1989), ("Castle in the Sky", 1986), ("Nausicaä of the Valley of the Wind", 1984),
    ("Ponyo", 2008), ("The Wind Rises", 2013), ("Your Name.", 2016), ("A Silent Voice", 2016), ("Akira", 1988),
    ("Perfect Blue", 1997), ("Weathering with You", 2019),
    # Pixar / Disney (famille)
    ("Toy Story", 1995), ("Toy Story 2", 1999), ("Toy Story 3", 2010), ("Toy Story 4", 2019), ("Finding Nemo", 2003),
    ("Finding Dory", 2016), ("Monsters, Inc.", 2001), ("Up", 2009), ("Inside Out", 2015), ("Coco", 2017),
    ("Frozen", 2013), ("Frozen II", 2019), ("Moana", 2016), ("Zootopia", 2016), ("Ratatouille", 2007), ("WALL·E", 2008),
    # Marvel / super-héros
    ("Iron Man", 2008), ("Iron Man 2", 2010), ("Iron Man 3", 2013), ("The Avengers", 2012),
    ("Avengers: Age of Ultron", 2015), ("Avengers: Infinity War", 2018), ("Avengers: Endgame", 2019), ("Thor", 2011),
    ("Thor: Ragnarok", 2017), ("Captain America: The First Avenger", 2011), ("Guardians of the Galaxy", 2014),
    ("Black Panther", 2018), ("Spider-Man: Homecoming", 2017), ("The Dark Knight", 2008), ("Batman Begins", 2005),
    # action
    ("John Wick", 2014), ("John Wick: Chapter 2", 2017), ("John Wick: Chapter 3 - Parabellum", 2019),
    ("Mad Max: Fury Road", 2015), ("Mission: Impossible - Fallout", 2018), ("Die Hard", 1988), ("Gladiator", 2000),
    ("The Matrix", 1999), ("The Matrix Reloaded", 2003), ("Top Gun: Maverick", 2022),
    # horreur
    ("The Conjuring", 2013), ("The Conjuring 2", 2016), ("Insidious", 2010), ("Hereditary", 2018), ("Get Out", 2017),
    ("It Follows", 2014), ("The Babadook", 2014), ("A Quiet Place", 2018), ("The Shining", 1980), ("Midsommar", 2019),
    ("Us", 2019), ("Sinister", 2012),
    # policier, drame, « cinéphile »
    ("The Godfather", 1972), ("The Godfather Part II", 1974), ("Pulp Fiction", 1994), ("Goodfellas", 1990),
    ("Se7en", 1995), ("Fight Club", 1999), ("The Shawshank Redemption", 1994), ("Parasite", 2019),
    ("No Country for Old Men", 2007), ("Heat", 1995), ("Reservoir Dogs", 1992), ("Taxi Driver", 1976),
    ("Zodiac", 2007), ("Prisoners", 2013), ("Oldboy", 2003), ("The Departed", 2006),
    # science-fiction
    ("Interstellar", 2014), ("Inception", 2010), ("Blade Runner 2049", 2017), ("Arrival", 2016), ("Dune", 2021),
    ("Alien", 1979), ("Aliens", 1986), ("Ex Machina", 2014), ("The Martian", 2015),
    # sagas
    ("Harry Potter and the Philosopher's Stone", 2001), ("Harry Potter and the Chamber of Secrets", 2002),
    ("Harry Potter and the Prisoner of Azkaban", 2004), ("Harry Potter and the Goblet of Fire", 2005),
    ("The Lord of the Rings: The Fellowship of the Ring", 2001), ("The Lord of the Rings: The Two Towers", 2002),
    ("The Lord of the Rings: The Return of the King", 2003),
    # comédies françaises
    ("The Intouchables", 2011), ("Welcome to the Sticks", 2008), ("Le Dîner de cons", 1998), ("Les Visiteurs", 1993),
    ("Amélie", 2001), ("OSS 117: Cairo, Nest of Spies", 2006), ("Serial (Bad) Weddings", 2014),
    # comédies romantiques
    ("Notting Hill", 1999), ("Love Actually", 2003), ("Crazy, Stupid, Love.", 2011), ("La La Land", 2016),
    ("Pride & Prejudice", 2005), ("About Time", 2013),
    # documentaires
    ("Free Solo", 2018), ("My Octopus Teacher", 2020), ("March of the Penguins", 2005), ("Jiro Dreams of Sushi", 2011),
]

# (titre, année, saisons, épisodes par saison)
SERIES = [
    ("Attack on Titan", 2013, 2, 6), ("Fullmetal Alchemist: Brotherhood", 2009, 1, 8), ("Death Note", 2006, 1, 8),
    ("Cowboy Bebop", 1998, 1, 8), ("Breaking Bad", 2008, 2, 6), ("The Sopranos", 1999, 1, 6), ("The Wire", 2002, 1, 6),
    ("True Detective", 2014, 1, 6), ("Stranger Things", 2016, 2, 6), ("The Haunting of Hill House", 2018, 1, 6),
    ("Bluey", 2018, 1, 8), ("Avatar: The Last Airbender", 2005, 1, 8), ("Friends", 1994, 2, 6),
    ("The Office", 2005, 2, 6), ("Planet Earth", 2006, 1, 6), ("The Mandalorian", 2019, 1, 6), ("Loki", 2021, 1, 6),
    ("Dark", 2017, 1, 6), ("Narcos", 2015, 1, 6), ("Kaamelott", 2005, 1, 8),
]

# Comptes simulés : ce que chacun a regardé (jours écoulés depuis), aimé, laissé tomber. « attendu » décrit, pour le
# rapport, ce qu'un humain s'attendrait à voir en tête : la pertinence se juge à la lecture, pas par une règle.
#   watched : [(titre, jours)]            film vu ou série (tous les épisodes disponibles) vue
#   episodes : [(série, n, jours)]        n premiers épisodes vus
#   abandoned : [(film, part, jours)]     film arrêté à cette part, jamais repris
#   favorites : [titre]
#   dislikes : [titre]                    pouce vers le bas
#   hidden : [titre]                      masqué partout (Jellyfin Enhanced, configuration complète seulement)
#   reviews : [(titre, étoiles)]          avis noté 1-5 (Jellyfin Enhanced, configuration complète seulement)
#   requests / watchlist : [(titre, année, "movie"|"tv")]   hors bibliothèque, faits dans Seerr (configuration écosystème)
#   arrive : (titre, année)               une demande de ce compte qui arrive ensuite dans la bibliothèque
PERSONAS = {
    "anime": {
        "attendu": "Ghibli et animation japonaise (films et séries d'anime) en tête ; pas de super-héros ni d'horreur",
        "watched": [("Spirited Away", 200), ("My Neighbor Totoro", 150), ("Princess Mononoke", 90), ("Your Name.", 40),
                    ("Howl's Moving Castle", 20)],
        "episodes": [("Attack on Titan", 12, 60), ("Death Note", 8, 15)],
        "favorites": ["Spirited Away", "Death Note"],
        "abandoned": [("Love Actually", 0.2, 80)],
        "hidden": ["Weathering with You"],
        "requests": [("The Boy and the Heron", 2023, "movie")],
        "watchlist": [("Suzume", 2022, "movie")],
        "reviews": [("Your Name.", 5)],
    },
    "action": {
        "attendu": "Marvel, John Wick 3, action ; la suite logique des sagas commencées",
        "watched": [("Iron Man", 300), ("The Avengers", 250), ("Thor", 200), ("Guardians of the Galaxy", 120),
                    ("John Wick", 90), ("John Wick: Chapter 2", 60), ("Mad Max: Fury Road", 30), ("Die Hard", 10)],
        "episodes": [("The Mandalorian", 6, 20)],
        "favorites": ["John Wick"],
        "abandoned": [("Pride & Prejudice", 0.15, 100)],
        "requests": [("John Wick: Chapter 4", 2023, "movie")],
    },
    "famille": {
        "attendu": "Pixar / Disney, Toy Story 3 (volet suivant), Bluey ; rien d'interdit aux enfants",
        "watched": [("Toy Story", 120), ("Toy Story 2", 100), ("Finding Nemo", 80), ("Frozen", 60), ("Moana", 30),
                    ("Zootopia", 12)],
        "episodes": [("Bluey", 8, 5)],
        "favorites": ["Moana"],
        "hidden": ["Toy Story 3"],
        "requests": [("Inside Out 2", 2024, "movie")],
        "arrive": ("Inside Out 2", 2024),
        "reviews": [("Frozen", 1)],
    },
    "horreur": {
        "attendu": "horreur et épouvante (Conjuring 2 en suite logique), Hill House ; pas d'animation familiale",
        "watched": [("The Conjuring", 150), ("Insidious", 120), ("Hereditary", 90), ("Get Out", 60),
                    ("It Follows", 30), ("The Shining", 10)],
        "episodes": [("The Haunting of Hill House", 3, 7)],
        "dislikes": ["Frozen"],
        "reviews": [("The Shining", 5), ("Insidious", 2)],
        "requests": [("Talk to Me", 2023, "movie")],
        "watchlist": [("The Conjuring: The Devil Made Me Do It", 2021, "movie")],
    },
    "cinephile": {
        "attendu": "policier / drame d'auteur (Godfather II, Heat, Taxi Driver, Oldboy…), The Wire, True Detective",
        "watched": [("The Godfather", 400), ("Pulp Fiction", 300), ("Goodfellas", 200), ("Se7en", 150),
                    ("Parasite", 100), ("No Country for Old Men", 50), ("Zodiac", 20)],
        "episodes": [("Breaking Bad", 12, 40), ("The Sopranos", 6, 10)],
        "favorites": ["The Godfather", "Breaking Bad"],
        "abandoned": [("Frozen II", 0.1, 70)],
        "requests": [("The Irishman", 2019, "movie"), ("Better Call Saul", 2015, "tv")],
    },
    "potter": {
        "attendu": "Harry Potter 3 en tête (le premier volet pas encore vu), jamais le 4 avant le 3 ; Le Seigneur des anneaux",
        "watched": [("Harry Potter and the Philosopher's Stone", 40), ("Harry Potter and the Chamber of Secrets", 20),
                    ("The Lord of the Rings: The Fellowship of the Ring", 10)],
    },
    "nouveau": {
        "attendu": "aucun goût connu : ce que le foyer regarde, puis les mieux notés ; liste non arbitraire",
    },
}
