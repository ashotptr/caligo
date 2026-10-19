SET search_path TO streamingservice;

CREATE TABLE bloguser (
  user_id integer NOT NULL,
  identity_id varchar(450) NOT NULL,
  PRIMARY KEY (user_id),
  CONSTRAINT bloguser_identity_id UNIQUE (identity_id),
  CONSTRAINT bloguser_ibfk_1 FOREIGN KEY (user_id) REFERENCES "user" (user_id) ON DELETE CASCADE
);
