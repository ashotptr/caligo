import os
from dataclasses import dataclass
from pathlib import Path


def optional_path(name):
    value = os.getenv(name)
    return Path(value) if value else None


@dataclass(frozen=True)
class Settings:
    root: Path
    database_url: str
    music_root: Path | None
    catalog_url: str | None
    schedule: bool

    @classmethod
    def from_env(cls):
        return cls(
            root=Path(os.getenv("ML_ROOT", "data")),
            database_url=os.getenv("ML_DATABASE_URL", "sqlite:///caligoml.db"),
            music_root=optional_path("ML_MUSIC_ROOT"),
            catalog_url=os.getenv("ML_CATALOG_DATABASE_URL") or None,
            schedule=os.getenv("ML_SCHEDULE", "on") == "on",
        )
