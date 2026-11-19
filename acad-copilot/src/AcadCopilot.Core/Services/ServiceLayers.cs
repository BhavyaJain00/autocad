namespace AcadCopilot.Core.Services;

/// <summary>Which building service a symbol or layer belongs to.</summary>
public enum ServiceDiscipline
{
    /// <summary>Electrical layout (lights, fans, sockets, switchboards, DB, earthing).</summary>
    Electrical,

    /// <summary>Plumbing layout (fixtures, traps, water supply, drainage).</summary>
    Plumbing,
}

/// <summary>A standard layer of the services drawings.</summary>
/// <param name="Name">Layer name (E-LITE ...).</param>
/// <param name="ColorIndex">AutoCAD colour index (ACI 1-255).</param>
/// <param name="Linetype">Linetype name (CONTINUOUS, DASHED ...).</param>
/// <param name="Lineweight">Lineweight in hundredths of a millimetre (25 = 0.25 mm), as AutoCAD's LineWeight enum.</param>
/// <param name="Description">What goes on the layer.</param>
/// <param name="Discipline">Electrical or plumbing.</param>
public sealed record ServiceLayer(string Name, short ColorIndex, string Linetype, int Lineweight, string Description, ServiceDiscipline Discipline);

/// <summary>
/// The layers of the electrical and plumbing first-draft layouts (pure data, shared by the symbol library DXF, the
/// insert_symbol_library tool and the knowledge recipes).
/// </summary>
public static class ServiceLayers
{
    /// <summary>Light points, fans, exhaust fans.</summary>
    public const string Lighting = "E-LITE";

    /// <summary>Sockets, AC points, geyser points, DB, earthing.</summary>
    public const string Power = "E-POWR";

    /// <summary>Switchboards.</summary>
    public const string Switches = "E-SWCH";

    /// <summary>Electrical legend, notes and labels.</summary>
    public const string ElectricalNotes = "E-ANNO";

    /// <summary>Water supply (inlet) pipes and tanks.</summary>
    public const string Water = "P-WATR";

    /// <summary>Drainage (outlet) pipes, stacks and inspection chambers.</summary>
    public const string Drainage = "P-DRAN";

    /// <summary>Sanitary fixtures, taps and traps.</summary>
    public const string Fixtures = "P-FIXT";

    /// <summary>Plumbing legend, notes and labels.</summary>
    public const string PlumbingNotes = "P-ANNO";

    /// <summary>Every services layer, electrical first.</summary>
    public static IReadOnlyList<ServiceLayer> All { get; } =
    [
        new(Lighting, 30, "CONTINUOUS", 25, "Electrical: light points, fans, exhaust fans and their switch wiring", ServiceDiscipline.Electrical),
        new(Power, 1, "CONTINUOUS", 25, "Electrical: 5A/15A sockets, AC and geyser points, DB, earthing", ServiceDiscipline.Electrical),
        new(Switches, 6, "CONTINUOUS", 25, "Electrical: switchboards", ServiceDiscipline.Electrical),
        new(ElectricalNotes, 7, "CONTINUOUS", 18, "Electrical: legend, notes, circuit labels", ServiceDiscipline.Electrical),
        new(Water, 150, "CONTINUOUS", 30, "Plumbing: water supply (inlet) pipes, tanks", ServiceDiscipline.Plumbing),
        new(Drainage, 32, "DASHED", 30, "Plumbing: drainage (outlet) pipes, stacks, inspection chambers", ServiceDiscipline.Plumbing),
        new(Fixtures, 8, "CONTINUOUS", 18, "Plumbing: WC, wash basin, taps, floor and nahani traps", ServiceDiscipline.Plumbing),
        new(PlumbingNotes, 7, "CONTINUOUS", 18, "Plumbing: legend, notes, slopes, pipe sizes", ServiceDiscipline.Plumbing),
    ];

    /// <summary>The layer named <paramref name="name"/> (any case), or null.</summary>
    public static ServiceLayer? Find(string? name) =>
        All.FirstOrDefault(l => string.Equals(l.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The layers of one discipline.</summary>
    public static IReadOnlyList<ServiceLayer> Of(ServiceDiscipline discipline) => [.. All.Where(l => l.Discipline == discipline)];

    /// <summary>The notes / legend layer of a discipline.</summary>
    public static string NotesLayer(ServiceDiscipline discipline) =>
        discipline == ServiceDiscipline.Electrical ? ElectricalNotes : PlumbingNotes;
}
