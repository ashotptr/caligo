SET search_path TO streamingservice;

CREATE FUNCTION after_watch_completion ()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = streamingservice
AS $$
BEGIN
    DELETE FROM currentcontentrecommendations c WHERE c.user_id = NEW.user_id;

    INSERT INTO currentcontentrecommendations (user_id, content_id, recommendation_reason, recommendation_date)
    WITH contentsimilarity AS (
        SELECT c.content_id,
               calculatesimilarity(NEW.content_id, c.content_id) AS similarity
        FROM content c
        WHERE c.content_id != NEW.content_id
    )
    SELECT NEW.user_id, contentsimilarity.content_id, 'Jaccard index', CURRENT_DATE
    FROM contentsimilarity
    ORDER BY similarity DESC
    LIMIT 10;

    RETURN NEW;
END;
$$;

CREATE TRIGGER after_watch_completion
AFTER INSERT ON watchhistory
FOR EACH ROW
EXECUTE FUNCTION after_watch_completion();

CREATE FUNCTION after_user_watch_count_update ()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = streamingservice
AS $$
DECLARE
    watch_count integer;
BEGIN
    SELECT COUNT(*)
    INTO watch_count
    FROM (
        SELECT DISTINCT user_id, content_id
        FROM watchhistory
        WHERE user_id = NEW.user_id
    ) AS distinct_watchhistory;

    IF watch_count = 50 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 1, LOCALTIMESTAMP);
    ELSIF watch_count = 100 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 2, LOCALTIMESTAMP);
    ELSIF watch_count = 200 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 3, LOCALTIMESTAMP);
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER after_user_watch_count_update
AFTER INSERT ON watchhistory
FOR EACH ROW
EXECUTE FUNCTION after_user_watch_count_update();

CREATE FUNCTION update_content_rating_after_feedback ()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = streamingservice
AS $$
DECLARE
    new_avg_rating real;
BEGIN
    SELECT AVG(rating)
    INTO new_avg_rating
    FROM usercontentinteraction uci
    WHERE uci.content_id = NEW.content_id;

    UPDATE content
    SET average_rating = new_avg_rating
    WHERE content_id = NEW.content_id;

    RETURN NEW;
END;
$$;

CREATE TRIGGER update_content_rating_after_feedback
AFTER INSERT ON usercontentinteraction
FOR EACH ROW
EXECUTE FUNCTION update_content_rating_after_feedback();
