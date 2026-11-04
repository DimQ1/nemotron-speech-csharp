# VoiceType.Uno interface localization

The Uno app speaks the 16 languages of its Store listing (`docs/store/imports/v2.0.0`): en, ru, de, fr, es, zh-CN, ja, ko,
pt-BR, it, ar, hi, tr, uk, pl, nl. The language is asked once at the first start (the system language preselected) and can
be changed at any time in the main menu (section Language, "Interface"). A change applies at once, without a restart.

## How it works

| Piece | Where | What |
|---|---|---|
| `Loc` | `VoiceType.Uno.Core/Localization/Loc.cs` | The string table of the current language. `Loc.T(key)`, `Loc.F(key, args)` (`string.Format` with `Loc.Culture`), `Loc.Plural(key, count, args)`, `Loc.SetLanguage(code)`, event `Loc.LanguageChanged`, `Loc.Languages` (code, native name, RTL). Starts in English, so unit tests see English. |
| `PluralRules` | `VoiceType.Uno.Core/Localization/PluralRules.cs` | CLDR cardinal categories (one/few/many/other, Arabic zero/two) for the 16 languages. |
| String files | `VoiceType.Uno.Core/Localization/Strings/<code>/<Area>.json` | Flat JSON objects `"Area.Context.Name": "text"`, embedded as `loc/<code>/<Area>.json`. English is the source; every language has the same files and keys (tests). |
| `{l:Tr Key=...}` | `VoiceType.Uno/Presentation/Localization/Tr.cs` | XAML markup extension, `xmlns:l="using:VoiceType.Uno.Presentation.Localization"`. Resolved when the page is built. |
| `LanguageSwitcher` | `VoiceType.Uno/Presentation/Localization/LanguageSwitcher.cs` | On `Loc.LanguageChanged` rebuilds the root frame: the same pages with the same navigation parameters and back stack, in the new language; Arabic sets `FlowDirection.RightToLeft` on the shell. Cached pages (`MainPage`) implement `IRetirablePage` and detach from the singleton view model. |
| `LanguagePrompt` | `VoiceType.Uno/Presentation/Localization/LanguagePrompt.cs` | The first-run question (while `AppSettings.UiLanguage` is empty) and `Apply(store, code)`: store + switch. |
| Tests | `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/Localization/` | Completeness per language, plural categories, placeholders, valid formats, every key used in the sources exists in English, switching. |

## Rules for code

- **Never write an interface text as a literal.** XAML: `Text="{l:Tr Key=History.List.Empty}"`. C#: `Loc.T("History.List.Empty")`.
- **Keys:** `<Area>.<Context>.<Name>`, PascalCase segments, ASCII letters/digits/dots. `<Area>` = the JSON file the key lives in
  (`Main`, `Settings`, `Models`, `History`, `Recording`, `Chat`, `Queue`, `Services`, `Language`, ...).
- **Interpolation → numbered placeholders:** `$"Downloading {name}, {pct:P0}"` becomes `Loc.F("Models.Download.Progress", name, pct)` with
  `"Downloading {0}, {1:P0}"`. Keep format specifiers inside the placeholder. Literal braces are `{{` `}}`.
- **Counts → plurals:** `Loc.Plural("History.Count", n)` with keys `History.Count.one` = `"{0} recording"` and `History.Count.other` =
  `"{0} recordings"` in English; translators add `few`/`many`/`zero`/`two` as their language needs (`PluralRules.CategoriesOf`).
  Extra arguments are `{1}`, `{2}`, ...
- **Do not split sentences** into concatenated pieces; one key per full sentence or label with placeholders, so word order can change.
- **Numbers and dates inside interface texts** use `Loc.Culture` (`Loc.F` does this; `date.ToString("d MMM", Loc.Culture)`).
- **Not localized:** brand and model names (VoiceType, Gemma, Nemotron, Parakeet, Qwen3, Hugging Face), log lines and
  `Console.WriteLine`, exception texts never shown to the user, stored setting values, file names and folder names,
  `AutomationProperties.AutomationId`, prompts sent to the language model (they stay English), `#if DEBUG` tooling texts.
- **Static texts are evaluated at use time:** a `static readonly string` or `const` with an interface text becomes a property
  (`public static string X => Loc.T(...)`).
- **Live switch:** pages and their view models are rebuilt on a language change. A long-lived object (a singleton service,
  `MainViewModel`, a store) that keeps an already formatted interface text must re-compute it on `Loc.LanguageChanged`
  (raised on the UI thread) and raise `PropertyChanged`.
- **English output stays exactly as it was**; the Core unit tests assert English texts.

## Adding a language

Add it to `Loc.Languages` (code, native name, English name, RTL), copy `Strings/en` to `Strings/<code>`, translate, run
`dotnet test apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/VoiceType.Uno.Tests.csproj --filter "FullyQualifiedName~Localization"`.
