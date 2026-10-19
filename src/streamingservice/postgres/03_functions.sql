SET search_path TO streamingservice;

CREATE FUNCTION calculatesimilarity (content1_id integer, content2_id integer)
RETURNS real
LANGUAGE plpgsql
STABLE
SET search_path = streamingservice
AS $$
#variable_conflict use_variable
DECLARE
    intersection_count integer;
    union_count integer;
BEGIN
    SELECT COUNT(*) INTO intersection_count
    FROM (
        SELECT genre_id
        FROM contentgenres
        WHERE content_id = content1_id
        AND genre_id IN (
            SELECT genre_id
            FROM contentgenres
            WHERE content_id = content2_id
        )
    ) AS intersect_genres;

    SELECT COUNT(*) INTO union_count
    FROM (
        SELECT genre_id
        FROM contentgenres
        WHERE content_id = content1_id
        UNION
        SELECT genre_id
        FROM contentgenres
        WHERE content_id = content2_id
    ) AS union_genres;

    RETURN CASE WHEN union_count > 0 THEN ROUND(intersection_count::numeric / union_count, 4) ELSE 0 END;
END;
$$;

CREATE FUNCTION checkactivesubscription (user_id integer)
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
        WHERE user_id = user_id
          AND CURRENT_DATE BETWEEN subscription_start_date AND subscription_end_date
    ) THEN B'1' ELSE B'0' END INTO is_active;

    RETURN is_active;
END;
$$;

CREATE FUNCTION calculatealltimespending (user_id integer)
RETURNS real
LANGUAGE plpgsql
STABLE
SET search_path = streamingservice
AS $$
#variable_conflict use_variable
DECLARE
    alltimespending real;
BEGIN
    SELECT SUM(amount::double precision)
    INTO alltimespending
    FROM payments
    WHERE payments.user_id = user_id
    GROUP BY payments.user_id;

    RETURN COALESCE(alltimespending, 0);
END;
$$;
