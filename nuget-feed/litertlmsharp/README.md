# Local NuGet feed for the LiteRtLmSharp packages

The app consumes **LiteRtLmSharp 1.5.0-dimq1.2**, native **LiteRT-LM 0.19.1 / C API 1.0.0**, from
[DimQ1/LiteRtLmSharp](https://github.com/DimQ1/LiteRtLmSharp). It includes the maintainer's reviewed
engine fixes from PR 15. Suffixed versions keep different fork/upstream assemblies out of the same
NuGet cache slot. All managed/runtime references and the fetch script read **[Version.props](Version.props)**.

- **GitHub Packages feed** — `https://nuget.pkg.github.com/DimQ1/index.json`. This is the feed the fork
  publishes to (`publish-github-packages.yml`); GitHub requires an authenticated read for it even though
  both repositories are public, so consuming it needs a token with the `read:packages` scope.
- **GitHub release assets** — the same `.nupkg` files are attached to the fork's
  [`v1.5.0-dimq1.2` release](https://github.com/DimQ1/LiteRtLmSharp/releases/tag/v1.5.0-dimq1.2),
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

## Updating NuGet packages

1. Publish a new, unused suffixed version in the fork. Its
   [update guide](https://github.com/DimQ1/LiteRtLmSharp/blob/master/docs/fork-updates.md) covers the
   native release, ABI audit, tests and **Publish to GitHub Packages** workflow.
2. Change only `LiteRtLmSharpVersion` in [Version.props](Version.props). It updates the library's
   managed/runtime references, Uno's macOS runtime version and the fetch script together.
3. Run the fetch script above (`-Rid osx-arm64` for macOS), restore, then build/test the application.
   The native engine tests use `SPEECHLIB_GEMMA_MODEL` for model-backed checks.
4. For direct GitHub Packages consumption, enable the existing `github-dimq1` source in
   [nuget.config](../../nuget.config) and provide `GITHUB_PACKAGES_TOKEN` with `read:packages` outside git.
   Otherwise the release mirror needs no credentials.

Do not swap only the native DLL: C API 1.0.0 changes function signatures and stop-token ownership.
Switching back to an older fork API also requires reverting the corresponding application adapter.
