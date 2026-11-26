using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using RimLumen.Core.Showcase;
using Verse;

namespace RimLumen.Core.Tests.Showcase;

[LumenTestSuite]
public static class ShowcaseSuite
{
    [LumenFact]
    public static void TryParseHandlesFlagOptionalCaptureCaseAndMissingEnable()
    {
        False(ShowcaseOptionsParser.TryParse(Array.Empty<string>(), out ShowcaseOptions empty));
        Equal(new ShowcaseOptions(false, null), empty);

        False(ShowcaseOptionsParser.TryParse(new[] { "RimWorld.exe", "-rimlumen-showcase-capture=C:/shot.png" }, out ShowcaseOptions captureOnly));
        Equal(new ShowcaseOptions(false, null), captureOnly);

        True(ShowcaseOptionsParser.TryParse(new[] { "RimWorld.exe", "-RiMLuMeN-ShOwCaSe" }, out ShowcaseOptions plain));
        Equal(new ShowcaseOptions(true, null), plain);

        True(ShowcaseOptionsParser.TryParse(new[]
        {
            "-RIMLUMEN-SHOWCASE-CAPTURE=\"C:/out dir/shot.png\"",
            "-rimlumen-showcase",
        }, out ShowcaseOptions quoted));
        Equal(new ShowcaseOptions(true, "C:/out dir/shot.png"), quoted);

        True(ShowcaseOptionsParser.TryParse(new[]
        {
            "-rimlumen-showcase",
            "-rimlumen-showcase-capture=",
        }, out ShowcaseOptions emptyCapture));
        Equal(new ShowcaseOptions(true, null), emptyCapture);
    }

    [LumenFact]
    public static void PngEncoderWritesValidHeaderChunksCrcAndZlibPixels()
    {
        const int width = 3;
        const int height = 2;
        byte[] rgba =
        {
            255, 0, 0, 255,   0, 255, 0, 255,   0, 0, 255, 255,
            10, 20, 30, 200,  40, 50, 60, 128, 250, 240, 230, 255,
        };

        byte[] png = ShowcasePngEncoder.EncodeRgba(rgba, width, height);
        byte[] expectedSig = { 137, 80, 78, 71, 13, 10, 26, 10 };
        True(png.Length > 8);
        for (int i = 0; i < expectedSig.Length; i++)
        {
            Equal(expectedSig[i], png[i]);
        }

        int offset = 8;
        string[] expectedTypes = { "IHDR", "IDAT", "IEND" };
        byte[]? idatData = null;

        foreach (string expectedType in expectedTypes)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            Equal(expectedType, type);

            ReadOnlySpan<byte> typeAndPayload = png.AsSpan(offset + 4, 4 + length);
            uint expectedCrc = ComputeCrc32(typeAndPayload);
            uint actualCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
            Equal(expectedCrc, actualCrc);

            ReadOnlySpan<byte> payload = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                Equal(13, length);
                Equal(width, BinaryPrimitives.ReadInt32BigEndian(payload[0..4]));
                Equal(height, BinaryPrimitives.ReadInt32BigEndian(payload[4..8]));
                Equal((byte)8, payload[8]);
                Equal((byte)6, payload[9]);
                Equal((byte)0, payload[10]);
                Equal((byte)0, payload[11]);
                Equal((byte)0, payload[12]);
            }
            else if (type == "IDAT")
            {
                idatData = payload.ToArray();
            }
            else if (type == "IEND")
            {
                Equal(0, length);
            }

