using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace PyCompat;

/// <summary>A settings file as a TomlTable, read as tomllib reads it (moved here from cover-rota, 13 Nov 2026, so every tool reads its settings the same way). Tomlyn 2.11.1's TomlSerializer gets two things
/// wrong when it builds a TomlTable (found by the parity fuzz, 13 Nov 2026): <b>an array of tables opened again after
/// another table (<c>[[squad]]</c>, <c>[leave]</c>, <c>[[squad]]</c>) keeps only the entries after the reopening</b>,
/// and a key given twice is taken as the last value, where TOML (and tomllib) refuse the file. So the file is parsed
/// strictly (Tomlyn's syntax parser, which reports duplicates and every other TOML error), and the table is built from
/// the syntax tree here.</summary>
public static class PyToml
{
    public static TomlTable Read(string text, string source)
    {
        var doc = SyntaxParser.ParseStrict(text, source, true);
        var root = new TomlTable();
        foreach (var kv in doc.KeyValues)
            Assign(root, kv);
        foreach (var table in doc.Tables)
        {
            var keys = Keys(table.Name!);
            var parent = Walk(root, keys[..^1]);
            var last = keys[^1];
            TomlTable current;
            if (table is TableArraySyntax)
            {
                if (!parent.TryGetValue(last, out var existing))
                    parent[last] = existing = new TomlTableArray();
                var array = existing as TomlTableArray ?? throw new TomlException($"{source}: {string.Join('.', keys)} is already defined as a value");
                current = [];
                array.Add(current);
            }
            else
            {
                if (!parent.TryGetValue(last, out var existing))
                    parent[last] = existing = new TomlTable();
                current = existing switch
                {
                    TomlTable t => t,
                    TomlTableArray a when a.Count > 0 => a[^1],
                    _ => throw new TomlException($"{source}: {string.Join('.', keys)} is already defined as a value"),
                };
            }
            foreach (var kv in table.Items)
                Assign(current, kv);
        }
        return root;
    }

    /// <summary>The table at a path of keys, made where missing; an array of tables on the way means its last table.</summary>
    private static TomlTable Walk(TomlTable table, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!table.TryGetValue(key, out var next))
                table[key] = next = new TomlTable();
            table = next switch
            {
                TomlTable t => t,
                TomlTableArray a when a.Count > 0 => a[^1],
                _ => throw new TomlException($"{key} is already defined as a value"),
            };
        }
        return table;
    }

    private static void Assign(TomlTable table, KeyValueSyntax kv)
    {
        var keys = Keys(kv.Key!);
        Walk(table, keys[..^1])[keys[^1]] = Value(kv.Value!);
    }

    private static string[] Keys(KeySyntax key)
    {
        var keys = new List<string> { Name(key.Key!) };
        foreach (var dotted in key.DotKeys)
            keys.Add(Name(dotted.Key!));
        return [.. keys];
    }

    private static string Name(BareKeyOrStringValueSyntax key) => key switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax s => s.Value!,
        _ => throw new TomlException($"A key of a kind this reader doesn't know: {key.GetType().Name}"),
    };

    private static object Value(ValueSyntax value) => value switch
    {
        StringValueSyntax s => s.Value!,
        IntegerValueSyntax i => i.Value,
        FloatValueSyntax f => f.Value,
        BooleanValueSyntax b => b.Value,
        DateTimeValueSyntax d => d.Value,
        ArraySyntax a => Array(a),
        InlineTableSyntax t => Inline(t),
        _ => throw new TomlException($"A value of a kind this reader doesn't know: {value.GetType().Name}"),
    };

    private static TomlArray Array(ArraySyntax syntax)
    {
        var array = new TomlArray();
        foreach (var item in syntax.Items)
            array.Add(Value(item.Value!));
        return array;
    }

    private static TomlTable Inline(InlineTableSyntax syntax)
    {
        var table = new TomlTable();
        foreach (var item in syntax.Items)
            Assign(table, item.KeyValue!);
        return table;
    }
}
