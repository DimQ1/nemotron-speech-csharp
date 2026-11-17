# Local NuGet feed for the LiteRtLmSharp packages

`SpeechLib.LiteRT.Native` references **LiteRtLmSharp 1.4.0-dimq1.1** (native LiteRT-LM v0.18.0), the fork's
build with the engine-usage improvements this repository uses. They are proposed upstream as
[OrihuelaConde/LiteRtLmSharp#15](https://github.com/OrihuelaConde/LiteRtLmSharp/pull/15); the maintainer
reworked them towards his own 1.4.0 (a different API — per-engine `AllowMultipleEngines`, `Greedy`/`TopK`
sent as `TopP`, a `Clone()` checkpoint instead of `Snapshot()`/`Resume()`), so this build carries a
prerelease suffix: a restore can never take upstream's 1.4.0 for the fork's build, or the reverse. The
engine comes from the fork
[DimQ1/LiteRtLmSharp](https://github.com/DimQ1/LiteRtLmSharp) and is published two ways:

- **GitHub Packages feed** — `https://nuget.pkg.github.com/DimQ1/index.json`. This is the feed the fork
  publishes to (`publish-github-packages.yml`); GitHub requires an authenticated read for it even though
  both repositories are public, so consuming it needs a token with the `read:packages` scope.
- **GitHub release assets** — the same `.nupkg` files are attached to the fork's
  [`v1.4.0-dimq1.1` release](https://github.com/DimQ1/LiteRtLmSharp/releases/tag/v1.4.0-dimq1.1),
  downloadable over plain HTTPS with no token and no GitHub CLI.

The root `nuget.config` maps `LiteRtLmSharp*` to **this directory** (the `litertlmsharp-local` folder
feed) so that a clone builds without any secret. Populate it once:

```powershell
pwsh tools/scripts/fetch-litertlmsharp-feed.ps1          # the packages this repo references
pwsh tools/scripts/fetch-litertlmsharp-feed.ps1 -All     # every package the release carries
```

The `.nupkg` files themselves are **not committed** (they are ~90 MB in total); this README keeps the
directory in git so the folder feed always exists. CI runs the same script before building a project
that consumes `SpeechLib.LiteRT.Native`.

Everything else this repository needs still comes from nuget.org, which the `nuget.config` mapping keeps
as the default source for every other package id.
