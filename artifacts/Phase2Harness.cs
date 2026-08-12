using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;
using WpfProtocolStudio.Interfaces;
using WpfProtocolStudio.Models;
using WpfProtocolStudio.Services;

internal static class Phase2Harness
{
    private static int Main(string[] args)
    {
        try
        {
            TestFixedLength();
            TestDelimiter();
            TestTimeInterval();
            TestDirectionIsolation();
            TestChecksums();
            TestPlugins(args.Length > 0 ? args[0] : "Plugins");
            Console.WriteLine("PHASE2_ALL_TESTS_PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PHASE2_TEST_FAILED: " + ex);
            return 1;
        }
    }

    private static ForwardingDataEventArgs Packet(DataDirection direction, params byte[] data)
    {
        return new ForwardingDataEventArgs(direction, data, "test");
    }

    private static void TestFixedLength()
    {
        using (var service = new DataFramingService())
        {
            var frames = new List<ForwardingDataEventArgs>();
            service.FrameReady += (_, e) => frames.Add(e);
            service.Configure(FrameMode.FixedLength, 4, null, 50);
            service.Process(Packet(DataDirection.ChannelA_Rx, 1, 2, 3));
            service.Process(Packet(DataDirection.ChannelA_Rx, 4, 5, 6, 7, 8, 9));
            Assert(frames.Count == 2, "Fixed length frame count");
            AssertEqual(frames[0].Data, new byte[] { 1, 2, 3, 4 }, "Fixed frame 1");
            AssertEqual(frames[1].Data, new byte[] { 5, 6, 7, 8 }, "Fixed frame 2");
            service.FlushAll();
            AssertEqual(frames[2].Data, new byte[] { 9 }, "Fixed remainder");
        }
        Console.WriteLine("FR27_FIXED_LENGTH=PASS");
    }

    private static void TestDelimiter()
    {
        using (var service = new DataFramingService())
        {
            var frames = new List<ForwardingDataEventArgs>();
            service.FrameReady += (_, e) => frames.Add(e);
            service.Configure(FrameMode.Delimiter, 8, new byte[] { 13, 10 }, 50);
            service.Process(Packet(DataDirection.ChannelA_Rx, Encoding.ASCII.GetBytes("ABC\r")));
            service.Process(Packet(DataDirection.ChannelA_Rx, Encoding.ASCII.GetBytes("\nDEF\r\n")));
            Assert(frames.Count == 2, "Delimiter frame count");
            AssertEqual(frames[0].Data, Encoding.ASCII.GetBytes("ABC\r\n"), "Delimiter frame 1");
            AssertEqual(frames[1].Data, Encoding.ASCII.GetBytes("DEF\r\n"), "Delimiter frame 2");
        }
        Console.WriteLine("FR27_DELIMITER=PASS");
    }

    private static void TestTimeInterval()
    {
        using (var service = new DataFramingService())
        using (var ready = new ManualResetEventSlim(false))
        {
            ForwardingDataEventArgs frame = null;
            service.FrameReady += (_, e) => { frame = e; ready.Set(); };
            service.Configure(FrameMode.TimeInterval, 8, null, 80);
            service.Process(Packet(DataDirection.ChannelB_Rx, 1, 2));
            Thread.Sleep(20);
            service.Process(Packet(DataDirection.ChannelB_Rx, 3, 4));
            Assert(ready.Wait(1000), "Idle timer did not fire");
            AssertEqual(frame.Data, new byte[] { 1, 2, 3, 4 }, "Idle time frame");
        }
        Console.WriteLine("FR27_TIME_INTERVAL=PASS");
    }

    private static void TestDirectionIsolation()
    {
        using (var service = new DataFramingService())
        {
            var frames = new List<ForwardingDataEventArgs>();
            service.FrameReady += (_, e) => frames.Add(e);
            service.Configure(FrameMode.FixedLength, 3, null, 50);
            service.Process(Packet(DataDirection.ChannelA_Rx, 1, 2));
            service.Process(Packet(DataDirection.ChannelB_Rx, 7, 8, 9));
            service.Process(Packet(DataDirection.ChannelA_Rx, 3));
            Assert(frames.Count == 2, "Direction isolation count");
            Assert(frames[0].Direction == DataDirection.ChannelB_Rx, "B direction identity");
            Assert(frames[1].Direction == DataDirection.ChannelA_Rx, "A direction identity");
            AssertEqual(frames[1].Data, new byte[] { 1, 2, 3 }, "A direction buffer");
        }
        Console.WriteLine("FR27_DIRECTION_ISOLATION=PASS");
    }

    private static void TestChecksums()
    {
        byte[] vector = Encoding.ASCII.GetBytes("123456789");
        Assert(ChecksumService.Calculate(ChecksumAlgorithm.Crc16Modbus, vector) == "0x4B37", "CRC16 MODBUS vector");
        Assert(ChecksumService.Calculate(ChecksumAlgorithm.Crc16CcittFalse, vector) == "0x29B1", "CRC16 CCITT vector");
        Assert(ChecksumService.Calculate(ChecksumAlgorithm.Crc32, vector) == "0xCBF43926", "CRC32 vector");
        Console.WriteLine("FR28_CRC16_MODBUS=0x4B37 PASS");
        Console.WriteLine("FR28_CRC16_CCITT_FALSE=0x29B1 PASS");
        Console.WriteLine("FR28_CRC32=0xCBF43926 PASS");
    }

    private static void TestPlugins(string pluginDirectory)
    {
        ProtocolPluginLoadResult result = ProtocolPluginService.Load(pluginDirectory);
        IProtocolParser parser = result.Parsers.FirstOrDefault(item => item.Name == "Phase2TestProtocol");
        Assert(parser != null, "External plugin not found: " + string.Join(" | ", result.Errors));
        Assert(parser.CanParse(new byte[] { 0xAA, 0x02 }), "Plugin CanParse");
        ProtocolParseResult parsed = parser.Parse(new byte[] { 0xAA, 0x02 });
        Assert(parsed.Success && parsed.Fields["Command"] == "0xAA", "Plugin Parse");
        Assert(result.Parsers.Count >= 2, "Built-in parser missing");
        Console.WriteLine("FR29_EXTERNAL_PLUGIN_LOAD=PASS");
        Console.WriteLine("FR29_PLUGIN_PARSE=PASS");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual(byte[] actual, byte[] expected, string message)
    {
        Assert(actual != null && actual.SequenceEqual(expected), message);
    }
}
