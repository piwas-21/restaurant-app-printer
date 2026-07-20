# Printer-App — Sofra ("craft") Branding Alignment Plan

> **Status:** P0–P2 COMPLETE (2026-07-21). P0 (#72) · P1 (#73) · P2a (#74) · P2b-1 (#75) · **P2b-2 (this
> PR)** all merged/open on `develop`. Every hardcoded colour site (≈96: inline XAML + C# `Colors.X` +
> the model string) is now migrated to the craft palette across all 5 pages, 2 converters, 1 service —
> theme-aware in both light and dark. The colour/typography/semantic work is done; **P3 (bespoke craft
> texture) remains optional/deferred.** Owed before a `develop→main` release: a Windows-head build + an
> on-device visual pass (macOS/CI builds Android only). Live status detail lives in the workspace memory
> `project_printer_app_branding`.
>
> This plan exists because the sofrapiwas design/colours were only **partially and inaccurately** applied
> in the printer-app MAUI UI. It scoped the gap and the phased path (below) that closed it.
>
> **Deliberate scope note:** the workspace CLAUDE.md frames the Sofra "craft" design system as bound to
> **sofra** (the site/control plane) + the **tenant frontend** — *not* this internal printer utility.
> Bringing the printer-app onto the brand is therefore a **new decision** (owner-requested 2026-07-20),
> not a pre-existing requirement. This plan records that.

---

## 1. Goal

