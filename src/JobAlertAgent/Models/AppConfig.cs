using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobAlertAgent.Models;

public static class MatchScope
{
    public const string TitleAndDescription = "titleAndDescription";
    public const string Title = "title";
}

public static class ProviderNames
{
    public const string Greenhouse = "greenhouse";
    public const string Ashby = "ashby";
    public const string Lever = "lever";
    public const string Workable = "workable";
    public const string Workday = "workday";
    public const string Amazon = "amazon";
    public const string Microsoft = "microsoft";
}

public sealed class KeywordConfig
{
    /// <summary>Text to look for, e.g. ".NET" or "Applied AI Engineer".</summary>
    public string Term { get; set; } = "";

    /// <summary>"titleAndDescription" (default) or "title".</summary>
    public string Scope { get; set; } = MatchScope.TitleAndDescription;
}

public sealed class SourceConfig
{
    /// <summary>
    /// Display name, also used as the prefix of the job key. Optional when a link is given: it is derived
    /// from the link. Changing it later makes that company's jobs look new once.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The easy way to configure a company: a career-site link (or shorthand such as "lever:meesho").
    /// Provider and slug are worked out from it.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>One of <see cref="ProviderNames"/>. Optional when <see cref="Url"/> is given.</summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// Provider-specific identifier: Greenhouse board token, Ashby job-board name, Lever site name.
    /// Not used by amazon / microsoft. Optional when <see cref="Url"/> is given.
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>Optional free-text location sent to search-based providers (Microsoft), e.g. "India".</summary>
    public string? Location { get; set; }

    /// <summary>
    /// Optional: keep only jobs whose location contains one of these places, e.g. ["India"] (which also covers
    /// the main Indian cities). Works for every provider. Empty = no restriction.
    /// </summary>
    public List<string> Locations { get; set; } = new();

    /// <summary>Optional ISO-3 country codes (Amazon), e.g. ["IND"]. Applied server-side and client-side.</summary>
    public List<string> CountryCodes { get; set; } = new();

    /// <summary>Set to false to temporarily disable a source without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Set when this entry cannot be used (typo, unknown site...). Such a source is reported in the
    /// email as "could not check" while all other companies keep working.
    /// </summary>
    [JsonIgnore]
    public string? ConfigError { get; set; }
}

public sealed class AppConfig
{
    public List<KeywordConfig> Keywords { get; set; } = new();
    public List<SourceConfig> Sources { get; set; } = new();

    /// <summary>Entries not seen in a scan for this many days are dropped from the seen-jobs store.</summary>
    public int SeenRetentionDays { get; set; } = 60;

