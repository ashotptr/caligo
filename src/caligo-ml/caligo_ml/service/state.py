from dataclasses import dataclass

from sqlalchemy import create_engine
from sqlalchemy.engine import Engine

from caligo_ml.service import store
from caligo_ml.service.champions import Champions
from caligo_ml.service.library import Library
from caligo_ml.service.settings import Settings


@dataclass
class State:
    settings: Settings
    engine: Engine
    catalog: Engine | None
    champions: Champions
    library: Library

    @classmethod
    def from_env(cls):
        settings = Settings.from_env()
        engine = store.connect(settings.database_url)
        catalog = create_engine(settings.catalog_url, pool_pre_ping=True) if settings.catalog_url else None
        with engine.connect() as connection:
            library = Library().refresh(connection)
        return cls(settings, engine, catalog, Champions(settings.root).refresh(), library)

    def close(self):
        self.engine.dispose()
        if self.catalog is not None:
            self.catalog.dispose()
