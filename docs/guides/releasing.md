# Releasing a version

Each release publishes the server image on Docker Hub, tagged with its version (`0.5.0`), its minor version (`0.5`)
and `latest`, and a GitHub release with the Windows agent (`StealthDesk.Agent.exe`) and its SHA-256. A pre-release
such as `0.5.0-rc.1` gets only its own tag, and `latest` stays where it was.

## What the Release workflow does

The [Release workflow](https://github.com/fkauanGIT/StealthDesk/blob/main/.github/workflows/release.yml) runs on version tags:

1. It checks that the tag matches `<Version>` in `Directory.Build.props` and the heading of
   [`release-notes/current.md`](https://github.com/fkauanGIT/StealthDesk/blob/main/release-notes/current.md), such as `# v0.5.0 — Deployment`, which becomes the
   release's title.
2. It builds the image once and runs the deployment tests on it, behind an HTTPS proxy. Nothing is published unless
   they pass.
3. It creates the release as a draft with the agent taken out of that image, pushes that same image, then makes the
   release public.
4. It opens a pull request that keeps the notes as `release-notes/v0.5.0.md` and starts `current.md` for the next
   version.

## Cutting a release

Write its notes in `release-notes/current.md` as changes land, set the version, merge, then tag `main` with an
annotated tag and push it (`git tag -a v0.5.0 -m "v0.5.0" && git push origin v0.5.0`). If a step after the tests
fails, run the workflow again by hand with the tag.

Pushing to Docker Hub takes a repository secret, `DOCKERHUB_TOKEN`: an access token of the `franciscokauan` account
with read and write access.
