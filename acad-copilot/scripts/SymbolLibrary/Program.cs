using System.Text;
using AcadCopilot.Core.Services;

// Writes the services symbol library DXF and validates it by reading it back.
// Usage: SymbolLibraryGen <output .dxf>
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SymbolLibraryGen <output .dxf>");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var text = SymbolLibraryDxf.Write();
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
File.WriteAllText(path, text, Encoding.ASCII);

var problems = DxfReader.Validate(File.ReadAllText(path, Encoding.ASCII));
var file = DxfReader.Parse(File.ReadAllText(path, Encoding.ASCII));
Console.WriteLine($"{path}: {new FileInfo(path).Length} bytes, {file.Blocks.Count} blocks, {file.Entities.Count(e => e.Type == "INSERT")} catalogue inserts, layers {string.Join(", ", file.Table("LAYER").Select(l => l.Get(2)))}");
if (problems.Count > 0)
{
    foreach (var problem in problems)
    {
        Console.Error.WriteLine("INVALID: " + problem);
    }

    return 1;
}

Console.WriteLine("Valid: every block, ATTDEF, INSERT/ATTRIB/SEQEND, layer and linetype checks out.");
return 0;
