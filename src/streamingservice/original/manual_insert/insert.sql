USE streamingservice;

INSERT INTO notification_reasons (reason_description)
VALUES 
('Content recommendation based on user watch history'),
('Subscription about to expire'),
('System maintenance notification'),
('New content added to a preferred genre'),
('Content removed from platform'),
('Promotional offer or discount');

INSERT INTO subtitle(content_id, language_id)
VALUES
(1, 1),
(1, 2),
(1, 3),
(2, 1),
(2, 4),
(3, 2),
(3, 5),
(4, 7),
(4, 2),
(5, 6),
(5, 1),
(5, 2),
(6, 2),
(6, 3),
(6, 8),
(7, 2),
(7, 9),
(8, 2),
(8, 8),
(9, 2),
(9, 11),
(10, 2),
(11, 2),
(12, 2),
(13, 2),
(14, 2),
(15, 2),
(16, 2),
(17, 2),
(18, 2),
(19, 2),
(20, 2);

INSERT INTO payments (user_id, date, amount, subscription_plan_id) VALUES
(21, '2024-01-15 10:00:00', 7.99, 4),
(21, '2024-02-15 10:00:00', 7.99, 4),
(21, '2024-03-15 10:00:00', 7.99, 4),
(22, '2024-01-16 12:00:00', 9.99, 5),
(22, '2024-02-16 12:00:00', 9.99, 5),
(22, '2024-03-16 12:00:00', 9.99, 5),
(23, '2024-01-17 14:00:00', 11.99, 6),
(23, '2024-02-17 14:00:00', 11.99, 6),
(23, '2024-03-17 14:00:00', 11.99, 6),
(24, '2024-01-18 16:00:00', 7.99, 4),
(24, '2024-02-18 16:00:00', 7.99, 4),
(24, '2024-03-18 16:00:00', 7.99, 4),
(25, '2024-01-19 18:00:00', 9.99, 5),
(25, '2024-02-19 18:00:00', 9.99, 5),
(25, '2024-03-19 18:00:00', 9.99, 5),
(26, '2024-01-20 20:00:00', 11.99, 6),
(26, '2024-02-20 20:00:00', 11.99, 6),
(26, '2024-03-20 20:00:00', 11.99, 6),
(27, '2024-01-21 10:00:00', 7.99, 4),
(27, '2024-02-21 10:00:00', 7.99, 4),
(27, '2024-03-21 10:00:00', 7.99, 4),
(28, '2024-01-22 12:00:00', 9.99, 5),
(28, '2024-02-22 12:00:00', 9.99, 5),
(28, '2024-03-22 12:00:00', 9.99, 5),
(29, '2024-01-23 14:00:00', 11.99, 6),
(29, '2024-02-23 14:00:00', 11.99, 6),
(29, '2024-03-23 14:00:00', 11.99, 6),
(30, '2024-01-24 16:00:00', 7.99, 4),
(30, '2024-02-24 16:00:00', 7.99, 4),
(30, '2024-03-24 16:00:00', 7.99, 4),
(31, '2024-01-25 18:00:00', 9.99, 5),
(31, '2024-02-25 18:00:00', 9.99, 5),
(31, '2024-03-25 18:00:00', 9.99, 5),
(32, '2024-01-26 20:00:00', 11.99, 6),
(32, '2024-02-26 20:00:00', 11.99, 6),
(32, '2024-03-26 20:00:00', 11.99, 6),
(33, '2024-01-27 10:00:00', 7.99, 4),
(33, '2024-02-27 10:00:00', 7.99, 4),
(33, '2024-03-27 10:00:00', 7.99, 4),
(34, '2024-01-28 12:00:00', 9.99, 5),
(34, '2024-02-28 12:00:00', 9.99, 5),
(34, '2024-03-28 12:00:00', 9.99, 5),
(35, '2024-01-29 14:00:00', 11.99, 6),
(35, '2024-02-29 14:00:00', 11.99, 6),
(35, '2024-03-29 14:00:00', 11.99, 6),
(36, '2024-01-30 16:00:00', 7.99, 4),
(36, '2024-02-28 16:00:00', 7.99, 4),
(36, '2024-03-30 16:00:00', 7.99, 4),
(37, '2024-01-31 18:00:00', 9.99, 5),
(37, '2024-02-29 18:00:00', 9.99, 5),
(37, '2024-03-31 18:00:00', 9.99, 5),
(38, '2024-01-01 10:00:00', 11.99, 6),
(38, '2024-02-01 10:00:00', 11.99, 6),
(38, '2024-03-01 10:00:00', 11.99, 6),
(39, '2024-01-02 12:00:00', 7.99, 4),
(39, '2024-02-02 12:00:00', 7.99, 4),
(39, '2024-03-02 12:00:00', 7.99, 4),
(40, '2024-01-03 14:00:00', 9.99, 5),
(40, '2024-02-03 14:00:00', 9.99, 5),
(40, '2024-03-03 14:00:00', 9.99, 5);

INSERT INTO comment (user_id, content_id, comment)
VALUES
(21, 1, 'Amazing storyline, keeps me hooked!'),
(22, 2, 'Historical drama at its finest.'),
(23, 3, 'The character development is fantastic.'),
(24, 4, 'This episode was so emotional.'),
(25, 5, 'Such a suspenseful turn of events!'),
(26, 6, 'The humor in this episode was spot on.'),
(27, 7, 'An intense and thrilling episode.'),
(28, 8, 'Loved the cliffhanger ending.'),
(29, 9, 'This one was a bit slower but still great.'),
(30, 10, 'The visuals in this episode were stunning.'),
(31, 11, 'The twists just keep getting better!'),
(32, 12, 'Such a powerful performance by the cast.'),
(33, 13, 'The plot is unraveling beautifully.'),
(34, 14, 'This episode is a game-changer.'),
(35, 15, 'Every detail was so well thought out.'),
(36, 16, 'Hilarious and heartwarming at the same time.'),
(37, 17, 'A dark but gripping episode.'),
(38, 18, 'This one had me on the edge of my seat.'),
(39, 19, 'Such clever writing and direction.'),
(40, 20, 'This episode had a lot of depth.'),
(21, 21, 'The ending was so unexpected!'),
(22, 22, 'This one made me think a lot.'),
(23, 23, 'The action sequences were incredible.'),
(24, 24, 'A very emotional and touching episode.'),
(25, 25, 'The tension just keeps building.'),
(26, 26, 'This one had me laughing out loud.'),
(27, 27, 'A great mix of action and drama.'),
(28, 1, 'The characters really shone in this episode.'),
(29, 2, 'Loved the attention to historical details.'),
(30, 3, 'One of the best episodes so far!'),
(31, 4, 'Such an intense and moving storyline.'),
(32, 5, 'The pacing in this episode was perfect.'),
(33, 6, 'A light-hearted break from the drama.'),
(34, 7, 'This episode changed everything!'),
(35, 8, 'A brilliant setup for the next episode.'),
(36, 9, 'The soundtrack was amazing.'),
(37, 10, 'This one had so many twists and turns.'),
(38, 11, 'A masterclass in storytelling.'),
(39, 12, 'The chemistry between the leads was great.'),
(40, 13, 'An absolute rollercoaster of emotions.'),
(21, 14, 'The episode was both hilarious and touching.'),
(22, 15, 'The writing in this episode was brilliant.'),
(23, 16, 'I didn’t see that ending coming!'),
(24, 17, 'The character arcs are so compelling.'),
(25, 18, 'This one was so thought-provoking.'),
(26, 19, 'The dialogue was top-notch.'),
(27, 20, 'This episode had everything: drama, action, humor.'),
(28, 21, 'A satisfying conclusion to the storyline.'),
(29, 22, 'Loved how the story tied back to earlier episodes.'),
(30, 23, 'A heartbreaking yet beautiful episode.'),
(31, 24, 'This one made me cry.'),
(32, 25, 'A standout episode for the season.'),
(33, 26, 'So many jaw-dropping moments!'),
(34, 27, 'This was a turning point for the series.'),
(35, 1, 'I’m so invested in these characters now.'),
(36, 2, 'The action scenes were so well done.'),
(37, 3, 'This one kept me guessing until the end.'),
(38, 4, 'Such a rich and emotional storyline.'),
(39, 5, 'A fantastic payoff to the buildup.'),
(40, 6, 'This episode had me laughing and crying.'),
(21, 7, 'A lot of unexpected twists.'),
(22, 8, 'The stakes just keep getting higher.'),
(23, 9, 'This was a standout episode for the series.'),
(24, 10, 'The visuals and cinematography were amazing.'),
(25, 11, 'A very satisfying resolution to the conflict.'),
(26, 12, 'This episode was pure genius.'),
(27, 13, 'The humor was perfectly balanced with the drama.'),
(28, 14, 'This one was emotionally draining, but so good.'),
(29, 15, 'A powerful and unforgettable episode.'),
(30, 16, 'The storytelling in this series is unmatched.'),
(31, 17, 'This episode had me on the edge of my seat.'),
(32, 18, 'The performances were incredible.'),
(33, 19, 'A fantastic blend of suspense and humor.'),
(34, 20, 'This episode added so much depth to the story.'),
(35, 21, 'A very clever and engaging episode.'),
(36, 22, 'The buildup to this episode was worth it.'),
(37, 23, 'The emotional payoff was perfect.'),
(38, 24, 'A great mix of action and character moments.'),
(39, 25, 'This episode hit all the right notes.'),
(40, 26, 'A standout performance by the lead actor.'),
(21, 27, 'This episode left me speechless.'),
(22, 1, 'The opening scene was phenomenal.'),
(23, 2, 'This episode had a lot of heart.'),
(24, 3, 'Such a thrilling and intense episode.'),
(25, 4, 'The ending left me wanting more.'),
(26, 5, 'A fun and lighthearted episode.'),
(27, 6, 'The drama in this one was next level.'),
(28, 7, 'The twists just keep coming.'),
(29, 8, 'This one was a great callback to earlier episodes.'),
(30, 9, 'The performances were top-notch.'),
(31, 10, 'The visuals were absolutely stunning.'),
(32, 11, 'This episode had me hooked from start to finish.'),
(33, 12, 'The writing was so sharp and clever.'),
(34, 13, 'This one had a perfect mix of humor and drama.'),
(35, 14, 'The characters really came into their own in this episode.');

