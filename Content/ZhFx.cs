using System.Text;

namespace RtsSkeleton.Content;

/// <summary>
/// Author a death effect: an <c>FXList</c>, the <c>ParticleSystem</c>s it fires, and the sprite
/// they draw with.
///
/// <para><b>Why this is the last one.</b> Every other content-art category is authored now —
/// mesh, texture, skeleton, animation, icons, terrain. FX was the hold-out, and it showed: a
/// unit on wholly authored art died <i>silently and invisibly</i>, because an adopted mesh
/// inherits its peer's death FX and an authored one inherits nothing. That was the last place
/// adopting retail art was structurally required rather than merely convenient.</para>
///
/// <para><b>The extraction sized the job before any of this was written.</b> `zhasset fx --stats`
/// measures 1,087 particle systems drawing on only <b>81 distinct textures</b> — small additive
/// sprites, reused everywhere. Authoring FX is therefore a handful of images and a template,
/// not a pipeline.</para>
///
/// <para><b>Three closed enums, checked against the C++ name tables and not against a grep.</b>
/// <c>Priority</c>, <c>Shader</c> and <c>Type</c> are parsed with <c>parseIndexList</c>, so an
/// unknown value is a hard load error — the fifth kind of literal that has cost this project a
/// boot. Reading the tables also turned up <c>SCORCHMARK</c> and <c>SMUDGE</c>, which are legal
/// and appear in no shipped INI: a grep would have under-counted the vocabulary, which is
/// exactly why the rule says the name table wins.</para>
/// </summary>
public static class ZhFx
{
    /// <summary><c>ParticlePriorityNames</c>, verbatim from <c>ParticleSys.h</c>.</summary>
    public static readonly string[] Priorities =
    {
        "NONE", "WEAPON_EXPLOSION", "SCORCHMARK", "DUST_TRAIL", "BUILDUP", "DEBRIS_TRAIL",
        "UNIT_DAMAGE_FX", "DEATH_EXPLOSION", "SEMI_CONSTANT", "CONSTANT", "WEAPON_TRAIL",
        "AREA_EFFECT", "CRITICAL", "ALWAYS_RENDER",
    };

    /// <summary><c>ParticleShaderTypeNames</c>, verbatim.</summary>
    public static readonly string[] Shaders = { "NONE", "ADDITIVE", "ALPHA", "ALPHA_TEST", "MULTIPLY" };

    /// <summary><c>ParticleTypeNames</c>, verbatim.</summary>
    public static readonly string[] Types =
    {
        "NONE", "PARTICLE", "DRAWABLE", "STREAK", "VOLUME_PARTICLE", "SMUDGE",
    };

    public sealed class Result
    {
        /// <summary>The ParticleSystem blocks. They go to <c>Data/INI/ParticleSystem/</c> and
        /// NOT beside the FXList: that is the directory the manager's own <c>init()</c> scans
        /// (measured from the boot log), and splitting them removes any question about which
        /// subsystem parses what and in which order.</summary>
        public readonly StringBuilder Systems = new();

        /// <summary>The FXList blocks, for <c>Data/INI/FXList/</c>.</summary>
        public readonly StringBuilder Ini = new();
        public readonly List<string> FxLists = new();
        public byte[] Sprite = Array.Empty<byte>();
        public string SpriteName = "";
    }

