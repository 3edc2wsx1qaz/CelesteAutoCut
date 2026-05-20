using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsClipSidecar;

public static class AppendOnlyJsonl
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static async Task AppendAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var line = JsonSerializer.Serialize(value, LineOptions);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    public static void Clear(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        stream.SetLength(0);
    }

    public static List<T> ReadAll<T>(string path)
    {
        var items = new List<T>();
        var lineNumber = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var item = JsonSerializer.Deserialize<T>(line, JsonDefaults.Options);
            if (item is null)
            {
                throw new InvalidDataException($"Could not parse JSONL line {lineNumber} in {path}.");
            }

            items.Add(item);
        }

        return items;
    }
}

public static class JsonFile
{
    public static T Read<T>(string path)
    {
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options);
        return value ?? throw new InvalidDataException($"Could not parse {path} as {typeof(T).Name}.");
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonDefaults.Options) + Environment.NewLine, new UTF8Encoding(false));
    }
}
