using System.Globalization;

namespace AcadCopilot.Core.Services;

/// <summary>One DXF group code / value pair.</summary>
public readonly record struct DxfPair(int Code, string Value);

/// <summary>A DXF entity (or table entry) with its pairs; complex entities carry their VERTEX / ATTRIB children.</summary>
public sealed class DxfEntity
{
    /// <summary>Creates an entity of <paramref name="type"/>.</summary>
    public DxfEntity(string type)
    {
        Type = type;
    }

    /// <summary>Entity type (LINE, INSERT, ATTDEF ...).</summary>
    public string Type { get; }

    /// <summary>Pairs after the 0 / type pair, in file order.</summary>
    public List<DxfPair> Pairs { get; } = [];

    /// <summary>VERTEX (POLYLINE) or ATTRIB (INSERT) children, without the SEQEND.</summary>
    public List<DxfEntity> Children { get; } = [];

    /// <summary>True when a SEQEND closed the children.</summary>
    public bool HasSeqEnd { get; set; }

    /// <summary>Layer (group 8), "0" when absent.</summary>
    public string Layer => Get(8) ?? "0";

    /// <summary>First value of <paramref name="code"/>, or null.</summary>
    public string? Get(int code)
    {
        foreach (var pair in Pairs)
        {
            if (pair.Code == code)
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>All values of <paramref name="code"/>.</summary>
    public IEnumerable<string> GetAll(int code) => Pairs.Where(p => p.Code == code).Select(p => p.Value);

    /// <summary>First value of <paramref name="code"/> as a number, or <paramref name="fallback"/>.</summary>
    public double GetDouble(int code, double fallback = 0) =>
        Get(code) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>First value of <paramref name="code"/> as an integer, or <paramref name="fallback"/>.</summary>
    public int GetInt(int code, int fallback = 0) =>
        Get(code) is { } text && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

/// <summary>A block definition read from the BLOCKS section.</summary>
public sealed class DxfBlock
{
    /// <summary>Creates a block.</summary>
    public DxfBlock(DxfEntity header)
    {
        Header = header;
    }

    /// <summary>The BLOCK entity (name 2, flags 70, base point 10/20/30).</summary>
    public DxfEntity Header { get; }

    /// <summary>Block name.</summary>
    public string Name => Header.Get(2) ?? string.Empty;

    /// <summary>Block flags (2 = has attribute definitions).</summary>
    public int Flags => Header.GetInt(70);

    /// <summary>The block's entities.</summary>
    public List<DxfEntity> Entities { get; } = [];

    /// <summary>True when ENDBLK closed the block.</summary>
    public bool Closed { get; set; }
}

/// <summary>A parsed DXF file (the parts the services library uses) and the problems found while reading it.</summary>
public sealed class DxfFile
{
    /// <summary>Header variables ($ACADVER ...) with their first value.</summary>
    public Dictionary<string, string> Header { get; } = new(StringComparer.Ordinal);

    /// <summary>Section names in file order.</summary>
    public List<string> Sections { get; } = [];

    /// <summary>Table entries by table name (LAYER, LTYPE, STYLE ...).</summary>
    public Dictionary<string, List<DxfEntity>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Block definitions in file order.</summary>
    public List<DxfBlock> Blocks { get; } = [];

    /// <summary>ENTITIES section.</summary>
    public List<DxfEntity> Entities { get; } = [];

    /// <summary>True when the file ends with 0 / EOF.</summary>
    public bool HasEof { get; set; }

    /// <summary>Problems in the structure itself (pairs, sections, unclosed blocks).</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Entries of table <paramref name="name"/> (empty when absent).</summary>
    public IReadOnlyList<DxfEntity> Table(string name) => Tables.TryGetValue(name, out var list) ? list : [];

    /// <summary>The block named <paramref name="name"/> (any case), or null.</summary>
    public DxfBlock? Block(string name) => Blocks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A small DXF reader (ASCII DXF, any version) for checking generated files: group code pairs, sections, tables,
/// blocks with their entities, entities, and POLYLINE / INSERT children up to SEQEND. <see cref="Validate"/> checks
/// the structure AutoCAD relies on when it reads blocks with attributes.
/// </summary>
public static class DxfReader
{
    /// <summary>Reads the pairs of <paramref name="text"/>; a broken pair is a problem in <paramref name="problems"/>.</summary>
    public static List<DxfPair> Pairs(string text, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
        {
            count--; // final line end
        }

        var pairs = new List<DxfPair>(count / 2);
        if (count % 2 != 0)
        {
            problems?.Add($"Odd number of lines ({count}): every group code needs a value line.");
            count--;
        }

        for (var i = 0; i < count; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                problems?.Add($"Line {i + 1}: '{lines[i]}' is not a group code.");
                return pairs;
            }

            pairs.Add(new DxfPair(code, lines[i + 1]));
        }

        return pairs;
    }

    /// <summary>Parses <paramref name="text"/>.</summary>
    public static DxfFile Parse(string text)
    {
        var file = new DxfFile();
        var pairs = Pairs(text, file.Problems);
        var i = 0;
        while (i < pairs.Count)
        {
            var pair = pairs[i];
            if (pair.Code == 0 && pair.Value == "EOF")
            {
                file.HasEof = true;
                if (i != pairs.Count - 1)
                {
                    file.Problems.Add("Data after EOF.");
                }

                break;
            }

            if (pair.Code != 0 || pair.Value != "SECTION" || i + 1 >= pairs.Count || pairs[i + 1].Code != 2)
            {
                file.Problems.Add($"Pair {i}: expected 0/SECTION followed by 2/<name>, found {pair.Code}/{pair.Value}.");
                return file;
            }

            var name = pairs[i + 1].Value;
            file.Sections.Add(name);
            var end = pairs.FindIndex(i + 2, p => p.Code == 0 && p.Value == "ENDSEC");
            if (end < 0)
            {
                file.Problems.Add($"Section {name} has no ENDSEC.");
                return file;
            }

            var body = pairs.GetRange(i + 2, end - i - 2);
            switch (name)
            {
                case "HEADER":
                    ReadHeader(body, file);
                    break;
                case "TABLES":
                    ReadTables(body, file);
                    break;
                case "BLOCKS":
                    ReadBlocks(body, file);
                    break;
                case "ENTITIES":
                    file.Entities.AddRange(ReadEntities(body, file, "ENTITIES"));
                    break;
            }

            i = end + 1;
        }

        if (!file.HasEof)
        {
            file.Problems.Add("The file does not end with 0/EOF.");
        }

        return file;
    }

    /// <summary>
    /// Every problem AutoCAD would trip over when reading blocks with attributes: structure, numbers, block names,
    /// BLOCK flag 2 iff ATTDEFs, ATTDEF tags, INSERTs of defined blocks with ATTRIBs matching the ATTDEF tags and a
    /// SEQEND, POLYLINEs with VERTEXes and SEQEND, layers and linetypes defined in the tables. Empty = valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(string text)
    {
        var file = Parse(text);
        var problems = new List<string>(file.Problems);
        foreach (var required in new[] { "HEADER", "TABLES", "BLOCKS", "ENTITIES" })
        {
            if (!file.Sections.Contains(required))
            {
                problems.Add($"Section {required} is missing.");
            }
        }

        var linetypes = new HashSet<string>(file.Table("LTYPE").Select(e => e.Get(2) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var layers = new HashSet<string>(file.Table("LAYER").Select(e => e.Get(2) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var styles = new HashSet<string>(file.Table("STYLE").Select(e => e.Get(2) ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        foreach (var layer in file.Table("LAYER"))
        {
            var linetype = layer.Get(6) ?? "CONTINUOUS";
            if (!linetypes.Contains(linetype))
            {
                problems.Add($"Layer {layer.Get(2)} uses linetype {linetype}, which is not in the LTYPE table.");
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in file.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Name))
            {
                problems.Add("A BLOCK has no name.");
                continue;
            }

            if (!names.Add(block.Name))
            {
                problems.Add($"Block {block.Name} is defined twice.");
            }

            if (!block.Closed)
            {
                problems.Add($"Block {block.Name} has no ENDBLK.");
            }

            if (block.Entities.Count == 0)
            {
                problems.Add($"Block {block.Name} is empty.");
            }

            var attdefs = block.Entities.Where(e => e.Type == "ATTDEF").ToList();
            if (((block.Flags & 2) != 0) != (attdefs.Count > 0))
            {
                problems.Add($"Block {block.Name}: flag 2 (has attributes) is {((block.Flags & 2) != 0 ? "set" : "clear")} but it has {attdefs.Count} ATTDEF(s).");
            }

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attdef in attdefs)
            {
                var tag = attdef.Get(2);
                if (string.IsNullOrWhiteSpace(tag) || tag.Contains(' ', StringComparison.Ordinal))
                {
                    problems.Add($"Block {block.Name}: an ATTDEF has no valid tag ('{tag}').");
                }
                else if (!tags.Add(tag))
                {
                    problems.Add($"Block {block.Name}: ATTDEF tag {tag} is used twice.");
                }

                if (attdef.Get(3) is null || attdef.Get(1) is null || attdef.Get(40) is null)
                {
                    problems.Add($"Block {block.Name}: ATTDEF {tag} needs a prompt (3), a default (1) and a height (40).");
                }
            }

            foreach (var entity in block.Entities)
            {
                CheckEntity(entity, $"block {block.Name}", file, layers, styles, problems);
            }
        }

        foreach (var entity in file.Entities)
        {
            CheckEntity(entity, "ENTITIES", file, layers, styles, problems);
        }

        return problems;
    }

    private static void CheckEntity(DxfEntity entity, string where, DxfFile file, HashSet<string> layers, HashSet<string> styles, List<string> problems)
    {
        foreach (var pair in entity.Pairs.Concat(entity.Children.SelectMany(c => c.Pairs)))
        {
            if (pair.Code is >= 10 and <= 59 &&
                !double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                problems.Add($"{where}: {entity.Type} group {pair.Code} is not a number: '{pair.Value}'.");
            }
        }

        if (!layers.Contains(entity.Layer))
        {
            problems.Add($"{where}: {entity.Type} is on layer {entity.Layer}, which is not in the LAYER table.");
        }

        if (entity.Type is "TEXT" or "ATTDEF" or "ATTRIB" && entity.Get(7) is { } style && !styles.Contains(style))
        {
            problems.Add($"{where}: {entity.Type} uses text style {style}, which is not in the STYLE table.");
        }

        switch (entity.Type)
        {
            case "POLYLINE":
                if (!entity.HasSeqEnd || entity.Children.Count < 2 || entity.Children.Any(c => c.Type != "VERTEX"))
                {
                    problems.Add($"{where}: a POLYLINE needs at least 2 VERTEX entities and a SEQEND.");
                }

                break;
            case "INSERT":
                var name = entity.Get(2) ?? string.Empty;
                var block = file.Block(name);
                if (block is null)
                {
                    problems.Add($"{where}: INSERT of {name}, which is not defined in BLOCKS.");
                    break;
                }

                var attributesFollow = entity.GetInt(66) == 1;
                var tags = block.Entities.Where(e => e.Type == "ATTDEF").Select(e => e.Get(2) ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (attributesFollow != (entity.Children.Count > 0) || (attributesFollow && !entity.HasSeqEnd))
                {
                    problems.Add($"{where}: INSERT of {name}: 66=1 must be followed by ATTRIB entities and a SEQEND (and only then).");
                }

                foreach (var attrib in entity.Children)
                {
                    if (attrib.Type != "ATTRIB")
                    {
                        problems.Add($"{where}: INSERT of {name} is followed by {attrib.Type} instead of ATTRIB.");
                    }
                    else if (!tags.Contains(attrib.Get(2) ?? string.Empty))
                    {
                        problems.Add($"{where}: INSERT of {name} has ATTRIB {attrib.Get(2)}, which its block does not define.");
                    }
                }

                if (attributesFollow && entity.Children.Count != tags.Count)
                {
                    problems.Add($"{where}: INSERT of {name} has {entity.Children.Count} ATTRIB(s) for {tags.Count} ATTDEF(s).");
                }

                break;
            case "ATTRIB" or "VERTEX" or "SEQEND":
                problems.Add($"{where}: stray {entity.Type} outside a POLYLINE / INSERT.");
                break;
        }
    }

    private static void ReadHeader(List<DxfPair> body, DxfFile file)
    {
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Code == 9 && i + 1 < body.Count && !file.Header.ContainsKey(body[i].Value))
            {
                file.Header[body[i].Value] = body[i + 1].Value;
            }
        }
    }

    private static void ReadTables(List<DxfPair> body, DxfFile file)
    {
        var i = 0;
        while (i < body.Count)
        {
            if (body[i].Code != 0 || body[i].Value != "TABLE" || i + 1 >= body.Count || body[i + 1].Code != 2)
            {
                file.Problems.Add($"TABLES: expected 0/TABLE and 2/<name>, found {body[i].Code}/{body[i].Value}.");
                return;
            }

            var name = body[i + 1].Value;
            var end = body.FindIndex(i, p => p.Code == 0 && p.Value == "ENDTAB");
            if (end < 0)
            {
                file.Problems.Add($"Table {name} has no ENDTAB.");
                return;
            }

            var entries = new List<DxfEntity>();
            DxfEntity? current = null;
            for (var j = i + 2; j < end; j++)
            {
                if (body[j].Code == 0)
                {
                    current = new DxfEntity(body[j].Value);
                    entries.Add(current);
                }
                else
                {
                    current?.Pairs.Add(body[j]);
                }
            }

            file.Tables[name] = entries;
            i = end + 1;
        }
    }

    private static void ReadBlocks(List<DxfPair> body, DxfFile file)
    {
        var i = 0;
        while (i < body.Count)
        {
            if (body[i].Code != 0 || body[i].Value != "BLOCK")
            {
                file.Problems.Add($"BLOCKS: expected 0/BLOCK, found {body[i].Code}/{body[i].Value}.");
                return;
            }

            var header = new DxfEntity("BLOCK");
            var j = i + 1;
            while (j < body.Count && body[j].Code != 0)
            {
                header.Pairs.Add(body[j++]);
            }

            var block = new DxfBlock(header);
            var end = body.FindIndex(j, p => p.Code == 0 && p.Value == "ENDBLK");
            if (end < 0)
            {
                file.Blocks.Add(block);
                return;
            }

            block.Entities.AddRange(ReadEntities(body.GetRange(j, end - j), file, "block " + block.Name));
            block.Closed = true;
            file.Blocks.Add(block);
            i = end + 1;
            while (i < body.Count && body[i].Code != 0)
            {
                i++; // ENDBLK's own pairs
            }
        }
    }

    private static List<DxfEntity> ReadEntities(List<DxfPair> body, DxfFile file, string where)
    {
        var flat = new List<DxfEntity>();
        DxfEntity? current = null;
        foreach (var pair in body)
        {
            if (pair.Code == 0)
            {
                current = new DxfEntity(pair.Value);
                flat.Add(current);
            }
            else if (current is null)
            {
                file.Problems.Add($"{where}: group {pair.Code} before the first entity.");
            }
            else
            {
                current.Pairs.Add(pair);
            }
        }

        // Group VERTEX / ATTRIB children under their POLYLINE / INSERT up to SEQEND.
        var result = new List<DxfEntity>();
        for (var k = 0; k < flat.Count; k++)
        {
            var entity = flat[k];
            result.Add(entity);
            var complex = entity.Type == "POLYLINE" || (entity.Type == "INSERT" && entity.GetInt(66) == 1);
            if (!complex)
            {
                continue;
            }

            var child = entity.Type == "POLYLINE" ? "VERTEX" : "ATTRIB";
            while (k + 1 < flat.Count && flat[k + 1].Type == child)
            {
                entity.Children.Add(flat[++k]);
            }

            if (k + 1 < flat.Count && flat[k + 1].Type == "SEQEND")
            {
                entity.HasSeqEnd = true;
                k++;
            }
        }

        return result;
    }
}
