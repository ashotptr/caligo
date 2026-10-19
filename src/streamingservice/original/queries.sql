-- Query 1 
-- Get all content watched by users in the last 30 days
SELECT CONCAT(u.first_name, ' ', u.last_name) AS user_name, c.title, wh.date
FROM watchhistory wh
JOIN user u ON wh.user_id = u.user_id
JOIN content c ON wh.content_id = c.content_id
WHERE wh.date > CURDATE() - INTERVAL 30 DAY
ORDER BY wh.date DESC;

-- Query 2
-- Find who watched the most content in every genre that had content
WITH userwatchcounts AS (
    SELECT u.*, c.genre_id, COUNT(wh.content_id) AS watch_count
    FROM watchhistory wh
    JOIN contentgenres c ON wh.content_id = c.content_id
    JOIN user u ON wh.user_id = u.user_id
    GROUP BY u.user_id, c.genre_id
),
maxwatchcounts AS (
    SELECT genre_id, MAX(watch_count) AS max_watch_count
    FROM userwatchcounts
    GROUP BY genre_id
)
SELECT g.name AS genre_name, 
       CONCAT(uwc.first_name, ' ', uwc.last_name) AS user_name, 
       uwc.watch_count AS watched_count
FROM genres g
JOIN userwatchcounts uwc ON uwc.genre_id = g.genre_id
JOIN maxwatchcounts mwc ON mwc.genre_id = g.genre_id AND uwc.watch_count = mwc.max_watch_count
ORDER BY watched_count DESC;

-- Query 3
-- Find the highest payment made by a user
SELECT CONCAT(u.first_name, ' ', u.last_name) AS user_name, MAX(p.amount) AS highest_payment
FROM payments p
JOIN user u ON p.user_id = u.user_id
GROUP BY u.user_id;

-- Query 4
-- Rank content based on user interactions (top 3)
SELECT c.title, COUNT(i.user_id) AS interactions,
       RANK() OVER (ORDER BY COUNT(i.user_id) DESC) as ranking
FROM usercontentinteraction i
JOIN content c ON i.content_id = c.content_id
GROUP BY c.content_id
ORDER BY ranking DESC
LIMIT 3;

-- Query 5
-- Get top 10 users who have spent overall the most money
SELECT CONCAT(u.first_name, ' ', u.last_name) AS user_name, calculatealltimespending(user_id) AS alltimespending
FROM user u
ORDER BY alltimespending DESC
LIMIT 10;

-- Query 6
-- Find top 10 similar contents to the most popular content
WITH contentsimilarity AS (
    SELECT c.title, 
           calculatesimilarity((SELECT content_id FROM popularcontent LIMIT 1), c.content_id) AS similarity
    FROM content c
    WHERE c.content_id != (SELECT content_id FROM popularcontent LIMIT 1)
)
SELECT title, similarity,
       DENSE_RANK() OVER (ORDER BY similarity DESC) AS similarity_rank
FROM contentsimilarity
ORDER BY similarity_rank
LIMIT 10;

-- Query 7
-- Find top 10 users who watched content but did not engage with it
SELECT CONCAT(u.first_name, ' ', u.last_name) AS user_name, COUNT(wh.content_id) AS watched_count
FROM watchhistory wh
JOIN user u ON wh.user_id = u.user_id
LEFT JOIN usercontentinteraction ui ON wh.content_id = ui.content_id AND wh.user_id = ui.user_id
LEFT JOIN comment com ON wh.content_id = com.content_id AND wh.user_id = com.user_id
WHERE ui.content_id IS NULL AND com.content_id IS NULL
GROUP BY u.user_id
ORDER BY watched_count DESC
LIMIT 10;

-- Query 8
-- Actors that participated in the top 3 most profitable movies
WITH topprofitablemovies AS (
    SELECT m.content_id, (m.box_office - m.budget) AS profit
    FROM movie m
    ORDER BY (m.box_office - m.budget) DESC
    LIMIT 3
)
SELECT a.actor_id, CONCAT(a.first_name, ' ', a.last_name) AS actor_name, c.title AS movie_title
FROM actor a
JOIN contentactor ca ON a.actor_id = ca.actor_id
JOIN content c ON ca.content_id = c.content_id
JOIN topprofitablemovies tpm ON c.content_id = tpm.content_id
ORDER BY tpm.profit DESC;

-- Query 9
-- Check what content did users whose subscription is currently ianctive watch during their subscription
SELECT c.content_id, c.title
FROM watchhistory w
JOIN user u ON w.user_id = u.user_id
JOIN content c ON w.content_id = c.content_id
WHERE checkactivesubscription(u.user_id) = 0 AND w.date BETWEEN u.subscription_start_date AND u.subscription_end_date;

-- Query 10
-- Get random 5 users in top 10 most active users
SELECT *
FROM activeusers
ORDER BY RAND()
LIMIT 5;