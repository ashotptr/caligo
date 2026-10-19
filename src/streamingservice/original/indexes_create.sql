-- query 1
CREATE INDEX idx_watchhistory_user_id ON watchhistory(user_id);
CREATE INDEX idx_user_user_id ON user(user_id);
CREATE INDEX idx_watchhistory_content_id ON watchhistory(content_id);
CREATE INDEX idx_content_content_id ON content(content_id);

-- query 2
CREATE INDEX idx_contentgenres_content_id ON contentgenres(content_id);
CREATE INDEX idx_contentgenres_genre_id ON contentgenres(genre_id);
CREATE INDEX idx_user_user_id ON user(user_id);

-- query 3
CREATE INDEX idx_payments_user_id ON payments(user_id);

-- query 4
CREATE INDEX idx_usercontentinteraction_content_id ON usercontentinteraction(content_id);

-- query 7
CREATE INDEX idx_usercontentinteraction_user_id ON usercontentinteraction(user_id);
CREATE INDEX idx_comment_user_id ON comment(user_id);
CREATE INDEX idx_comment_content_id ON comment(content_id);

-- query 8
CREATE INDEX idx_movie_content_id ON movie(content_id);
CREATE INDEX idx_contentactor_content_id ON contentactor(content_id);
CREATE INDEX idx_contentactor_actor_id ON contentactor(actor_id);
CREATE INDEX idx_actor_actor_id ON actor(actor_id);