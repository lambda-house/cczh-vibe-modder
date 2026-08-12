using System.Text;

namespace RtsSkeleton.Content;

/// <summary>
/// Author a pack's sound: the <c>.wav</c> files and the <c>AudioEvent</c> blocks that name them.
///
/// <para><b>The census that shaped this.</b> The install ships <b>8,638 audio files, 1,049.6 MB</b>
/// — 8,582 <c>.wav</c> and 56 <c>.mp3</c>, which is 47% of the install's bytes and 0% of the
/// simulation. Eight distinct wave formats appear, and the split is the useful part: <b>5,346
/// files are plain PCM (<c>wFormatTag = 1</c>) and 3,236 are IMA ADPCM (<c>tag = 17</c>)</b>. The
/// single largest bucket, 5,157 files, is <b>mono / 22,050 Hz / 16-bit PCM</b>. So the format that
/// is both most typical and cheapest to produce are the same format, and <b>authoring audio needs
/// no encoder at all</b> — a 44-byte RIFF header and the samples. That was the one unknown worth
/// settling before this file existed; it collapsed the slice from "write a codec" to "write a
/// header".</para>
///
/// <para><b>Three closed enums, taken from the C++ name tables rather than from a grep</b> —
/// <c>Priority</c>, <c>Type</c> and <c>Control</c>, all in <c>INIAudioEventInfo.cpp</c>. The last
/// two are <c>parseBitString32</c>, so they are SPACE-SEPARATED FLAG SETS and not single values:
/// <c>Type = world shrouded everyone</c> is three bits, and reading one as a scalar silently keeps
/// only the first. <c>Priority</c> alone is <c>parseIndexList</c>.</para>
///
/// <para><b>Where the file goes is computed, not declared.</b>
/// <c>AudioEventRTS::generateFilenamePrefix</c> builds
/// <c>{AudioRoot}\{SoundsFolder}\{name}.{SoundsExtension}</c> from <c>AudioSettings.ini</c> —
/// <c>Data\Audio\Sounds\name.wav</c> as shipped. An event never names its file; the entries in
/// <c>Sounds</c> ARE the base filenames. Localisation is a preference and not a requirement:
/// <c>adjustForLocalization</c> tries <c>Sounds\{Language}\</c> first and <b>silently keeps the
/// unlocalised path when that misses</b>, so flat output is correct even for <c>voice</c> events.
/// </para>
///
/// <para><b>The verification ceiling, stated because it cannot be worked around.</b> The engine we
/// target — GeneralsX on arm64 — creates an OpenAL context and allocates sources, and then never
/// loads a sample: <c>OpenALAudioManager</c> carries no decoder, calls <c>alBufferData</c> nowhere,
/// and its <c>update()</c> is a documented no-op pending "Phase 2". <b>This build cannot emit a
/// sound, from any input.</b> Everything here is therefore checked against schema authority and by
/// our own reader, and its playback is NOT claimed. See <c>docs/ZERO-HOUR-MODEL.md</c>.</para>
/// </summary>
public static class ZhAudio
{
    /// <summary><c>theAudioPriorityNames</c>, verbatim. <c>parseIndexList</c> — one value.</summary>
    public static readonly string[] Priorities = { "LOWEST", "LOW", "NORMAL", "HIGH", "CRITICAL" };

    /// <summary><c>theSoundTypeNames</c>, verbatim. <c>parseBitString32</c> — a flag SET.</summary>
    public static readonly string[] Types =
    {
        "UI", "WORLD", "SHROUDED", "GLOBAL", "VOICE", "PLAYER", "ALLIES", "ENEMIES", "EVERYONE",
    };

    /// <summary><c>theAudioControlNames</c>, verbatim. <c>parseBitString32</c> — a flag SET.</summary>
    public static readonly string[] Controls = { "LOOP", "RANDOM", "ALL", "POSTDELAY", "INTERRUPT" };

    /// <summary>The plurality format, and the one an absent decoder is likeliest to survive.</summary>
    public const int SampleRate = 22050;

    public sealed class Result
    {
        /// <summary>The AudioEvent blocks, for <c>Data/INI/SoundEffects/</c> — a directory the
        /// boot log shows being scanned, so a pack extends retail's set rather than replacing
        /// its file.</summary>
        public readonly StringBuilder Ini = new();

        /// <summary>Base name (no extension, no directory) → RIFF bytes. The key is exactly what
        /// goes in an event's <c>Sounds</c> line, because the engine derives the path.</summary>
        public readonly Dictionary<string, byte[]> Waves = new(StringComparer.Ordinal);

