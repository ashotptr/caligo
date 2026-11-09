from prefect import flow, serve

from caligo_ml.service import jobs
from caligo_ml.service.state import State


def job(name):
    state = State.from_env()
    try:
        return jobs.run(state, name)
    finally:
        state.close()


@flow(name="score-songs", retries=1, retry_delay_seconds=300)
def score_songs():
    return job("score_songs")


@flow(name="album-palettes", retries=1, retry_delay_seconds=300)
def album_palettes():
    return job("album_palettes")


@flow(name="moderate-backlog", retries=2, retry_delay_seconds=60)
def moderate_backlog():
    return job("moderate_backlog")


@flow(name="drift-report")
def drift_report():
    return job("drift_report")


if __name__ == "__main__":
    serve(
        score_songs.to_deployment(name="hourly", cron="0 * * * *"),
        album_palettes.to_deployment(name="every-six-hours", cron="15 */6 * * *"),
        moderate_backlog.to_deployment(name="every-ten-minutes", interval=600),
        drift_report.to_deployment(name="daily", cron="30 5 * * *"),
    )
