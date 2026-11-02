SET search_path TO streamingservice;

CREATE OR REPLACE FUNCTION checkactivesubscription (user_id integer)
RETURNS bit
LANGUAGE plpgsql
STABLE
SET search_path = streamingservice
AS $$
#variable_conflict use_variable
DECLARE
    is_active bit;
BEGIN
    SELECT CASE WHEN EXISTS (
        SELECT 1
        FROM "user"
        WHERE "user".user_id = user_id
          AND CURRENT_DATE BETWEEN subscription_start_date AND subscription_end_date
    ) THEN B'1' ELSE B'0' END INTO is_active;

    RETURN is_active;
END;
$$;