INSERT INTO usercontentinteraction (content_id, user_id, liked, rating)
VALUES
(1, 21, 1, 9.2),
(2, 22, 0, 6.8),
(3, 23, 1, 8.5),
(4, 24, 1, 10.0),
(5, 25, 0, 4.1),
(6, 26, 1, 7.7),
(7, 27, 0, 3.3),
(8, 28, 1, 9.6),
(9, 29, 0, 5.9),
(10, 30, 1, 6.5),
(11, 31, 1, 8.8),
(12, 32, 0, 3.9),
(13, 33, 1, 7.2),
(14, 34, 1, 9.8),
(15, 35, 0, 5.6),
(16, 36, 1, 4.4),
(17, 37, 0, 6.3),
(18, 38, 1, 7.1),
(19, 39, 1, 8.4),
(20, 40, 0, 3.8),
(21, 21, 1, 7.9),
(22, 22, 0, 2.9),
(23, 23, 1, 6.7),
(24, 24, 1, 9.1),
(25, 25, 0, 5.4),
(26, 26, 1, 8.0),
(27, 27, 0, 3.7);

INSERT INTO watchhistory (content_id, user_id, timestamp, date)
VALUES
(1, 21, 125, '2023-05-30'),
(2, 22, 300, '2023-06-29'),
(3, 23, 450, '2023-07-28'),
(1, 21, 125, '2024-05-30'),
(2, 22, 300, '2024-06-29'),
(3, 23, 450, '2024-07-28'),
(4, 24, 600, '2024-08-27'),
(5, 25, 700, '2024-09-26'),
(1, 21, 125, '2024-11-30'),
(2, 22, 300, '2024-11-29'),
(3, 23, 450, '2024-11-28'),
(4, 24, 600, '2024-11-27'),
(5, 25, 700, '2024-11-26'),
(6, 26, 100, '2024-11-25'),
(7, 27, 200, '2024-11-24'),
(8, 28, 300, '2024-11-23'),
(9, 29, 400, '2024-11-22'),
(10, 30, 500, '2024-11-21'),
(11, 31, 125, '2024-11-20'),
(12, 32, 300, '2024-11-19'),
(13, 33, 450, '2024-11-18'),
(14, 34, 600, '2024-11-17'),
(15, 35, 700, '2024-11-16'),
(16, 36, 100, '2024-11-15'),
(17, 37, 200, '2024-11-14'),
(18, 38, 300, '2024-11-13'),
(19, 39, 400, '2024-11-12'),
(20, 40, 500, '2024-11-11'),
(21, 21, 125, '2024-11-10'),
(22, 22, 300, '2024-11-09'),
(23, 23, 450, '2024-11-08'),
(24, 24, 600, '2024-11-07'),
(25, 25, 700, '2024-11-06'),
(26, 26, 100, '2024-11-05'),
(27, 27, 200, '2024-11-04'),
(1, 28, 300, '2024-11-03'),
(2, 29, 400, '2024-11-02'),
(3, 30, 500, '2024-11-01'),
(4, 31, 125, '2024-10-31'),
(5, 32, 300, '2024-10-30'),
(6, 33, 450, '2024-10-29'),
(7, 34, 600, '2024-10-28'),
(8, 35, 700, '2024-10-27'),
(9, 36, 100, '2024-10-26'),
(10, 37, 200, '2024-10-25'),
(11, 38, 300, '2024-10-24'),
(12, 39, 400, '2024-10-23'),
(13, 40, 500, '2024-10-22'),
(14, 21, 125, '2024-10-21'),
(15, 22, 300, '2024-10-20'),
(16, 23, 450, '2024-10-19'),
(17, 24, 600, '2024-10-18'),
(18, 25, 700, '2024-10-17'),
(19, 26, 100, '2024-10-16'),
(20, 27, 200, '2024-10-15'),
(21, 28, 300, '2024-10-14'),
(22, 29, 400, '2024-10-13'),
(23, 30, 500, '2024-10-12'),
(24, 31, 125, '2024-10-11'),
(25, 32, 300, '2024-10-10'),
(26, 33, 450, '2024-10-09'),
(27, 34, 600, '2024-10-08'),
(1, 35, 700, '2024-10-07'),
(2, 36, 100, '2024-10-06'),
(3, 37, 200, '2024-10-05'),
(4, 38, 300, '2024-10-04'),
(5, 39, 400, '2024-10-03'),
(6, 40, 500, '2024-10-02'),
(7, 21, 125, '2024-10-01'),
(8, 22, 300, '2024-09-30'),
(9, 23, 450, '2024-09-29'),
(10, 24, 600, '2024-09-28'),
(11, 25, 700, '2024-09-27'),
(12, 26, 100, '2024-09-26'),
(13, 27, 200, '2024-09-25'),
(14, 28, 300, '2024-09-24'),
(15, 29, 400, '2024-09-23'),
(16, 30, 500, '2024-09-22'),
(17, 31, 125, '2024-09-21'),
(18, 32, 300, '2024-09-20'),
(19, 33, 450, '2024-09-19'),
(20, 34, 600, '2024-09-18'),
(21, 35, 700, '2024-09-17'),
(22, 36, 100, '2024-09-16'),
(23, 37, 200, '2024-09-15'),
(24, 38, 300, '2024-09-14'),
(25, 39, 400, '2024-09-13'),
(26, 40, 500, '2024-09-12'),
(27, 21, 125, '2024-09-11');

INSERT INTO contentlanguages(content_id, language_id)
VALUES
(1, 1),
(1, 2),
(1, 3),
(2, 1),
(2, 4),
(3, 2),
(3, 5),
(4, 7),
(4, 2),
(5, 6),
(5, 1),
(5, 2),
(6, 2),
(6, 3),
(6, 8),
(7, 2),
(7, 9),
(8, 2),
(8, 8),
(9, 2),
(9, 11),
(10, 2),
(11, 2),
(12, 2),
(13, 2),
(14, 2),
(15, 2),
(16, 2),
(17, 2),
(18, 2),
(19, 2),
(20, 2);

INSERT INTO contentdistributingcompanies(content_id, distributing_company_id)
VALUES
(1, 15),
(2, 16),
(3, 8),
(4, 17),
(5, 16),
(6, 18),
(7, 10),
(8, 19),
(9, 8),
(10, 20),
(11, 15),
(12, 21),
(13, 8),
(14, 8),
(15, 16),
(16, 16),
(17, 17),
(18, 20),
(19, 12),
(20, 16);

INSERT INTO contentgenres(content_id, genre_id)
VALUES
(1, 1),
(1, 11),
(2, 48),
(3, 1),
(3, 6),
(3, 5),
(4, 1),
(4, 5),
(4, 3),
(5, 1),
(5, 48),
(6, 2),
(6, 5),
(7, 1),
(7, 10),
(8, 3),
(8, 6),
(9, 6),
(9, 13),
(10, 3),
(10, 7),
(11, 12),
(11, 7),
(12, 3),
(12, 11),
(13, 3),
(14, 7),
(14, 25),
(15, 1),
(15, 13),
(16, 4),
(16, 48),
(17, 37),
(18, 19),
(18, 46),
(19, 1),
(19, 3),
(20, 1),
(20, 48);

INSERT INTO contentactor(content_id, actor_id, payment)
VALUES
(2, 21, null),
(1, 40, null),
(3, 22, null),
(4, 23, null),
(5, 39, null),
(6, 38, null),
(7, 37, null),
(8, 36, null),
(9, 35, null),
(10, 34, null),
(11, 33, null),
(12, 32, null),
(13, 31, null),
(14, 30, null),
(15, 29, null),
(16, 28, null),
(17, 27, null),
(18, 26, null),
(19, 25, null),
(20, 24, null);

INSERT INTO episode (series_id, season_number, episode_number, content_id)
VALUES
(1, 1, 1, 21),
(1, 1, 2, 22),
(1, 1, 3, 23),
(1, 1, 4, 24),
(1, 1, 5, 25),
(1, 1, 6, 26),
(1, 1, 7, 27);

