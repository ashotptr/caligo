SET search_path TO streamingservice;

INSERT INTO notification_reasons VALUES
(7, 'You''ve watched 50 titles'),
(8, 'You''ve watched 100 titles'),
(9, 'You''ve watched 200 titles');

ALTER TABLE notification_reasons ALTER COLUMN reason_id RESTART WITH 10;

CREATE OR REPLACE FUNCTION after_user_watch_count_update ()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = streamingservice
AS $$
DECLARE
    watch_count integer;
BEGIN
    IF EXISTS (
        SELECT 1
        FROM watchhistory
        WHERE user_id = NEW.user_id
          AND content_id = NEW.content_id
          AND "watched-content_id" < NEW."watched-content_id"
    ) THEN
        RETURN NEW;
    END IF;

    SELECT COUNT(DISTINCT content_id)
    INTO watch_count
    FROM watchhistory
    WHERE user_id = NEW.user_id
      AND "watched-content_id" <= NEW."watched-content_id";

    IF watch_count = 50 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 7, LOCALTIMESTAMP);
    ELSIF watch_count = 100 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 8, LOCALTIMESTAMP);
    ELSIF watch_count = 200 THEN
        INSERT INTO notifications (user_id, reason_id, notification_date)
        VALUES (NEW.user_id, 9, LOCALTIMESTAMP);
    END IF;

    RETURN NEW;
END;
$$;
