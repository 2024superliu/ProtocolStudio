using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WpfProtocolStudio.Channels;
using WpfProtocolStudio.Engine;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;
using WpfProtocolStudio.Services;

internal static class TcpFileReceiveHarness
{
    private static int Main(string[] args)
    {
        string directory = args.Length > 0
            ? args[0]
            : Path.Combine(Environment.CurrentDirectory, "TcpFileReceiveResult");

        int port;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Directory.CreateDirectory(directory);
        var receiver = new FileTransferReceiver();
        var engine = new ForwardingEngine { IsForwardingEnabled = false };
        var server = new TcpServerChannel { LocalPort = port };
        var client = new TcpClientChannel { TargetIp = "127.0.0.1", TargetPort = port };
        EventHandler<ForwardingDataEventArgs> handler = (sender, eventArgs) =>
        {
            if (eventArgs.Direction == DataDirection.ChannelA_Rx)
                receiver.Process(eventArgs.Direction, eventArgs.Data);
        };

        engine.DataForwarded += handler;
        receiver.ConfigureDirection(DataDirection.ChannelA_Rx, true, directory);

        string[] names;
        byte[][] bodies;
        if (args.Length > 1 && File.Exists(args[1]))
        {
            names = new[] { Path.GetFileName(args[1]) };
            bodies = new[] { File.ReadAllBytes(args[1]) };
        }
        else
        {
            names = new[] { "network-one.txt", "network-two.bin" };
            bodies = new[]
            {
                Encoding.UTF8.GetBytes("TCP complete file number one"),
                new byte[] { 10, 20, 30, 40, 50, 0, 255, 128, 1, 2, 3, 4, 5 }
            };
        }

        try
        {
            if (!server.OpenAsync().GetAwaiter().GetResult())
                throw new InvalidOperationException("TCP 服务端打开失败");
            engine.AttachChannelA(server);
            if (!client.OpenAsync().GetAwaiter().GetResult())
                throw new InvalidOperationException("TCP 客户端打开失败");

            Thread.Sleep(300);

            for (int index = 0; index < names.Length; index++)
            {
                byte[] hash;
                using (var sha256 = SHA256.Create())
                    hash = sha256.ComputeHash(bodies[index]);

                byte[] header = FileTransferProtocol.CreateHeader(
                    names[index], bodies[index].Length, hash);

                int sent = client.SendAsync(header).GetAwaiter().GetResult();
                if (sent != header.Length)
                    throw new IOException("文件头发送不完整");

                int offset = 0;
                while (offset < bodies[index].Length)
                {
                    int count = Math.Min(5, bodies[index].Length - offset);
                    var chunk = new byte[count];
                    Buffer.BlockCopy(bodies[index], offset, chunk, 0, count);
                    sent = client.SendAsync(chunk).GetAwaiter().GetResult();
                    if (sent != count)
                        throw new IOException("文件正文发送不完整");
                    offset += count;
                }
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                bool ready = true;
                foreach (string name in names)
                    ready &= File.Exists(Path.Combine(directory, "A_RX_" + name));
                if (ready) break;
                Thread.Sleep(50);
            }

            for (int index = 0; index < names.Length; index++)
            {
                string path = Path.Combine(directory, "A_RX_" + names[index]);
                if (!File.Exists(path))
                    throw new FileNotFoundException("未生成接收文件", path);

                byte[] saved = File.ReadAllBytes(path);
                bool exact = saved.Length == bodies[index].Length;
                if (exact)
                {
                    for (int byteIndex = 0; byteIndex < saved.Length; byteIndex++)
                    {
                        if (saved[byteIndex] != bodies[index][byteIndex])
                        {
                            exact = false;
                            break;
                        }
                    }
                }

                Console.WriteLine(
                    Path.GetFileName(path) +
                    ": sent=" + bodies[index].Length +
                    ", saved=" + saved.Length +
                    ", exact=" + exact);

                if (!exact)
                    throw new InvalidDataException("文件内容不一致：" + path);
            }

            Console.WriteLine("TCP port=" + port);
            Console.WriteLine("Directory=" + directory);
            return 0;
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
