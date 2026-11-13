namespace Datum.Core;

// A constant Rpm, or a swing out to Swing degrees and back every Period seconds.
public sealed record MotionMotorSpec(string Face, double? Rpm, double? Swing, double? Period, bool Reverse)
{
    // The component the motor turns against, comp#N; SOLIDWORKS uses the ground when it's null.
    public string? RelativeTo { get; init; }

    public void Validate()
    {
        if (Rpm is null == Swing is null || Rpm is <= 0 || (Swing is not null && Swing <= 0) || (Swing is null ? Period is not null : Period is not > 0))
        {
            throw new DatumException(ErrorCode.InvalidArgument, "a motor needs rpm above 0, or swing and period above 0", "Nothing was changed");
        }
    }
}

public sealed record MotionPathSpec(string Kind, double Seconds, double Start)
{
    private static readonly string[] Kinds = ["explode", "collapse"];

    public string NormalizedKind => Kind.Trim().ToLowerInvariant();

    public void Validate()
    {
        if (!Kinds.Contains(NormalizedKind))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown motion path '{Kind}'", $"Use {string.Join(", ", Kinds)}");
        }

        if (Seconds <= 0 || Start < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "a motion path needs seconds above 0 and a start of 0 or more", "Nothing was changed");
        }
    }
}

// How far a component has gone from where it was at time 0. Turned adds up, so two full turns read 720. Its sign is
// the direction about the axis the component first turned on, so parts on parallel axes can be compared.
public sealed record MotionSample(double Seconds, string Component, double Turned, double Moved)
{
    private const double StillTolerance = 1e-3;

    // The time SOLIDWORKS actually showed, when it didn't settle at the one asked for.
    public double? Reached { get; init; }

    public bool Stationary => Math.Abs(Turned) < StillTolerance && Moved < StillTolerance;

    public override string ToString() =>
        $"{Numbers.Format(Seconds)} s{(Reached is { } reached ? $" (SOLIDWORKS stopped at {Numbers.Format(reached)} s)" : "")} {Component}: turned {Numbers.Format(Turned)}°, moved {Numbers.Format(Moved)} mm";
}

// Checks are frames rendered the same way just before the video, since Datum can't decode the video itself.
public sealed record MotionVideo(IReadOnlyList<Raster> Checks, VideoFile File);

public static class MotionTimes
{
    public const int MostSamples = 40;

    // Evenly spaced through the study, or the times asked for, in order.
    public static IReadOnlyList<double> Pick(double seconds, int samples, IReadOnlyList<double>? times)
    {
        if (times is { Count: > 0 })
        {
            return times.Count > MostSamples || times.Any(t => t < 0 || t > seconds)
                ? throw new DatumException(ErrorCode.InvalidArgument, $"give 1 to {MostSamples} times between 0 and the study's {Numbers.Format(seconds)} s", "Nothing was run")
                : [.. times.Distinct().Order()];
        }

        return samples is < 1 or > MostSamples
            ? throw new DatumException(ErrorCode.InvalidArgument, $"samples must be 1 to {MostSamples}", "Nothing was run")
            : [.. Enumerable.Range(1, samples).Select(i => seconds * i / samples)];
    }
}

public static class Gif
{
    private const int MinCodeSize = 8;
    private const int MaxCode = 4095;
    private const int FirstGrey = 216;
    private const int Greys = 32;
    private const int GreySpread = 24;

    // A 6 x 6 x 6 colour cube and a ramp of greys: CAD pictures use few colours and many greys, and one table lets
    // every frame share it.
    public static byte[] Encode(IReadOnlyList<Raster> frames, int delayCentiseconds)
    {
        if (frames.Count == 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "an animation needs at least one frame");
        }

        using var gif = new MemoryStream();
        using var writer = new BinaryWriter(gif);
        writer.Write("GIF89a"u8);
        writer.Write((ushort)frames[0].Width);
        writer.Write((ushort)frames[0].Height);
        writer.Write((byte)0xF7);
        writer.Write((byte)0);
        writer.Write((byte)0);
        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                {
                    writer.Write((byte)(r * 51));
                    writer.Write((byte)(g * 51));
                    writer.Write((byte)(b * 51));
                }
            }
        }

        for (var grey = 0; grey < Greys; grey++)
        {
            var level = (byte)(grey * 255 / (Greys - 1));
            writer.Write(new[] { level, level, level });
        }

        for (var padding = FirstGrey + Greys; padding < 256; padding++)
        {
            writer.Write(new byte[3]);
        }

        // Loop forever.
        writer.Write(new byte[] { 0x21, 0xFF, 11 });
        writer.Write("NETSCAPE2.0"u8);
        writer.Write(new byte[] { 3, 1, 0, 0, 0 });
        foreach (var frame in frames)
        {
            writer.Write(new byte[] { 0x21, 0xF9, 4, 0 });
            writer.Write((ushort)delayCentiseconds);
            writer.Write(new byte[] { 0, 0, 0x2C, 0, 0, 0, 0 });
            writer.Write((ushort)frame.Width);
            writer.Write((ushort)frame.Height);
            writer.Write((byte)0);
            writer.Write((byte)MinCodeSize);
            WriteBlocks(writer, Compress(Indexed(frame)));
        }

        writer.Write((byte)0x3B);
        writer.Flush();
        return gif.ToArray();
    }

    private static byte[] Indexed(Raster frame)
    {
        var indexed = new byte[frame.Width * frame.Height];
        for (var i = 0; i < indexed.Length; i++)
        {
            int r = frame.Rgb[i * 3], g = frame.Rgb[(i * 3) + 1], b = frame.Rgb[(i * 3) + 2];
            var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            indexed[i] = spread < GreySpread
                ? (byte)(FirstGrey + (((r + g + b) / 3 * (Greys - 1)) + 127) / 255)
                : (byte)((((r + 25) / 51) * 36) + (((g + 25) / 51) * 6) + ((b + 25) / 51));
        }

        return indexed;
    }

    private static byte[] Compress(byte[] pixels)
    {
        const int clear = 1 << MinCodeSize;
        const int end = clear + 1;
        using var output = new MemoryStream();
        var (bits, count) = (0, 0);
        var size = MinCodeSize + 1;
        void Put(int code)
        {
            bits |= code << count;
            count += size;
            while (count >= 8)
            {
                output.WriteByte((byte)(bits & 0xFF));
                bits >>= 8;
                count -= 8;
            }
        }

        var table = new Dictionary<int, int>();
        var next = end + 1;
        Put(clear);
        int current = pixels[0];
        foreach (var pixel in pixels.Skip(1))
        {
            var key = (current << 8) | pixel;
            if (table.TryGetValue(key, out var known))
            {
                current = known;
                continue;
            }

            Put(current);
            if (next > MaxCode)
            {
                Put(clear);
                table.Clear();
                next = end + 1;
                size = MinCodeSize + 1;
            }
            else
            {
                if (next == 1 << size)
                {
                    size++;
                }

                table[key] = next++;
            }

            current = pixel;
        }

        Put(current);
        Put(end);
        if (count > 0)
        {
            output.WriteByte((byte)(bits & 0xFF));
        }

        return output.ToArray();
    }

    private static void WriteBlocks(BinaryWriter writer, byte[] data)
    {
        for (var at = 0; at < data.Length; at += 255)
        {
            var length = Math.Min(255, data.Length - at);
            writer.Write((byte)length);
            writer.Write(data, at, length);
        }

        writer.Write((byte)0);
    }
}
