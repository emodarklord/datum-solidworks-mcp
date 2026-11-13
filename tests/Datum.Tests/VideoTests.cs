using System.Buffers.Binary;
using Datum.Core;

namespace Datum.Tests;

public sealed class VideoTests
{
    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Mvhd(uint scale, uint duration)
    {
        var body = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12), scale);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), duration);
        return Box("mvhd", body);
    }

    [Fact]
    public void AFinishedMp4GivesItsLength()
    {
        var file = Box("ftyp", new byte[8]).Concat(Box("mdat", new byte[64])).Concat(Box("moov", Mvhd(1000, 8050))).ToArray();

        Assert.Equal(new VideoFile(true, 8.05), VideoFile.Read(file));
    }

    [Fact]
    public void AnMp4WithoutItsIndexIsNotFinished()
    {
        var file = Box("ftyp", new byte[8]).Concat(Box("mdat", new byte[64])).ToArray();

        Assert.False(VideoFile.Read(file).Complete);
        Assert.False(VideoFile.Read([]).Complete);
    }

    [Fact]
    public void AnAviGivesItsLengthFromItsHeader()
    {
        var file = new byte[64];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)file.Length - 8);
        "avih"u8.CopyTo(file.AsSpan(20));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 50_000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 161);

        Assert.Equal(new VideoFile(true, 8.05), VideoFile.Read(file));
    }

    private static Raster Frame(int left, int right, int top, int bottom)
    {
        var frame = Raster.Filled(100, 100, 255, 255, 255);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                frame.Rgb[((y * 100) + x) * 3] = 0;
            }
        }

        return frame;
    }

    [Fact]
    public void ACentredModelFramesWell()
    {
        var framing = Framing.Of([Frame(30, 60, 20, 80), Frame(40, 70, 20, 80)])!;

        Assert.Equal(new Framing(0.3, 0.7, 0.2, 0.8), framing);
        Assert.Null(framing.Problem);
        Assert.Equal("the model spans 30% to 70% of the width and 20% to 80% of the height", framing.ToString());
    }

    [Fact]
    public void AnOffCentreOrClippedModelIsAProblem()
    {
        Assert.Equal("the model is off centre", Framing.Of([Frame(40, 98, 20, 80)])!.Problem);
        Assert.Equal("the model touches the edge of the frame", Framing.Of([Frame(0, 60, 20, 80)])!.Problem);
        Assert.Null(Framing.Of([Frame(0, 0, 0, 0)]));
    }
}