    /// <summary>
    /// One death effect per pack: a flash, a smoke puff and a shockwave, which is the shape
    /// every retail structure death uses. Per-pack rather than per-unit because the systems
    /// are reusable by construction — that is the whole lesson of 1,087 systems sharing 81
    /// textures, and a per-unit copy would multiply the INI for no visual difference.
    /// </summary>
    public static Result Write(string pack, string spriteTexture)
    {
        var r = new Result { SpriteName = spriteTexture };
        string P(string n) => $"{pack}_{n}";
        string fxName = P("DeathFX");
        r.FxLists.Add(fxName);

        // Field ORDER and SHAPE copied from a system that demonstrably loads; the values are
        // ours. The same discipline as the W3D template and the map's lighting chunk — the
        // layout has to come from something real, the content does not.
        void System(string name, string priority, string shader, double sizeLo, double sizeHi,
                    int lifetime, int burst, (int R, int G, int B) c0, (int R, int G, int B) c1)
        {
            r.Systems.AppendLine($"ParticleSystem {name}");
            r.Systems.AppendLine($"  Priority = {priority}");
            r.Systems.AppendLine("  IsOneShot = Yes");
            r.Systems.AppendLine($"  Shader = {shader}");
            r.Systems.AppendLine("  Type = PARTICLE");
            r.Systems.AppendLine($"  ParticleName = {spriteTexture}");
            r.Systems.AppendLine("  AngleZ = 0.00 6.28");
            r.Systems.AppendLine("  AngularRateZ = -0.05 0.05");
            r.Systems.AppendLine("  AngularDamping = 0.95 0.99");
            r.Systems.AppendLine("  VelocityDamping = 0.80 0.90");
            r.Systems.AppendLine("  Gravity = 0.00");
            r.Systems.AppendLine($"  Lifetime = {lifetime}.00 {lifetime}.00");
            r.Systems.AppendLine("  SystemLifetime = 1");
            r.Systems.AppendLine($"  Size = {sizeLo:0.00} {sizeHi:0.00}");
            r.Systems.AppendLine("  StartSizeRate = 0.00 0.00");
            r.Systems.AppendLine("  SizeRate = 0.60 1.20");
            r.Systems.AppendLine("  SizeRateDamping = 0.96 0.99");
            // Alpha ramps in then out. The trailing integer is the KEYFRAME, so the ramp is
            // expressed as time points and not as a curve.
            r.Systems.AppendLine("  Alpha1 = 0.90 1.00 0");
            r.Systems.AppendLine($"  Alpha2 = 0.00 0.00 {lifetime}");
            r.Systems.AppendLine($"  Color1 = R:{c0.R} G:{c0.G} B:{c0.B} 0");
            r.Systems.AppendLine($"  Color2 = R:{c1.R} G:{c1.G} B:{c1.B} {lifetime}");
            r.Systems.AppendLine("  ColorScale = 0.00 0.00");
            r.Systems.AppendLine("  BurstDelay = 0.00 0.00");
            r.Systems.AppendLine($"  BurstCount = {burst}.00 {burst}.00");
            r.Systems.AppendLine("  InitialDelay = 0.00 0.00");
            r.Systems.AppendLine("  DriftVelocity = X:0.00 Y:0.00 Z:0.05");
            r.Systems.AppendLine("  VelocityType = OUTWARD");
            r.Systems.AppendLine("  VelOutward = 0.60 1.40");
            r.Systems.AppendLine("  VolumeType = SPHERE");
            r.Systems.AppendLine("  VolSphereRadius = 6.00");
            r.Systems.AppendLine("  IsHollow = No");
            r.Systems.AppendLine("  IsGroundAligned = No");
            r.Systems.AppendLine("End").AppendLine();
        }

        System(P("DeathFlash"), "DEATH_EXPLOSION", "ADDITIVE", 22, 34, 12, 5, (255, 230, 150), (255, 90, 20));
        System(P("DeathSmoke"), "DEATH_EXPLOSION", "ALPHA", 18, 30, 55, 8, (70, 66, 62), (24, 22, 20));
        System(P("DeathSpark"), "DEBRIS_TRAIL", "ADDITIVE", 3, 6, 30, 14, (255, 200, 120), (180, 60, 10));

        r.Ini.AppendLine($"FXList {fxName}");
        foreach (var n in new[] { "DeathFlash", "DeathSmoke", "DeathSpark" })
        {
            r.Ini.AppendLine("  ParticleSystem");
            r.Ini.AppendLine($"    Name = {P(n)}");
            r.Ini.AppendLine("  End");
        }
        r.Ini.AppendLine("End").AppendLine();

        r.Sprite = Sprite(64);
        return r;
    }

    /// <summary>
    /// A soft radial blob: white in the middle, falling to black at the rim.
    ///
    /// <para>Black is the correct "empty" for an ADDITIVE sprite — adding zero changes nothing,
    /// so the square edges of the texture disappear. Using transparent-black-with-alpha instead
    /// would be the instinct from ordinary compositing and would leave a visible box, because
    /// additive blending ignores the alpha channel.</para>
    ///
    /// <para>The falloff is squared rather than linear: a linear ramp reads as a flat disc with
    /// a hard edge, and a squared one reads as a glow.</para>
    /// </summary>
    public static byte[] Sprite(int size)
    {
        var px = new byte[size * size * 3];
        double c = (size - 1) / 2.0, rmax = c;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / rmax;
                double v = Math.Clamp(1.0 - d, 0.0, 1.0);
                byte l = (byte)Math.Round(255 * v * v);
                int o = ((size - 1 - y) * size + x) * 3;      // TGA rows run bottom-up
                px[o] = px[o + 1] = px[o + 2] = l;
            }

        var tga = new byte[18 + px.Length];
        tga[2] = 2;                                            // uncompressed true-colour
        BitConverter.GetBytes((ushort)size).CopyTo(tga, 12);
        BitConverter.GetBytes((ushort)size).CopyTo(tga, 14);
        tga[16] = 24;
        px.CopyTo(tga, 18);
        return tga;
    }
}
