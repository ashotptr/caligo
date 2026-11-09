import sqlalchemy as sa
from alembic import op

revision = "0002"
down_revision = "0001"
branch_labels = None
depends_on = None


def upgrade():
    op.add_column("song_analysis", sa.Column("genre_features", sa.String(32)))
    op.add_column("song_analysis", sa.Column("genre_cnn", sa.String(32)))
    op.add_column("song_analysis", sa.Column("spectrum", sa.JSON))
    op.add_column("song_analysis", sa.Column("embedding", sa.JSON))


def downgrade():
    with op.batch_alter_table("song_analysis") as table:
        for name in ("embedding", "spectrum", "genre_cnn", "genre_features"):
            table.drop_column(name)
