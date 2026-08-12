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

        /// <summary>A soft 32-bit cloud, for the systems that blend with <c>Shader = ALPHA</c>
        /// rather than <c>ADDITIVE</c>. The two need genuinely different textures and sharing
        /// one is not a saving — see <c>CloudSprite</c>.</summary>
        public byte[] Cloud = Array.Empty<byte>();
        public string CloudName = "";

        /// <summary>The tread-dust pair, left then right. Named so the compiler can hand them
        /// to <c>W3DTankDraw</c>'s <c>TreadDebrisLeft</c> / <c>TreadDebrisRight</c> and displace
        /// the EA-authored systems its C++ constructor would otherwise supply.</summary>
        public string TreadDustLeft = "";
        public string TreadDustRight = "";
    }

    /// <summary>
    /// One death effect per pack: a flash, a smoke puff and a shockwave, which is the shape
    /// every retail structure death uses. Per-pack rather than per-unit because the systems
    /// are reusable by construction — that is the whole lesson of 1,087 systems sharing 81
    /// textures, and a per-unit copy would multiply the INI for no visual difference.
    /// </summary>
    public static Result Write(string pack, string spriteTexture, string? deathSound = null)
    {
        var r = new Result
        {
            SpriteName = spriteTexture,
            // The ALPHA systems need their OWN texture and cannot share the additive one. An
            // additive sprite is a white blob on BLACK, because adding zero is what makes its
            // square edge vanish; hand that to an ALPHA system and the black is opaque, so the
            // particle renders as a dark square. Our smoke had been doing exactly that since
            // FX landed, which is visible only against a light background.
            CloudName = Path.GetFileNameWithoutExtension(spriteTexture) + "_cloud.tga",
        };
        string P(string n) => $"{pack}_{n}";
        string fxName = P("DeathFX");
        r.FxLists.Add(fxName);

        // Field ORDER and SHAPE copied from a system that demonstrably loads; the values are
        // ours. The same discipline as the W3D template and the map's lighting chunk — the
        // layout has to come from something real, the content does not.
        void System(string name, string priority, string shader, double sizeLo, double sizeHi,
                    int lifetime, int burst, (int R, int G, int B) c0, (int R, int G, int B) c1,
                    string? sprite = null)
        {
            r.Systems.AppendLine($"ParticleSystem {name}");
            r.Systems.AppendLine($"  Priority = {priority}");
            r.Systems.AppendLine("  IsOneShot = Yes");
            r.Systems.AppendLine($"  Shader = {shader}");
            r.Systems.AppendLine("  Type = PARTICLE");
            r.Systems.AppendLine($"  ParticleName = {sprite ?? spriteTexture}");
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
        // The smoke is ALPHA and therefore takes the CLOUD sprite, not the additive blob.
        System(P("DeathSmoke"), "DEATH_EXPLOSION", "ALPHA", 18, 30, 55, 8, (70, 66, 62), (24, 22, 20), r.CloudName);
        System(P("DeathSpark"), "DEBRIS_TRAIL", "ADDITIVE", 3, 6, 30, 14, (255, 200, 120), (180, 60, 10));

        r.Ini.AppendLine($"FXList {fxName}");
        foreach (var n in new[] { "DeathFlash", "DeathSmoke", "DeathSpark" })
        {
            r.Ini.AppendLine("  ParticleSystem");
            r.Ini.AppendLine($"    Name = {P(n)}");
            r.Ini.AppendLine("  End");
        }

        // The audible half of the same event. An FXList nests `Sound` exactly as it nests
        // `ParticleSystem` — a keyword, a `Name`, an `End` — so the death is ONE authored thing
        // with a picture and a noise, rather than two that have to be kept pointing at each other.
        if (deathSound is not null)
        {
            r.Ini.AppendLine("  Sound");
            r.Ini.AppendLine($"    Name = {deathSound}");
            r.Ini.AppendLine("  End");
        }
        r.Ini.AppendLine("End").AppendLine();

        // ---- TREAD DUST: closing a borrow no lint could see. ---------------------------------
        //
        // `W3DTankDrawModuleData`'s CONSTRUCTOR assigns m_treadDebrisNameLeft/Right the string
        // literals "TrackDebrisDirtLeft" and "TrackDebrisDirtRight", both of which are EA
        // ParticleSystem blocks. So adopting W3DTankDraw makes a pack depend on retail content
        // with NO REFERENCE ANYWHERE for a checker to find: the zero-borrow lint reads what the
        // pack names, and the pack names nothing. It appeared in game as a dust plume behind a
        // tank whose every byte we thought we had authored.
        //
        // The general lesson is bigger than these two names: a module you have not fully
        // specified runs on defaults chosen by whoever wrote it, and some of those defaults are
        // content. Read a module's constructor, not only its buildFieldParse.
        //
        // Shape copied from TrackDebrisDirtLeft, which demonstrably loads; the values are ours.
        // The fields that make it a ground trail rather than a burst are IsOneShot = No,
        // SystemLifetime = 0 (it runs until the emitter stops it) and a LINE volume laid along
        // the belt — a death burst is a one-shot sphere and would puff once and stop.
        void Dust(string name, double y)
        {
            r.Systems.AppendLine($"ParticleSystem {name}");
            r.Systems.AppendLine("  Priority = DUST_TRAIL");
            r.Systems.AppendLine("  IsOneShot = No");
            r.Systems.AppendLine("  Shader = ALPHA");
            r.Systems.AppendLine("  Type = PARTICLE");
            r.Systems.AppendLine($"  ParticleName = {r.CloudName}");
            r.Systems.AppendLine("  AngleZ = 0.00 0.00");
            r.Systems.AppendLine("  AngularRateZ = -0.10 0.10");
            r.Systems.AppendLine("  AngularDamping = 0.95 0.95");
            r.Systems.AppendLine("  VelocityDamping = 0.95 0.90");
            r.Systems.AppendLine("  Gravity = 0.02");
            r.Systems.AppendLine("  Lifetime = 30.00 30.00");
            r.Systems.AppendLine("  SystemLifetime = 0");
            r.Systems.AppendLine("  Size = 1.00 1.00");
            r.Systems.AppendLine("  StartSizeRate = 0.00 0.00");
            r.Systems.AppendLine("  SizeRate = 1.50 3.00");
            r.Systems.AppendLine("  SizeRateDamping = 0.98 0.95");
            r.Systems.AppendLine("  Alpha1 = 1.00 1.00 0");
            r.Systems.AppendLine("  Alpha2 = 0.00 0.00 30");
            r.Systems.AppendLine("  Color1 = R:196 G:180 B:142 0");
            r.Systems.AppendLine("  Color2 = R:0 G:0 B:0 0");
            r.Systems.AppendLine("  ColorScale = 0.01 0.10");
            r.Systems.AppendLine("  BurstDelay = 0.00 0.00");
            r.Systems.AppendLine("  BurstCount = 0.00 2.00");
            r.Systems.AppendLine("  InitialDelay = 0.00 0.00");
            r.Systems.AppendLine("  DriftVelocity = X:0.00 Y:0.00 Z:0.00");
            r.Systems.AppendLine("  VelocityType = ORTHO");
            r.Systems.AppendLine("  VelOrthoX = -1.50 -1.00");     // thrown backwards, as by a belt
            r.Systems.AppendLine("  VelOrthoY = 0.50 0.50");
            r.Systems.AppendLine("  VelOrthoZ = 0.50 0.50");
            r.Systems.AppendLine("  VolumeType = LINE");
            // The emitter is a LINE along the belt's ground contact, not a point. Retail's runs
            // -5..15 at y 8 for a tank about half our width; ours matches the Mangal's ground
            // run and the y of its TREADFX bones, which is where the mud ribbon is measured too.
            r.Systems.AppendLine($"  VolLineStart = X:-13.00 Y:{y:0.00} Z:0.00");
            r.Systems.AppendLine($"  VolLineEnd = X:13.00 Y:{y:0.00} Z:0.00");
            r.Systems.AppendLine("  IsHollow = No");
            r.Systems.AppendLine("  IsGroundAligned = No");
            r.Systems.AppendLine("End").AppendLine();
        }

        r.TreadDustLeft = P("TreadDustLeft");
        r.TreadDustRight = P("TreadDustRight");
        Dust(r.TreadDustLeft, 13.5);
        Dust(r.TreadDustRight, -13.5);

        r.Sprite = Sprite(64);
        r.Cloud = CloudSprite(64);
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
    /// <summary>
    /// A soft cloud puff for <c>Shader = ALPHA</c>: WHITE throughout, with the falloff in the
    /// ALPHA channel instead of the colour.
    ///
    /// <para><b>This is not the additive sprite with an alpha channel bolted on, and the
    /// difference is the whole point.</b> An additive particle encodes its falloff as
    /// brightness on black, because adding zero is exactly what makes the square edge of the
    /// texture disappear. An ALPHA particle multiplies its colour by the system's
    /// <c>Color1</c>/<c>Color2</c> ramp and composites by alpha — so black in the texture is
    /// not "nothing", it is opaque black, and the same sprite renders as a dark square with a
    /// bright core. White-with-alpha is the correct encoding: the tint comes entirely from the
    /// system, and the shape comes entirely from the alpha.</para>
    ///
    /// <para>The falloff is cubed rather than squared. A dust cloud has a soft edge and a
    /// weak centre; the squared ramp that reads as a <i>glow</i> for an additive spark reads
    /// as a hard ball of smoke here.</para>
    /// </summary>
    public static byte[] CloudSprite(int size)
    {
        var px = new byte[size * size * 4];
        double c = (size - 1) / 2.0, rmax = c;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / rmax;
                double v = Math.Clamp(1.0 - d, 0.0, 1.0);
                int o = ((size - 1 - y) * size + x) * 4;       // TGA rows run bottom-up
                px[o] = px[o + 1] = px[o + 2] = 255;           // BGR: white, tinted by the system
                px[o + 3] = (byte)Math.Round(255 * v * v * v);
            }

        var tga = new byte[18 + px.Length];
        tga[2] = 2;                                            // uncompressed true-colour
        BitConverter.GetBytes((ushort)size).CopyTo(tga, 12);
        BitConverter.GetBytes((ushort)size).CopyTo(tga, 14);
        tga[16] = 32;                                          // 32-bit
        tga[17] = 0x08;                                        // 8 alpha bits, origin bottom-left
        px.CopyTo(tga, 18);
        return tga;
    }

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