            offset += 12 + length;
        }

        Equal(png.Length, offset);
        True(idatData is not null);

        using var compressedStream = new MemoryStream(idatData!);
        using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress);
        using var rawStream = new MemoryStream();
        zlib.CopyTo(rawStream);
        byte[] raw = rawStream.ToArray();

        int stride = width * 4;
        Equal(height * (1 + stride), raw.Length);
        for (int y = 0; y < height; y++)
        {
            Equal((byte)0, raw[y * (1 + stride)]);
            for (int x = 0; x < stride; x++)
            {
                Equal(rgba[y * stride + x], raw[y * (1 + stride) + 1 + x]);
            }
        }
    }

    [LumenFact]
    public static void StationLayoutProducesSixMonotonicStationsAndClampedArena()
    {
        var mapSize = new IntVec3(250, 1, 250);
        var mapCenter = new IntVec3(125, 0, 125);
        var stations = ShowcaseStage.ComputeStationLayout(mapCenter, mapSize);
        Equal(6, stations.Count);

        IntVec3 arenaCenter = ShowcaseStage.ComputeArenaCenter(mapCenter, mapSize);
        CellRect arena = ShowcaseStage.ComputeArenaRect(arenaCenter, mapSize);
        Equal(ShowcaseStage.ArenaWidth, arena.Width);
        Equal(ShowcaseStage.ArenaHeight, arena.Height);

        int previousX = int.MinValue;
        for (int i = 0; i < stations.Count; i++)
        {
            ShowcaseStationInfo station = stations[i];
            Equal(i + 1, station.Index);
            False(string.IsNullOrWhiteSpace(station.Title));
            False(string.IsNullOrWhiteSpace(station.Subtitle));
            True(station.AnchorCell.x > previousX);
            Equal(arenaCenter.z, station.AnchorCell.z);
            True(arena.Contains(station.AnchorCell));
            previousX = station.AnchorCell.x;
        }

        var cornerStations = ShowcaseStage.ComputeStationLayout(new IntVec3(5, 0, 5), mapSize);
        IntVec3 cornerCenter = ShowcaseStage.ComputeArenaCenter(new IntVec3(5, 0, 5), mapSize);
        CellRect cornerArena = ShowcaseStage.ComputeArenaRect(cornerCenter, mapSize);
        True(cornerArena.minX >= 0 && cornerArena.maxX < mapSize.x);
        True(cornerArena.minZ >= 0 && cornerArena.maxZ < mapSize.z);
        int prevCornerX = int.MinValue;
        foreach (ShowcaseStationInfo station in cornerStations)
        {
            True(station.AnchorCell.x > prevCornerX);
            True(cornerArena.Contains(station.AnchorCell));
            prevCornerX = station.AnchorCell.x;
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    // Storyboard contract: cues run in time order; each station lights up on screen during the pan (half width 16 cells
    // at the pan zoom on 16:9); every firework has climbed before the freeze; segment B switches Standard -> Crisp -> Foggy
    // and turns the arrows on in its second half.
    [LumenFact]
    public static void TrailerTimelineKeepsStoryboardOrder()
    {
        TrailerCue[] cues = ShowcaseTrailerTimeline.Cues;
        for (int i = 1; i < cues.Length; i++) True(cues[i - 1].Time <= cues[i].Time);
        float halfWidth = ShowcaseTrailerTimeline.PanRootSize * 16f / 9f;
        var stations = cues.Where(c => c.Kind == TrailerCueKind.StartStation).ToArray();
        Equal(ShowcaseTrailerTimeline.StationOffsetsX.Length, stations.Length);
        foreach (TrailerCue cue in stations)
        {
            True(cue.Time < ShowcaseTrailerTimeline.SegmentAEnd);
            True(MathF.Abs(ShowcaseTrailerTimeline.StationOffsetsX[cue.Index] - ShowcaseTrailerTimeline.PanX(cue.Time)) <= halfWidth);
        }
        // 1.0 s is the firework climb before its burst (RimLumenDemoEffects.CreateFireworkAsset).
        True(cues.Where(c => c.Kind == TrailerCueKind.LaunchFirework).All(c => c.Time + 1f < ShowcaseTrailerTimeline.SegmentAEnd));
        TrailerCue segmentB = cues.Single(c => c.Kind == TrailerCueKind.SegmentBStart);
        True(cues.Single(c => c.Kind == TrailerCueKind.Freeze).Time < segmentB.Time);
        var packs = cues.Where(c => c.Kind == TrailerCueKind.SwitchPack).ToArray();
        True(packs.Select(c => c.PackId).SequenceEqual(new[] { ShaderPackCatalogIds.Standard, ShaderPackCatalogIds.Crisp, ShaderPackCatalogIds.Foggy }));
        True(packs.All(c => c.Time >= segmentB.Time && c.Time < ShowcaseTrailerTimeline.SegmentBEnd));
        True(cues.Single(c => c.Kind == TrailerCueKind.ArrowsOn).Time
            >= (segmentB.Time + ShowcaseTrailerTimeline.SegmentBEnd) / 2f);
        Equal(TrailerCueKind.End, cues[^1].Kind);
    }

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool condition)
    {
        if (condition) throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
