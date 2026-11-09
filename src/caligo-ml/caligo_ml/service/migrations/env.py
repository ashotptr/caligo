from alembic import context
from sqlalchemy import create_engine

from caligo_ml.service.store import metadata

engine = create_engine(context.config.get_main_option("sqlalchemy.url"))

with engine.connect() as connection:
    context.configure(connection=connection, target_metadata=metadata)
    with context.begin_transaction():
        context.run_migrations()

engine.dispose()