INSERT INTO season (series_id, season_number, name)
VALUES
(1, 1, 'Breaking Bad season 1'),
(1, 2, 'Breaking Bad season 2'),
(1, 3, 'Breaking Bad season 3'),
(1, 4, 'Breaking Bad season 4'),
(1, 5, 'Breaking Bad season 5'),
(2, 1, 'Rome season 1'),
(2, 2, 'Rome season 2'),
(3, 1, 'Game of Thrones season 1'),
(3, 2, 'Game of Thrones season 2'),
(3, 3, 'Game of Thrones season 3'),
(3, 4, 'Game of Thrones season 4'),
(3, 5, 'Game of Thrones season 5'),
(3, 6, 'Game of Thrones season 6'),
(3, 7, 'Game of Thrones season 7'),
(3, 8, 'Game of Thrones season 8'),
(4, 1, 'Steins;Gate'),
(4, 2, 'Steins;Gate 0'),
(5, 1, 'Death Note'),
(6, 1, 'The Office season 1'),
(6, 2, 'The Office season 2'),
(6, 3, 'The Office season 3'),
(6, 4, 'The Office season 4'),
(6, 5, 'The Office season 5'),
(6, 6, 'The Office season 6'),
(6, 7, 'The Office season 7'),
(6, 8, 'The Office season 8'),
(6, 9, 'The Office season 9'),
(7, 1, 'Better Call Saul season 1'),
(7, 2, 'Better Call Saul season 2'),
(7, 3, 'Better Call Saul season 3'),
(7, 4, 'Better Call Saul season 4'),
(7, 5, 'Better Call Saul season 5'),
(7, 6, 'Better Call Saul season 6'),
(8, 1, 'Stranger Things season 1'),
(8, 2, 'Stranger Things season 2'),
(8, 3, 'Stranger Things season 3'),
(8, 4, 'Stranger Things season 4'),
(8, 5, 'Stranger Things season 5'),
(8, 6, 'Stranger Things season 6'),
(9, 1, 'Legion season 1'),
(9, 2, 'Legion season 2'),
(10, 1, 'Rick and Morty season 1'),
(10, 2, 'Rick and Morty season 2'),
(10, 3, 'Rick and Morty season 3'),
(10, 4, 'Rick and Morty season 4'),
(10, 5, 'Rick and Morty season 5'),
(10, 6, 'Rick and Morty season 6'),
(10, 7, 'Rick and Morty season 7'),
(10, 8, 'Rick and Morty season 8'),
(10, 9, 'Rick and Morty season 9'),
(10, 10, 'Rick and Morty season 10'),
(10, 11, 'Rick and Morty season 11'),
(10, 12, 'Rick and Morty season 12');

INSERT INTO series (description, title, release_date)
VALUES
('A chemistry teacher diagnosed with inoperable lung cancer turns to manufacturing and selling methamphetamine with a former student to secure his family\'s future.', 'Breaking Bad', '2008-01-01'),
('A down-to-earth account of the lives of both illustrious and ordinary Romans set in the last days of the Roman Republic.', 'Rome', '2005-01-01'),
('Nine noble families fight for control over the lands of Westeros, while an ancient enemy returns after being dormant for millennia.', 'Game of Thrones', '2011-01-01'),
('After discovering time travel, a university student and his colleagues must use their knowledge of it to stop an evil organization and their diabolical plans.', 'Steins;Gate', '2011-01-01'),
('An intelligent high school student goes on a secret crusade to eliminate criminals from the world after discovering a notebook capable of killing anyone whose name is written into it.', 'Death Note', '2006-01-01'),
('A mockumentary on a group of typical office workers, where the workday consists of ego clashes, inappropriate behavior, tedium and romance.', 'The Office', '2005-01-01'),
('The trials and tribulations of criminal lawyer Jimmy McGill in the years leading up to and past his fateful run-in with Walter White and Jesse Pinkman.', 'Better Call Saul', '2015-01-01'),
('In 1980s Indiana, a group of young friends witness supernatural forces and secret government exploits. As they search for answers, the children unravel a series of extraordinary mysteries.', 'Stranger Things', '2016-01-01'),
('David Haller is a troubled young man diagnosed as schizophrenic, but after a strange encounter he discovers special powers that will change his life forever.', 'Legion', '2017-01-01'),
('The fractured domestic lives of a nihilistic mad scientist and his anxious grandson are further complicated by their inter-dimensional misadventures.', 'Rick and Morty', '2013-01-01');

INSERT INTO movie (content_id, budget, box_office)
VALUES
(1, 70000000, 217000000),
(2, 250000000, 1100000000),
(3, 63000000, 100900000),
(4, 20000000, 75000000),
(5, 200000000, 1300000000),
(6, 10000000, 126000000),
(7, 78000000, 865000000),
(8, 75000000, 272200000),
(9, 25000000, 174000000),
(10, 18000000, 72400000),
(11, 81000000, 484000000),
(12, NULL, 282048),
(13, 7000000, 43069254),
(14, 13000000, 115000000),
(15, 200000000, 869000000),
(16, 350600000, 955800000),
(17, 40000000, NULL),
(18, 103000000, 320300000),
(19, 160000000, 837000000),
(20, 150000000, 449300000);

INSERT INTO content (description, title, duration, release_date, production_company_id)
VALUES
('Diagnosed with terminal lung cancer, chemistry teacher Walter White teams up with former student Jesse Pinkman to cook and sell crystal meth.', 'Pilot', 58, '2008-01-20', 8),
('After their first drug deal goes terribly wrong, Walt and Jesse are forced to dispose a pair of corpses. Meanwhile, Skyler grows suspicious of her husband\'s activities.', 'Cat\'s in the Bag...', 48, '2008-01-27', 8),
('As Walt cleans up the mess that was left after his first drug deal, Skyler gets too close to the truth about his double life.', '...And the Bag\'s in the River', 48, '2008-02-10', 8),
('Walt tells the rest of his family about his cancer. Jesse tries to make amends with his own parents.', 'Cancer Man', 48, '2008-02-17', 8),
('Walt rejects everyone who tries to help him with the cancer. Jesse tries his best to create Walt\'s meth, with the help of an old friend.', 'Gray Matter', 48, '2008-02-24', 8),
('With the side effects and cost of his treatment mounting, Walt demands that Jesse finds a wholesaler to buy their drugs - which lands him in trouble.', 'Crazy Handful of Nothin\'', 48, '2008-03-02', 8),
('Walt and Jesse try to up their game by making more of the crystal every week for Tuco. Unfortunately, some of the ingredients they need are not easy to find. Meanwhile, Skyler suspects her sister of shoplifting.', 'A No-Rough-Stuff-Type Deal', 48, '2008-03-09', 8),
('As his kingdom is being threatened by the Turks, young prince Vlad Tepes must become a monster feared by his own people in order to obtain the power needed to protect his own family, and the families of his kingdom.', 
 'Dracula Untold', 92, '2014-01-01', 52),
('Political involvement in the Avengers'' affairs causes a rift between Captain America and Iron Man.', 
 'Captain America: Civil War', 147, '2016-01-01', 35),
('An insomniac office worker and a devil-may-care soap maker form an underground fight club that evolves into much more.', 
 'Fight Club', 139, '1999-01-01', 53),
('When Enola Holmes, Sherlock''s sister, discovers her mother is missing, she endeavors to find her, becoming a super-sleuth in her own right as she outwits her famous brother and unravels a dangerous conspiracy.', 
 'Enola Holmes', 123, '2020-01-01', 1),
('T''Challa, heir to the hidden but advanced kingdom of Wakanda, must step forward to lead his people into a new future and must confront a challenger from his country''s past.', 
 'Black Panther', 134, '2018-01-01', 35),
('In pursuit of a serial killer, an FBI agent uncovers a series of occult clues that she must solve to end his terrifying killing spree.', 
 'Longlegs', 101, '2024-01-01', 58),
('Katniss Everdeen and Peeta Mellark become targets of the Capitol after their victory in the 74th Hunger Games sparks a rebellion in the Districts of Panem.', 
 'The Hunger Games: Catching Fire', 146, '2013-01-01', 57),
('After their high school basketball coach passes away, five good friends and former teammates reunite for a Fourth of July holiday weekend.', 
 'Grown Ups', 102, '2010-01-01', 10),
('A writer encounters the owner of an aging high-class hotel, who tells him of his early years serving as a lobby boy in the hotel''s glorious years under an exceptional concierge.', 
 'The Grand Budapest Hotel', 99, '2014-01-01', 56),
('Cady Heron is a hit with The Plastics, the A-list girl clique at her new school, until she makes the mistake of falling for Aaron Samuels, the ex-boyfriend of alpha Plastic Regina George.', 
 'Mean Girls', 97, '2004-01-01', 2),
('A whiny news reporter is given the chance to step into God''s shoes.', 
 'Bruce Almighty', 101, '2003-01-01', 3),
('A tutor is being assigned an unexpected task at a mansion and finds himself struggling with the obsessions of his student, who threatens to expose his darkest secrets.', 
 'The Tutor', 92, '2023-01-01', 3),
('Frank, a single man raising his child prodigy niece Mary, is drawn into a custody battle with his mother.', 
 'Gifted', 101, '2017-01-01', 55),
('An elderly Margaret Thatcher talks to the imagined presence of her recently deceased husband as she struggles to come to terms with his death while scenes from her past life, from girlhood to British prime minister, intervene.', 
 'The Iron Lady', 105, '2011-01-01', 39),
('The Guardians struggle to keep together as a team while dealing with their personal family issues, notably Star-Lord''s encounter with his father, the ambitious celestial being Ego.', 
 'Guardians of the Galaxy Vol. 2', 136, '2017-01-01', 35),
('While on a journey of physical and spiritual healing, a brilliant neurosurgeon is drawn into the world of the mystic arts.', 
 'Doctor Strange', 115, '2016-01-01', 35),
('Half a year into his solo mission on the edge of the solar system, an astronaut concerned with the state of his life back on Earth is helped by an ancient creature he discovers in the bowels of his ship.', 
 'Spaceman', 107, '2024-01-01', 59),
