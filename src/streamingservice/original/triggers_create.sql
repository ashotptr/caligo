DELIMITER //

CREATE TRIGGER after_watch_completion
AFTER INSERT ON watchhistory
FOR EACH ROW
BEGIN
	DELETE FROM currentcontentrecommendations c WHERE c.user_id = NEW.user_id;
	
	INSERT INTO currentcontentrecommendations (user_id, content_id, recommendation_reason, recommendation_date)
	WITH contentsimilarity AS (
		SELECT c.content_id,
			   calculatesimilarity(NEW.content_id, c.content_id) AS similarity
		FROM content c
		WHERE c.content_id != NEW.content_id
	)
	SELECT NEW.user_id, contentsimilarity.content_id, "Jaccard index", CURDATE()
	FROM contentsimilarity
	ORDER BY similarity DESC
	LIMIT 10;
END;

//
DELIMITER ;

DELIMITER //

CREATE TRIGGER after_user_watch_count_update
AFTER INSERT ON watchhistory
FOR EACH ROW
BEGIN
    DECLARE watch_count INT;

    SELECT COUNT(*) 
    INTO watch_count
    FROM (
        SELECT DISTINCT user_id, content_id 
        FROM watchhistory 
        WHERE user_id = NEW.user_id
    ) AS distinct_watchhistory;

    IF watch_count = 50 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 1, NOW());
    ELSEIF watch_count = 100 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 2, NOW());
    ELSEIF watch_count = 200 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 3, NOW());
    END IF;
END;

//

DELIMITER ;

DELIMITER //

CREATE TRIGGER update_content_rating_after_feedback
AFTER INSERT ON usercontentinteraction
FOR EACH ROW
BEGIN
    DECLARE new_avg_rating FLOAT;

    SELECT AVG(rating)
    INTO new_avg_rating
    FROM usercontentinteraction uci
    WHERE uci.content_id = NEW.content_id;

    UPDATE content
    SET average_rating = new_avg_rating
    WHERE content_id = NEW.content_id;
END;
//

DELIMITER ;