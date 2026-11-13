using System.Buffers.Binary;

namespace Datum.Core;

// What a finished video file says about itself. An MP4 is only playable once its moov box is written, which SOLIDWORKS
// does last, and an AVI carries its frame count and rate in the avih header.
public sealed record VideoFile(bool Complete, double? Seconds)
{
    private const int MicrosecondsPerSecond = 1_000_000;

    public static VideoFile Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Read(bytes);
        }
        catch (IOException)
        {
            return new VideoFile(false, null);
        }
    }

    public static VideoFile Read(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) ? Avi(bytes) : Mp4(bytes);

    private static VideoFile Mp4(ReadOnlySpan<byte> bytes)
    {
        for (var at = 0; at + 8 <= bytes.Length;)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
            var header = 8;
            if (size == 1 && at + 16 <= bytes.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes[(at + 8)..]);
                header = 16;
            }

            if (size < header || at + size > bytes.Length)
            {
                return new VideoFile(false, null);
            }

            if (bytes.Slice(at + 4, 4).SequenceEqual("moov"u8))
            {
                return new VideoFile(true, MovieSeconds(bytes.Slice(at + header, (int)size - header)));
            }

            at += (int)size;
        }

        return new VideoFile(false, null);
    }

    private static double? MovieSeconds(ReadOnlySpan<byte> moov)
    {
        var at = moov.IndexOf("mvhd"u8);
        if (at < 4 || at + 28 > moov.Length)
        {
            return null;
        }

        var body = moov[(at + 4)..];
        var (scale, duration) = body[0] == 1 && body.Length >= 32
            ? (BinaryPrimitives.ReadUInt32BigEndian(body[20..]), (double)BinaryPrimitives.ReadUInt64BigEndian(body[24..]))
            : (BinaryPrimitives.ReadUInt32BigEndian(body[12..]), BinaryPrimitives.ReadUInt32BigEndian(body[16..]));
        return scale == 0 ? null : duration / scale;
    }

    private static VideoFile Avi(ReadOnlySpan<byte> bytes)
    {
        var at = bytes.IndexOf("avih"u8);
        if (at < 0 || at + 28 > bytes.Length)
        {
            return new VideoFile(false, null);
        }

        var microseconds = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]);
        var frames = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 24)..]);
        var complete = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) + 8 <= bytes.Length && frames > 0;
        return new VideoFile(complete, complete ? (double)frames * microseconds / MicrosecondsPerSecond : null);
    }
}

// Where the model sits in a set of frames, as fractions of the width and height, against a plain background taken from
// the top-left pixel.
public sealed record Framing(double Left, double Right, double Top, double Bottom)
{
    private const int ColourTolerance = 24;
    private const double EdgeMargin = 0.02;
    private const double CentreTolerance = 0.05;

    public static Framing? Of(IReadOnlyList<Raster> frames)
    {
        var (left, right, top, bottom) = (1.0, 0.0, 1.0, 0.0);
        foreach (var frame in frames)
        {
            var background = (frame.Rgb[0], frame.Rgb[1], frame.Rgb[2]);
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    var i = ((y * frame.Width) + x) * 3;
                    if (Math.Abs(frame.Rgb[i] - background.Item1) + Math.Abs(frame.Rgb[i + 1] - background.Item2) + Math.Abs(frame.Rgb[i + 2] - background.Item3) > ColourTolerance)
                    {
                        (left, right) = (Math.Min(left, (double)x / frame.Width), Math.Max(right, (double)(x + 1) / frame.Width));
                        (top, bottom) = (Math.Min(top, (double)y / frame.Height), Math.Max(bottom, (double)(y + 1) / frame.Height));
                    }
                }
            }
        }

        return right > left ? new Framing(left, right, top, bottom) : null;
    }

    public double CentreX => (Left + Right) / 2;

    public double CentreY => (Top + Bottom) / 2;

    public string? Problem =>
        Left < EdgeMargin || Top < EdgeMargin || Right > 1 - EdgeMargin || Bottom > 1 - EdgeMargin ? "the model touches the edge of the frame"
        : Math.Abs(((Left + Right) / 2) - 0.5) > CentreTolerance || Math.Abs(((Top + Bottom) / 2) - 0.5) > CentreTolerance ? "the model is off centre"
        : null;

    public override string ToString() =>
        $"the model spans {Percent(Left)} to {Percent(Right)} of the width and {Percent(Top)} to {Percent(Bottom)} of the height";

    private static string Percent(double fraction) => $"{Math.Round(fraction * 100)}%";
}
