using Sem.Web.Core;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
void Throws<TException>(Action action, string label) where TException : Exception
{
    try { action(); }
    catch (TException) { checks++; Console.WriteLine("PASS " + label); return; }
    throw new InvalidOperationException("FAIL " + label + " (expected " + typeof(TException).Name + ")");
}
HexColor C(string value) => HexColor.Parse(value);
ThemeDefinition Custom(string id, string name, string accent, string background, string surface, string text) =>
    new(id, name, C(accent), C(background), C(surface), C(text));

var builtIn = ThemeCatalog.Default;
Check(builtIn.Themes.Count == 10, "built-in catalog contains exactly ten themes");
Check(builtIn.Themes.Select(x => x.Id).SequenceEqual(new[]
    { "green", "blue", "purple", "amber", "rose", "teal", "slate", "navy", "coral", "mono" }),
    "built-in theme IDs and ordering are preserved");
Check(builtIn.GetRequired("green").Name == "Forest" && builtIn.GetRequired("navy").Name == "Midnight",
    "built-in IDs retain their approved display names");
var expectedThemes = new[]
{
    ("green", "Forest", "#1E6557", "#F5F7F3", "#FFFEFA", "#18312E"),
    ("blue", "Ocean", "#285F91", "#F5F7F3", "#FFFEFA", "#18312E"),
    ("purple", "Plum", "#775087", "#F5F7F3", "#FFFEFA", "#18312E"),
    ("amber", "Amber", "#906322", "#F5F7F3", "#FFFEFA", "#18312E"),
    ("rose", "Rose", "#A14365", "#FBF3F6", "#FFFAFB", "#36212A"),
    ("teal", "Teal", "#14766E", "#EFF8F7", "#FAFFFF", "#183A37"),
    ("slate", "Slate", "#526577", "#F1F4F7", "#FFFFFF", "#263440"),
    ("navy", "Midnight", "#31547B", "#EDF2F8", "#F8FBFF", "#1E3047"),
    ("coral", "Coral", "#AA513F", "#FFF3ED", "#FFFAF6", "#482D25"),
    ("mono", "Monochrome", "#555555", "#F5F5F5", "#FFFFFF", "#252525")
};
Check(expectedThemes.All(expected =>
{
    var actual = builtIn.GetRequired(expected.Item1);
    return actual.Name == expected.Item2 && actual.Accent.Value == expected.Item3 &&
           actual.Background.Value == expected.Item4 && actual.Surface.Value == expected.Item5 &&
           actual.Text.Value == expected.Item6;
}), "all ten complete palettes match the approved mock");
Check(HexColor.Parse("#aBcD09").Value == "#ABCD09", "hex parser accepts either hex-letter case and canonicalizes");
Check(!HexColor.TryParse(null, out _) && !HexColor.TryParse("", out _) && !HexColor.TryParse("#123", out _) &&
      !HexColor.TryParse("123456", out _) && !HexColor.TryParse("#12GG56", out _) && !HexColor.TryParse("#１２３４５６", out _),
    "hex parser rejects null, empty, malformed, and non-ASCII values");
Throws<ArgumentException>(() => HexColor.Parse("url(https://bad.example)"), "arbitrary CSS and URLs are rejected");
Throws<ArgumentNullException>(() => new ThemeCatalog(null!), "null catalog input is rejected");
Throws<ArgumentException>(() => new ThemeCatalog(Array.Empty<ThemeDefinition>()), "empty catalog is rejected");
Throws<ArgumentException>(() => new ThemeCatalog(new ThemeDefinition[] { builtIn.GetRequired("green"), builtIn.GetRequired("green") }),
    "duplicate catalog IDs are rejected");
Throws<ArgumentException>(() => new ThemeCatalog(new ThemeDefinition[] { null! }), "null catalog entries are rejected");
Throws<ArgumentException>(() => Custom("bad id", "Bad", "#000000", "#000000", "#000000", "#000000"),
    "malformed theme IDs are rejected");
Throws<ArgumentException>(() => Custom("bad", "  ", "#000000", "#000000", "#000000", "#000000"),
    "blank theme names are rejected");
Throws<ArgumentException>(() => Custom("bad", new string('x', 31), "#000000", "#000000", "#000000", "#000000"),
    "theme names over 30 characters are rejected");
Throws<ArgumentNullException>(() => new ThemeDefinition("null-color", "Null", null!, C("#000000"), C("#000000"), C("#000000")),
    "null typed colors are rejected");
Throws<ArgumentException>(() => new ThemeLayer(themeId: "Forest"), "theme references are case-sensitive and IDs are lowercase");
Throws<ArgumentException>(() => new ThemeLayer(themeId: ""), "empty theme references are rejected");
Throws<ArgumentOutOfRangeException>(() => new ThemeLayer(mode: (ThemeMode)99), "unknown modes are rejected");
Throws<ArgumentNullException>(() => ThemeResolver.Resolve(null!), "null resolver catalog is rejected");

var customTheme = Custom("custom-future", "Future", "#010203", "#111213", "#212223", "#313233");
var inputThemes = builtIn.Themes.ToList();
inputThemes.Add(customTheme);
var injectedCatalog = new ThemeCatalog(inputThemes);
inputThemes.Clear();
Check(injectedCatalog.Themes.Count == 11 && injectedCatalog.GetRequired("custom-future").Accent.Value == "#010203",
    "injected catalog is an isolated snapshot for future catalogs");
