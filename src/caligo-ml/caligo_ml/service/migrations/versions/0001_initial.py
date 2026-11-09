import sqlalchemy as sa
from alembic import op

revision = "0001"
down_revision = None
branch_labels = None
depends_on = None


def upgrade():
    op.create_table(
        "prediction_log",
        sa.Column("id", sa.Integer, primary_key=True, autoincrement=True),
        sa.Column("created_at", sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.Column("task", sa.String(32), nullable=False),
        sa.Column("model_version", sa.Integer, nullable=False),
        sa.Column("source", sa.String(16), nullable=False),
        sa.Column("subject", sa.String(512)),
        sa.Column("profile", sa.JSON, nullable=False),
        sa.Column("output", sa.JSON, nullable=False),
        sa.Column("latency_ms", sa.Float, nullable=False),
    )
    op.create_index("ix_prediction_log_created_at", "prediction_log", ["created_at"])
    op.create_index("ix_prediction_log_task", "prediction_log", ["task"])

    op.create_table(
        "song_analysis",
        sa.Column("path", sa.String(512), primary_key=True),
        sa.Column("title", sa.String(255)),
        sa.Column("artist", sa.String(255)),
        sa.Column("album", sa.String(255)),
        sa.Column("analyzed_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("file_size", sa.Integer, nullable=False),
        sa.Column("modified", sa.Float, nullable=False),
        sa.Column("problem", sa.String(32)),
        sa.Column("features", sa.JSON),
        sa.Column("genre", sa.String(32)),
        sa.Column("genres", sa.JSON),
        sa.Column("energy", sa.Float),
        sa.Column("valence", sa.Float),
        sa.Column("danceability", sa.Float),
        sa.Column("instrumental", sa.Float),
        sa.Column("sound_group", sa.Integer),
        sa.Column("versions", sa.JSON, nullable=False),
        sa.Column("genre_label", sa.String(32)),
        sa.Column("labelled_at", sa.DateTime(timezone=True)),
    )

    op.create_table(
        "album_palette",
        sa.Column("folder", sa.String(255), primary_key=True),
        sa.Column("cover", sa.String(512), nullable=False),
        sa.Column("colors", sa.JSON, nullable=False),
        sa.Column("accent", sa.String(7)),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
    )

    op.create_table(
        "comment_moderation",
        sa.Column("comment_id", sa.Integer, primary_key=True, autoincrement=False),
        sa.Column("content_id", sa.Integer, nullable=False),
        sa.Column("text", sa.Text, nullable=False),
        sa.Column("scored_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("spam", sa.Float),
        sa.Column("toxicity", sa.Float),
        sa.Column("flagged", sa.Boolean, nullable=False),
        sa.Column("reasons", sa.JSON, nullable=False),
        sa.Column("versions", sa.JSON, nullable=False),
        sa.Column("label", sa.String(16)),
        sa.Column("labelled_at", sa.DateTime(timezone=True)),
    )
    op.create_index("ix_comment_moderation_flagged", "comment_moderation", ["flagged"])

    op.create_table(
        "drift_report",
        sa.Column("id", sa.Integer, primary_key=True, autoincrement=True),
        sa.Column("created_at", sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.Column("task", sa.String(32), nullable=False),
        sa.Column("kind", sa.String(16), nullable=False),
        sa.Column("name", sa.String(128), nullable=False),
        sa.Column("psi", sa.Float, nullable=False),
        sa.Column("status", sa.String(8), nullable=False),
        sa.Column("rows", sa.Integer, nullable=False),
    )
    op.create_index("ix_drift_report_created_at", "drift_report", ["created_at"])

    op.create_table(
        "input_problem",
        sa.Column("id", sa.Integer, primary_key=True, autoincrement=True),
        sa.Column("created_at", sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.Column("source", sa.String(64), nullable=False),
        sa.Column("problem", sa.String(256), nullable=False),
    )
    op.create_index("ix_input_problem_created_at", "input_problem", ["created_at"])

    op.create_table(
        "job_run",
        sa.Column("id", sa.Integer, primary_key=True, autoincrement=True),
        sa.Column("job", sa.String(32), nullable=False),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("finished_at", sa.DateTime(timezone=True)),
        sa.Column("status", sa.String(16), nullable=False),
        sa.Column("detail", sa.JSON),
    )
    op.create_index("ix_job_run_job", "job_run", ["job"])


def downgrade():
    for table in ("job_run", "input_problem", "drift_report", "comment_moderation", "album_palette",
                  "song_analysis", "prediction_log"):
        op.drop_table(table)