('After his home is conquered by the tyrannical emperors who now lead Rome, Lucius is forced to enter the Colosseum and must look to his past to find strength to return the glory of Rome to its people.', 
 'Gladiator II', 148, '2024-01-01', 54),
('A thief who steals corporate secrets through the use of dream-sharing technology is given the inverse task of planting an idea into the mind of a C.E.O., but his tragic past may doom the project and his team to disaster.', 
 'Inception', 148, '2010-01-01', 1),
('The powerful but arrogant god Thor is cast out of Asgard to live amongst humans in Midgard (Earth), where he soon becomes one of their finest defenders.', 
 'Thor', 115, '2011-01-01', 35);

INSERT INTO user 
(first_name, last_name, dob, email, password, gender, subscription_plan_id, subscription_start_date, subscription_end_date) 
VALUES 
('John', 'Doe', '1985-06-15', 'john.doe@gmail.com', 'john1234', 'Male', 4, '2024-01-01', '2024-12-31'),
('Jane', 'Smith', '1990-03-22', 'jane.smith@gmail.com', 'smithpassword', 'Female', 5, '2024-02-01', '2025-01-31'),
('Michael', 'Johnson', '1982-11-08', 'michael.johnson@gmail.com', 'mike@1982', 'Male', 6, '2024-03-01', '2025-02-28'),
('Emily', 'Williams', '1995-07-30', 'emily.williams@gmail.com', 'emily2024', 'Female', 4, '2024-04-01', '2024-12-31'),
('David', 'Brown', '1980-05-20', 'david.brown@gmail.com', 'david#1980', 'Male', 5, '2024-05-01', '2025-04-30'),
('Sophia', 'Jones', '1992-12-17', 'sophia.jones@gmail.com', 'sophia&jones', 'Female', 6, '2024-06-01', '2025-05-31'),
('Daniel', 'Miller', '1988-02-12', 'daniel.miller@gmail.com', 'daniel1988@', 'Male', 4, '2024-07-01', '2024-12-31'),
('Olivia', 'Davis', '1994-09-10', 'olivia.davis@gmail.com', 'olivia@2024', 'Female', 5, '2024-08-01', '2025-07-31'),
('James', 'Martinez', '1987-01-25', 'james.martinez@gmail.com', 'james123#', 'Male', 6, '2024-09-01', '2025-08-31'),
('Charlotte', 'Hernandez', '1991-04-04', 'charlotte.hernandez@gmail.com', 'charl0tte@91', 'Female', 4, '2024-10-01', '2024-12-31'),
('Benjamin', 'Garcia', '1993-06-14', 'benjamin.garcia@gmail.com', 'ben@jamin93', 'Male', 5, '2024-11-01', '2025-10-31'),
('Amelia', 'Lopez', '1996-08-18', 'amelia.lopez@gmail.com', 'amelia1996#', 'Female', 6, '2024-12-01', '2025-11-30'),
('Lucas', 'Gonzalez', '1983-12-05', 'lucas.gonzalez@gmail.com', 'lucas@1983', 'Male', 4, '2024-01-01', '2024-12-31'),
('Isabella', 'Perez', '1990-10-29', 'isabella.perez@gmail.com', 'isabella_90', 'Female', 5, '2024-02-01', '2025-01-31'),
('Ethan', 'Wilson', '1998-03-11', 'ethan.wilson@gmail.com', 'ethan@1998', 'Male', 6, '2024-03-01', '2025-02-28'),
('Ava', 'Anderson', '1986-05-23', 'ava.anderson@gmail.com', 'ava_1986@', 'Female', 4, '2024-04-01', '2024-12-31'),
('Alexander', 'Thomas', '1984-11-14', 'alexander.thomas@gmail.com', 'alex@1984', 'Male', 5, '2024-05-01', '2025-04-30'),
('Mia', 'Jackson', '1992-01-09', 'mia.jackson@gmail.com', 'mia@1992', 'Female', 6, '2024-06-01', '2025-05-31'),
('Henry', 'White', '1995-08-03', 'henry.white@gmail.com', 'henrywhite@95', 'Male', 4, '2024-07-01', '2024-12-31'),
('Ella', 'Harris', '1993-09-26', 'ella.harris@gmail.com', 'ella@1993', 'Female', 5, '2024-08-01', '2025-07-31');


INSERT INTO subscriptionplan (payment_info, plan_info) 
VALUES 
(
'{"monthly_price": "€7.99"}', 
'{"name": "Basic", "video_and_sound_quality": "Good", "resolution": "720p (HD)", "supported_devices": ["TV", "computer", "mobile phone", "tablet"], "devices_household_can_watch_at_same_time": 1, "download_devices": 1}'
),
(
'{"monthly_price": "€9.99"}', 
'{"name": "Standard", "video_and_sound_quality": "Great", "resolution": "1080p (Full HD)", "supported_devices": ["TV", "computer", "mobile phone", "tablet"], "devices_household_can_watch_at_same_time": 2, "download_devices": 2}'
),
(
'{"monthly_price": "€11.99"}', 
'{"name": "Premium", "video_and_sound_quality": "Best", "resolution": "4K (Ultra HD) + HDR", "spatial_audio": "Included", "supported_devices": ["TV", "computer", "mobile phone", "tablet"], "devices_household_can_watch_at_same_time": 4, "download_devices": 6}'
);

INSERT INTO actor (first_name, last_name, dob, nationality, gender, biography)
VALUES
('Anthony', 'Mackie', '1978-09-23', 'American', 'Male', "Anthony Mackie is an American actor. He was born in New Orleans, Louisiana, to Martha (Gordon) and Willie Mackie, Sr., who owned a business, Mackie Roofing. Anthony has been featured in feature films, television series and Broadway and Off-Broadway plays, including Ma Rainey's Black Bottom, Drowning Crow, McReele, A Soldier's Play, and Talk, by Carl Hancock Rux, for which he won an Obie Award in 2002. In 2002, he was featured in Eminem's debut film, 8 Mile, playing Papa Doc, a member of Leaders of the Free World. He was nominated for Best Supporting Actor at the 2009 Independent Spirit Awards for his role in _The Hurt Locker (2009)_(QV). This is Mackie's second ISA nomination, the first coming for his work in _Brother to Brother (2003)_, where he was nominated for Best Actor. Also in 2009, Mackie portrayed rapper Tupac Shakur in the film Notorious (2009). He appears in the Matt Damon film The Adjustment Bureau (2011) where he plays Harry Mitchell, a sympathetic member of a shadowy supernatural group that controls human destiny."),
('Helena', 'Bonham Carter', '1966-05-26', 'British', 'Female', "Helena Bonham Carter is an actress of great versatility, one of the UK's finest and most successful.

Bonham Carter was born May 26, 1966 in Golders Green, London, England, the youngest of three children of Elena (née Propper de Callejón), a psychotherapist, and Raymond Bonham Carter, a merchant banker. Through her father, she is the great-granddaughter of former Prime Minister Herbert H. Asquith, and her blue-blooded family tree also contains Barons and Baronesses, diplomats, and a director, Bonham Carter's great-uncle Anthony Asquith, who made Pygmalion (1938) and The Importance of Being Earnest (1952), among others. Cousin Crispin Bonham-Carter is also an actor. Her maternal grandfather, Eduardo Propper de Callejón, was a Spanish diplomat who was awarded the honorific Righteous Among the Nations, by Israel, for helping save Jews during World War II (Eduardo's own father was a Czech Jew). Helena's maternal grandmother, Hélène Fould-Springer, was from an upper-class Jewish family from France, Austria, and Germany, and later converted to her husband's Catholic faith.

Bonham Carter experienced family dramas during her childhood, including her father's stroke - which left him wheelchair-bound. She attended South Hampstead High School and Westminster School in London, and subsequently devoted herself to an acting career. That trajectory actually began in 1979 when, at age thirteen, she entered a national poetry writing competition and used her second place winnings to place her photo in the casting directory \"Spotlight.\" She soon had her first agent and her first acting job, in a commercial, at age sixteen. She then landed a role in the made-for-TV movie A Pattern of Roses (1983), which subsequently led to her casting in the Merchant Ivory films A Room with a View (1985), director James Ivory's tasteful adaptation of E.M. Forster's novel, and Lady Jane (1986), giving a strong performance as the uncrowned Queen of England. She had roles in three other productions under the Merchant-Ivory banner (director Ivory, producer Ismail Merchant, and screenwriter Ruth Prawer Jhabvala): an uncredited appearance in Maurice (1987), and large roles in Where Angels Fear to Tread (1991) and Howards End (1992).

Often referred to as the \"corset queen\" or \"English rose\" because of her early work, Bonham Carter continued to surprise audiences with magnificent performances in a variety of roles from her more traditional corset-clad character in The Wings of the Dove (1997) and Shakespearian damsels to the dark and neurotic anti-heroines of Fight Club (1999). Her acclaimed performance in The Wings of the Dove (1997) earned her a Best Actress Academy Award nomination, a Golden Globe Best Actress nomination, a BAFTA Best Actress nomination, and a SAG Awards Best Actress nomination. It also won her a Best Actress Award from the National Board of Review, the Los Angeles Film Critics, the Boston Society Film Critics, the Broadcast Film Critics Association, the Texas Society of Film Critics, and the Southeastern Film Critics Association.

