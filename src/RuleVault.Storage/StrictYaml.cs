using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace RuleVault.Storage;

public static class StrictYaml
{
    private static readonly Regex AmbiguousPlainScalar = new(
        "^(?:null|Null|NULL|yes|Yes|YES|no|No|NO|on|On|ON|off|Off|OFF|[-+]?\\d|[-+]?\\d.*\\d|\\d{4}-\\d{2}-\\d{2}|0x[0-9A-Fa-f]+|0o[0-7]+|0b[01]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, object?> ParseFrontmatter(string text, StorageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits ??= new StorageLimits();
        if (Encoding.UTF8.GetByteCount(text) > limits.MaxYamlBytes)
        {
            throw new StorageFormatException("YAML_TOO_LARGE", $"YAML exceeds {limits.MaxYamlBytes} bytes.");
        }

        try
        {
            var parser = new Parser(new StringReader(text));
            if (!parser.MoveNext())
            {
                throw new StorageFormatException("YAML_STREAM", "YAML stream is empty.");
            }
            Expect<StreamStart>(parser);
            if (!parser.MoveNext() || parser.Current is not DocumentStart)
            {
                throw new StorageFormatException("YAML_DOCUMENT", "Exactly one YAML document is required.");
            }

            if (!parser.MoveNext())
            {
                throw new StorageFormatException("YAML_ROOT", "YAML document has no root value.");
            }

            var root = ParseNode(parser, limits, depth: 0);
            if (root is not Dictionary<string, object?> mapping)
            {
                throw new StorageFormatException("YAML_ROOT_MAPPING", "Frontmatter root must be a mapping.");
            }

            if (parser.Current is not DocumentEnd)
            {
                if (!parser.MoveNext() || parser.Current is not DocumentEnd)
                {
                    throw new StorageFormatException("YAML_DOCUMENT", "Multiple YAML documents are not allowed.");
                }
            }

            if (!parser.MoveNext() || parser.Current is not StreamEnd)
            {
                throw new StorageFormatException("YAML_DOCUMENT", "Trailing YAML documents or directives are not allowed.");
            }

            return mapping;
        }
        catch (StorageFormatException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new StorageFormatException("YAML_INVALID", exception.Message, exception);
        }
    }

    private static object? ParseNode(IParser parser, StorageLimits limits, int depth)
    {
        if (depth > limits.MaxDepth)
        {
            throw new StorageFormatException("YAML_DEPTH", $"YAML nesting exceeds {limits.MaxDepth}.");
        }

        return parser.Current switch
        {
            MappingStart mapping => ParseMapping(parser, mapping, limits, depth),
            SequenceStart sequence => ParseSequence(parser, sequence, limits, depth),
            Scalar scalar => ParseScalar(parser, scalar, limits),
            AnchorAlias => throw new StorageFormatException("YAML_ALIAS", "YAML aliases are not allowed."),
            _ => throw new StorageFormatException("YAML_FEATURE", "Unsupported YAML event in strict frontmatter.")
        };
    }

    private static Dictionary<string, object?> ParseMapping(
        IParser parser,
        MappingStart mapping,
        StorageLimits limits,
        int depth)
    {
        RejectTagOrAnchor(mapping.Tag, mapping.Anchor);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (parser.MoveNext() && parser.Current is not MappingEnd)
        {
            if (parser.Current is not Scalar key || !IsAsciiKey(key.Value))
            {
                throw new StorageFormatException("YAML_KEY", "Mapping keys must be ASCII scalar strings.");
            }

            if (!result.TryAdd(key.Value, null))
            {
                throw new StorageFormatException("YAML_DUPLICATE_KEY", $"Duplicate YAML key '{key.Value}'.");
            }

            if (!parser.MoveNext())
            {
                throw new StorageFormatException("YAML_VALUE", "Mapping key has no value.");
            }

            result[key.Value] = ParseNode(parser, limits, depth + 1);
            if (result.Count > limits.MaxCollectionItems)
            {
                throw new StorageFormatException("YAML_MEMBER_LIMIT", "YAML mapping has too many members.");
            }
        }

        if (parser.Current is not MappingEnd)
        {
            throw new StorageFormatException("YAML_MAPPING", "Unterminated YAML mapping.");
        }

        return result;
    }

    private static List<object?> ParseSequence(IParser parser, SequenceStart sequence, StorageLimits limits, int depth)
    {
        RejectTagOrAnchor(sequence.Tag, sequence.Anchor);
        var result = new List<object?>();
        while (parser.MoveNext() && parser.Current is not SequenceEnd)
        {
            result.Add(ParseNode(parser, limits, depth + 1));
            if (result.Count > limits.MaxCollectionItems)
            {
                throw new StorageFormatException("YAML_SEQUENCE_LIMIT", "YAML sequence has too many items.");
            }
        }

        if (parser.Current is not SequenceEnd)
        {
            throw new StorageFormatException("YAML_SEQUENCE", "Unterminated YAML sequence.");
        }

        return result;
    }

    private static object ParseScalar(IParser parser, Scalar scalar, StorageLimits limits)
    {
        RejectTagOrAnchor(scalar.Tag, scalar.Anchor);
        if (scalar.Value.Length > limits.MaxStringLength)
        {
            throw new StorageFormatException("YAML_STRING_TOO_LARGE", "YAML scalar exceeds the configured limit.");
        }

        if (scalar.Style == ScalarStyle.Plain)
        {
            if (scalar.Value is "true" or "false")
            {
                return scalar.Value == "true";
            }

            if (AmbiguousPlainScalar.IsMatch(scalar.Value))
            {
                throw new StorageFormatException("YAML_AMBIGUOUS_SCALAR", "Ambiguous plain YAML scalar is not allowed.");
            }
        }

        return scalar.Value;
    }

    private static void RejectTagOrAnchor(TagName tag, AnchorName anchor)
    {
        if (!tag.IsEmpty || !anchor.IsEmpty)
        {
            throw new StorageFormatException("YAML_EXTENSION", "YAML tags and anchors are not allowed.");
        }
    }

    private static bool IsAsciiKey(string value)
    {
        return value.Length > 0 && value.All(character => character is >= (char)0x21 and <= (char)0x7E);
    }

    private static void Expect<T>(IParser parser)
        where T : ParsingEvent
    {
        if (parser.Current is not T)
        {
            throw new StorageFormatException("YAML_STREAM", $"Expected {typeof(T).Name}.");
        }
    }
}