Check(!injectedCatalog.TryGet("CUSTOM-FUTURE", out _) && injectedCatalog.TryGet("custom-future", out _),
    "catalog lookups use exact case-sensitive IDs");
if (injectedCatalog.Themes is IList<ThemeDefinition> readOnlyThemes)
    Throws<NotSupportedException>(() => readOnlyThemes.Add(customTheme), "catalog collection cannot be mutated through its interface");
else
    throw new InvalidOperationException("FAIL catalog exposes a read-only list interface");

var defaults = ThemeResolver.Resolve(builtIn);
Check(defaults.Accent.Value == "#1E6557" && defaults.Background.Value == "#F5F7F3" &&
      defaults.Surface.Value == "#FFFEFA" && defaults.Text.Value == "#18312E" && defaults.Mode == ThemeMode.System,
    "null layers resolve to documented Forest/System defaults");
var site = new ThemeLayer(themeId: "blue", mode: ThemeMode.Dark);
var eventLayer = new ThemeLayer(themeId: "rose");
var inheritedFromEvent = ThemeResolver.Resolve(builtIn, null, eventLayer, site);
Check(inheritedFromEvent.Accent.Value == "#A14365" && inheritedFromEvent.Background.Value == "#FBF3F6" &&
      inheritedFromEvent.Surface.Value == "#FFFAFB" && inheritedFromEvent.Text.Value == "#36212A",
    "event theme supplies its complete palette ahead of site theme");
Check(inheritedFromEvent.Mode == ThemeMode.Dark, "mode inherits from site independently of selected palette");
var personal = new ThemeLayer(themeId: "teal", mode: ThemeMode.Light);
var resolved = ThemeResolver.Resolve(builtIn, personal, eventLayer, site);
Check(resolved.Accent.Value == "#14766E" && resolved.Background.Value == "#EFF8F7" &&
      resolved.Surface.Value == "#FAFFFF" && resolved.Text.Value == "#183A37",
    "personal theme supplies its complete palette ahead of event and site themes");
Check(resolved.Mode == ThemeMode.Light, "personal mode inherits independently from personal theme selection");
var siteOnly = ThemeResolver.Resolve(builtIn, null, null, site);
Check(siteOnly.Accent.Value == "#285F91" && siteOnly.Mode == ThemeMode.Dark,
    "site theme and mode supply values when higher layers are null");
var customResolved = ThemeResolver.Resolve(injectedCatalog, new ThemeLayer(themeId: "custom-future"));
Check(customResolved.Accent.Value == "#010203" && customResolved.Background.Value == "#111213" &&
      customResolved.Surface.Value == "#212223" && customResolved.Text.Value == "#313233",
    "resolver uses an explicitly injected custom catalog");

try
{
    ThemeResolver.Resolve(builtIn, null, new ThemeLayer(themeId: "missing-theme"), new ThemeLayer(themeId: "green"));
    throw new InvalidOperationException("FAIL missing explicit reference is rejected");
}
catch (ThemeReferenceException error)
{
    Check(error.LayerName == "event" && error.ThemeId == "missing-theme", "missing reference reports the offending layer and ID");
}
Throws<ThemeReferenceException>(() => ThemeResolver.Resolve(builtIn, new ThemeLayer(themeId: "teal"),
    new ThemeLayer(themeId: "missing-theme"), new ThemeLayer(themeId: "green")), "shadowed event reference is rejected beneath valid personal selection");
Throws<ThemeReferenceException>(() => ThemeResolver.Resolve(builtIn, new ThemeLayer(themeId: "teal"),
    new ThemeLayer(themeId: "rose"), new ThemeLayer(themeId: "missing-theme")), "shadowed site reference is rejected beneath valid personal and event selections");
var editedForest = Custom("green", "Edited Forest", "#010101", "#020202", "#030303", "#040404");
var editedCatalog = new ThemeCatalog(new[] { editedForest });
var editedFallback = ThemeResolver.Resolve(editedCatalog);
Check(editedFallback.Accent == editedForest.Accent && editedFallback.Background == editedForest.Background &&
    editedFallback.Surface == editedForest.Surface && editedFallback.Text == editedForest.Text,
    "null selection uses the injected catalog Forest rather than static built-in colors");
var forestless = new ThemeCatalog(new[] { customTheme });
Throws<ThemeReferenceException>(() => ThemeResolver.Resolve(forestless), "missing fallback Forest fails closed in injected catalog");
Check(ThemeResolver.Resolve(forestless, new ThemeLayer("custom-future")).Accent == customTheme.Accent,
    "explicit valid selection does not require unused fallback Forest");
Check(ThemeResolver.Resolve(builtIn, new ThemeLayer(mode: ThemeMode.System), null, site).Mode == ThemeMode.System,
    "explicit System overrides lower-priority Dark rather than inheriting");

var repeated = ThemeResolver.Resolve(builtIn, personal, eventLayer, site);
Check(repeated.Accent == resolved.Accent && repeated.Background == resolved.Background && repeated.Surface == resolved.Surface &&
      repeated.Text == resolved.Text && repeated.Mode == resolved.Mode && personal.ThemeId == "teal" &&
      eventLayer.ThemeId == "rose" && site.ThemeId == "blue" && site.Mode == ThemeMode.Dark,
    "resolution is deterministic and does not mutate inputs");
Console.WriteLine($"PASS all {checks} theme-value checks");
