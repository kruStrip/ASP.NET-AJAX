# Repo notes

- Coursework monorepo (ASP.NET Core, .NET 8, mostly Russian docs). Root dirs (`TaskBoard`, `DashboardAdmin`, `TaskFlowApi`, `LiveMonitor`, `SecureTodo`) are older practice projects; `4_курс/Разработка_веб_приложений_ASP.NET_AJAX/` holds the course-structure copies (made by `setup_course_repo.sh`; originals are kept). Do not assume edits to one copy propagate to the other.
- The only active, CI-covered project is the final project «МузЛото»: `4_курс/Разработка_веб_приложений_ASP.NET_AJAX/Итоговый_проект/проект/` (call it `$P`). Run all dotnet/npm/script commands from `$P`.
- **Read `$P/AGENTS.md` first** (business rules, privacy, payment/messaging constraints). Spec amendments in `$P/docs/DECISIONS.md` override the ТЗ docx/pdf; keep `$P/docs/STATUS.md` current. The repo is public: no secrets, `.env`, or customer data.
- Note: the `Итоговый_проект/README.md` and course-level `README.md` disagree on status; trust `$P/docs/STATUS.md` and the code.

## MuzLoto commands (mirror `.github/workflows/muzloto.yml`)

- Build: `dotnet build src/MuzLoto.Web/MuzLoto.Web.csproj -c Release`
- Unit tests: `dotnet test tests/MuzLoto.Tests/MuzLoto.Tests.csproj --filter 'Category!=Integration'`
- Integration tests need real PostgreSQL and `MUZLOTO_PG_TEST=1`, `--filter 'Category=Integration'`; use a separate DB, never `muzloto_dev`.
- Dev server: `bash scripts/dev.sh` (http://localhost:5080, Development env; sources `scripts/common.sh`). Config comes from env vars like `ConnectionStrings__Default`, `Jwt__Key`/`Issuer`/`Audience`, `BootstrapAdmin__Email`/`Password` (see `.env.example`).
- EF migrations use the local tool (`dotnet tool restore`, then `dotnet tool run dotnet-ef ... --project src/MuzLoto.Web`). CI generates `InitialCreate`/`RefundWorkflow` migrations only if missing from `src/MuzLoto.Web/Data/Migrations`.
- Browser SignalR client is installed via `bash scripts/install-client.sh` (npm `@microsoft/signalr` into wwwroot/lib/signalr); CI smoke-checks `/lib/signalr/signalr.min.js`, `/health`, and that `/api/v2/events` returns 401 unauthenticated.

## CI/CD

- Any push to `main` touching `$P/**` auto-deploys to production (build -> tests -> Docker image to ghcr -> SSH deploy -> HTTPS smoke test). Treat merging/pushing to `main` as a production release.