        /// <summary>Event name by role, for the compiler to reference.</summary>
        public readonly Dictionary<string, string> Events = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// One sound set per pack, not per unit — the same economy as the FX systems, and for the same
    /// measured reason: retail's 743 sound effects are shared across 2,102 objects, so per-unit
    /// copies multiply the INI and change nothing anyone can hear.
    /// </summary>
    public static Result Write(string pack)
    {
        var r = new Result();
        string P(string n) => $"{pack}_{n}";

        // Base filenames are kept SHORT and distinct from the event names. Retail's are 8.3-ish
        // (`vgenlo2a`, `isabotab`) because they predate long-name filesystems; ours need not be,
        // but keeping file and event namespaces separate makes a missing file legible — you can
        // tell "no such event" from "no such wave" by which name the error prints.
        void Event(string role, string priority, string type, string? control,
                   int volume, string wave, byte[] pcm, int? limit = null, int? minRange = null,
                   int? maxRange = null)
        {
            string name = P(role);
            r.Events[role] = name;
            r.Waves[wave] = Riff(pcm);

            r.Ini.AppendLine($"AudioEvent {name}");
            r.Ini.AppendLine($"  Priority = {priority}");
            if (control is not null) r.Ini.AppendLine($"  Control = {control}");
            r.Ini.AppendLine($"  Sounds = {wave}");
            r.Ini.AppendLine($"  Volume = {volume}");
            if (limit is int l) r.Ini.AppendLine($"  Limit = {l}");
            if (minRange is int mn) r.Ini.AppendLine($"  MinRange = {mn}");
            if (maxRange is int mx) r.Ini.AppendLine($"  MaxRange = {mx}");
            r.Ini.AppendLine($"  Type = {type}");
            r.Ini.AppendLine("End").AppendLine();
        }

        // A weapon report: a hard transient with almost no attack, and a fast exponential decay.
        // `Limit` matters more than the waveform does — twenty units firing without one is twenty
        // overlapping copies, which is how a mod ends up louder than the game.
        Event("WeaponFire", "NORMAL", "world shrouded everyone", null, 70,
              P("fire").ToLowerInvariant(), Report(0.28, 190, 0.9), limit: 4,
              minRange: 100, maxRange: 700);

        // Death: longer, darker, and CRITICAL — a death that is dropped for a priority reason
        // reads as a bug, because the thing it belongs to visibly vanished.
        Event("Death", "CRITICAL", "world shrouded everyone", null, 90,
              P("death").ToLowerInvariant(), Report(0.85, 70, 0.55), limit: 6,
              minRange: 150, maxRange: 1200);

        // The engine loop. `Control = loop all` is two bits, and the wave must contain a WHOLE
        // number of cycles of every partial in it or the seam clicks once per repetition —
        // audible, and the kind of defect that survives review because nobody plays a loop twice.
        Event("MoveLoop", "LOW", "world shrouded everyone", "loop all", 35,
              P("engine").ToLowerInvariant(), EngineLoop(0.5, 58), limit: 3,
              minRange: 60, maxRange: 500);

        // Selection and move-order acknowledgements. `Type = ui` on purpose: these are feedback to
        // the player who clicked, not events in the world, so they must not attenuate with camera
        // distance or fall silent under shroud. Marking them `voice` would be the instinct and is
        // wrong here — `voice` is the localised speech channel, and these are not speech.
        Event("Select", "NORMAL", "ui player", null, 60,
              P("sel").ToLowerInvariant(), Chirp(0.10, 880, 1320), limit: 2);
        Event("Move", "NORMAL", "ui player", null, 60,
              P("mov").ToLowerInvariant(), Chirp(0.12, 660, 440), limit: 2);
        Event("Attack", "NORMAL", "ui player", null, 65,
              P("atk").ToLowerInvariant(), Chirp(0.14, 520, 780), limit: 2);

        return r;
    }

    // ------------------------------------------------------------------ synthesis
    //
    // Everything below is deterministic by construction: one xorshift seeded from a constant, and
    // otherwise pure arithmetic. Two compiles of the same pack must produce byte-identical waves,
    // for the same reason every other authored asset must — the pack's contentHash is the anchor a
    // measurement is quoted against, and an asset that differs per run makes it meaningless.

