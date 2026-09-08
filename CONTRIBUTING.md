# Contributing to Jellyfin IMDb Ratings

Issues and pull requests are welcome!

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Submit a pull request

## Building

```bash
dotnet build --configuration Release
```

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
```

## Badges

The `coverage-N%` badge is maintained by hand from the `line-rate` that `dotnet test --collect:"XPlat Code Coverage"` writes to its Cobertura report, rounded to a whole percent.

The `dependencies-N outdated` badge in the README is maintained by hand: `N` is the row count from `dotnet list package --outdated` across both `Jellyfin.Plugin.ImdbRatings.csproj` and the test project, excluding the `Jellyfin.*` packages that are intentionally pinned for server backwards compatibility, so bump it whenever you change a `PackageReference`.

## Reporting Issues

- Search existing issues before opening a new one
- Include Jellyfin version, plugin version, and relevant logs

## Rules

- Keep branches, commits, and PRs focused. Do not mix unrelated local changes into the same PR.
- Use semantic names by default.

## Naming

- Branches: `fix/<scope>-<summary>`, `feat/<scope>-<summary>`, `refactor/<scope>-<summary>`
- Commits: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`
- PR titles: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`

## Pull Requests

- Keep changes focused and minimal
- Test against a running Jellyfin instance before submitting
- Describe what your PR changes and why

## LLM Disclosure

This project uses LLM-assisted development (Claude). Contributions generated with AI assistance are welcome, but please review and test all code before submitting.
