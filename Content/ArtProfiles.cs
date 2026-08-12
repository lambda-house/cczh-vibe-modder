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
        /// <summary>Art-side bone to rotate. Distinct from <see cref="Turret"/>, which is the
        /// LOGIC turret: declaring only that gives a unit which aims and fires with a welded
        /// turret, because nothing tells the renderer which bone to spin.</summary>
        public string? TurretBone { get; set; }
        public string? LaunchBone { get; set; }
        public string? FireFXBone { get; set; }
        public string? MuzzleFlash { get; set; }
        /// <summary>Default-death presentation, measured from the peer's ALL-deathtype block.
        /// Mesh-specific by nature: OCL_CrusaderTurret throws THAT tank's turret.</summary>
        public string? DeathFX { get; set; }
        public string? DeathOCL { get; set; }
        public string? DeathFXFinal { get; set; }
        public string? DeathOCLFinal { get; set; }
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
    /// <summary>
    /// Load, then fold in any profile catalogues sitting beside a pack's own art.
    ///
    /// <para>Profiles are MEASURED for adopted retail art and DECLARED for authored art, and
    /// the two live in different places: the measured catalogue is generated once into
    /// <c>reference/</c>, while <c>zhasset models</c> writes a declared one beside every mesh
    /// it builds. Loading only the first means an authored model has no profile at all — the
    /// compiler falls back to guessed geometry and emits no turret, no launch bone and no
    /// muzzle flash, so a correctly rigged model arrives completely static.</para>
    ///
    /// <para>Later files win, so a pack's declaration overrides a measurement of the same
    /// name — which is the right way round: we chose those dimensions.</para>
    /// </summary>
    public static ArtProfiles Load(string? path, IEnumerable<string>? alsoBeside = null)
    {
        var a = new ArtProfiles();
        foreach (var p in Paths(path, alsoBeside)) a.Merge(p);
        return a;
    }

    private static IEnumerable<string> Paths(string? path, IEnumerable<string>? alsoBeside)
    {
        if (path is not null) yield return path;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var art in alsoBeside ?? Enumerable.Empty<string>())
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(art));
            if (dir is null || !seen.Add(dir)) continue;
            yield return Path.Combine(dir, "art-profiles.json");
        }
    }

    private void Merge(string? path)
    {
        var a = this;
        if (path is null || !File.Exists(path)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("models", out var models)) return;

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
                TurretBone = S("turretBone"),
                LaunchBone = S("launchBone"),
                FireFXBone = S("fireFXBone"),
                MuzzleFlash = S("muzzleFlash"),
                DeathFX = S("deathFX"),
                DeathOCL = S("deathOCL"),
                DeathFXFinal = S("deathFXFinal"),
                DeathOCLFinal = S("deathOCLFinal"),
                UnitCreatePoint = S("unitCreatePoint"),
                NaturalRallyPoint = S("naturalRallyPoint"),
                Kind = S("kind"),
            };
        }
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
