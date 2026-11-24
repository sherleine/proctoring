# AI service image (CPU). Build from the ai-service folder:
#   docker build -t proctoring-ai-service .
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    HF_HOME=/models \
    AUDIO_STORE_PATH=/data/audio

# libsndfile for soundfile
RUN apt-get update && apt-get install -y --no-install-recommends libsndfile1 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
RUN pip install torch==2.15.0 torchaudio==2.11.1 --index-url https://download.pytorch.org/whl/cpu
COPY requirements.txt .
RUN pip install -r requirements.txt

COPY app ./app

RUN useradd --create-home --uid 10001 ai && mkdir -p /models && chown ai /models
USER ai

EXPOSE 8090
HEALTHCHECK --interval=15s --timeout=5s --start-period=120s --retries=5 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8090/health/ready', timeout=4).status == 200 else 1)"
CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8090"]