Make the printer-app's MAUI UI read as the same product as sofrapiwas.com: correct brand colours,
semantic status colours from the craft palette, and (optionally) craft typography — in **both** light and
dark themes. Today the app is ~60% scaffolded toward this but the **values are drifted**, the **semantic
layer is missing**, **typography is un-ported**, and **~96 colour decisions are hardcoded** (over half in
C# code-behind/converters where an XAML/token swap can't reach them).

## 2. Source of truth

Sofra is **Tailwind v4** — tokens live in CSS, no `tailwind.config.ts`:
- `sofra/app/globals.css` — the authoritative token values (HSL in `:root`/`.dark`, hex in `@theme inline`).
- `sofra/docs/design-tokens.md` — human-readable table + rationale.

The system is **"craft / handmade, food-warm"**: flat pigment, no gradients, kitchen/pantry colour names
(terracotta, olive, saffron, cream, ink, beige). **Dark mode is a different atmosphere** (late-evening
kitchen aubergine), deliberately *not* an invert.

> **Fix a dangling ref while here:** `Resources/Styles/Colors.xaml:9` cites a `DESIGN.md` that does not
> exist in the repo. Either add that file (a short printer-app design note pointing at the sofra tokens)
> or correct the comment. Do it in P0.

## 3. Current state (from the 2026-07-20 audit)

| Area | State |
|---|---|
| `Resources/Styles/Colors.xaml` | Craft-*named* tokens exist but **values drift** from real Sofra hex; no accent/olive/saffron/error/warning tokens. |
| `Resources/Styles/Styles.xaml` (456 LOC) | Implicit control styles mostly tokenised, but Entry/Editor/Frame still use generic `Gray*`/`Black`/`White`; `Headline`/`SubHeadline` still use the **old purple** `MidnightBlue`; page bg default uses `OffBlack`. |
| Page backgrounds / card surfaces / muted text | ✅ already tokenised + theme-correct across all 5 pages (`DiagnosticsPage` is cleanest). |
| Buttons + status/semantic colours | ❌ **hardcoded inline** (`#2196F3`, `Green`, `Orange`, `#F44336`, …) and **dark-mode-blind**. |
| C# code-behind | ❌ **~54 `Colors.X` sites** (MainPage.xaml.cs alone ≈30) set status/toggle colours in code — theme-blind. |
| Converters | `BoolToColorConverter` + `LogTypeToColorConverter` hardcode 13 raw colours (Blue/Purple/etc. — **not in the craft palette**). |
| Model leak | `OrderHistoryService.cs:196` returns colour **strings** ("Green"/"Orange"/"Red") — presentation in a service. |
| Typography | ❌ OpenSans only; **none** of Sofra's 5 craft fonts (Quicksand/Amatic SC/Caveat/Kalam/Special Elite). |
| Android native | `Platforms/Android/Resources/values/colors.xml` — `colorPrimary` wrong; `colorAccent` happens to = true terracotta. |

**≈96 hardcoded colour decision sites** total (≈41 XAML inline + ≈54 C# + 1 model string), across 5 pages,
2 converters, 1 service.

## 4. The palette mapping (P0 core)

Correct `Colors.xaml` to the real Sofra hex and add the missing semantic tokens. Light → Dark:

| Token (printer-app) | Current | → Correct (Sofra) light | Dark |
|---|---|---|---|
| `Primary` (terracotta) | `#89341a` | `#A84B2F` | `#F2B48C` |
| `TextPrimary` (ink) | `#241912` | `#3B2E26` | `#F2E9DD` |
| `PageBackground` (cream) | `#fff8f5` | `#FFF9F2` | `#211A16` |
| `CardBackground` (plate) | `#fffcf8` ✓ | `#FFFCF8` | `#2C241F` |
| `CardBorder` (beige) | `#e6d7cd` | `#EAE0D2` | `#3E3630` |
| `Secondary` → **olive** | `#ffc55f` | `#7C8450` (text-safe `#5A6139`) | `#B5BC8A` |
| **`Accent`** → saffron (new) | — | `#D9A441` (text-safe `#8A6D2B`) | `#E8C87C` |
| **`Success`** → moss | `#28A745` | `#8CB89A` (text-safe `#4C7259`) | `#8CB89A` |
| **`Warning`** (new) | — | `#D9A441` (text `#8A6D2B`) | `#E8C87C` |
| **`Error`** (new) | — | `#CC5A50` (deliberately pinker than terracotta) | `#E8948C` |

Radius: Sofra is tight `2px` + a signature hand-drawn irregular radius (not portable to MAUI 1:1 — treat as
optional P3). Keep MAUI corner radii sane but stop the ad-hoc `8/10/15/20/25` spread.

## 5. Phased plan

### P0 — Token values + purge stale (centralised, low risk) — **do first**
- Correct every `Colors.xaml` value per §4; add `Accent`, `Warning`, `Error` tokens (+ their `*Dark` and
  text-safe variants) and matching `SolidColorBrush` wrappers.
- Retarget the stale refs in `Styles.xaml`: `MidnightBlue`→ink, `OffBlack`→aubergine bg, `Gray*` borders→
  `CardBorder`/beige, Entry/Editor text `Black`/`White`→`TextPrimary`/Dark.
- Update `Platforms/Android/Resources/values/colors.xml` (`colorPrimary`→`#A84B2F`).
- Resolve the `DESIGN.md` dangling ref.
- **Risk:** low — one/two files drive the whole implicit look. **Reviewable in isolation.**

### P1 — Implicit control styles + fonts (centralised)
- In `Styles.xaml`, make the implicit `Button`/`Entry`/`Editor`/`Frame`/`Label` defaults reference the
  corrected tokens so every *unstyled* control is on-brand automatically.
- **Typography decision (owner):** port the 5 craft fonts, or a pragmatic subset (e.g. **Quicksand** body +
  one display face)? A thermal-printer utility arguably doesn't need Amatic/Caveat/Kalam. Whatever is
  chosen: drop `.ttf`s into `Resources/Fonts/`, register in `MauiProgram.cs`, set the default font family.

### P2 — Semantic layer: converters + code-behind (scattered, the real work)
This is where the ~96 hardcoded sites live and where a token swap **cannot** reach.
- **Define status semantics as tokens first** (done in P0): success(moss)/warning(saffron)/error(brick)/
  info. Sofra ships **no blue/purple**, so `LogTypeToColorConverter`'s 8 raw colours must be **re-mapped**
  to craft equivalents (a design decision — e.g. SSE→olive, Order→terracotta, PrintRequest→saffron,
  PrintSuccess→moss, PrintError→error, Error→error-dark, Warning→warning, default→muted).
- Rewire the **inline** XAML colours on `MainPage`, `OrderManagementPage`, `UpdaterWindow`,
  `PrintStyleSettingsPage` to the tokens.
- Rewire the **C# `Colors.X`** sites so status/toggle colours resolve from the palette **theme-aware**
  (`Application.Current.Resources` + `RequestedTheme`), not literal `Colors.Green`. `MainPage.xaml.cs`
  (~30 sites) is the hotspot.
- Move `OrderHistoryService.StatusColor` (colour strings in a service) **out** to a converter/UI — same
  reasoning that already moved `LogEntry.TypeColor` out (see `ValueConverters.cs` note).
- **⚠️ File-length landmine:** `Converters/` limit is **60 LOC** and `ValueConverters.cs` is already at 59.
  Adding theme-aware colour resolution there **will breach it** → split converters into separate files or
  introduce a small `IStatusColorResolver` service. `MainPage.xaml.cs` (already large) must not cross 200
  LOC when its colour logic is refactored — likely needs a helper/service extraction. Check
  `scripts/file-length-baseline.txt`.

### P3 — Craft visual language (optional, bespoke)
Paper texture, torn/deckled edges, hand-drawn irregular border radius, artisanal offset shadows, display
type. **No MAUI equivalent** — bespoke effort. Recommend **deferring** unless the owner wants the printer
app to feel as expressive as the site; for an operator utility, P0–P2 delivers the brand correctly.

## 6. Constraints (printer-app/CLAUDE.md)
- **Dark mode = MAUI `{AppThemeBinding Light=… Dark=…}`** (a *third* mechanism vs sofra `.dark` and the
  tenant-frontend `data-theme`). Token *values* port cleanly (paired light/dark hex both sides); the risk
  is C# literals (`Colors.X`) that can't flip — those must go through resource lookup.
- **File-length (blocking):** Converters 60 · `.xaml.cs` 200 · Services 300. P2 will bump against these
  (see landmine above).
- **No "no-inline-hex" rule exists** in printer-app CLAUDE.md today (unlike sofra §5.5). Consider **adding
  one** in P2 so the ~96 sites don't silently regrow — this is the durable fix.
- `Platforms/Windows/Package.appxmanifest` splash/icon identity is ADR-gated (§9) — only relevant if P3
  touches app iconography.

## 7. Test impact
**None on the existing test suites** — unit + E2E tests are service-level and assert **no** UI colours.
The one code touchpoint is `LogTypeToColorConverter` (re-mapped in P2); no test asserts its output, so the
re-skin and the test work are independent and can proceed in parallel. (This is why the A0–A3 test work
was completed first.)

## 8. Open decisions for the owner
1. **Typography:** full 5-font craft set, or Quicksand-body + one display face, or keep OpenSans and only
   fix colours? (Recommendation: Quicksand body + optional display; skip Caveat/Kalam/Amatic for a utility.)
2. **`Secondary` semantics:** confirm printer-app `Secondary` → **olive** and add **saffron** as `Accent`
   (matches Sofra's secondary=olive / accent=saffron).
3. **LogType colour re-map:** approve the craft mapping in P2 (no blue/purple exist in the palette).
4. **P3 craft texture:** in or out for the utility app? (Recommendation: out / defer.)
5. **Add a no-inline-hex rule** to printer-app CLAUDE.md as part of P2? (Recommendation: yes.)

## 9. Sequencing recommendation
P0 → P1 → P2 as **separate PRs** (P0 is a clean, low-risk, high-visual-payoff token correction; land it
first). P3 optional/deferred. Each PR through the normal gates. Estimated shape: P0 ≈ 1 focused session,
P1 ≈ 1 (fonts + implicit styles), P2 ≈ 2–3 (the scattered code-behind + converter/service extraction under
the file-length caps).
