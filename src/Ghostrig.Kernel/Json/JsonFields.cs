using System.Globalization;
using System.Text.Json;
using Ghostrig.Kernel.Errors;

namespace Ghostrig.Kernel.Json;

/// <summary>
/// Strict, path-aware accessors for hand-authored JSON content. Every failure names the content
/// source and JSON path so authoring mistakes surface as explicit validation errors, never defaults.
/// </summary>
internal readonly struct JsonFields
{
    private readonly JsonElement _element;
    private readonly string _source;
    private readonly string _path;

    public JsonFields(JsonElement element, string source, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ContentValidationException(source, $"{path} must be a JSON object.");
        }

        _element = element;
        _source = source;
        _path = path;
    }

    public string Path => _path;

    public ContentValidationException Error(string message) => new(_source, $"{_path}: {message}");

    public bool Has(string name) => _element.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public string RequireString(string name)
    {
        var value = Require(name, JsonValueKind.String).GetString()!;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Error($"'{name}' must not be empty.");
        }

        return value;
    }

    public string? OptionalString(string name) =>
        Has(name) ? Require(name, JsonValueKind.String).GetString() : null;

    public bool OptionalBool(string name, bool fallback)
    {
        if (!Has(name))
        {
            return fallback;
        }

        _element.TryGetProperty(name, out var value);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Error($"'{name}' must be a boolean."),
        };
    }

    public long RequireInt64(string name, long min = long.MinValue, long max = long.MaxValue)
    {
        var element = Require(name, JsonValueKind.Number);
        if (!element.TryGetInt64(out var value))
        {
            throw Error($"'{name}' must be an integer.");
        }

        if (value < min || value > max)
        {
            throw Error($"'{name}' = {value} is outside [{min}, {max}].");
        }

        return value;
    }

    public int RequireInt32(string name, int min = int.MinValue, int max = int.MaxValue) =>
        (int)RequireInt64(name, min, max);

    public long OptionalInt64(string name, long fallback, long min = long.MinValue, long max = long.MaxValue) =>
        Has(name) ? RequireInt64(name, min, max) : fallback;

    public ulong RequireUInt64(string name)
    {
        var element = Require(name, JsonValueKind.Number);
        if (!element.TryGetUInt64(out var value))
        {
            throw Error($"'{name}' must be a non-negative integer.");
        }

        return value;
    }

    /// <summary>Reads a decimal number and converts it to an integer scaled by <paramref name="scale"/> (fixed point).</summary>
    public long RequireFixedPoint(string name, long scale, long min = 0, long max = long.MaxValue)
    {
        var element = Require(name, JsonValueKind.Number);
        if (!decimal.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            throw Error($"'{name}' must be a number.");
        }

        var scaled = decimal.Round(d * scale, MidpointRounding.AwayFromZero);
        if (scaled < min || scaled > max)
        {
            throw Error($"'{name}' = {d} is outside the permitted range.");
        }

        return (long)scaled;
    }

    public JsonFields RequireObject(string name) => new(Require(name, JsonValueKind.Object), _source, $"{_path}.{name}");

    public IEnumerable<JsonFields> RequireObjectArray(string name)
    {
        var array = Require(name, JsonValueKind.Array);
        return EnumerateObjects(array, name);
    }

    public IEnumerable<JsonFields> OptionalObjectArray(string name) =>
        Has(name) ? RequireObjectArray(name) : [];

    public IReadOnlyList<string> OptionalStringArray(string name)
    {
        if (!Has(name))
        {
            return [];
        }

        var array = Require(name, JsonValueKind.Array);
        var result = new List<string>();
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Error($"'{name}[{i}]' must be a non-empty string.");
            }

            result.Add(item.GetString()!);
            i++;
        }

        return result;
    }

    private IEnumerable<JsonFields> EnumerateObjects(JsonElement array, string name)
    {
        var list = new List<JsonFields>();
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            list.Add(new JsonFields(item, _source, $"{_path}.{name}[{i}]"));
            i++;
        }

        return list;
    }

    private JsonElement Require(string name, JsonValueKind kind)
    {
        if (!_element.TryGetProperty(name, out var value))
        {
            throw Error($"missing required property '{name}'.");
        }

        if (value.ValueKind != kind)
        {
            throw Error($"'{name}' must be of JSON type {kind}, found {value.ValueKind}.");
        }

        return value;
    }

    public static JsonDocument Parse(string json, string source)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException ex)
        {
            throw new ContentValidationException(source, $"malformed JSON: {ex.Message}");
        }
    }
}