    private static uint _rng;
    private static void Seed(uint s) => _rng = s | 1u;
    private static double Noise()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng / 2147483648.0) - 1.0;                 // [-1, 1)
    }

    /// <summary>
    /// A percussive report: filtered noise over a falling tone, with an exponential amplitude
    /// decay. <paramref name="tone"/> low and <paramref name="decay"/> long reads as an explosion;
    /// high and short reads as a gunshot. One generator covers both because the difference between
    /// them really is only those two numbers.
    /// </summary>
    private static byte[] Report(double seconds, double tone, double decay)
    {
        Seed(0x9E3779B9u ^ (uint)(tone * 1000));
        int n = (int)(SampleRate * seconds);
        var s = new short[n];
        double lp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;
            double env = Math.Exp(-t / (decay * seconds));

            // A one-pole low-pass whose cutoff FALLS with the envelope. A fixed cutoff gives a
            // flat hiss; sweeping it down is what makes a burst read as something with mass.
            double k = 0.05 + 0.55 * env;
            lp += k * (Noise() - lp);

            double body = Math.Sin(2 * Math.PI * tone * t * (0.6 + 0.4 * env));
            double v = env * (0.72 * lp + 0.28 * body);
            s[i] = Clip(v);
        }
        return Pcm(s);
    }

    /// <summary>
    /// A seamless engine loop. The length is rounded so the buffer holds an INTEGER number of
    /// fundamental cycles, and every partial is an integer multiple of that fundamental — which is
    /// what makes the wrap-around silent. Round the length instead and the loop ticks at exactly
    /// the repeat rate, which sounds like a bug in the mixer rather than in the asset.
    /// </summary>
    private static byte[] EngineLoop(double seconds, double fundamental)
    {
        int cycles = Math.Max(1, (int)Math.Round(seconds * fundamental));
        int n = (int)Math.Round(cycles * SampleRate / fundamental);
        double f = (double)cycles * SampleRate / n / SampleRate;   // cycles per sample, exact

        Seed(0x85EBCA6Bu);
        var s = new short[n];
        double lp = 0;
        for (int i = 0; i < n; i++)
        {
            double ph = 2 * Math.PI * f * i;
            double v = 0.50 * Math.Sin(ph)
                     + 0.25 * Math.Sin(2 * ph)
                     + 0.14 * Math.Sin(3 * ph)
                     + 0.08 * Math.Sin(5 * ph);

            // Broadband texture, low-passed hard so it sits under the tone rather than beside it.
            lp += 0.08 * (Noise() - lp);
            s[i] = Clip(0.62 * v + 0.18 * lp);
        }
        return Pcm(s);
    }

    /// <summary>
    /// A two-tone acknowledgement. Short, with a raised-cosine window at both ends: an abrupt
    /// start or stop on a pure tone is a step discontinuity, and a step is a click.
    /// </summary>
    private static byte[] Chirp(double seconds, double f0, double f1)
    {
        int n = (int)(SampleRate * seconds);
        var s = new short[n];
        for (int i = 0; i < n; i++)
        {
            double u = (double)i / n;
            double t = (double)i / SampleRate;
            double f = f0 + (f1 - f0) * u;
            int edge = Math.Max(1, n / 8);
            double w = i < edge ? 0.5 * (1 - Math.Cos(Math.PI * i / edge))
                     : i >= n - edge ? 0.5 * (1 - Math.Cos(Math.PI * (n - 1 - i) / edge))
                     : 1.0;
            s[i] = Clip(0.55 * w * (Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(4 * Math.PI * f * t)));
        }
        return Pcm(s);
    }

    private static short Clip(double v) => (short)Math.Round(32767 * Math.Clamp(v, -1.0, 1.0));

    private static byte[] Pcm(short[] s)
    {
        var b = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            b[i * 2] = (byte)(s[i] & 0xFF);
            b[i * 2 + 1] = (byte)((s[i] >> 8) & 0xFF);
        }
        return b;
    }

    /// <summary>
    /// The 44-byte canonical RIFF/WAVE header over mono 16-bit PCM. Little-endian throughout —
    /// note that this is the opposite of the <c>.big</c> TOC and of the W3D chunk sizes, both of
    /// which this project also writes, so the endianness is worth stating rather than inferring.
    /// </summary>
    public static byte[] Riff(byte[] pcm, int rate = SampleRate, int channels = 1, int bits = 16)
    {
        var o = new byte[44 + pcm.Length];
        void A(string s, int at) { for (int i = 0; i < s.Length; i++) o[at + i] = (byte)s[i]; }
        void U32(uint v, int at) => BitConverter.GetBytes(v).CopyTo(o, at);
        void U16(ushort v, int at) => BitConverter.GetBytes(v).CopyTo(o, at);

        int blockAlign = channels * bits / 8;
        A("RIFF", 0);  U32((uint)(36 + pcm.Length), 4);      // size of everything after this field
        A("WAVE", 8);
        A("fmt ", 12); U32(16, 16);                          // PCM fmt chunks are 16 bytes
        U16(1, 20);                                          // wFormatTag = 1, plain PCM
        U16((ushort)channels, 22);
        U32((uint)rate, 24);
        U32((uint)(rate * blockAlign), 28);                  // byte rate
        U16((ushort)blockAlign, 32);
        U16((ushort)bits, 34);
        A("data", 36); U32((uint)pcm.Length, 40);
        pcm.CopyTo(o, 44);
        return o;
    }
}
