using GlassLink.Core.Du;
using GlassLink.Core.Flash;

namespace GlassLink.Core.Tests;

public class FlashTests
{
    [Fact]
    public void Slip_frames_survive_the_bytes_it_uses_itself_and_noise_between_packets()
    {
        byte[] packet = [0x01, 0xC0, 0x02, 0xDB, 0x03];
        var framed = Slip.Encode(packet);
        Assert.Equal(new byte[] { 0xC0, 0x01, 0xDB, 0xDC, 0x02, 0xDB, 0xDD, 0x03, 0xC0 }, framed);

        var slip = new Slip();
        var noise = "ESP-ROM:esp32p4-eco2-20240710\r\nwaiting for download\r\n"u8.ToArray();      // the boot log before the answer
        var stream = noise.Concat(framed).Concat(Slip.Encode([0x07])).ToArray();
        var packets = new List<byte[]>();
        foreach (var chunk in stream.Chunk(3))                                  // in pieces, as a serial port delivers
        {
            packets.AddRange(slip.Feed(chunk));
        }

        Assert.Equal(2, packets.Count);
        Assert.Equal(packet, packets[0]);
        Assert.Equal(new byte[] { 0x07 }, packets[1]);
    }

    [Fact]
    public void A_flash_set_is_what_flash_args_lists_and_never_more()
    {
        var folder = Directory.CreateTempSubdirectory("glasslink-flashset").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "bootloader"));
            Directory.CreateDirectory(Path.Combine(folder, "partition_table"));
            File.WriteAllBytes(Path.Combine(folder, "bootloader", "bootloader.bin"), new byte[23_136]);
            File.WriteAllBytes(Path.Combine(folder, "partition_table", "partition-table.bin"), new byte[3_072]);
            File.WriteAllBytes(Path.Combine(folder, "ota_data_initial.bin"), Enumerable.Repeat((byte)0xFF, 8_192).ToArray());
            var app = new byte[10_000];
            app[0] = 0xE9;
            BitConverter.GetBytes((ushort)100).CopyTo(app, 15);
            BitConverter.GetBytes((ushort)199).CopyTo(app, 17);
            BitConverter.GetBytes(0xABCD5432u).CopyTo(app, 32);
            System.Text.Encoding.ASCII.GetBytes(Firmware.ProjectName).CopyTo(app, 32 + 48);
            File.WriteAllBytes(Path.Combine(folder, "glasslink_du.bin"), app);
            var args = Path.Combine(folder, "flash_args");
            File.WriteAllText(args, """
                --flash_mode dio --flash_freq 80m --flash_size 16MB
                0x2000 bootloader/bootloader.bin
                0x20000 glasslink_du.bin
                0x8000 partition_table/partition-table.bin
                0xf000 ota_data_initial.bin
                """);

            var set = FlashSet.Load(folder);
            Assert.Equal(16 * 1024 * 1024, set.FlashSize);
            Assert.Equal(new uint[] { 0x2000, 0x8000, 0xf000, 0x20000 }, set.Parts.Select(p => p.Offset));   // NVS (0x9000) is not written
            Assert.Equal("v1.00-v1.99", set.App.ChipRange);

            var listed = File.ReadAllText(args);                                    // a signed release build: the size is the bootloader's
            File.WriteAllText(args, listed.Replace("--flash_size 16MB", "--flash_size keep"));
            Assert.Throws<InvalidDataException>(() => FlashSet.Load(folder));      // a bootloader without a header says nothing
            File.WriteAllBytes(Path.Combine(folder, "bootloader", "bootloader.bin"), [0xE9, 0x03, 0x02, 0x4F, .. new byte[23_132]]);
            Assert.Equal(16 * 1024 * 1024, FlashSet.Load(folder).FlashSize);
            File.WriteAllText(args, listed);

            File.AppendAllText(args, "\n0x9000 ../../secret.bin\n");                // a path out of the folder is refused
            Assert.Throws<InvalidDataException>(() => FlashSet.Load(folder));
            File.WriteAllText(args, "--flash_size 16MB\n0x2000 bootloader/bootloader.bin\n0x3000 partition_table/partition-table.bin\n0x20000 glasslink_du.bin\n");
            Assert.Throws<InvalidDataException>(() => FlashSet.Load(folder));      // the bootloader would be overwritten
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static readonly byte[] Mac = [0x30, 0xED, 0xA0, 0xE1, 0x23, 0x45];

    [Fact]
    public void A_board_is_known_by_the_MAC_its_DU_reported_or_else_by_the_serial_the_MAC_gives()
    {
        byte[] newer = [0x80, 0xF1, 0xB2, 0x00, 0x00, 0x01];
        var modules = new System.Text.Json.Nodes.JsonObject
        {
            ["ff6932d7c8dd6b878973c439"] = new System.Text.Json.Nodes.JsonObject { ["label"] = "DU2", ["mac"] = "30eda0e12345" },   // a serial from before #24
            [BoardIdentity.SerialFromMac(newer)] = new System.Text.Json.Nodes.JsonObject { ["label"] = "DU3" },                   // one made from its MAC
        };
        Assert.Equal("ff6932d7c8dd6b878973c439", BoardIdentity.KnownSerial(modules, Mac));
        Assert.Equal(BoardIdentity.SerialFromMac(newer), BoardIdentity.KnownSerial(modules, newer));
        Assert.Null(BoardIdentity.KnownSerial(modules, [1, 2, 3, 4, 5, 6]));
        Assert.Null(BoardIdentity.KnownSerial(null, Mac));

        DuInfo Info(string json) => DuInfo.From(System.Text.Json.JsonDocument.Parse(json).RootElement)!;
        Assert.Equal("30eda0e12345", Info("""{"fw":"0.11.0","mac":"30EDA0E12345"}""").Mac);
        Assert.Equal("", Info("""{"fw":"0.11.0","mac":"30:ed:a0"}""").Mac);
        Assert.Equal("", Info("""{"fw":"0.10.0"}""").Mac);
    }

    [Fact]
    public void The_flasher_resets_a_board_into_its_bootloader_and_reads_which_chip_and_DU_it_is()
    {
        var board = new FakeP4Rom(103, Mac);
        var loader = new EspLoader(board);
        var facts = loader.Connect();

        Assert.Equal(103, facts.Revision);
        Assert.Equal("v1.3", facts.RevisionName);
        Assert.Equal(Mac, facts.Mac);
        Assert.Equal(BoardIdentity.SerialFromMac(Mac), facts.DuSerial);
        Assert.Equal(24, facts.DuSerial.Length);
        Assert.Equal(["attach", $"params {16 * 1024 * 1024}"], board.Seen);          // a v1 chip: no register writes
        Assert.False(board.Running);

        loader.HardReset();
        Assert.True(board.Running);                                              // and it runs its app again
    }

    [Fact]
    public void A_v3_chip_gets_its_flash_powered_before_the_flasher_attaches_it()
    {
        var v31 = new FakeP4Rom(301, Mac);
        Assert.Equal(301, new EspLoader(v31).Connect().Revision);
        Assert.True(v31.Seen.IndexOf("write 5011010C") is >= 0 and var w && w < v31.Seen.IndexOf("attach"));
        Assert.Equal(0u, v31.Registers[0x501151B8] & 0x80);                        // the pulse ends low

        var v32 = new FakeP4Rom(302, Mac);                                          // a second session after a UART reset
        v32.Registers[0x5012D034] = 1 << 16;
        v32.Registers[0x501153FC] = 3;
        new EspLoader(v32).Connect();
        Assert.Equal(0u, v32.Registers[0x501153FC]);
        Assert.DoesNotContain("write 5011010C", v32.Seen);
    }

    [Fact]
    public void Install_writes_each_part_erased_and_checked_and_sends_a_garbled_block_again()
    {
        var board = new FakeP4Rom(103, Mac) { GarbleBlock = 2 };
        Array.Fill(board.Flash, (byte)0x00, 0x20000, 0x2000);                     // an old app there
        var data = Enumerable.Range(0, 5_001).Select(i => (byte)(i * 7)).ToArray();   // 5 blocks, not a multiple of 4
        var loader = new EspLoader(board);
        loader.Connect();
        Assert.True(loader.SetBaud(460_800));
        Assert.Equal(460_800, board.BaudRate);

        var progress = new List<double>();
        loader.Write(new FlashPart(0x20000, "glasslink_du.bin", data), progress.Add);

        Assert.Equal(data, board.Flash.AsSpan(0x20000, data.Length).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, board.Flash.AsSpan(0x20000 + data.Length, 3).ToArray());    // padded with FF
        Assert.All(board.Flash.AsSpan(0x20000 + data.Length, 0x2000 - data.Length).ToArray(), b => Assert.Equal(0xFF, b));
        Assert.Contains("begin 20000 5 blocks", board.Seen);
        Assert.Equal([0.2, 0.4, 0.6, 0.8, 1.0], progress);                          // block 2 counted once, after its resend
    }

    [Fact]
    public void A_part_that_does_not_read_back_right_is_an_error_and_so_is_a_chip_that_is_no_P4()
    {
        var board = new FakeP4Rom(103, Mac) { CorruptFlash = true };
        var loader = new EspLoader(board);
        loader.Connect();
        var bad = Assert.Throws<FlashException>(() => loader.Write(new FlashPart(0x8000, "partition-table.bin", new byte[3_072])));
        Assert.Contains("did not arrive intact", bad.Message);

        var other = new FakeP4Rom(103, Mac) { ChipId = 13 };                         // an ESP32-S3
        Assert.Contains("no ESP32-P4", Assert.Throws<FlashException>(() => new EspLoader(other).Connect()).Message);
    }

    [Fact]
    public void A_baud_rate_the_board_does_not_take_falls_back_to_115200_and_still_writes()
    {
        var board = new FakeP4Rom(103, Mac) { TakesBaud = false };
        var loader = new EspLoader(board);
        loader.Connect();
        Assert.False(loader.SetBaud(460_800));
        Assert.Equal(115_200, board.BaudRate);

        loader.Write(new FlashPart(0xf000, "ota_data_initial.bin", Enumerable.Repeat((byte)0xFF, 8_192).ToArray()));
        Assert.Contains("begin f000 8 blocks", board.Seen);
    }
}
