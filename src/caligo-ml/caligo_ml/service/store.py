from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import (
    JSON,
    Boolean,
    Column,
    DateTime,
    Float,
    Integer,
    MetaData,
    String,
    Table,
    Text,
    create_engine,
    func,
    text,
)
from sqlalchemy.engine import make_url

metadata = MetaData()

predictions = Table(
    "prediction_log", metadata,
    Column("id", Integer, primary_key=True, autoincrement=True),
    Column("created_at", DateTime(timezone=True), server_default=func.now(), nullable=False, index=True),
    Column("task", String(32), nullable=False, index=True),
    Column("model_version", Integer, nullable=False),
    Column("source", String(16), nullable=False),
    Column("subject", String(512)),
    Column("profile", JSON, nullable=False),
    Column("output", JSON, nullable=False),
    Column("latency_ms", Float, nullable=False),
)

songs = Table(
    "song_analysis", metadata,
    Column("path", String(512), primary_key=True),
    Column("title", String(255)),
    Column("artist", String(255)),
    Column("album", String(255)),
    Column("analyzed_at", DateTime(timezone=True), nullable=False),
    Column("file_size", Integer, nullable=False),
    Column("modified", Float, nullable=False),
    Column("problem", String(32)),
    Column("features", JSON),
    Column("genre", String(32)),
    Column("genres", JSON),
    Column("energy", Float),
    Column("valence", Float),
    Column("danceability", Float),
    Column("instrumental", Float),
    Column("sound_group", Integer),
    Column("genre_features", String(32)),
    Column("genre_cnn", String(32)),
    Column("spectrum", JSON),
    Column("embedding", JSON),
    Column("cnn_chances", JSON),
    Column("versions", JSON, nullable=False),
    Column("genre_label", String(32)),
    Column("labelled_at", DateTime(timezone=True)),
)

albums = Table(
    "album_palette", metadata,
    Column("folder", String(255), primary_key=True),
    Column("cover", String(512), nullable=False),
    Column("colors", JSON, nullable=False),
    Column("accent", String(7)),
    Column("updated_at", DateTime(timezone=True), nullable=False),
)

comments = Table(
    "comment_moderation", metadata,
    Column("comment_id", Integer, primary_key=True, autoincrement=False),
    Column("content_id", Integer, nullable=False),
    Column("text", Text, nullable=False),
    Column("scored_at", DateTime(timezone=True), nullable=False),
    Column("spam", Float),
    Column("toxicity", Float),
    Column("flagged", Boolean, nullable=False, index=True),
    Column("reasons", JSON, nullable=False),
    Column("versions", JSON, nullable=False),
    Column("label", String(16)),
    Column("labelled_at", DateTime(timezone=True)),
)

drift = Table(
    "drift_report", metadata,
    Column("id", Integer, primary_key=True, autoincrement=True),
    Column("created_at", DateTime(timezone=True), server_default=func.now(), nullable=False, index=True),
    Column("task", String(32), nullable=False),
    Column("kind", String(16), nullable=False),
    Column("name", String(128), nullable=False),
    Column("psi", Float, nullable=False),
    Column("status", String(8), nullable=False),
    Column("rows", Integer, nullable=False),
)

problems = Table(
    "input_problem", metadata,
    Column("id", Integer, primary_key=True, autoincrement=True),
    Column("created_at", DateTime(timezone=True), server_default=func.now(), nullable=False, index=True),
    Column("source", String(64), nullable=False),
    Column("problem", String(256), nullable=False),
)

job_runs = Table(
    "job_run", metadata,
    Column("id", Integer, primary_key=True, autoincrement=True),
    Column("job", String(32), nullable=False, index=True),
    Column("started_at", DateTime(timezone=True), nullable=False),
    Column("finished_at", DateTime(timezone=True)),
    Column("status", String(16), nullable=False),
    Column("detail", JSON),
)

MIGRATIONS = Path(__file__).with_name("migrations")


def create_database(url):
    address = make_url(url)
    if not address.drivername.startswith("postgresql"):
        return
    server = create_engine(address.set(database="postgres"), isolation_level="AUTOCOMMIT")
    with server.connect() as connection:
        found = connection.execute(text("SELECT 1 FROM pg_database WHERE datname = :name"),
                                   {"name": address.database}).first()
        if found is None:
            connection.execute(text(f'CREATE DATABASE "{address.database}"'))
    server.dispose()


def migrate(url):
    config = Config()
    config.set_main_option("script_location", str(MIGRATIONS))
    config.set_main_option("sqlalchemy.url", url.replace("%", "%%"))
    command.upgrade(config, "head")


def connect(url):
    create_database(url)
    migrate(url)
    return create_engine(url, pool_pre_ping=True)