    private static readonly JsonSerializerOptions ObjectOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads the config. Keywords and sources may each be written as a plain string
    /// ("React", "https://jobs.lever.co/meesho") or as an object with extra settings.
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);

        return Parse(File.ReadAllText(path), path);
    }

    public static AppConfig Parse(string json, string origin = "config")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{origin} is not valid JSON ({ex.Message}). Typical causes: a missing comma between entries, or a missing quote.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{origin} must contain a JSON object.");

            var config = new AppConfig();

            if (Find(root, "seenRetentionDays") is { ValueKind: JsonValueKind.Number } days && days.TryGetInt32(out var d))
                config.SeenRetentionDays = d;

            foreach (var item in Items(root, "keywords"))
            {
                config.Keywords.Add(item.ValueKind == JsonValueKind.String
                    ? new KeywordConfig { Term = item.GetString() ?? "" }
                    : Read<KeywordConfig>(item, "keyword"));
            }

            foreach (var item in Items(root, "sources"))
            {
                config.Sources.Add(item.ValueKind == JsonValueKind.String
                    ? new SourceConfig { Url = item.GetString() }
                    : Read<SourceConfig>(item, "source"));
            }

            config.Validate();
            return config;
        }
    }

    /// <summary>
    /// Fatal problems (no keywords, bad keyword scope) throw. Problems with an individual company are
    /// non-fatal: they are stored in <see cref="SourceConfig.ConfigError"/> and surfaced in the email,
    /// so one typo cannot silence the daily alert for everyone else.
    /// </summary>
    public void Validate()
    {
        if (Keywords.Count == 0)
            throw new InvalidDataException("Config must contain at least one keyword.");

        foreach (var k in Keywords)
        {
            if (string.IsNullOrWhiteSpace(k.Term))
                throw new InvalidDataException("A keyword is empty.");
            if (k.Scope is not (MatchScope.Title or MatchScope.TitleAndDescription))
                throw new InvalidDataException(
                    $"Keyword '{k.Term}' has invalid scope '{k.Scope}'. Use '{MatchScope.Title}' or '{MatchScope.TitleAndDescription}'.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Sources)
        {
            ResolveSource(s);
            if (s.ConfigError is null && !names.Add(s.Name))
                s.ConfigError = $"Duplicate company name '{s.Name}'. Give each company a unique \"name\".";
        }
    }

    private static void ResolveSource(SourceConfig s)
    {
        s.ConfigError = null;
        s.Provider = (s.Provider ?? "").Trim().ToLowerInvariant();
        s.Slug = string.IsNullOrWhiteSpace(s.Slug) ? null : s.Slug.Trim();
        s.Name = (s.Name ?? "").Trim();
        var label = !string.IsNullOrWhiteSpace(s.Name) ? s.Name : s.Url ?? s.Slug ?? "(unnamed source)";

        if (!string.IsNullOrWhiteSpace(s.Url))
        {
            if (!SourceUrlParser.TryParse(s.Url, out var provider, out var slug, out var error))
            {
                s.ConfigError = error;
                if (s.Name.Length == 0) s.Name = label;
                return;
            }

            if (s.Provider.Length > 0 && s.Provider != provider)
            {
                s.ConfigError = $"'{label}': provider '{s.Provider}' does not match the link, which is a {provider} site.";
                if (s.Name.Length == 0) s.Name = label;
                return;
            }

            s.Provider = provider;
            s.Slug ??= slug;
        }

        if (s.Provider.Length == 0)
        {
            s.ConfigError = $"'{label}' needs a \"url\" (or a \"provider\"). {SourceUrlParser.SupportedHelp}";
            if (s.Name.Length == 0) s.Name = label;
            return;
        }

        if (s.Provider is not (ProviderNames.Greenhouse or ProviderNames.Ashby or ProviderNames.Lever or ProviderNames.Workable
            or ProviderNames.Workday or ProviderNames.Amazon or ProviderNames.Microsoft))
        {
            s.ConfigError = $"'{label}' has unknown provider '{s.Provider}'. "
                            + "Use greenhouse, lever, ashby, workable, workday, amazon or microsoft - or just give a \"url\".";
            if (s.Name.Length == 0) s.Name = label;
            return;
        }

        if (s.Provider is ProviderNames.Greenhouse or ProviderNames.Ashby or ProviderNames.Lever or ProviderNames.Workable or ProviderNames.Workday && s.Slug is null)
        {
            s.ConfigError = $"'{label}' ({s.Provider}) needs a \"slug\" or a \"url\" that contains the company name.";
            if (s.Name.Length == 0) s.Name = label;
            return;
        }

        if (s.Name.Length == 0) s.Name = SourceUrlParser.DeriveName(s.Provider, s.Slug);
    }

    private static JsonElement? Find(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        var el = Find(root, name);
        if (el is null) return Enumerable.Empty<JsonElement>();
        if (el.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"\"{name}\" must be a list, like [ ... ].");
        return el.Value.EnumerateArray().ToList();
    }

    private static T Read<T>(JsonElement el, string what)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Each {what} must be a quoted string or an object, found: {el.GetRawText()}");
        try
        {
            return el.Deserialize<T>(ObjectOptions)!;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid {what} entry {el.GetRawText()}: {ex.Message}", ex);
        }
    }
}

