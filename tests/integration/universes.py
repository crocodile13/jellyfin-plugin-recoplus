"""Univers du banc réaliste : de vrais titres (métadonnées TMDb figées), rangés par grands goûts. Les comptes générés
(bench.py) piochent dans ces univers selon leur profil ; un même titre peut appartenir à plusieurs univers.
(titre, année, sorte) — sorte : "m" film, "s" série. Aucune donnée réelle de visionnage.
"""

U = {
    "ghibli_anime": [
        ("Spirited Away", 2001, "m"), ("My Neighbor Totoro", 1988, "m"), ("Princess Mononoke", 1997, "m"), ("Howl's Moving Castle", 2004, "m"),
        ("Kiki's Delivery Service", 1989, "m"), ("Castle in the Sky", 1986, "m"), ("Nausicaä of the Valley of the Wind", 1984, "m"),
        ("Ponyo", 2008, "m"), ("The Wind Rises", 2013, "m"), ("Porco Rosso", 1992, "m"), ("Whisper of the Heart", 1995, "m"),
        ("Grave of the Fireflies", 1988, "m"), ("The Boy and the Heron", 2023, "m"), ("Your Name.", 2016, "m"), ("A Silent Voice", 2016, "m"),
        ("Weathering with You", 2019, "m"), ("Suzume", 2022, "m"), ("Akira", 1988, "m"), ("Perfect Blue", 1997, "m"),
        ("Paprika", 2006, "m"), ("Wolf Children", 2012, "m"), ("Ghost in the Shell", 1995, "m"),
        ("Attack on Titan", 2013, "s"), ("Fullmetal Alchemist: Brotherhood", 2009, "s"), ("Death Note", 2006, "s"), ("Cowboy Bebop", 1998, "s"),
        ("Demon Slayer: Kimetsu no Yaiba", 2019, "s"), ("Jujutsu Kaisen", 2020, "s"), ("Spy x Family", 2022, "s"), ("Frieren: Beyond Journey's End", 2023, "s"),
        ("One Punch Man", 2015, "s"), ("Neon Genesis Evangelion", 1995, "s"), ("Haikyu!!", 2014, "s"), ("Mob Psycho 100", 2016, "s"),
    ],
    "famille": [
        ("Toy Story", 1995, "m"), ("Toy Story 2", 1999, "m"), ("Toy Story 3", 2010, "m"), ("Toy Story 4", 2019, "m"), ("Finding Nemo", 2003, "m"),
        ("Finding Dory", 2016, "m"), ("Monsters, Inc.", 2001, "m"), ("Up", 2009, "m"), ("Inside Out", 2015, "m"), ("Coco", 2017, "m"),
        ("Frozen", 2013, "m"), ("Frozen II", 2019, "m"), ("Moana", 2016, "m"), ("Zootopia", 2016, "m"), ("Ratatouille", 2007, "m"),
        ("WALL·E", 2008, "m"), ("The Incredibles", 2004, "m"), ("Incredibles 2", 2018, "m"), ("Encanto", 2021, "m"), ("Tangled", 2010, "m"),
        ("Shrek", 2001, "m"), ("Shrek 2", 2004, "m"), ("How to Train Your Dragon", 2010, "m"), ("How to Train Your Dragon 2", 2014, "m"),
        ("Kung Fu Panda", 2008, "m"), ("Despicable Me", 2010, "m"), ("Paddington", 2014, "m"), ("Paddington 2", 2017, "m"),
        ("The Lion King", 1994, "m"), ("Aladdin", 1992, "m"), ("Soul", 2020, "m"), ("Luca", 2021, "m"), ("The Mitchells vs. the Machines", 2021, "m"),
        ("Bluey", 2018, "s"), ("Avatar: The Last Airbender", 2005, "s"), ("Gravity Falls", 2012, "s"), ("Peppa Pig", 2004, "s"), ("Hilda", 2018, "s"),
    ],
    "superheros": [
        ("Iron Man", 2008, "m"), ("Iron Man 2", 2010, "m"), ("Iron Man 3", 2013, "m"), ("The Avengers", 2012, "m"),
        ("Avengers: Age of Ultron", 2015, "m"), ("Avengers: Infinity War", 2018, "m"), ("Avengers: Endgame", 2019, "m"), ("Thor", 2011, "m"),
        ("Thor: Ragnarok", 2017, "m"), ("Captain America: The First Avenger", 2011, "m"), ("Captain America: The Winter Soldier", 2014, "m"),
        ("Captain America: Civil War", 2016, "m"), ("Guardians of the Galaxy", 2014, "m"), ("Guardians of the Galaxy Vol. 2", 2017, "m"),
        ("Black Panther", 2018, "m"), ("Doctor Strange", 2016, "m"), ("Spider-Man: Homecoming", 2017, "m"), ("Spider-Man: No Way Home", 2021, "m"),
        ("Spider-Man: Into the Spider-Verse", 2018, "m"), ("The Dark Knight", 2008, "m"), ("Batman Begins", 2005, "m"), ("The Dark Knight Rises", 2012, "m"),
        ("The Batman", 2022, "m"), ("Joker", 2019, "m"), ("Logan", 2017, "m"), ("Deadpool", 2016, "m"), ("Wonder Woman", 2017, "m"),
        ("The Mandalorian", 2019, "s"), ("Loki", 2021, "s"), ("The Boys", 2019, "s"), ("Daredevil", 2015, "s"), ("WandaVision", 2021, "s"),
    ],
    "action": [
        ("John Wick", 2014, "m"), ("John Wick: Chapter 2", 2017, "m"), ("John Wick: Chapter 3 - Parabellum", 2019, "m"), ("John Wick: Chapter 4", 2023, "m"),
        ("Mad Max: Fury Road", 2015, "m"), ("Mission: Impossible - Fallout", 2018, "m"), ("Mission: Impossible - Ghost Protocol", 2011, "m"),
        ("Die Hard", 1988, "m"), ("Die Hard with a Vengeance", 1995, "m"), ("Gladiator", 2000, "m"), ("The Matrix", 1999, "m"), ("The Matrix Reloaded", 2003, "m"),
        ("Top Gun: Maverick", 2022, "m"), ("Terminator 2: Judgment Day", 1991, "m"), ("Aliens", 1986, "m"), ("Heat", 1995, "m"), ("Speed", 1994, "m"),
        ("The Raid", 2011, "m"), ("Casino Royale", 2006, "m"), ("Skyfall", 2012, "m"), ("The Bourne Identity", 2002, "m"), ("The Bourne Ultimatum", 2007, "m"),
        ("Edge of Tomorrow", 2014, "m"), ("Extraction", 2020, "m"), ("Nobody", 2021, "m"), ("Kill Bill: Vol. 1", 2003, "m"),
        ("Reacher", 2022, "s"), ("Jack Ryan", 2018, "s"), ("24", 2001, "s"),
    ],
    "horreur": [
        ("The Conjuring", 2013, "m"), ("The Conjuring 2", 2016, "m"), ("Insidious", 2010, "m"), ("Hereditary", 2018, "m"), ("Get Out", 2017, "m"),
        ("It Follows", 2014, "m"), ("The Babadook", 2014, "m"), ("A Quiet Place", 2018, "m"), ("The Shining", 1980, "m"), ("Midsommar", 2019, "m"),
        ("Us", 2019, "m"), ("Sinister", 2012, "m"), ("The Exorcist", 1973, "m"), ("Halloween", 1978, "m"), ("Alien", 1979, "m"), ("The Thing", 1982, "m"),
        ("It", 2017, "m"), ("Scream", 1996, "m"), ("The Witch", 2015, "m"), ("Talk to Me", 2023, "m"), ("Smile", 2022, "m"), ("Train to Busan", 2016, "m"),
        ("The Ring", 2002, "m"), ("Barbarian", 2022, "m"), ("X", 2022, "m"), ("Nope", 2022, "m"),
        ("The Haunting of Hill House", 2018, "s"), ("Midnight Mass", 2021, "s"), ("Stranger Things", 2016, "s"), ("The Walking Dead", 2010, "s"),
    ],
    "polar": [
        ("The Godfather", 1972, "m"), ("The Godfather Part II", 1974, "m"), ("Pulp Fiction", 1994, "m"), ("Goodfellas", 1990, "m"), ("Se7en", 1995, "m"),
        ("Fight Club", 1999, "m"), ("The Departed", 2006, "m"), ("No Country for Old Men", 2007, "m"), ("Reservoir Dogs", 1992, "m"), ("Taxi Driver", 1976, "m"),
        ("Zodiac", 2007, "m"), ("Prisoners", 2013, "m"), ("Oldboy", 2003, "m"), ("Memories of Murder", 2003, "m"), ("Gone Girl", 2014, "m"),
        ("The Silence of the Lambs", 1991, "m"), ("L.A. Confidential", 1997, "m"), ("Casino", 1995, "m"), ("The Irishman", 2019, "m"),
        ("Knives Out", 2019, "m"), ("Sicario", 2015, "m"), ("Drive", 2011, "m"), ("Nightcrawler", 2014, "m"), ("Mystic River", 2003, "m"),
        ("Breaking Bad", 2008, "s"), ("The Sopranos", 1999, "s"), ("The Wire", 2002, "s"), ("True Detective", 2014, "s"), ("Narcos", 2015, "s"),
        ("Better Call Saul", 2015, "s"), ("Mindhunter", 2017, "s"), ("Fargo", 2014, "s"), ("Peaky Blinders", 2013, "s"), ("Ozark", 2017, "s"),
    ],
    "scifi": [
        ("Interstellar", 2014, "m"), ("Inception", 2010, "m"), ("Blade Runner", 1982, "m"), ("Blade Runner 2049", 2017, "m"), ("Arrival", 2016, "m"),
        ("Dune", 2021, "m"), ("Dune: Part Two", 2024, "m"), ("Ex Machina", 2014, "m"), ("The Martian", 2015, "m"), ("2001: A Space Odyssey", 1968, "m"),
        ("Children of Men", 2006, "m"), ("Gravity", 2013, "m"), ("Annihilation", 2018, "m"), ("Her", 2013, "m"), ("Moon", 2009, "m"),
        ("District 9", 2009, "m"), ("Looper", 2012, "m"), ("Star Wars", 1977, "m"), ("The Empire Strikes Back", 1980, "m"), ("Back to the Future", 1985, "m"),
        ("Everything Everywhere All at Once", 2022, "m"), ("Edge of Tomorrow", 2014, "m"), ("The Matrix", 1999, "m"),
        ("Dark", 2017, "s"), ("Severance", 2022, "s"), ("The Expanse", 2015, "s"), ("Black Mirror", 2011, "s"), ("Westworld", 2016, "s"),
        ("Andor", 2022, "s"), ("Foundation", 2021, "s"),
    ],
    "fantasy": [
        ("Harry Potter and the Philosopher's Stone", 2001, "m"), ("Harry Potter and the Chamber of Secrets", 2002, "m"),
        ("Harry Potter and the Prisoner of Azkaban", 2004, "m"), ("Harry Potter and the Goblet of Fire", 2005, "m"),
        ("Harry Potter and the Order of the Phoenix", 2007, "m"), ("Harry Potter and the Half-Blood Prince", 2009, "m"),
        ("The Lord of the Rings: The Fellowship of the Ring", 2001, "m"), ("The Lord of the Rings: The Two Towers", 2002, "m"),
        ("The Lord of the Rings: The Return of the King", 2003, "m"), ("The Hobbit: An Unexpected Journey", 2012, "m"),
        ("Pan's Labyrinth", 2006, "m"), ("The Princess Bride", 1987, "m"), ("Stardust", 2007, "m"), ("Fantastic Beasts and Where to Find Them", 2016, "m"),
        ("The Chronicles of Narnia: The Lion, the Witch and the Wardrobe", 2005, "m"), ("Pirates of the Caribbean: The Curse of the Black Pearl", 2003, "m"),
        ("Game of Thrones", 2011, "s"), ("House of the Dragon", 2022, "s"), ("The Witcher", 2019, "s"), ("Arcane", 2021, "s"), ("The Wheel of Time", 2021, "s"),
        ("His Dark Materials", 2019, "s"), ("Shadow and Bone", 2021, "s"),
    ],
    "cinema_fr": [
        ("The Intouchables", 2011, "m"), ("Welcome to the Sticks", 2008, "m"), ("Le Dîner de cons", 1998, "m"), ("Les Visiteurs", 1993, "m"),
        ("Amélie", 2001, "m"), ("OSS 117: Cairo, Nest of Spies", 2006, "m"), ("Serial (Bad) Weddings", 2014, "m"), ("La Haine", 1995, "m"),
        ("The Artist", 2011, "m"), ("A Prophet", 2009, "m"), ("Portrait of a Lady on Fire", 2019, "m"), ("Anatomy of a Fall", 2023, "m"),
        ("Les Misérables", 2019, "m"), ("The Taste of Things", 2023, "m"), ("Jules and Jim", 1962, "m"), ("The 400 Blows", 1959, "m"),
        ("Asterix & Obelix: Mission Cleopatra", 2002, "m"), ("Les Bronzés font du ski", 1979, "m"), ("Delicatessen", 1991, "m"), ("Mesrine: Killer Instinct", 2008, "m"),
        ("Kaamelott", 2005, "s"), ("Call My Agent!", 2015, "s"), ("Lupin", 2021, "s"), ("The Bureau", 2015, "s"), ("Engrenages", 2005, "s"),
    ],
    "coree": [
        ("Parasite", 2019, "m"), ("Oldboy", 2003, "m"), ("Memories of Murder", 2003, "m"), ("Train to Busan", 2016, "m"), ("The Handmaiden", 2016, "m"),
        ("Burning", 2018, "m"), ("Mother", 2009, "m"), ("I Saw the Devil", 2010, "m"), ("Decision to Leave", 2022, "m"), ("The Wailing", 2016, "m"),
        ("Squid Game", 2021, "s"), ("Crash Landing on You", 2019, "s"), ("Kingdom", 2019, "s"), ("Extraordinary Attorney Woo", 2022, "s"),
        ("Goblin", 2016, "s"), ("Reply 1988", 2015, "s"), ("Itaewon Class", 2020, "s"), ("All of Us Are Dead", 2022, "s"),
    ],
    "romance": [
        ("Notting Hill", 1999, "m"), ("Love Actually", 2003, "m"), ("Crazy, Stupid, Love.", 2011, "m"), ("La La Land", 2016, "m"),
        ("Pride & Prejudice", 2005, "m"), ("About Time", 2013, "m"), ("The Notebook", 2004, "m"), ("Before Sunrise", 1995, "m"), ("Before Sunset", 2004, "m"),
        ("When Harry Met Sally...", 1989, "m"), ("Pretty Woman", 1990, "m"), ("10 Things I Hate About You", 1999, "m"), ("Crazy Rich Asians", 2018, "m"),
        ("Eternal Sunshine of the Spotless Mind", 2004, "m"), ("Call Me by Your Name", 2017, "m"), ("Titanic", 1997, "m"), ("Mamma Mia!", 2008, "m"),
        ("Bridgerton", 2020, "s"), ("Normal People", 2020, "s"), ("Emily in Paris", 2020, "s"), ("Outlander", 2014, "s"), ("Heartstopper", 2022, "s"),
    ],
    "drame": [
        ("The Shawshank Redemption", 1994, "m"), ("Forrest Gump", 1994, "m"), ("Schindler's List", 1993, "m"), ("Whiplash", 2014, "m"),
        ("The Social Network", 2010, "m"), ("There Will Be Blood", 2007, "m"), ("Moonlight", 2016, "m"), ("Manchester by the Sea", 2016, "m"),
        ("12 Years a Slave", 2013, "m"), ("The Green Mile", 1999, "m"), ("Good Will Hunting", 1997, "m"), ("Oppenheimer", 2023, "m"),
        ("Past Lives", 2023, "m"), ("Aftersun", 2022, "m"), ("Nomadland", 2020, "m"), ("Marriage Story", 2019, "m"), ("The Pianist", 2002, "m"),
        ("Succession", 2018, "s"), ("The Crown", 2016, "s"), ("Mad Men", 2007, "s"), ("The Bear", 2022, "s"), ("Chernobyl", 2019, "s"),
        ("This Is Us", 2016, "s"), ("The Last of Us", 2023, "s"),
    ],
    "comedie": [
        ("Superbad", 2007, "m"), ("The Grand Budapest Hotel", 2014, "m"), ("The Big Lebowski", 1998, "m"), ("Monty Python and the Holy Grail", 1975, "m"),
        ("Groundhog Day", 1993, "m"), ("The Hangover", 2009, "m"), ("Hot Fuzz", 2007, "m"), ("Shaun of the Dead", 2004, "m"), ("Bridesmaids", 2011, "m"),
        ("Step Brothers", 2008, "m"), ("Mean Girls", 2004, "m"), ("Barbie", 2023, "m"), ("Game Night", 2018, "m"),
        ("Friends", 1994, "s"), ("The Office", 2005, "s"), ("Brooklyn Nine-Nine", 2013, "s"), ("Parks and Recreation", 2009, "s"), ("Seinfeld", 1989, "s"),
        ("How I Met Your Mother", 2005, "s"), ("The Good Place", 2016, "s"), ("Ted Lasso", 2020, "s"), ("Schitt's Creek", 2015, "s"), ("Fleabag", 2016, "s"),
        ("What We Do in the Shadows", 2019, "s"), ("Abbott Elementary", 2021, "s"), ("Only Murders in the Building", 2021, "s"),
    ],
    "documentaire": [
        ("Free Solo", 2018, "m"), ("My Octopus Teacher", 2020, "m"), ("March of the Penguins", 2005, "m"), ("Jiro Dreams of Sushi", 2011, "m"),
        ("Man on Wire", 2008, "m"), ("Searching for Sugar Man", 2012, "m"), ("Senna", 2010, "m"), ("Amy", 2015, "m"), ("Icarus", 2017, "m"),
        ("The Social Dilemma", 2020, "m"), ("Won't You Be My Neighbor?", 2018, "m"), ("Fire of Love", 2022, "m"),
        ("Planet Earth", 2006, "s"), ("Planet Earth II", 2016, "s"), ("Our Planet", 2019, "s"), ("Blue Planet II", 2017, "s"), ("Cosmos: A Spacetime Odyssey", 2014, "s"),
        ("Making a Murderer", 2015, "s"), ("The Last Dance", 2020, "s"), ("Formula 1: Drive to Survive", 2019, "s"), ("Chef's Table", 2015, "s"), ("Tiger King", 2020, "s"),
    ],
}


def titles():
    """Tous les titres (sans doublon) : films et séries."""
    seen, movies, series = set(), [], []
    for items in U.values():
        for t, y, k in items:
            if (t, y) in seen:
                continue
            seen.add((t, y))
            (movies if k == "m" else series).append((t, y))
    return movies, series
