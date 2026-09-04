# QuizManager

Clean V2 foundation for Factburst Quiz Manager.

## Goals

- Keep the desktop UI responsive at all times.
- Separate quiz/domain logic from WPF UI concerns.
- Isolate WPF rendering on a dedicated STA worker.
- Preserve compatibility with existing FactVaultManager user data without copying secrets into source control.
- Build quiz generation, rendering, audio, publishing, and automation as independently testable services.

## Planned solution

- `src/QuizManager.Desktop` — WPF shell and UI orchestration only.
- `src/QuizManager.Core` — domain models and application services with no WPF dependency.
- `src/QuizManager.Infrastructure` — SQLite, settings, secure local secrets, files, and external integrations.
- `src/QuizManager.Rendering` — isolated rendering pipeline and STA worker boundary.
- `tests/` — unit and integration tests.

The legacy `Vault-manager` repository remains untouched while V2 is developed and validated.
