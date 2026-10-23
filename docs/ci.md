# CI/CD

Status 2026-10-23. Repository `DimQ1/nemotron-speech-csharp`, default branch `master`. Everything in this document describes the
files in `.github/`; **none of these workflows has run on GitHub in its current form** (they were linted and their embedded scripts parsed
locally, see [What could not be verified](#what-could-not-be-verified-and-how-to-try-it-safely)). Decisions that the maintainer has not
answered yet carry the default that was applied and the words *default, maintainer to confirm* (see [Open decisions](#open-decisions)).

## At a glance

| File | Name in the UI | Runs on | What it does |
|---|---|---|---|
| `.github/workflows/ci.yml` | `CI` | pull request, push to `master` (not for docs-only changes), manual | Build + model-free unit tests of 9 test projects on `windows-latest` and `ubuntu-latest`; no-GenAI guard on Windows; `actionlint` over the workflows |
| `.github/workflows/build-voicetype-uno.yml` | `VoiceType.Uno CI` | pull request and push to `master` that touch the app, the libraries, the feed script, `nuget.config` or the workflows; manual | Platform packages through `uno-publish.yml`: Windows, Linux, Android (not on pull requests) |
| `.github/workflows/uno-publish.yml` | `VoiceType.Uno publish (reusable)` | called by the three workflows around it | The one definition of the Windows / Linux / Android / iOS jobs |
| `.github/workflows/build-voicetype-uno-ios.yml` | `VoiceType.Uno iOS` | pull request and push to `master` with the same path filter as the Uno CI (without the Linux-only script), manual | Unsigned iOS builds on a Mac runner, Simulator and device, through the `ios` job of `uno-publish.yml`. Separate from the Uno CI so that a slow or red iOS build never blocks the other platforms |
| `.github/workflows/release-voicetype-uno.yml` | `VoiceType.Uno Tagged Release` | tag `uno-v*` | Version from the tag, all platform packages (Windows, Linux, Android, and the two unsigned iOS builds), GitHub release |
| `.github/workflows/release-voicetype-uno-android.yml` | `VoiceType.Uno Android Release` | tag `uno-android-v*`, manual | Android package only; only a tag creates a release |
| `.github/workflows/voicetype-uno-linux-smoke.yml` | `VoiceType.Uno Linux Smoke` | pull request and push to `master` with nearly the same path filter as the Uno CI, manual | Publish the Linux head and start it under Xvfb without a model for 10 s |
| `.github/workflows/build-voicetype-winui-msix.yml` | `VoiceType.WinUI MSIX` | manual only | EXPERIMENTAL: unsigned MSIX of the WinUI app (Store / WACK candidate) |
| `.github/workflows/convert-parakeet-tdt.yml` | `Convert Parakeet TDT to ONNX` | manual only | Python export of the Parakeet TDT model, optional upload to Hugging Face. Unchanged, never run, see [Known issues](#known-issues-and-backlog) |
| `.github/actions/setup-build/action.yml` | (composite action) | used by every .NET job | .NET 10 SDK with the NuGet cache, then `tools/scripts/fetch-litertlmsharp-feed.ps1` |

Removed in this change: `build-voicetype-release.yml` (`VoiceType CI`) and `release-voicetype-tag.yml` (`VoiceType Tagged Release`). Both
built `VoiceType/VoiceType.csproj` and `VoiceType.Tests`, the WPF app that commit `be92e25` deleted; the first had been red on every
push since 2026-08-04, the second (tag `v*`) was dormant and would have failed.

### Things every .NET job has in common

* `actions/checkout@v7`, then the composite action `setup-build`: `actions/setup-dotnet@v5` (`10.0.x`, NuGet cache keyed on the csproj
  files and `nuget.config`) and `pwsh tools/scripts/fetch-litertlmsharp-feed.ps1`. The feed step must run **before any restore**:
  `nuget.config` maps `LiteRtLmSharp*` to the folder feed `nuget-feed/litertlmsharp`, whose `.nupkg` files are not in git (4 packages,
  about 91 MB, plain HTTPS download from the `v1.4.0` release of the fork `DimQ1/LiteRtLmSharp`, no token). The default `-Version` of the
  script and the `Version` in `SpeechLib.LiteRT.Native.csproj` must move together.
* `permissions: contents: read` at the top of every workflow, `contents: write` only on the two `release` jobs; a `concurrency` group
  per workflow and ref (cancel in progress for pull requests only, never for `master` or tags); a `timeout-minutes` on every job.
* The dead `-p:GpuArch=CPU` is gone from the workflows. It is still passed by a few scripts that cannot be changed from `.github`
  (see backlog).

## What runs when

| Event | `CI` | `VoiceType.Uno CI` | `Linux Smoke` | Releases |
|---|---|---|---|---|
| pull request | yes, unless only `docs/**` or `*.md` changed | Windows + Linux packages (path filter) | yes (path filter) | no |
| push to `master` | same | Windows + Linux + **Android** (path filter) | yes (path filter) | no |
| tag `uno-vX.Y.Z` | no | no | no | `VoiceType.Uno Tagged Release` |
| tag `uno-android-vX.Y.Z` | no | no | no | `VoiceType.Uno Android Release` |
| manual | yes | yes (input `android`) | yes | Android release workflow: build only |

Path filter of the Uno CI and the smoke test (the smoke test has no entry for the two `tools/scripts` files): the workflow files, `.github/actions/**`, `apps/VoiceType.Uno/**`, `libraries/SpeechLib/**`,
`libraries/VoiceType.Hotkeys/**`, `nuget.config`, `global.json` (does not exist yet), and for the Uno CI also `tools/scripts/fetch-litertlmsharp-feed.ps1`
and `tools/scripts/x11-window-fixer.py` (it goes into the `.deb`). The old trigger on the branch `feature/uno-platform-linux` was dropped:
a pull request covers any other branch.

## Tests in CI

`ci.yml` runs `dotnet test -c Release --filter "FullyQualifiedName!~.E2E_&FullyQualifiedName!~.Real_"` for each of these projects,
on both operating systems. The filter removes every model-backed class (`E2E_*`: Nemotron, Parakeet TDT, Qwen3, diarization, Uno;
`Real_*`: Gemma), so a result does not depend on what a runner happens to contain.

| Project | Tests selected by the filter |
|---|---|
| `SpeechLib.Tests` | 256 |
| `SpeechLib.Nemotron.Tests` | about 378 (`Unit_*`; 264 when counted on 2026-10-22, the suite is still growing) |
| `SpeechLib.ParakeetTdt.Tests` | 39 |
| `SpeechLib.Diarization.Tests` | 73 |
| `SpeechLib.ModelDownload.Tests` | 23 |
| `SpeechLib.AudioStorage.Tests` | 43 |
| `SpeechLib.Agents.Tests` | 43 |
| `SpeechLib.LiteRT.Native.Tests` | 50 |
| `VoiceType.Uno.Tests` (not in `NemotronSpeech.slnx`, so it needs its own call) | about 1,090 (1,085 on 2026-10-22) |
| **Total** | **about 1,990** (1,876 when counted on 2026-10-22; a few Windows-only tests skip themselves on Linux) |

The 2026-10-22 count came from `dotnet test --list-tests` with the same filter on the Release build of the migration worktree; the
Nemotron and Uno rows were raised from the later gate runs of the migration (`Unit_` 378; Uno 1,094 tests in total, of which the few `E2E_` ones are excluded by the filter). Treat the total as
"about 2,000"; the real number is in the `.trx` files that every run uploads (`test-results-<os>`, 7 days). The Linux numbers are a forecast:
none of the nine projects has ever run on Linux.

Windows additionally builds the whole `NemotronSpeech.slnx` (CLI, WinUI head, libraries, tools), builds the Uno desktop head for `win-x64`
and runs `tools/scripts/check-no-genai.ps1`. Linux builds the CLI, `VoiceType.Hotkeys`, `WerEval` and `DiarizationEval` explicitly (the
WinUI head is Windows-only). Both start with a check that the tracked Nemotron fixtures exist.

## Run the same checks locally

PowerShell (Windows); the same commands work in a POSIX shell with the filter in single quotes.

```powershell
# once, and after a change of the LiteRtLmSharp version: the folder feed (must precede every restore)
pwsh tools/scripts/fetch-litertlmsharp-feed.ps1

# build
dotnet build NemotronSpeech.slnx -c Release
dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -c Release -f net10.0-desktop -r win-x64

# the model-free tests, exactly as CI selects them
$filter = 'FullyQualifiedName!~.E2E_&FullyQualifiedName!~.Real_'
Get-ChildItem libraries/SpeechLib/tests, apps/VoiceType.Uno/tests -Recurse -Filter *.Tests.csproj |
  ForEach-Object { dotnet test $_.FullName -c Release --filter $filter }

# no ONNX Runtime GenAI in the restore state or in the output of the CLI, WinUI and Uno desktop heads
pwsh tools/scripts/check-no-genai.ps1
```

`check-no-genai.ps1` needs PowerShell 7 (`#Requires -Version 7.0`) and **builds and restores nothing**: it reads `obj/project.assets.json`
and `bin/Release` of the CLI, the WinUI head and the Uno desktop head, so the two build commands above must have run first (with
`-r win-x64`, otherwise the Uno restore also pulls the `net10.0-android` target, which needs the Android workload). It exits 1 when
GenAI is found, 2 when no head output exists. It checks Windows-style heads: on Linux the WinUI head has no restore state, so the
CI job runs it on Windows only.

Other local facts that matter for CI parity:

* `dotnet test NemotronSpeech.slnx` (no filter) also runs the model-backed `E2E_*` classes whenever the models are found (in the checkout
  or in the main checkout of a worktree); they take minutes to an hour. Use the filter above for a CI-like run.
* Model-backed Nemotron runs: see `AGENTS.md` (env vars `NEMOTRON_MODEL_DIR_*`, `NEMOTRON_FULL_GOLDEN=1`).
* There is no root `global.json` yet: at the repository root `dotnet` uses the newest installed SDK (a .NET 11 preview on the maintainer's
  laptop), CI uses the newest `10.0.x` (open decision D4). `apps/VoiceType.Uno/src/global.json` only pins the Uno SDK (`Uno.Sdk 6.7.30`).
* Lint the workflows: run the official `actionlint` binary from the repository root, or
  `docker run --rm -v "$PWD:/repo" --workdir /repo rhysd/actionlint:1.7.12`. The Docker image also runs `shellcheck`; the local Windows
  binary does not (no `shellcheck` installed), so a style finding in an embedded bash block can first appear in CI.

## Release

1. Make sure `CI` and `VoiceType.Uno CI` are green on `master`.
2. Tag and push. The tag must be exactly `uno-vMAJOR.MINOR.PATCH` (the first job rejects anything else):
   ```powershell
   git tag uno-v1.9.0
   git push origin uno-v1.9.0
   ```
   `VoiceType.Uno Tagged Release` builds Windows, Linux and Android and publishes the GitHub release (not a draft) with generated notes.
   Assets: `VoiceType.Uno-win-x64.zip`, `VoiceType.Uno-linux-x64.tar.gz`, `voicetype-uno_<version>_amd64.deb`, the `README.md` and
   `SHA256SUMS` of the `.deb` folder, `*-Signed.aab`, `*-Signed.apk`. The release step fails when one of them is missing.
3. Android-only hotfix: tag `uno-android-vMAJOR.MINOR.PATCH`; the workflow can also be started by hand, which builds and keeps the
   packages as workflow artifacts without a release.
4. Version numbers come from the tag: `ApplicationDisplayVersion` = `X.Y.Z` (the app reads it back for the History `app.version`, the
   `.deb` takes it too) and the Android `versionCode` = `X*10000 + Y*100 + Z` (`1.8.1` becomes `10801`). CI builds use
   `1.0.<run number>` and the run number as code.
5. After the run: read the job summary of the Android job (signing state and certificate fingerprint, see below) and the Linux job
   (the `.deb` is installed with `apt` on the Ubuntu 24.04 runner, which checks that the `Depends` line resolves; the runner image already carries many
   libraries, so this is not a clean-room install test).

**Version rule for Android.** Both tag series produce the same app, and Android refuses an update whose `versionCode` is not higher.
An Android-only tag must therefore be numerically above the last `uno-v` tag (after `uno-v1.8.1` use `uno-android-v1.8.2`, not
`uno-android-v1.2.0`; the earlier `uno-android-v1.1.x` numbers are below `uno-v1.8.x`). This only starts to matter once releases are
signed with a stable key.

## Android signing

Today (and with the defaults of this change) the Android job has no keystore: .NET for Android then signs with a debug key that is
generated on the runner, so **every build has a different certificate and cannot update an installed copy** (the user must uninstall
first, which also deletes the downloaded models; Google Play rejects such a package). This was verified on three published releases
(`uno-android-v1.1.0`, `uno-android-v1.1.1`, `uno-v1.7.0`: three different signer certificates). The job says so as an annotation and in
the job summary.

To sign with a stable key (instructions only, nothing was created):

```powershell
keytool -genkeypair -v -keystore voicetype-release.keystore -alias voicetype -keyalg RSA -keysize 4096 -validity 10000
[Convert]::ToBase64String([IO.File]::ReadAllBytes('voicetype-release.keystore')) | Set-Clipboard   # paste into the secret
```

Store the secrets in Settings, Secrets and variables, Actions (`gh secret set <NAME>` works too), **back the keystore up offline** (a lost
key ends the update path for every installed copy), then set `require-android-signing: true` in `release-voicetype-uno.yml` and
`release-voicetype-uno-android.yml` so that a release without the secrets fails instead of shipping a debug-signed package. The first
release with the new key cannot update installs of the old throw-away keys (one-time uninstall). Passwords reach MSBuild through the
documented `file:` prefix (the `env:` prefix is not supported for AAB).

## Secrets

Names only. No secret is created or required by the defaults; a missing optional secret degrades as described.

| Secret | Used by | If missing |
|---|---|---|
| `ANDROID_KEYSTORE_BASE64` | `uno-publish.yml`, Android job | debug-signed package, warning (or failure with `require-android-signing: true`) |
| `ANDROID_KEY_ALIAS` | same | same |
| `ANDROID_KEYSTORE_PASSWORD` | same | same |
| `ANDROID_KEY_PASSWORD` | same, optional | the keystore password is used |
| `HF_TOKEN` | `convert-parakeet-tdt.yml` | nothing is uploaded (see Known issues) |
| `IOS_CERTIFICATE_P12_BASE64`, `IOS_CERTIFICATE_PASSWORD`, `IOS_PROVISIONING_PROFILE_BASE64`, `APPSTORE_API_KEY_ID`, `APPSTORE_API_ISSUER_ID`, `APPSTORE_API_PRIVATE_KEY` | declared by `uno-publish.yml` for the commented-out signed-IPA steps of the `ios` job | not read today: the iOS builds are unsigned |
| `WINUI_SIGNING_PFX_BASE64`, `WINUI_SIGNING_PFX_PASSWORD` | only named in a comment of `build-voicetype-winui-msix.yml` | not read |

Secrets are not available to pull requests from forks; every check of `CI` works without them.

## What CI does not cover

* **Model-backed tests**: about 730 `E2E_*` / `Real_*` tests (Nemotron alone about 726, plus Parakeet, Qwen3, diarization, Gemma and the
  Uno recognition tests) need models and audio that are not in git (`models/`, `Test-Audio/`). They run on the maintainer's machine; the
  complete GenAI oracle (`NEMOTRON_FULL_GOLDEN=1`, about an hour) too. A self-hosted runner holding the models is the way to automate
  them (decision D10).
* **The real WebGPU execution provider** (plugin or driver faults end the test process; it is tested only in a child process on a machine
  with a discrete GPU), live audio capture (microphone, loopback), typing into other applications (`SendInput`, XTest), UIA layout checks.
* **Android on a device or emulator** (the job only builds the AAB and APK), **iOS on a device or in the Simulator** (the job only compiles and links; the microphone code has never run), **macOS** (the Skia head lists
  `UseMacOS()`, nothing builds or tests it), **Windows ARM64 and Linux arm64**.
* **Windows signing and the Store**: the Uno zip and the MSIX are unsigned; `build-voicetype-winui-msix.yml` is manual and never run.
* **Bit identity of the mel front end on Linux**: the strict test is Windows x64 only, a tolerance test runs everywhere
  (`docs/research/nemotron-pure-ort-followups.md`, P1).

## iOS

`VoiceType.Uno.csproj` has an **opt-in** `net10.0-ios` target (Uno 6, Skia on UIKit). It is opt-in because the iOS target needs the `ios`
workload and an Xcode, which only a Mac has: `-p:UnoIosOnly=true` makes `net10.0-ios` the only target (build with `-f net10.0-ios`, never
with `-p:TargetFrameworks`, which leaks into every referenced library). The default target set of the other platforms is unchanged.

**Status (2026-10-23):** the first green run built both variants on `macos-26` (Simulator about 35 min, device/AOT about 40 min; artifacts
82 MB and 61 MB). The first attempts failed on the unpinned workload (27.x needs Xcode 27.1), on the Xcode version (26.6, not 26.5) and on
`NAudio.Lame.dll` in the AOT step (see below). The tagged release now attaches both unsigned builds.

What is in the repository:

* `Platforms/iOS/Main.iOS.cs` (the `UseAppleUIKit()` host), `Info.plist` (microphone usage text, `audio` background mode, no non-exempt
  encryption), an empty `Entitlements.plist`. Minimum iOS 15.
* `Services/Audio/IosAudioSourceFactory.cs`: microphone capture with `AVAudioEngine` (permission request, input tap, conversion to the
  recognizer rate and mono). Mic only: iOS gives an app no system-audio capture. **It compiles against the real iOS API and has never run.**
* `uno-publish.yml`, job `ios` (matrix, `macos-26`): installs the `ios` workload **pinned** to 26.5.10318 and selects **Xcode 26.6** (the
  pack says "requires Xcode 26.6"; the first two parts of the workload version are not the Xcode version). The unpinned workload is 27.x, whose
  Xcode is not on the image. Two builds, both with `EnableCodeSigning=false` and the version of the run (`1.0.<run number>`, build number
  `<run number>`): `iossimulator-arm64` (artifact `VoiceType.Uno-ios-simulator`, runs in the Xcode Simulator of an Apple-silicon Mac:
  `xcrun simctl install booted VoiceType.Uno.app` after unzipping) and `ios-arm64` (artifact `VoiceType.Uno-ios-device-unsigned`: the
  Release / linker / AOT pass for a real iPhone; **it cannot be installed**, a device needs a signature). When the build fails the first
  distinct errors are repeated as annotations (hosted logs need a login).
* The iOS target leaves the Windows audio stack out: no reference to `SpeechLib.Audio.NAudio3` (its `NAudio.Lame.dll` fails the AOT
  compiler), no `WasapiPcmOutput.cs`, and `AudioPlayerFactory` returns the null player (no playback output on iOS yet).
* `build-voicetype-uno-ios.yml` runs it (pull request, `master`, manual). It is its own workflow so that the 30+ minute Mac build and
  any iOS problem stay away from the Windows / Linux / Android packages.

What does not exist on iOS and is switched off or null: Gemma translation and chat (LiteRtLmSharp has no iOS native), typing into other
applications, global hotkeys, loopback and mix capture, tray indicator, playback of recordings (`NullAudioPlayer`: the Skia head has no iOS
output yet). `Microsoft.ML.OnnxRuntime` 1.30.0 ships the iOS xcframework (linked statically, CoreML as a weak framework) through the package's
own targets; no Mac Catalyst runtime.

What is missing for the next stages (stop after any of them if the product decision D5 changes):

3. **Minimal functional app, 2-3 weeks:** run the Simulator build, then a device; check the recognizer on the device (ORT iOS, memory limits of
   the 0.6B models, INT4 only), the AVAudioEngine capture and its interruptions (calls, route changes), model storage in the app sandbox,
   background recording, an iOS playback output, the layout on a phone. Needs an iPhone and a Mac.
4. **Store pipeline, about 1 week plus review:** signed IPA and TestFlight upload in CI. The commented steps at the end of the `ios` job
   show the shape. Needs the Apple Developer Program, an Apple Distribution certificate, a provisioning profile, an App Store Connect API
   key (the six secrets in the table above) and a final bundle id: `ApplicationId` is still the template value
   `com.companyname.voicetype-uno`; decide it together with D3 and D6 before the first upload.

## Open decisions

The maintainer has not answered these. The default column is what is in the repository now; every row is *default, maintainer to confirm*.

| # | Question | Default applied |
|---|---|---|
| D1 | Delete the two stale WPF workflows? | **Deleted** (`build-voicetype-release.yml`, `release-voicetype-tag.yml`). `convert-parakeet-tdt.yml` left as is (see Known issues) |
| D2 | Android release signing | **No keystore created.** The workflow reads the four secrets when present; otherwise debug-signed with a loud warning; `require-android-signing` stays `false` |
| D3 | Android identity and versions | `ApplicationId` (`com.companyname.voicetype-uno`, a template placeholder) **unchanged**; display version and `versionCode` come from the tag (rule above) |
| D4 | Pin the SDK with a root `global.json`? | **None yet** (to be added after the migration gate); `global.json` is already in the Uno path filters. Workload set not pinned either: `dotnet workload install android` takes the latest manifest; pin it with `--version <workload set>` after the first green run prints it |
| D5 | iOS: do it at all, how far? | **Stages 1 and 2 done** (head compiles, Simulator and device builds, both unsigned); stages 3 and 4 not started, see the iOS section |
| D6 | Store naming (final bundle / package id, display name) | **Not decided**, nothing changed; decide together with D3 before the first Play or App Store upload |
| D7 | Linux packaging scope | **Unchanged**: linux-x64 `.deb` and `tar.gz`; no arm64, AppImage, Flatpak or rpm |
| D8 | Windows signing | **None**: the Uno zip and the MSIX are unsigned (SmartScreen warns) |
| D9 | Required checks | **Nothing marked required** (branch protection is a repository setting). Recommended once the first runs are green: `CI / Build + unit tests (windows-latest)`, `CI / Build + unit tests (ubuntu-latest)`, `CI / Lint workflows (actionlint)`, `VoiceType.Uno Linux Smoke / Linux build + startup smoke`. `ci.yml` skips docs-only changes (`paths-ignore`), which leaves a required check pending on such pull requests: remove the `paths-ignore` of `ci.yml` before requiring it. The GenAI guard is blocking inside the Windows job (no `continue-on-error`) |
| D10 | Test coverage in CI | **Model-free tests only**: about 2,000 tests in 9 projects (table above); model-backed E2E stay local until a self-hosted runner exists |
| D11 | Nemotron `Data/` fixtures in git | **Tracked.** Verified: `git check-ignore` reports no file of `libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/Data` as ignored (the `.gitignore` rule `data/` is neutralised by an exception and does not match `Data` on a case-sensitive file system). They are 330 files, 2.7 MB, **still untracked in the migration worktree**: `git add` them with the migration (`git ls-files libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/Data \| wc -l` must print 330, today's count). `ci.yml` fails fast with one message when they are missing |
| D12 | Clean up the stale WPF documentation | **Done** in `AGENTS.md` and `README.md` (WPF app, `VoiceType.Tests`, WPF-only conventions and pitfalls); `.github/instructions/voicetype-wpf.instructions.md` (its `applyTo` matched no path) deleted; research and plan documents are history and untouched |

## Known issues and backlog

* **`convert-parakeet-tdt.yml`** (manual, never run): the upload step has `env: HF_TOKEN` and `if: env.HF_TOKEN != ''` on the same step. The
  documentation does not say whether a step's own `env` is visible to its `if`, and the common experience is that it is not, so the upload would
  never run. Left untouched because that is not certain. Safe fix when the workflow is first used: a step before it that writes
  `present=${{ secrets.HF_TOKEN != '' }}` into `GITHUB_OUTPUT`, and `if: steps.<id>.outputs.present == 'true'` on the upload step.
* **Scripts and files that cannot be changed from `.github`** and still pass the dead `-p:GpuArch=CPU` (harmless, ignored): `build-deb.sh:36`,
  `build-deb.ps1:48`, WinUI `build-dev.ps1:60` and `build-store-release.ps1:46,60`, `tools/scripts/run-voicetype-uno-linux.ps1:37`, the comment in
  `NemotronSpeech.slnx:12`.
* `apps/VoiceType.WinUI/.../VoiceType.WinUI.csproj` hard-codes `_Net10RuntimeDir` to `...\Microsoft.NETCore.App\10.0.9`; on a runner with another
  patch it only warns (`netstandard.dll` not found). The WinUI head is built by `CI` (Windows leg) and the manual MSIX workflow, never on a runner before.
* A development signing key, `VoiceTypeDev.pfx`, is tracked in the WinUI folder and referenced by no script; consider removing it from git.
* Backlog, none of it drafted on purpose (each changes repository behaviour): a root `global.json` and a workload-set pin (D4); actions pinned by
  commit SHA and a Dependabot entry for actions (the majors in use are `checkout@v7`, `setup-dotnet@v5`, `upload-artifact@v4`,
  `download-artifact@v4`, `action-gh-release@v2`, all working; newer majors exist and should be tried on a branch); `SHA256SUMS` for every release
  asset (today only the `.deb` folder has one); an `.slnf` with the cross-platform projects so that the Linux leg needs one line;
  `--no-restore` and a `-PublishDir` parameter for `check-no-genai.ps1` so that it can check the folder that ships; Android on `ubuntu-24.04` with
  a single `AndroidPackageFormat=aab;apk` publish (roughly halves the job, unverified); win-arm64 and linux-arm64 packages.
* Runner pins: `ubuntu-24.04` for the Linux package and smoke job (the `.deb` `Depends` names the 24.04 libraries), `windows-latest` for Windows and
  Android (the known-green image of earlier releases), `macos-26` for the iOS job.

## What could not be verified and how to try it safely

Not verified, because it only exists on GitHub's runners: **every workflow run**, the cache behaviour, the Linux results of all nine test projects
(WSL on the maintainer's laptop has no `dotnet` and no network), the `check-no-genai.ps1` step and the WinUI build on a hosted runner, the Android
workload and the signing path (read from the documentation, never exercised), the `.deb` install step, and anything about macOS and iOS (no Mac; the laptop's
`ios` workload belongs to another SDK band, so `net10.0-ios` cannot be restored here; the iOS-only C# file was compiled against the iOS
reference assembly in a scratch project). Hosted-runner facts (images, Xcode versions) come from the `actions/runner-images` README of
2026-10-22 and move.

What was done locally: `actionlint` 1.7.12 (official release binary, SHA-256 checked against the release's checksum file) over `.github`: 0 findings, but without
`shellcheck` and `pyflakes` (not installed); every embedded PowerShell block was parsed with the PowerShell parser and every bash block with `bash -n`
(0 errors); the tag-to-version logic of both release workflows was run for `uno-v1.8.1` (1.8.1, 10801), `uno-v2.0.10`, `uno-v1.2` and `uno-vfoo` (the last two rejected).

Safe way to try, in this order:

1. **Draft pull request** from the branch. `pull_request` workflows run the pull request's own files, so `CI`, `VoiceType.Uno CI` and the smoke test run without
   touching `master` (the path filters include `.github/**`). `workflow_dispatch` only appears for a workflow file once it exists on the default branch.
2. Read the first runs: test counts per project (`.trx`), the Linux failures if any (suspects: tests that compare paths case-insensitively, tests that write
   below `HOME`), the `check-no-genai.ps1` output, the workload version printed by the Android job, the Android signing line in the job summary, the
   smoke log for the native-library warning. Pin or fix what they show.
3. **Tag on a fork** (no secrets there, so it also proves the unsigned path, and nothing public appears on the real repository), then on the real repository
   with a throw-away tag such as `uno-v0.0.1`; delete the tag and the release afterwards.
