use streamingservice;

CREATE VIEW popularcontent AS
WITH popularcontent AS (
    SELECT
        c.content_id,
        COUNT(w.user_id) AS totalviews
    FROM 
        content c
    LEFT JOIN watchhistory w ON c.content_id = w.content_id
    GROUP BY c.content_id
    ORDER BY totalviews DESC
    LIMIT 10
)
SELECT c.content_id, c.title, pc.totalviews
FROM content c
JOIN popularcontent pc ON c.content_id = pc.content_id;

CREATE VIEW activeusers AS
WITH 
    userviews AS (
        SELECT user_id, COUNT(*) AS total_views
        FROM watchhistory
        GROUP BY user_id
    ),
    userinteractions AS (
        SELECT user_id, COUNT(DISTINCT content_id) AS total_interactions
        FROM usercontentinteraction
        GROUP BY user_id
    ),
    usercomments AS (
        SELECT user_id, COUNT(*) AS comments_left
        FROM comment
        GROUP BY user_id
    )
SELECT 
    u.user_id, 
    CONCAT(u.first_name, ' ', u.last_name) AS full_name, 
    COALESCE(uv.total_views, 0) AS total_views,
    COALESCE(ui.total_interactions, 0) AS total_interactions,
    COALESCE(uc.comments_left, 0) AS comments_left
FROM 
    user u
LEFT JOIN userviews uv ON u.user_id = uv.user_id
LEFT JOIN userinteractions ui ON u.user_id = ui.user_id
LEFT JOIN usercomments uc ON u.user_id = uc.user_id
ORDER BY total_views + total_interactions + comments_left DESC
LIMIT 10;

CREATE VIEW engagingcontent AS
WITH 
    contentviews AS (
        SELECT content_id, COUNT(*) AS total_views
        FROM watchhistory
        GROUP BY content_id
    ),
    contentinteractions AS (
        SELECT content_id, COUNT(DISTINCT user_id) AS total_interactions
        FROM usercontentinteraction
        GROUP BY content_id
    ),
    contentcomments AS (
        SELECT content_id, COUNT(*) AS comments_left
        FROM comment
        GROUP BY content_id
    )
SELECT 
    c.content_id,
    c.title,
    COALESCE(cv.total_views, 0) AS total_views,
    COALESCE(ci.total_interactions, 0) AS total_interactions,
    COALESCE(cc.comments_left, 0) AS comments_left,
    ((total_interactions + comments_left) / total_views) AS engagement_ratio
FROM 
    content c
LEFT JOIN contentviews cv ON c.content_id = cv.content_id
LEFT JOIN contentinteractions ci ON c.content_id = ci.content_id
LEFT JOIN contentcomments cc ON c.content_id = cc.content_id
HAVING 
    total_views > 0
ORDER BY 
    engagement_ratio DESC
LIMIT 10;

