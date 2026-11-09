import sqlalchemy as sa
from alembic import op

revision = "0003"
down_revision = "0002"
branch_labels = None
depends_on = None


def upgrade():
    op.add_column("song_analysis", sa.Column("cnn_chances", sa.JSON))


def downgrade():
    with op.batch_alter_table("song_analysis") as table:
        table.drop_column("cnn_chances")
