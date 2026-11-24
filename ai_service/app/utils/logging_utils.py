import logging


def configure_logging(level: str) -> None:
    logging.basicConfig(
        level=level.upper(),
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
    )
    # Uvicorn's access log duplicates RequestContextMiddleware.
    logging.getLogger("uvicorn.access").setLevel(logging.WARNING)
