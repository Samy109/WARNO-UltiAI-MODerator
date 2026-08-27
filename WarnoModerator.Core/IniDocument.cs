using System.Text;

namespace WarnoModerator.Core;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> SectionNames => _sections.Keys;

    public static IniDocument Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        return Parse(reader.ReadToEnd());
    }

    public static IniDocument Parse(string text)
    {
        var document = new IniDocument();
        var section = string.Empty;

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                document.GetOrCreateSection(section);
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = StripInlineComment(line[(equals + 1)..]).Trim();
            document.Set(section, key, value);
        }

        return document;
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
            ? value
            : null;

    public int GetInt(string section, string key, int fallback = 0) =>
        int.TryParse(Get(section, key), out var value) ? value : fallback;

    public IReadOnlyDictionary<string, string> GetSection(string section) =>
        _sections.TryGetValue(section, out var values)
            ? new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Set(string section, string key, string value) => GetOrCreateSection(section)[key] = value;

    public void Save(string path)
    {
        var builder = new StringBuilder();
        foreach (var section in _sections)
        {
            if (section.Key.Length > 0)
            {
                builder.Append('[').Append(section.Key).AppendLine("]");
            }

            foreach (var pair in section.Value)
            {
                builder.Append(pair.Key).Append(" = ").AppendLine(pair.Value);
            }

            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private Dictionary<string, string> GetOrCreateSection(string section)
    {
        if (!_sections.TryGetValue(section, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = values;
        }

        return values;
    }

    private static string StripInlineComment(string value)
    {
        var semicolon = value.IndexOf(';');
        return semicolon >= 0 ? value[..semicolon] : value;
    }
}

