USE streamingservice;

DELIMITER //

CREATE FUNCTION calculatesimilarity (content1_id INT, content2_id INT)
RETURNS FLOAT
DETERMINISTIC
BEGIN
    DECLARE intersection_count INT;
    DECLARE union_count INT;

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

    RETURN IF(union_count > 0, intersection_count / union_count, 0);
END;

//
DELIMITER ;

DELIMITER //

CREATE FUNCTION checkactivesubscription (user_id INT)
RETURNS BIT
DETERMINISTIC
BEGIN
    DECLARE is_active BIT;
    
    SELECT EXISTS (
        SELECT 1 
        FROM user 
        WHERE user_id = user_id 
          AND CURRENT_DATE BETWEEN subscription_start_date AND subscription_end_date
    ) INTO is_active;
    
    RETURN is_active;
END;

//
DELIMITER ;

DELIMITER //

CREATE FUNCTION calculatealltimespending(user_id INT)
RETURNS FLOAT
DETERMINISTIC
BEGIN
    DECLARE alltimespending FLOAT;
    
    SELECT SUM(amount)
    INTO alltimespending
    FROM payments
    WHERE payments.user_id = user_id
    GROUP BY payments.user_id;
    
    RETURN IFNULL(alltimespending, 0);
END;

//
DELIMITER ;