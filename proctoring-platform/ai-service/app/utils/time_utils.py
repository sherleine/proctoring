from datetime import datetime, timezone


def utc_now_iso() -> str:
    """ISO-8601 UTC with milliseconds, the timestamp format used by every contract message."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
