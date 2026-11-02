SET search_path TO streamingservice;

CREATE FUNCTION after_content_genre_added ()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = streamingservice
AS $$
BEGIN
    INSERT INTO notifications (user_id, reason_id, notification_date)
    SELECT fans.user_id, 4, LOCALTIMESTAMP
    FROM (
        SELECT w.user_id
        FROM watchhistory w
        JOIN contentgenres g ON g.content_id = w.content_id
        WHERE g.genre_id = NEW.genre_id AND w.content_id <> NEW.content_id
        UNION
        SELECT i.user_id
        FROM usercontentinteraction i
        JOIN contentgenres g ON g.content_id = i.content_id
        WHERE g.genre_id = NEW.genre_id AND i.content_id <> NEW.content_id AND i.liked = B'1'
    ) AS fans
    WHERE NOT EXISTS (
        SELECT 1
        FROM notifications n
        WHERE n.user_id = fans.user_id
          AND n.reason_id = 4
          AND n.notification_date >= CURRENT_DATE
    );

    RETURN NEW;
END;
$$;

CREATE TRIGGER after_content_genre_added
AFTER INSERT ON contentgenres
FOR EACH ROW
EXECUTE FUNCTION after_content_genre_added();
