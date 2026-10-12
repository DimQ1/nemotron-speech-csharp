# Local NuGet feed for the LiteRtLmSharp packages

`SpeechLib.LiteRT.Native` references **LiteRtLmSharp 1.4.0** (native LiteRT-LM v0.18.0), the fork's build
with the engine-usage improvements this repository uses. Upstream's own 1.3.0 on nuget.org is the same
native version but a different build with the same package number and without those improvements (they
are proposed upstream as [OrihuelaConde/LiteRtLmSharp#15](https://github.com/OrihuelaConde/LiteRtLmSharp/pull/15)),
which is why the fork publishes its build under 1.4.0. The engine comes from the fork
[DimQ1/LiteRtLmSharp](https://github.com/DimQ1/LiteRtLmSharp) and is published two ways:

- **GitHub Packages feed** — `https://nuget.pkg.github.com/DimQ1/index.json`. This is the feed the fork
  publishes to (`publish-github-packages.yml`); GitHub requires an authenticated read for it even though
  both repositories are public, so consuming it needs a token with the `read:packages` scope.
- **GitHub release assets** — the same `.nupkg` files are attached to the fork's
  [`v1.4.0` release](https://github.com/DimQ1/LiteRtLmSharp/releases/tag/v1.4.0), downloadable over plain
  HTTPS with no token and no GitHub CLI.

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
