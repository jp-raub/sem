# Theme values foundation

This change adds a pure, independently testable catalog and inheritance resolver. It does **not** make themes selectable in the application, persist preferences, change the database schema, render CSS, or provide a site-wide accessible appearance feature. It establishes value semantics for a later integration; no claim is made that the feature is complete or active in the UI.

## API (`Sem.Web.Core`)

- `HexColor.Parse` / `TryParse` accepts only exactly `#RRGGBB` with ASCII hexadecimal digits. Values are normalized to uppercase. CSS functions, shorthand colors, alpha values, URLs, and arbitrary CSS are rejected.
- `ThemeDefinition` is immutable and contains an ID, a nonblank display name of at most 30 characters, and the four typed colors: `Accent`, `Background`, `Surface`, and `Text`. IDs are lowercase ASCII kebab-case and are compared ordinally/case-sensitively.
- `ThemeCatalog.Default` is an immutable ten-entry built-in catalog. `ThemeCatalog(IEnumerable<ThemeDefinition>)` copies and validates an injected catalog, rejecting empty catalogs, null entries, and duplicate IDs. Resolver callers pass a catalog explicitly; there is no mutable global catalog.
- `ThemeLayer` contains only nullable `ThemeId` and nullable `ThemeMode`. A theme reference selects the complete catalog palette; colors are not individually overridden in personal or event layers. The enum values are `Light`, `Dark`, and `System`.
- `ThemeResolver.Resolve(catalog, personal, eventLayer, site)` resolves theme selection and mode independently. For each dimension it chooses the first non-null value in personal → event → site order. A selected theme supplies all four colors together. Null theme selection falls back to the supplied catalog’s `green` (Forest) entry; if that required fallback entry is absent, resolution throws `ThemeReferenceException` for `fallback` rather than importing static colors. This preserves future catalog edits. A valid explicit selection does not require an unused Forest entry; null mode falls back to System. System is preserved as a value and is not interpreted by the server.
- Every explicit theme ID is checked against the supplied catalog before resolution, including a reference shadowed by a higher-priority layer. Malformed IDs are rejected when constructing a layer; well-formed but absent IDs throw `ThemeReferenceException` with the offending layer and ID. There is no silent fallback for explicit references.

The built-in catalog preserves the four original IDs (`green`, `blue`, `purple`, `amber`) and adds `rose`, `teal`, `slate`, `navy`, `coral`, and `mono`.

## Verification

Run only the isolated pure-value checks with:

```sh
dotnet run --project tests/Sem.ThemeValueChecks/Sem.ThemeValueChecks.csproj
```

The check project compiles the single foundation source file directly; it does not reference or build the web application. It exercises catalog values, validation, immutability, deterministic inheritance, custom catalog injection, and invalid explicit references. It makes no database, UI, rendering, persistence, or deployment claim.
