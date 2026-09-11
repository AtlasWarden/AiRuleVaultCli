using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace RuleVault.Storage;

public static class StrictJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static JsonDocument Parse(ReadOnlySpan<byte> utf8, StorageLimits? limits = null)
    {
        limits ??= new StorageLimits();
        if (utf8.Length > limits.MaxJsonBytes)
        {
            throw new StorageFormatException("JSON_TOO_LARGE", $"JSON exceeds {limits.MaxJsonBytes} bytes.");
        }

        var normalized = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF
            ? utf8[3..]
            : utf8;
        try
        {
            var reader = new Utf8JsonReader(
                normalized,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxDepth
                });

            var objects = new Stack<HashSet<string>>(capacity: 8);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.PropertyName:
                        if (objects.Count == 0 || !objects.Peek().Add(reader.GetString()!))
                        {
                            throw new StorageFormatException("JSON_DUPLICATE_KEY", "Duplicate JSON object member.");
                        }

                        break;
                    case JsonTokenType.EndObject:
                        objects.Pop();
                        break;
                }
            }

            using var document = JsonDocument.Parse(
                normalized.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxDepth
                });
            ValidateLimits(document.RootElement, depth: 0, limits);
            return JsonDocument.Parse(document.RootElement.GetRawText());
        }
        catch (StorageFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new StorageFormatException("JSON_INVALID", exception.Message, exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new StorageFormatException("UTF8_INVALID", "Input is not valid UTF-8.", exception);
        }
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8, StorageLimits? limits = null)
    {
        using var document = Parse(utf8, limits);
        try
        {
            return document.RootElement.Deserialize<T>(SerializerOptions)
                ?? throw new StorageFormatException("JSON_NULL", "JSON value deserialized to null.");
        }
        catch (StorageFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new StorageFormatException("JSON_SCHEMA", exception.Message, exception);
        }
    }

    private static void ValidateLimits(JsonElement element, int depth, StorageLimits limits)
    {
        if (depth > limits.MaxDepth)
        {
            throw new StorageFormatException("JSON_DEPTH", $"JSON nesting exceeds {limits.MaxDepth}.");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString()!.Length > limits.MaxStringLength:
                throw new StorageFormatException("JSON_STRING_TOO_LARGE", "JSON string exceeds the configured limit.");
            case JsonValueKind.Object:
                if (element.EnumerateObject().Count() > limits.MaxCollectionItems)
                {
                    throw new StorageFormatException("JSON_MEMBER_LIMIT", "JSON object has too many members.");
                }

                foreach (var property in element.EnumerateObject())
                {
                    ValidateLimits(property.Value, depth + 1, limits);
                }

                break;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > limits.MaxCollectionItems)
                {
                    throw new StorageFormatException("JSON_ARRAY_LIMIT", "JSON array has too many items.");
                }

                foreach (var child in element.EnumerateArray())
                {
                    ValidateLimits(child, depth + 1, limits);
                }

                break;
        }
    }
}
