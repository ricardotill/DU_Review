# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

DU Review: a .NET 10 Blazor Server app that processes Slack submissions for the Destination Unknown scouting game. Groups post files in their own Slack channel with an assignment number; jury members review with emoji; the app downloads/sorts files, computes scores and shows a live dashboard.

User-facing text, the README and `docs/` are in **Dutch**; code, comments, logs and tests are in English. Keep that split. When behavior changes, update the matching section of `README.md` (and `docs/slack-sync.md` / `docs/handleiding.md` where relevant).

## Commands

```bash
# Run standalone with SQLite (http://localhost:5231); creates scouting_dev.db + scouting_storage/ with sample data
dotnet run --project src/ScoutingAutomation.Web --launch-profile http

# Run with PostgreSQL via .NET Aspire (needs Docker)
dotnet run --project src/ScoutingAutomation.AppHost

# Build / all tests
dotnet build ScoutingAutomation.slnx
dotnet test ScoutingAutomation.slnx

# One project, one class, one test
dotnet test tests/ScoutingAutomation.UnitTests
dotnet test tests/ScoutingAutomation.UnitTests --filter "FullyQualifiedName~SlackCatchUpServiceTests"
dotnet test tests/ScoutingAutomation.UnitTests --filter "FullyQualifiedName~SlackCatchUpServiceTests.SomeTestName"

# Game day: Release publish to ~/du-app, Slack tokens from user secrets, Mac kept awake
./run-gameday.sh
```

The Web build runs `npm ci` and copies Bootstrap / Bootstrap Icons into `wwwroot/lib` (MSBuild targets in `ScoutingAutomation.Web.csproj`), so Node/npm must be installed.

To test without Slack set `Scouting:Slack:SimulationMode=true` (e.g. `appsettings.Development.json`) and use the Slack-simulator page. Reset local state by deleting `scouting_dev.db*` and the group folders in `scouting_storage/`.

## Architecture

Projects (dependency direction: Web → Infrastructure → Domain/Contracts):

- **Contracts**: `ScoutingSettings` (the `Scouting` config section) and Slack DTOs.
- **Domain**: entities, enums, repository interfaces, and the rules (best status wins, points per assignment counted once per group).
- **Infrastructure**: logic is grouped by feature, namespace follows folder: `Workflow/` (submission workflow, review queue, change notifier), `Slack/` (API + history clients) with `Slack/Events` (dispatcher, queue, event log, retry, locks/gate, request verifier), `Slack/SocketMode` and `Slack/Sync` (catch-up, rebuild), `Announcements/`, `Settings/` (runtime settings, `GameTime`), `Access/`, `Storage/`, `Data/` (DbContext, `DatabaseInitializer`, CSV reader), `Repositories/`. All registration is in `Extensions/ServiceCollectionExtensions.cs`.
- **Web**: Blazor pages/components, `Program.cs` (DB selection, auth, `/api/slack/events` webhook, export endpoints), key-based auth in `Auth/`.
- **AppHost / ServiceDefaults**: Aspire host with PostgreSQL.

### Slack event flow

Events arrive either via the Events API webhook (`/api/slack/events`, signature verified by `SlackRequestVerifier`; disabled when Socket Mode is on) or via `SlackSocketModeService` (acknowledged immediately, queued per channel in `SlackEventQueue`). Both go through `SlackEventDispatcher`, which:

1. enters `SlackProcessingGate` (blocks all processing while a full rebuild runs),
2. enters `SlackChannelLocks` for the channel lane (serializes live events, catch-up and dashboard overrides per channel),
3. calls `SubmissionWorkflowService` (the core: message/edit/delete/reaction handling, file download, status changes, bot feedback),
4. logs to `SlackEventLog` (the `/events` page) and records failures as `FailedSlackEvent` rows for retry via `SlackEventRetryService`.

Any new code path that mutates submissions must respect the same gate + per-channel lock, or it will race with live events.

