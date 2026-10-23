Closes #

## What changed

-

## How to test

The CI check builds the solution and runs every test project on this PR. To run them locally:

- [ ] `dotnet build StealthDesk.slnx --verbosity quiet`
- [ ] `dotnet run --project tests/StealthDesk.Server.Tests`

## Checklist

- [ ] PR title follows Conventional Commits (`feat: ...`, `fix: ...`, `chore: ...`)
- [ ] Every acceptance criterion in the issue is met
- [ ] Documentation in `docs/` (or the README) updated if run steps, settings, endpoints, or project status changed
- [ ] No unrelated files (`git status` was checked before committing)
