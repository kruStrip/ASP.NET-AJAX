# AI agent instructions — МузЛото

- Source of truth: ../ТЗ/МузЛото — Единое техническое задание.docx and ../ТЗ/Требования к итоговому проекту.pdf, **amended by docs/DECISIONS.md** (it wins on conflict). Do not infer undocumented features or claim external integrations work without testing.
- Customers book through the site's own form on `/` (it replaced FormDesigner, decided 2026-10-29 evening; the FormDesigner webhook stays only for old links). No automatic customer messaging: staff contact customers themselves. Payment details live only in the database (admin page `/payment-settings`), never in the repository.
- Pending reservations do not consume capacity. Only manual payment confirmation does. Completion deletes Pending records but preserves paid history. Capacity confirmation must remain atomic under concurrency.
- Never commit customer personal data, real keys, production exports or .env files. The repository is public.
- Keep `docs/STATUS.md` up to date, distinguishing implemented from planned and verified from unverified features.
- Before treating a stage as complete, execute: `dotnet build src/MuzLoto.Web/MuzLoto.Web.csproj` and `dotnet test tests/MuzLoto.Tests/MuzLoto.Tests.csproj` (integration tests: `MUZLOTO_PG_TEST=1` and a separate test database, never `muzloto_dev`). Test concurrency against real PostgreSQL before operating live.