**Slack is the source of truth.** Recovery always reads from Slack, never the other way round: `SlackCatchUpService` (on startup and every Socket Mode reconnect; today's messages, adds missing data, never deletes, keeps overrides) and `SlackRebuildService` (full or per-group wipe-and-replay from history, backups to `scouting_storage/_backup/`). Dashboard overrides are temporary and are dropped by a rebuild. See `docs/slack-sync.md` for the full recovery ladder.

Two separate HTTP clients talk to Slack: `SlackClient` (Web API calls + file downloads) and `SlackHistoryClient` (history reads for catch-up/rebuild). Both deliberately opt out of the app-wide resilience handler and do their own retries — don't re-add it.

### Persistence

- EF Core `ScoutingDbContext`; PostgreSQL under Aspire, otherwise SQLite (`Program.cs` decides). There are **no EF migrations**: `DatabaseInitializer` calls `EnsureCreated` and then adds later columns/tables/indexes with idempotent raw SQL, for both PostgreSQL and SQLite. New schema changes must be added there for both providers.
- Files: `LocalFileStorageService` under `Scouting:StorageRoot`, per-group folder with `approved/`, `awesome/`, `rejected/` subfolders; names built by `SubmissionFileName`. Downloads go to a temp name then rename; a failed DB save moves the file back.
- `runtime-settings.json` (`RuntimeSettingsService`: in-app changes to reviewers, bot texts, event times, rules, bonus categories — these win over `appsettings.json`) and `access-keys.json` (`AccessKeyService`) are written atomically via `DurableFile`.
- Assignments come from `src/ScoutingAutomation.Web/opdrachtenpunten.csv` (code, points, optional title/description), read at startup. An assignment is identified by a **string code**: a number ("12") or letters for a special assignment ("A"). Normalise and sort with `Domain/Models/AssignmentCode` (numbers by value, then letters), never with plain string ordering.
- Hand-entered bonus points live per group and category key in `GroupBonusPoints` (history in `BonusPointsEntries`). The categories in use come from `RuntimeSettingsService.BonusCategories` (`RuntimeSettingsService.BonusCategories.cs`); points of a removed category are kept but count nowhere.
- Relative paths in config are resolved against the content root in `Program.cs`.

### UI

Interactive Server Blazor. Components live in `Components/`: `Pages/`, `Layout/`, `Panels/` (configuration/admin panels), `Scores/` (scoreboard, charts, dashboard), `Messaging/` (Slack composer/editor/links) and `Shared/` (small generic components, `LiveComponentBase`). Pages update live through `SubmissionChangeNotifier`. Access is one shared access key (role `KeyAuth.JuryRole`) plus an admin key, and a key per group (role `KeyAuth.GroupRole`, keyed by Slack channel in `AccessKeyService`); cookie auth in `Auth/KeyAuth.cs`. Admin-only actions check `KeyAuth.AdminRole` (`Auth/AdminActions.cs`), jury actions `Auth/StaffActions.cs`. The default policy (`Auth/GroupAccessHandler.cs`) is default-deny for groups: only pages/endpoints marked `[GroupAccess]` are open to them, and those must limit a group to its own data server-side (`KeyAuth.GroupId`). Whether groups see the standing (switch, periods, beamer, interim standing) lives in `RuntimeSettingsService.Standings.cs`; pages follow it via `Standings/StandingClock`. Culture is fixed to `nl-NL`, and times use `GameTime` (the configured `Scouting:TimeZone`, default Europe/Amsterdam) rather than server-local time.

## Tests

xUnit + FluentAssertions. Both test projects are organised by topic folder (`Workflow`, `Slack`, `Storage`, `Data`, `Model`, `Settings`, `Web`, `Components`, `Hosting`).

- **UnitTests**: NSubstitute, EF Core InMemory, bUnit for Razor components; shared helpers in `TestSupport.cs`.
- **IntegrationTests**: `TestApp` wraps `WebApplicationFactory<Program>` with a temporary SQLite DB and storage folder; `FakeSlackApi` stands in for Slack, `SlackEvents` builds event payloads, `LogCapture` asserts on logs. Use `FakeTimeProvider` (the app takes `TimeProvider` from DI) instead of real waits.