In the late 1990s, Bonham Carter embarked on the next phase of her career, moving from capable actress to compelling star. Audiences and critics had long been enchanted by her delicate beauty, evocative of another time and place. Her late '90s and early and mid 2000s roles included Mick Jackson's Live from Baghdad (2002), alongside Michael Keaton, receiving a nomination for both an Emmy and a Golden Globe; Paul Greengrass' The Theory of Flight (1998), in which she played a victim of motor neurone disease; Trevor Nunn's Twelfth Night (1996), in which she played Olivia; opposite Woody Allen in his Mighty Aphrodite (1995); Mort Ransen's Margaret's Museum (1995); Kenneth Branagh's Frankenstein (1994); and Franco Zeffirelli's Hamlet (1990).

Other notable credits include her appearance with Steve Martin in Novocaine (2001), Tim Burton's remake of Planet of the Apes, in which she played an ape, Thaddeus O'Sullivan's The Heart of Me (2002), opposite Paul Bettany, and Big Fish (2003), her second effort with Tim Burton, in which she appeared as a witch.

In between her films, Helena has managed a few television appearances, which include her portrayal of Jacqui Jackson in Magnificent 7 (2005), the tale of a mother struggling to raise seven children - three daughters and four autistic boys; as Anne Boleyn in the two-parter biopic of Henry VIII starring Ray Winstone; and as Morgan Le Fey, alongside Sam Neill and Miranda Richardson, in Merlin. Earlier television appearances include Michael Mann's Miami Vice (1984) as Don Johnson's junkie fiancée, and as a stripper who wins Rik Mayall's heart in Dancing Queen (1993). Helena has also appeared on stage, in productions of Trelawney of the Wells, The Barber of Seville, House of Bernarda Alba, The Chalk Garden, and Woman in White.

Bonham Carter was nominated for a Golden Globe for the fifth time for her role in partner Tim Burton's film adaptation of the Stephen Sondheim musical, Sweeney Todd: The Demon Barber of Fleet Street (2007), for which Burton and co-star Johnny Depp were also nominated. For the role, she was awarded Best Actress at the Evening Standard British Film Awards 2008. Other 2000s work includes playing Mrs Bucket in Tim Burton's massive hit Charlie and the Chocolate Factory (2005), providing the voices for the aristocratic Lady Campanula Tottington in Wallace & Gromit: The Curse of the Were-Rabbit (2005) and for the eponymous dead heroine in Tim Burton's spooky Corpse Bride (2005), and co-starring in Conversations with Other Women (2005) opposite Aaron Eckhart.

