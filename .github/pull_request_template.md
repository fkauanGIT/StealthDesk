Closes #

## What changed

-

## How to test

The CI check builds the solution and runs every test project on this PR. To run them locally:

- [ ] `dotnet build StealthDesk.slnx --verbosity quiet`
- [ ] `dotnet run --project Tests/StealthDesk.Web.Server.Tests`

## Checklist

- [ ] PR title follows Conventional Commits (`feat: ...`, `fix: ...`, `chore: ...`)
- [ ] Every acceptance criterion in the issue is met
- [ ] README updated if run steps, endpoints, or project status changed
- [ ] No unrelated files (`git status` was checked before committing)
