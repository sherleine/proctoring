# Audio proctoring POC

The application lives in **[proctoring-platform/](proctoring-platform/README.md)**: a standalone audio-proctoring
proof of concept (React + TypeScript frontend, ASP.NET Core .NET 9 backend, Redis Streams, Python AI service,
SQLite, local audio storage). Start there.

| Path | Contents |
|---|---|
| `proctoring-platform/` | the application, its docs, contracts and tests |
| `proctoring-platform/docs/proposal/` | the original POC proposal |

Secrets (`.env` files, tokens) are never committed; each service has a `.env.example` template.

The first prototype (20 Nov 2026) was removed from the working tree; its source is still in the git history
(commit `38f9dcf`). To rerun the evaluation scripts in `proctoring-platform/ai-service/evaluation/`, download
LibriSpeech test-clean (https://www.openslr.org/12, `test-clean.tar.gz`, CC BY 4.0) into an `eval-data/` folder
here (ignored by git).
