SET search_path TO streamingservice;

DO $$
DECLARE
    free_plan integer;
BEGIN
    INSERT INTO subscriptionplan (payment_info, plan_info)
    VALUES (
        '{"monthly_price": "€0.00"}',
        '{"name": "Free", "video_and_sound_quality": "Best", "resolution": "4K (Ultra HD) + HDR", "spatial_audio": "Included", "supported_devices": ["TV", "computer", "mobile phone", "tablet"], "devices_household_can_watch_at_same_time": 4, "download_devices": 6}')
    RETURNING subscription_plan_id INTO free_plan;

    DELETE FROM payments WHERE subscription_plan_id <> free_plan;

    UPDATE "user" SET subscription_plan_id = free_plan;

    DELETE FROM subscriptionplan WHERE subscription_plan_id <> free_plan;
END;
$$;
