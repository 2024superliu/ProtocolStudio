using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using WpfProtocolStudio.Channels;
using WpfProtocolStudio.Engine;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;
using WpfProtocolStudio.Services;

internal static class UartAssistRawHarness
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        string outputDirectory = args[0];
        string sourceFile = args[1];
        Directory.CreateDirectory(outputDirectory);

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var receiver = new RawBurstFileReceiver();
        var engine = new ForwardingEngine { IsForwardingEnabled = false };
        var server = new TcpServerChannel { LocalPort = port };
        var client = new TcpClientChannel { TargetIp = "127.0.0.1", TargetPort = port };
        EventHandler<ForwardingDataEventArgs> handler = (sender, eventArgs) =>
        {
            if (eventArgs.Direction == DataDirection.ChannelA_Rx)
                receiver.Process(eventArgs.Direction, eventArgs.Data);
        };

        engine.DataForwarded += handler;
        receiver.ConfigureDirection(DataDirection.ChannelA_Rx, true, outputDirectory);

        try
        {
            if (!server.OpenAsync().GetAwaiter().GetResult()) throw new Exception("服务端打开失败");
            engine.AttachChannelA(server);
            if (!client.OpenAsync().GetAwaiter().GetResult()) throw new Exception("客户端打开失败");
            Thread.Sleep(300);

            byte[] buffer = new byte[8192];
            using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    byte[] block = read == buffer.Length ? buffer : buffer.Take(read).ToArray();
                    int sent = client.SendAsync(block).GetAwaiter().GetResult();
                    if (sent != read) throw new IOException("发送不完整");
                    Thread.Sleep(150);
                }
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            string savedFile = null;
            while (DateTime.UtcNow < deadline)
            {
                savedFile = Directory.GetFiles(outputDirectory, "A_RX_*.docx").FirstOrDefault();
                if (savedFile != null) break;
                Thread.Sleep(100);
            }

            if (savedFile == null) throw new FileNotFoundException("未生成 DOCX 接收文件");
            byte[] sourceHash;
            byte[] savedHash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(sourceFile)) sourceHash = sha.ComputeHash(stream);
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(savedFile)) savedHash = sha.ComputeHash(stream);

            bool exact = sourceHash.SequenceEqual(savedHash);
            Console.WriteLine("Source=" + sourceFile);
            Console.WriteLine("Saved=" + savedFile);
            Console.WriteLine("SourceBytes=" + new FileInfo(sourceFile).Length);
            Console.WriteLine("SavedBytes=" + new FileInfo(savedFile).Length);
            Console.WriteLine("HashEqual=" + exact);
            return exact ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            engine.DataForwarded -= handler;
            engine.AttachChannelA(null);
            try { client.CloseAsync().GetAwaiter().GetResult(); } catch { }
            try { server.CloseAsync().GetAwaiter().GetResult(); } catch { }
            client.Dispose();
            server.Dispose();
            receiver.Dispose();
        }
    }
}
