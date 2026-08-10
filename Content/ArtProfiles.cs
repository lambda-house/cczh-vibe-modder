using System.Text.Json;

namespace RtsSkeleton.Content;

/// <summary>
/// Measured contracts for adoptable meshes, keyed by MODEL name.
///
/// A model renders for free; it does not BEHAVE for free. Dimensions, weapon bones, the
/// muzzle-flash sub-object and whether it carries a turret all have to be declared, and every
/// one of them fails SILENTLY — the pack lints, compiles, boots 42/42 and plays wrong.
/// Adopting a single mesh hit all four: units created inside a building, a permanent flame
/// welded to a gun, and a tank that never fired.
///
/// The profile belongs to the MESH, not to our unit, so it is measured once from the objects
/// that already use it (<c>tools/zhasset artprofile</c>) rather than hand-copied per unit.
///
/// GENERATED LOCALLY, NEVER COMMITTED. The catalogue is derived from EA's <c>Data/INI</c>,
/// which stays theirs; <c>reference/</c> is gitignored. Ship the extractor, never the extract.
/// Its absence is therefore normal — the compiler falls back to defaults and lint says so.
///
/// The sharp edge this exposes: retail's 2,928 UNREFERENCED meshes are "free to adopt"
/// precisely because no object uses them — which is the same reason there is no profile to
/// measure. Free of conflicts and free of guidance are the same fact.
/// </summary>
public sealed class ArtProfiles
{
    public sealed class Profile
    {
        public string? Geometry { get; set; }
        public string? MajorRadius { get; set; }
        public string? MinorRadius { get; set; }
        public string? Height { get; set; }
        public bool Turret { get; set; }
        public string? LaunchBone { get; set; }
        public string? FireFXBone { get; set; }
        public string? MuzzleFlash { get; set; }
        public string? UnitCreatePoint { get; set; }
        public string? NaturalRallyPoint { get; set; }
        public string? Kind { get; set; }
        public List<string>? UsedBy { get; set; }
    }

    private readonly Dictionary<string, Profile> _byModel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many meshes the catalogue describes. 0 means none was loaded.</summary>
    public int Count => _byModel.Count;

    public bool TryGet(string model, out Profile profile) => _byModel.TryGetValue(model, out profile!);

    /// <summary>
    /// Load a catalogue, or return an empty one if the file is absent. Absence is a normal
    /// state, not an error: the catalogue is generated from a local retail install that a
    /// given checkout may not have.
    /// </summary>
    public static ArtProfiles Load(string? path)
    {
        var a = new ArtProfiles();
        if (path is null || !File.Exists(path)) return a;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("models", out var models)) return a;

        foreach (var m in models.EnumerateObject())
        {
            string? S(string k) => m.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                                   ? v.GetString() : null;
            a._byModel[m.Name] = new Profile
            {
                Geometry = S("geometry"),
                MajorRadius = S("majorRadius"),
                MinorRadius = S("minorRadius"),
                Height = S("height"),
                Turret = m.Value.TryGetProperty("turret", out var t) && t.ValueKind == JsonValueKind.True,
                LaunchBone = S("launchBone"),
                FireFXBone = S("fireFXBone"),
                MuzzleFlash = S("muzzleFlash"),
                UnitCreatePoint = S("unitCreatePoint"),
                NaturalRallyPoint = S("naturalRallyPoint"),
                Kind = S("kind"),
            };
        }
        return a;
    }

    /// <summary>
    /// Their point fields read "X: -10.0  Y:-30.0   Z:0.0" with irregular spacing. Normalise
    /// so emitted output is stable regardless of how the source happened to be typed.
    /// </summary>
    public static string NormalisePoint(string raw)
    {
        var parts = raw.Replace(" ", "").Split(new[] { "X:", "Y:", "Z:" }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"X:{parts[0]} Y:{parts[1]} Z:{parts[2]}" : raw;
    }
}