After their meeting while filming Planet of the Apes (2001), Bonham Carter and Tim Burton made seven films together. They lived in adjoining residences in London, shared a connecting hallway, and have two children: Billy Ray Burton, born in 2003, and Nell Burton, who was born in 2007. Ironically, a mutual love of Sweeney Todd was part of the initial attraction for the pair. Bonham Carter has said in numerous interviews that her audition process for the role of Mrs. Lovett was the most grueling of her career and that, ultimately, it was Sondheim who she had to convince that she was right for the role."),
('Millie', 'Bobby Brown', '2004-02-19', 'British', 'Female', "Millie Bobby Brown (born 19 February 2004) is an English actress and model. She rose to prominence for her role as Eleven in the Netflix science fiction drama series Stranger Things (2016), for which she earned a Primetime Emmy Award nomination for Outstanding Supporting Actress in a Drama Series at age 13. She is also the youngest person ever to feature on TIME 100 list.

Millie was born in Marbella, Andalusia, Spain, the third of four children of English parents, Kelly and Robert Brown. The family moved to Bournemouth, Dorset, when Brown was around four years old, and then to Orlando, Florida, four years later. Here, Millie went to acting workshops to pass time on a Saturday, and it was there that a top Hollywood talent scout called and told Millie's parents that \"she has instincts you cannot teach.\" She advised Millie's parents that she could \"mix it with the best kids in Hollywood.\" They packed up and drove from Orlando to Los Angeles, and within a week, Millie was meeting with the town's top children's talent agencies. She was offered representation by all the agents that she met. Within three months of being in Hollywood, Millie was cast as young Alice in ABC's fantasy drama series Once Upon a Time in Wonderland (2013), a spin-off of Once Upon a Time (2011).

In November 2013, after just one self-taped audition, and without meeting the producers/directors, Millie was offered the role of Madison O'Donnell in BBC America paranormal drama-thriller series Intruders (2014). She then made guest appearances in the CBS police procedural drama NCIS (2003), the ABC sitcom Modern Family (2009), and the ABC medical drama series Grey's Anatomy (2005). In 2016, Brown played Eleven in the Netflix science fiction drama series Stranger Things. Her portrayal received critical acclaim and she was nominated for the Screen Actors Guild Award for Outstanding Performance by a Female Actor in a Drama Series and the Primetime Emmy Award for Outstanding Supporting Actress in a Drama Series. She won the Screen Actors Guild Award for Outstanding Performance by an Ensemble in a Drama Series with her co-stars, and won the 43rd Saturn Award for Best Performance by a Younger Actor in a Television Series. In November 2016, Brown starred in the music video for Sigma and Birdy's single \"Find Me\". Since November 2016, she has appeared in commercial advertisements for Citigroup. In January 2017, she made her modeling debut in Calvin Klein's By Appointment campaign. The following month, she was signed to the agency IMG Models. Brown made her feature film debut in the Godzilla sequel, Godzilla: King of the Monsters (2019). In January 2018, Brown was cast to star and produce the film adaptation of the Enola Holmes Mysteries book.

On 20 April, 2018, Brown became the youngest person ever to be included on Time magazine's list of the world's 100 most influential people.

She resides in London and Atlanta, Georgia."),
('Chris', 'Hemsworth', '1983-08-11', 'Australian', 'Male', "Christopher \"Chris\" Hemsworth was born on August 11, 1983 in Melbourne, Victoria, Australia to Leonie Hemsworth (née van Os), an English teacher & Craig Hemsworth, a social-services counselor. His brothers are actors, Liam Hemsworth & Luke Hemsworth; he is of Dutch (from his immigrant maternal grandfather), Irish, English, Scottish, and German ancestry. His uncle, by marriage, was Rod Ansell, the bushman who inspired the comedy film Crocodile Dundee (1986).

Chris saw quite a bit of the country in his youth, after his family moved to the Northern Territory before finally settling on Phillip Island, to the south of Melbourne. In 2004, he unsuccessfully auditioned for the role of Robbie Hunter in the Australian soap opera Home and Away (1988) but was recalled for the role of Kim Hyde which he played until 2007. In 2006, he entered the Australian version of Dancing with the Stars (2004) and his popularity in the soap enabled him to hang on until show 7 (Episode #5.7 (2006)) when he became the fifth contestant to be eliminated.

His first Hollywood appearance was in the science fiction blockbuster Star Trek (2009), but it was his titular role in the superhero blockbuster Thor (2011) which propelled him to prominence worldwide. He reprised the character in the superhero blockbusters The Avengers (2012), Thor: The Dark World (2013), Avengers: Age of Ultron (2015), Thor: Ragnarok (2017), Avengers: Infinity War (2018) and Avengers: Endgame (2019).

Chris's American representative, management company ROAR, also manages actress Elsa Pataky, and it was through them that the two met, marrying in 2010. The couple have a daughter and twin sons. He was appointed Member of the Order of Australia at the 2021 Queen's Birthday Honours for his services to the performing arts and to charitable organisations."),
('Elliot', 'Page', '1987-02-21', 'Canadian', 'Other', "Elliot Page was born in Halifax, Nova Scotia to Martha Philpotts, a teacher, and Dennis Page, a graphic designer. Page wanted to start acting at an early age and attended the Neptune Theater School. They began their career at the age of 10 on the award-winning television series Pit Pony (1999), for which they received a Gemini nomination and a Young Artist Awards nomination. Later, Page appeared in Marion Bridge (2002), which won the award for Best Canadian First Feature at the Toronto International Film Festival. They won a Gemini Award for their role of Lilith in the first season of ReGenesis (2004), a one-hour drama for TMN/Movie Central, and for the cable feature, Ghost Cat (2004), for Best Performance in a Children's or Youth Program or Series. In addition, Page appeared in the cult hit TV series Trailer Park Boys (2001).

As the lead in David Slade's Hard Candy (2005), which premiered at the Sundance Film Festival, Page garnered much praise for their tour de force performance as a 14-year-old who meets a 30-year-old photographer on the Internet and then looks to expose him as a pedophile. Films that followed included the title role of Bruce McDonald's The Tracey Fragments (2007); An American Crime (2007), also starring Catherine Keener; and the third installation of the X-Men franchise, X-Men: The Last Stand (2006), where Page played Kitty Pryde.

With their breakout role in Jason Reitman's hit comedy Juno (2007), about an offbeat teenager who finds herself unexpectedly pregnant, Page received Academy Award, BAFTA, Golden Globe and SAG Best Actress nominations, and won the Independent Spirit Award for their performance. They followed up that turn with the lead in Drew Barrymore's directorial debut, the roller-derby comedy-drama Whip It (2009), Christopher Nolan's psychological thriller Inception (2010), the independent film Peacock (2010), and the dark comedy Super (2010), opposite Rainn Wilson and Liv Tyler.

Page co-starred alongside Jesse Eisenberg, Alison Pill, Alec Baldwin, and Greta Gerwig in the Woody Allen ensemble comedy To Rome with Love (2012), and appeared in the thriller The East (2013), a story centered on a contract worker (played by Brit Marling) tasked with infiltrating an anarchist group, only to find herself falling for its leader (played by Alexander Skarsgård)."),
('Pedro', 'Pascal', '1975-04-02', 'Chilean-American', 'Male', "Pedro Pascal is a Chilean-born actor. He is best known for portraying the roles of Oberyn Martell in the fourth season of the HBO series Game of Thrones (2011), Javier Peña in the Netflix series Narcos (2015), the titular character in the Disney+ series The Mandalorian (2019) and Joel Miller in the HBO series The Last of Us (2023).

He's long-time friends with Triple Frontier (2019) co-star Oscar Isaac."),
('Adam', 'Sandler', '1966-09-09', 'American', 'Male', "Adam Richard Sandler was born September 9, 1966 in Brooklyn, New York, to Judith (Levine), a teacher at a nursery school, and Stanley Alan Sandler, an electrical engineer. He is of Russian Jewish descent. At 17, he took his first step towards becoming a stand-up comedian when he spontaneously took the stage at a Boston comedy club. He found he was a natural comic. He nurtured his talent while at New York University (graduating with a Bachelor of Fine Arts degree in 1991) by performing regularly in clubs and at universities. During his freshman year, he snagged a recurring role as the Huxtable family's friend Smitty on The Cosby Show (1984). While working at a comedy club in L.A., he was \"discovered\" by Dennis Miller, who recommended him to Saturday Night Live (1975) producer Lorne Michaels and told him that Sandler had a big talent. This led to his being cast in the show in 1990, which he also wrote for in addition to performing. After Saturday Night Live (1975), Sandler went on to the movies, starring in such hit comedies as Airheads (1994), Happy Gilmore (1996), Billy Madison (1995) and Big Daddy (1999). He has also starred in Mr. Deeds (2002) alongside Winona Ryder; Eight Crazy Nights (2002), an animated movie about the Jewish festival of Chanukah; and Punch-Drunk Love (2002). He also writes and produces many of his own films and has composed songs for several of them, including The Wedding Singer (1998). Sandler has had several of his songs placed on the \"Billboard\" charts, including the classic \"The Chanukah Song\"."),
('Benedict', 'Wong', '1970-02-20', 'British', 'Male', "Benedict Wong is a British actor. He is known for his roles as Kublai Khan in Netflix's Marco Polo (2014-2016), Bruce Ng in The Martian (2015), and Wong in the Marvel Cinematic Universe since Doctor Strange (2016). Wong was born on 3 July 1971 in Eccles, Greater Manchester, the son of Hong Kong immigrant parents who had traveled through Ireland before settling in England. He was brought up in Eccles, and attended Salford City College (then called De La Salle Sixth Form College) in the surrounding area of Salford. He then took a two-year performing arts course at Salford City College."),
('Kurt', 'Russell', '1951-03-17', 'American', 'Male', "Kurt Vogel Russell was born on March 17, 1951 in Springfield, Massachusetts and raised in Thousand Oaks, California to Louise Julia Russell (née Crone), a dancer & Bing Russell, an actor. He is of English, German, Scottish and Irish descent. His first roles were as a child on television series, including a lead role on the Western series The Travels of Jaimie McPheeters (1963). Russell landed a role in the Elvis Presley movie, It Happened at the World's Fair (1963), when he was eleven years old. Walt Disney himself signed Russell to a 10-year contract, and, according to Robert Osborne, he became the studio's top star of the 1970s. Having voiced adult Copper in the animated Disney film The Fox and the Hound (1981), Russell is one of the few famous child stars in Hollywood who has been able to continue his acting career past his teen years.

Kurt spent the early 1970s playing minor league baseball. In 1979, he gave a classic performance as Elvis Presley in John Carpenter's ABC TV movie Elvis (1979), and married the actress who portrayed Priscilla Presley in the film, Season Hubley. He was nominated for an Emmy Award for the role. He followed with roles in a string of well-received films, including Used Cars (1980) and Silkwood (1983), for which he was nominated for the Golden Globe Award for Best Supporting Actor - Motion Picture. During the 1980s, he starred in several films by director Carpenter; they created some of his best-known roles, including the infamous anti-hero Snake Plissken in the futuristic action film Escape from New York (1981) (and later in its sequel Escape from L.A. (1996)), Antarctic helicopter pilot R.J. MacReady in the horror film The Thing (1982), and Jack Burton in the fantasy film Big Trouble in Little China (1986), all of which have since become cult classics.

In 1983, he became reacquainted with Goldie Hawn (who appeared with him in The One and Only, Genuine, Original Family Band (1968)) when they worked together on Swing Shift (1984). The two have lived together ever since. They made another film together, Garry Marshall's comedy Overboard (1987). His other 1980s titles include The Best of Times (1986), Tequila Sunrise (1988), Winter People (1989) and Tango & Cash (1989).

In 1991, he headlined the firefighter drama Backdraft (1991), he starred as Wyatt Earp in the Western film Tombstone (1993), and had a starring role as Colonel Jack O'Neil in the science fiction film Stargate (1994). In the mid-2000s, his portrayal of U.S. Olympic hockey coach Herb Brooks in Miracle (2004) won the praise of critics. In 2006, he appeared in the disaster-thriller Poseidon (2006), and in 2007, in Quentin Tarantino's Death Proof (2007) segment from the film Grindhouse (2007). Russell appeared in The Battered Bastards of Baseball (2014), a documentary about his father and the Portland Mavericks, which debuted at the Sundance Film Festival in 2014. Russell starred in the Western films Bone Tomahawk (2015) and The Hateful Eight (2015), and had a leading role in the dramatization Deepwater Horizon (2016). He also co-starred in the action sequels Furious 7 (2015) and The Fate of the Furious (2017).

Russell and Goldie Hawn live on a 72-acre retreat, Home Run Ranch, outside of Aspen. He has two sons, Boston Russell (from his marriage to Hubley) and Wyatt Russell (with Hawn). He also raised Hawn's children, actors Oliver Hudson and Kate Hudson, who consider him their father. Russell is also an avid gun enthusiast, a hunter and a staunch supporter of the Second Amendment of the United States Constitution. He is also an FAA-licensed private pilot holding single/multi-engine and instrument ratings, and is an Honorary Board Member of the humanitarian aviation organization Wings of Hope."),
('Meryl', 'Streep', '1949-06-22', 'American', 'Female', "Considered by many critics to be the greatest living actress, Meryl Streep has been nominated for the Academy Award an astonishing 21 times, and has won it three times. Meryl was born Mary Louise Streep in 1949 in Summit, New Jersey, to Mary Wolf (Wilkinson), a commercial artist, and Harry William Streep, Jr., a pharmaceutical executive. Her father was of German and Swiss-German descent, and her mother had English, Irish, and German ancestry.

Meryl's early performing ambitions leaned toward the opera. She became interested in acting while a student at Vassar and upon graduation she enrolled in the Yale School of Drama. She gave an outstanding performance in her first film role, Julia (1977), and the next year she was nominated for her first Oscar for her role in The Deer Hunter (1978). She went on to win the Academy Award for her performances in Kramer vs. Kramer (1979) and Sophie's Choice (1982), in which she gave a heart-wrenching portrayal of an inmate mother in a Nazi death camp.

A perfectionist in her craft and meticulous and painstaking in her preparation for her roles, Meryl turned out a string of highly acclaimed performances over the next decade in great films like Silkwood (1983); Out of Africa (1985); Ironweed (1987); and A Cry in the Dark (1988). Her career declined slightly in the early 1990s as a result of her inability to find suitable parts, but she shot back to the top in 1995 with her performance as Clint Eastwood's married lover in The Bridges of Madison County (1995) and as the prodigal daughter in Marvin's Room (1996). In 1998 she made her first venture into the area of producing, and was the executive producer for the moving ...First Do No Harm (1997). A realist when she talks about her future years in film, she remarked that \"...no matter what happens, my work will stand...\" "),
('Octavia', 'Spencer', '1970-05-25', 'American', 'Female', "Spencer is a native of Montgomery, Alabama, which she claims is the proverbial buckle of the Bible belt. She's the sixth of seven siblings and holds a BS in Liberal Arts from Auburn University. A \"closet\" lover of acting, this practical Alabamian knew that she'd someday work in the film industry, but never dreamed it would be in front of the camera. In 1995, acclaimed director Joel Schumacher changed all that by giving her a small part opposite Sandra Bullock in the hit film A Time to Kill, and Spencer was on her way. In 1996, she teamed up with Bullock again in Bullock's directorial debut of Making Sandwiches, a short film that premiered at the 1997 Sundance Film Festival.

Spencer made her stage debut in Los Angeles and originated the role of \"LaSonia\" (pronounced lasagna) in famed writer/director Del Shore's, The Trials and Tribulations of a Trailer Trash Housewife, starring opposite veteran actors Beth Grant, Dale Dickey and David Steen (2003). The play garnered Spencer and her fellow cast mates critical acclaim and a bevy of awards. Since then, Spencer has continued to see success as an actor in both film and television, working alongside Hollywood's elite. In February 2009, she was lauded by Los Angeles Times publication: The Envelope, for her brief but memorable performance in the Will Smith drama Seven Pounds."),
('Noah', 'Schnapp', '2004-10-03', 'American', 'Male', "Noah can be seen in a central role on season 2 of the Award-winning Netflix series \"Stranger Things\" where he portrays Will Byers opposite Winona Ryder. The series was nominated for various awards in 2016 and 2017, including a Golden Globe for Best Drama Television Series, a SAG Award for Outstanding Performance by an Ensemble in a Drama Series (winner) and an MTV Movie & TV Award for Show of the Year (winner). He is also an honoree of Variety's Youth Impact Award.

Noah will next be seen as the lead of \"Abe\", an indie drama that tells the story of Abraham, a 12 year old boy with a passion for cooking who has never known a family dinner without arguments. Directed by Fernando Grostein Andrade, the film is in production in New York.

Schnapp's additional film credits include Steven Spielberg's Oscar winning Cold War thriller, \"Bridge of Spies\", as well as the indie feature \"We Only Know So Much\", based on the Elizabeth Crane novel. In addition, Noah also participated in the Sundance Director's Lab, where he shot the lead role of 'Henry' in director Brent Green's feature, \"First Night on Earth.\" Most notably, Noah voiced the role of the one and only 'Charlie Brown' in the Fox feature film \"Peanuts\", director Steve Martino's new take on Charles Schulz's classic series."),
('Jennifer', 'Aniston', '1969-02-11', 'American', 'Female', "Jennifer Aniston was born in Sherman Oaks, California, to actors John Aniston and Nancy Dow. Her father was Greek, and her mother was of English, Irish, Scottish, and Italian descent. Jennifer spent a year of her childhood living in Greece with her family. Her family then relocated to New York City where her parents divorced when she was nine. Jennifer was raised by her mother and her father landed a role, as \"Victor Kiriakis\", on the daytime soap Days of Our Lives (1965). Jennifer had her first taste of acting at age 11 when she joined the Rudolf Steiner School's drama club. It was also at the Rudolf Steiner School that she developed her passion for art. She began her professional training as a drama student at New York's School of Performing Arts, aka the \"Fame\" school. It was a division of Fiorello H. LaGuardia High School of Music and the Arts. In 1987, after graduation, she appeared in such Off-Broadway productions as \"For Dear Life\" and \"Dancing on Checker's Grave\". In 1990, she landed her first television role, as a series regular on Molloy (1990). She also appeared in The Edge (1992), Ferris Bueller (1990), and had a recurring part on Herman's Head (1991). By 1993, she was floundering. Then, in 1994, a pilot called \"Friends Like These\" came along. Originally asked to audition for the role of \"Monica\", Aniston refused and auditioned for the role of \"Rachel Green\", the suburban princess turned coffee peddler. With the success of the series Friends (1994), Jennifer has become famous and sought-after as she turns her fame into movie roles during the series hiatus."),
('Amy', 'Poehler', '1971-09-16', 'American', 'Female', "Amy Meredith Poehler was born in Newton, Massachusetts, to high school teachers Eileen Frances (Milmore) and William Grinstead Poehler. Her brother is comedian Greg Poehler. She is of Irish (from her mother) and English, Irish, German, and Portuguese (from her father) descent. Amy was first involved with sketch comedy when she joined the group My Mother's Flea Bag when she was attending Boston College. In 1993, she went to Chicago where she studied at Second City and Improv Olympics. There, she met Del Close, who later became the voice of the UCB opening scene. In 1996, she joined the Upright Citizen's Brigade with Matt Besser, Ian Roberts, and Matt Walsh. Later on, the group moved to New York and became a Comedy Central show. The show went on only for three seasons. However, the group stayed together at the Upright Citizen's Brigade Theater. Today, the theater is one of the leading centers for improv and sketch shows.

After the Upright Citizens Brigade (1998) sketch show came and went, Amy joined the cast of Saturday Night Live (1975) in 2001. By the end of Christmas break of that year, she became a regularly featured performer. She has brought a slew of great performances on every show, such as impersonations of celebrities such as Kelly Ripa or Sharon Osbourne. When Jimmy Fallon left at the end of the 2003-04 season, Amy joined Tina Fey as a co-anchor for Weekend Update. Her Hollywood star is also growing bright, as she has done several feature films, including Blades of Glory (2007) with her then-real-life husband and Arrested Development (2003) star Will Arnett; and the Farrelly brothers-directed remake of The Heartbreak Kid (1993), in which she stars alongside another Arrested Development (2003) star, Jason Bateman.

Among her 2010s film starring roles are Sisters (2015), with Tina Fey, and The House (2017), with Will Ferrell. Having played one of her first roles in the indie Wet Hot American Summer (2001), she reprised her role in the television mini-series Wet Hot American Summer: First Day of Camp (2015) and Wet Hot American Summer: Ten Years Later (2017).

Amy was married to Canadian actor and comedian Will Arnett from 2003 to 2016. The couple have two children."),
('Jason', 'Schwartzman', '1980-06-26', 'American', 'Male', "Jason Francesco Schwartzman is an American actor and musician. Schwartzman made his film debut in Wes Anderson's 1998 film Rushmore, and has gone on to appear in seven other Anderson films: The Darjeeling Limited (2007), Fantastic Mr. Fox (2009), Moonrise Kingdom (2012), The Grand Budapest Hotel (2014), Isle of Dogs (2018), The French Dispatch (2021) and Asteroid City (2023). His other film roles include Spun (2003), I Heart Huckabees (2004), Marie Antoinette (2006), and Klaus (2019). Schwartzman starred in the television series Bored to Death (2009-11) and appeared in the fourth season of the FX anthology series Fargo (2020). He was an executive producer on the Amazon Prime show Mozart in the Jungle (2014-18), a series he also acted in. Schwartzman has released three albums through his solo project Coconut Records, having previously been drummer in the rock band Phantom Planet."),
('Maya', 'Rudolph', '1972-07-27', 'American', 'Female', "Maya Rudolph was born on July 27, 1972 in Gainesville, Florida, to Richard Rudolph, a music producer, and soul singer Minnie Riperton. Her mother was African-American and her father is Ashkenazi Jewish (from a family from Lithuania, Russia, Germany, and Hungary). In 1973, Maya, her parents, and her older brother, Marc Rudolph, moved to California to further Minnie's music career. Here Minnie recorded \"Lovin' You\", her most famous single, in which one can hear her sing \"Maya, Maya, Maya\" at the end of the song; Riperton said that the song was used as a lullaby for Maya.

During adolescence, Maya attended St. Augustine by the Sea School, where she met childhood friend, Gwyneth Paltrow. The Paltrows and the Rudolphs became family friends and, in 2000, Richard Rudolph and Maya filled the role of music supervisors on the Bruce Paltrow-directed film Duets (2000), which starred Gwyneth.

In 1990, Maya enrolled at the University of California at Santa Cruz, majoring in photography. It was here that Maya formed the band \"Supersauce\" with fellow students. After graduation in 1994, Maya left the band and soon joined The Rentals, fronted by Weezer bassist Matt Sharp. Maya was featured on the 1999 release \"Seven More Minutes\", where she sang backup vocals on \"Barcelona\" and \"My Head is in the Sun\". Maya began touring with the group, singing backup and playing Moog synthesizer. When The Rentals disbanded, Maya decided to pursue her dream of a career in comedy, joining the famed troupe \"The Groundlings\".

On May 6, 2000, Maya joined the cast of Saturday Night Live (1975), and became one of that show's most popular performers. Famous sketches include a dead-on impression of fashion diva Donatella Versace; high school flake \"Megan\", the host of her own morning talk show, \"Wake-up WakeField\"; and one of the members of the R&B parody \"Gemini's Twin\". In 2006, she co-starred in the film A Prairie Home Companion (2006), directed by the legendary Robert Altman and based on the NPR show by Garrison Keillor.

Maya has four children with her partner, filmmaker Paul Thomas Anderson."),
('Leven', 'Rambin', '1990-05-17', 'American', 'Female', "Leven Alice Rambin is an American actress. She is known for playing look-alike half-sisters Lily Montgomery and Ava Benton on All My Children and her recurring roles in Grey's Anatomy and Gone, as well Terminator: The Sarah Connor Chronicles, One Tree Hill, Wizards of Waverly Place, and CSI: Miami. She appeared in the sci-fi film The Hunger Games (2012) as the District 1 tribute Glimmer, and appeared as Clarisse La Rue in the fantasy film Percy Jackson: Sea of Monsters (2013).

Rambin was born in Houston, Texas, to Joseph Howard Rambin III and the former Karen Stacy Guthrie, founders of a real estate company. She has three siblings, her brother, Joseph Rambin, an older half-sister, Mary Rambin, and an older half-brother, Jay Rambin. Rambin began performing in school plays, and studied at the Houston School of Film and Theatre. She also went to St. Francis Episcopal Day School, where she starred in her first school play. She then moved to New York when she got her first major role on All My Children. While acting, she has also pursued her high school diploma through the Texas Tech University Independent School District.

Rambin started her career at age of 13, as a series regular on ABC's All My Children from 2004 to 2008, playing the role of Lily Montgomery, the autistic daughter of Jackson Montgomery, played by Walt Willey. It was announced that Rambin had been cast in another role on All My Children, playing the street-smart but lovable older half sister of Lily Montgomery, Ava Benton, where the role only lasted for two consecutive years. Rambin was the only actress from All My Children to be nominated for the 33rd Annual Daytime Awards and 34th Annual Daytime Emmy Awards. She was nominated for Outstanding Younger Actress in a Drama Series for her roles of Lily Montgomery and Ava Benton. She later appeared on an episode of Law & Order: Special Victims Unit.

In 2010, Rambin appeared as the daughter of Virginia Madsen's and David James Elliott's characters on the ABC television series, Scoundrels. However, the series ended its eight-episode run on August 15, 2010, due to low ratings. Rambin appeared on the Disney Channel show, Wizards of Waverly Place, in the recurring role of Rosie, who is a Guardian Angel turned Angel of Darkness who was in love with Justin to steal the Moral Compass; Rambin made her debut on the episode \"Everything Rosie for Justin\" and left the show after the episode \"Wizards vs. Angels\". Later, Rambin was cast in the recurring role of Chloe Hall, on the long-running CW series, One Tree Hill. Then later, Rambin was cast as Molly Sloan on the CBS crime-drama CSI: Miami.

Rambin was cast as Daisy in the romantic comedy Two Night Stand (2014). Rambin appeared in the comedy drama, Walter (2014) as Kendall, the main love interest of the title character. Rambin also appeared in Seven Minutes (2014) as Kate. She was cast as Natalie in The Tomorrow People, appearing in the final three episodes of the television series in 2014. Rambin later appeared in True Detective as Athena Bezzerides and appeared in the film I Am Michael in 2015 as Catherine. In 2014, she landed the lead role of Kayla Canyon in the independently produced pilot, Dr. Del, opposite John Hawkes. Later that year, she was cast in the lead role of Fern Sreaves in Tatterdemalion, an independent film co-written and directed by Ramaa Moseley. In 2016, she was cast in a series regular role on the Hulu cult drama, The Path, opposite Aaron Paul and Michelle Monaghan. In 2017, Rambin was cast in the starring role of Kick Lanigan in the NBC Universal series, Gone, based on the novel One Kick by Chelsea Cain."),
('Blair', 'Underwood', '1964-08-25', 'American', 'Male', "A true multi-hyphenate, Blair Underwood is enjoying success in film, television and theatre, as an actor, director and producer. Underwood recently returned to Broadway starring opposite David Alan Grier in the Pulitzer Prize winning drama \"A Solider's Play\" for director Kenny Leon and the Roundabout Theatre Company. He also co-stars in Justin Simien's \"Bad Hair\" which will premiere at the 2020 Sundance Film Festival. Also this year, Underwood stars opposite Octavia Spencer & Tiffany Haddish in Netflix's highly anticipated limited series \"Self Made: Inspired by the Life of Madam CJ Walker\" (March 20).

Underwood recently appeared in the Netflix Emmy-Award winning limited series \"When They See Us.\" He also had a recurring role on the Netflix comedy series, \"Dear White People\" and can be seen in Clark Johnson's \"Juanita,\" opposite Alfre Woodard, also for Netflix. He spent two years as a series regular on the ABC drama series \"Quantico,\" while also recurring on another hit ABC drama \"MARVEL AGENTS OF S.H.I.E.L.D. \" He also had a co-starring role in \"The After Party,\" from writer/director Ian Edelman, which Netflix released late in 2018.

Past television credits include series regular roles on \"Dirty Sexy Money,\" \"The New Adventures of Old Christine,\" \"In Treatment,\" \"The Event\" and \"L.A. Law\". Film credits include \"Deep Impact,\" \"Set It Off,\" \"Rules of Engagement,\" \"Just Cause,\" \"Madea's Family Reunion\" and Steven Soderbergh's \"Full Frontal.\" Underwood co-starred opposite Cicely Tyson in the Lifetime telefilm & theatre production of \"A Trip to Bountiful,\" based on the Tony Award-winning play.

In 2012 he made his acclaimed Broadway debut in the iconic role of Stanley in Tennessee Williams' \"A Streetcar Named Desire,\" for which he earned a 2012 Drama League Distinguished Performance Award nomination. He also starred in \"Paradise Blue\" at the Williamstown Theatre Festival and \"Othello\" at the Old Globe Theatre.

Underwood also has several projects in the development pipeline as a director, including \"Viral,\" a feature based on a Joe McClean script. In 2010 he made his feature film directing debut with \"The Bridge to Nowhere,\" which starred Ving Rhames, Danny Masterson, Bijou Phillips and Alex Breckenridge.

Underwood is an Emmy Award-winner (as producer of the philanthropy-centered NBC Saturday morning series \"Give\"), a two-time Golden Globe Award nominee, and has been nominated for 17 NAACP Image Awards (seven wins). He won a Grammy Award for Best Spoken Word as co-narrator of Al Gore's audiobook, An Inconvenient Truth. A newly minted member of the Academy of Motion Picture Arts and Sciences, he is also active in several philanthropic endeavors."),
('Chadwick', 'Boseman', '1976-11-29', 'American', 'Male', "Chadwick Boseman was an American actor. He is known for his portrayal of T'Challa / Black Panther in the Marvel Cinematic Universe from 2016 to 2019, particularly in Black Panther (2018), and for his starring roles as several pioneering Americans, Jackie Robinson in 42 (2013), James Brown in Get on Up (2014), and Thurgood Marshall in Marshall (2017). He also had choice parts in The Express (2008), Draft Day (2014), and Message from the King (2016). Born in Anderson, South Carolina, he attended Howard University and studied at the Oxford Mid-Summer Program for acting, before moving to Los Angeles in 2008 to pursue his craft on the big screen. He died in 2020, after a four year bout with colon cancer, during which time he had starred in several of the biggest movies ever made."),
('Luke', 'Evans', '1979-04-15', 'British', 'Male', "Luke George Evans was born in Pontypool, Wales, and grew up in Aberbargoed, in the south of Wales. He is the son of Yvonne (Lewis) and David Evans. He moved to Cardiff at the age 17. He then won a scholarship to the London Studio Centre, and graduated in 2000.

He starred in many of London's West End theatre productions. In 2009, he landed the role of Apollo in Clash of the Titans (2010) (2010). Then, he quickly landed roles in Sex & Drugs & Rock & Roll (2010) (2010), Robin Hood (2010) (2010), Tamara Drewe (2010) (2010), Blitz (2011) (2010), The Three Musketeers (2011) (2011), Ashes (2011), Immortals (2011) (2011) and The Raven (2012) (2012). He's also scheduled to star in The Amateur American (2012), No One Lives (2012) (2012) and Only God Forgives (2013) (2013).

Luke Evans resides in Shoreditch, London.");

INSERT INTO genres (name)
VALUES
('Action'),
('Horror'),
('Drama'),
('Science fiction'),
('Thriller'),
('Comedy'),
('Romance'),
('Western'),
('Crime film'),
('Drama'),
('Fantasy'),
('Comedy'),
('Adventure'),
('Documentary'),
('Musical'),
('Mystery'),
('Animation'),
('Narrative'),
('History'),
('Romantic comedy'),
('Experimental'),
('Fantasy'),
('Noir'),
('Children''s film'),
('War'),
('Fiction'),
('Romance'),
('Animated film'),
('Martial Arts'),
('Exploitation'),
('Dark comedy'),
('Epic'),
('Crime'),
('Anime'),
('Slasher'),
('Short'),
('Science fiction'),
('Fantasy'),
('Magical Realism'),
('Thriller'),
('Fairy tale'),
('Disaster'),
('Science'),
('Sports'),
('Spaghetti western'),
('Historical Fiction'),
('Melodrama'),
('Superhero'),
('Art');

INSERT INTO languages (name)
VALUES
('Spanish'),
('English'),
('Russian'),
('Arabic'),
('German'),
('Danish'),
('French'),
('Greek'),
('Italian'),
('Croatian'),
('Dutch'),
('Turkish'),
('Polish');

INSERT INTO productioncompanies (name)
VALUES
('Tango Entertainment'),
('Range Media Partners'),
('Lionsgate Films'),
('TSG Entertainment'),
('FilmNation Entertainment'),
('Scott Free Productions'),
('Fox 2000 Pictures'),
('Legendary Entertainment'),
('Warner Bros.'),
('Paramount Pictures'),
('Universal Pictures'),
('20th Century Studios'),
('Miramax'),
('Metro-Goldwyn-Mayer'),
('New Line Cinema'),
('Sony Pictures'),
('DreamWorks Pictures'),
('Columbia Pictures'),
('Walt Disney Studios'),
('Starz Entertainment Corp.'),
('Focus Features'),
('The Walt Disney Company'),
('TriStar Pictures'),
('The Weinstein Company'),
('2929 Entertainment'),
('Filmways'),
('Netflix'),
('Orion Pictures'),
('Vitagraph Studios'),
('Touchstone Pictures'),
('RLJE Films'),
('RKO Pictures'),
('Lucasfilm'),
('Legendary Entertainment'),
('Gaumont'),
('Amblin Entertainment'),
('Artisan Entertainment'),
('606 Films'),
('American International Pictures'),
('SF-Produktion'),
('Alliance Communications'),
('CBS Films'),
('Marvel Studios'),
('Moving Picture Company'),
('Viacom'),
('DreamWorks Animation'),
('Film4 Productions'),
('Summit Entertainment'),
('WildBrain'),
('Happy Madison Productions'),
('Bavaria Film'),
('American Zoetrope'),
('AVM Productions'),
('Five Star Production'),
('Castle Rock Entertainment'),
('Hollywood Pictures'),
('Dimension Films'),
('Carolco Pictures'),
('40 Acres and a Mule Filmworks');

INSERT INTO distributingcompanies (name)
VALUES
('Vertical'),
('Paramount Pictures'),
('Sony Pictures'),
('Neon'),
('Netflix'),
('Walt Disney Studios Motion Pictures'),
('Universal Pictures'),
('Reel One Entertainment'),
('Cineverse'),
('1844 Entertainment'),
('Focus Features'),
('Pathé'),
('FilmRise'),
('Magnolia Pictures'),
('20th Century Studios'),
('IFC Films'),
('Lionsgate'),
('Searchlight Pictures'),
('Warner Bros.'),
('Media Asia'),
('Abramorama');