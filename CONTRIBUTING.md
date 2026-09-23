# Contributing

Every change starts from an issue, lives on its own branch, and reaches `main` through a pull request.

```
issue #12 ──▶ branch chore/12-rename-web-projects ──▶ commits ──▶ PR "chore: rename web projects to PascalCase"
                                                                     │  body: Closes #12
                                                                     ▼
                                                     Squash and merge into main
                                                     → issue #12 closes, remote branch is deleted
```

**One issue = one branch = one pull request.** Epics are the exception: they have no branch; each of their child issues does.

## 0. Create an issue

Use **New issue** on GitHub and pick a template — each one sets the title prefix and the `type:` label:

| Template | Title prefix | Use for |
|---|---|---|
| Feature | `[FEATURE]` | new behavior |
| Bug | `[BUG]` | something that worked, or should work, and doesn't |
| Chore | `[CHORE]` | build, dependencies, renames, project structure — no behavior change |
| Test | `[TEST]` | tests only |
| Docs | `[DOCS]` | documentation only |
| Epic | `[EPIC]` | a milestone-sized goal split into child issues |

Then set, in the right sidebar:

- **Labels**: one `area:` (`server`, `agent`, `contracts`, `web-client`, `desktop-client`, `infra`) and one `priority:` (`high` blocks other work, `medium` is planned for the current milestone, `low` is nice to have)
- **Milestone**: the version it belongs to (`v0.1 - Agent connectivity`, …)
- **Assignee**: whoever will do it

Size: an issue should fit in one branch you can finish in a few sessions. If the scope checklist grows past ~8 items, split it and add a "Blocked by" between the parts.
If it is a child of an epic, add it to the epic's **Issues** checklist.

## 1. Pick an issue

- Pick an issue whose dependencies ("Blocked by #N") are already closed.
- Assign it to yourself.

## 2. Create a branch

Always branch from an up-to-date `main`:

```powershell
git switch main
git pull
git switch -c chore/12-rename-web-projects
```

**Branch name:** `<type>/<issue-number>-<short-description>` — lowercase, words separated by `-`.

| Issue title prefix | Branch type | Example |
|---|---|---|
| `[FEATURE]` | `feat` | `feat/15-device-manager` |
| `[BUG]` | `fix` | `fix/31-heartbeat-null-tenant` |
| `[CHORE]` | `chore` | `chore/12-rename-web-projects` |
| `[TEST]` | `test` | `test/28-agent-hub-integration-tests` |
| `[DOCS]` | `docs` | `docs/29-contributing-guide` |

`[EPIC]` issues never get a branch; their child issues do.

## 3. Commit

Format: [Conventional Commits](https://www.conventionalcommits.org/).

```
<type>(<optional scope>): <what changed, imperative, lowercase, no period>

<optional body: why>

Refs #<issue>
```

- **type**: `feat`, `fix`, `chore`, `test`, `docs`, `refactor`
- **scope** (optional): `server`, `agent`, `contracts`, `web-client`, `desktop-client`, `infra`
- Imperative mood: "add", "rename", "fix" — not "added" / "adds".
- Subject line ≤ 72 characters.

Examples:

```
chore: rename web projects to PascalCase
feat(server): add device manager to persist agent heartbeats
test(server): cover signed heartbeat rejection
fix(agent): retry connection when server returns 503
```

Stage files by path. Never `git add .` / `git add -A`:

```powershell
git status --short
git add StealthDesk.slnx StealthDesk.Web.Server StealthDesk.Web.Client Tests
git commit -m "chore: rename web projects to PascalCase" -m "Refs #12"
```

Several small commits per branch are fine: they are squashed on merge.

## 4. Check before opening the PR

```powershell
dotnet build StealthDesk.slnx --verbosity quiet
dotnet run --project Tests/StealthDesk.Web.Server.Tests
```

Walk through the issue's **Acceptance criteria** and tick every box.

## 5. Open the pull request

```powershell
git push -u origin chore/12-rename-web-projects
gh pr create --title "chore: rename web projects to PascalCase"
```

`gh pr create` (and the GitHub web page) fills the body from `.github/pull_request_template.md`.

- **PR title** = the commit message you want on `main` (same Conventional Commits format).
- **PR body** must contain `Closes #<issue>` so the issue closes on merge.
- Fill "What changed" and "How to test".

## 6. Merge

Use **Squash and merge** (GitHub button, or `gh pr merge --squash`).

- `main` gets **one commit per issue**: `chore: rename web projects to PascalCase (#29)` — `#29` is the PR number, and the PR links to the issue.
- The remote branch is deleted automatically (repository setting "Automatically delete head branches").
- Issue #12 closes automatically because of `Closes #12`.

## 7. Clean up locally

```powershell
git switch main
git pull
git fetch --prune              # forget remote branches that were deleted
git branch -D chore/12-rename-web-projects
```

`-D` (capital) is needed after a squash merge: git can't tell the branch was merged, because `main` has a new squashed commit rather than your original commits.

## Working on two issues at once

Uncommitted files follow you when you switch branches. Before switching, either commit on the current branch or stash by path:

```powershell
git stash push -u -m "wip #14" -- <paths>
git stash list
git stash pop                  # when you come back
```

If `main` moved while your branch was open, bring it in before the PR:

```powershell
git switch feat/14-agent-hub-contract
git merge main
```
