// 联机复现探针：模拟星露谷 PC 客户端的局域网发现 + 连接流程，
// 用于自主复现"其他 PC 连接鸿蒙主机时主机崩溃"（不需要真游戏）。
// 用法: dotnet lidgren-probe.dll <host> [port=24642] [waitSec=40]
// 注意：Lidgren 用 Resolving 事件从固定路径加载（deps.json 不声明它的运行时资产）。
using System;
using System.Runtime.Loader;
using System.Threading;

class Program
{
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name == "Lidgren.Network")
                return ctx.LoadFromAssemblyPath("/tmp/Lidgren.Network.orig.dll");
            return null;
        };
        return Run(args);
    }

    static int Run(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 ? int.Parse(args[1]) : 24642;
        int waitSec = args.Length > 2 ? int.Parse(args[2]) : 40;

        var config = new Lidgren.Network.NetPeerConfiguration("StardewValley")
        {
            ConnectionTimeout = 30f,
        };
        config.EnableMessageType(Lidgren.Network.NetIncomingMessageType.DiscoveryResponse);
        var client = new Lidgren.Network.NetClient(config);
        client.Start();

        Console.WriteLine($"[probe] DiscoverKnownPeer {host}:{port}");
        client.DiscoverKnownPeer(host, port);

        Lidgren.Network.NetConnection conn = null;
        string lastStatus = "";
        var directSent = false;
        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(waitSec);
        while (DateTime.UtcNow < deadline)
        {
            // 3 秒没等到 DiscoveryResponse 就直接 Connect（诊断用；真实客户端只等响应）
            if (conn == null && !directSent && (DateTime.UtcNow - start).TotalSeconds > 3)
            {
                directSent = true;
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), port);
                conn = client.Connect(ep);
                Console.WriteLine("[probe] direct Connect() sent (no discovery response)");
            }
            Lidgren.Network.NetIncomingMessage msg;
            while ((msg = client.ReadMessage()) != null)
            {
                switch (msg.MessageType)
                {
                    case Lidgren.Network.NetIncomingMessageType.DiscoveryResponse:
                        Console.WriteLine($"[probe] DiscoveryResponse from {msg.SenderEndPoint}");
                        try
                        {
                            int ver = msg.ReadInt32();
                            string app = msg.ReadString();
                            Console.WriteLine($"[probe]   protocol={ver} app={app}");
                        }
                        catch (Exception e) { Console.WriteLine($"[probe]   parse: {e.Message}"); }
                        if (conn == null)
                        {
                            conn = client.Connect(msg.SenderEndPoint);
                            Console.WriteLine("[probe] Connect() sent");
                        }
                        break;
                    case Lidgren.Network.NetIncomingMessageType.StatusChanged:
                        lastStatus = msg.ReadString();
                        Console.WriteLine($"[probe] Status={conn?.Status} ({lastStatus})");
                        break;
                    case Lidgren.Network.NetIncomingMessageType.Data:
                        Console.WriteLine($"[probe] Data len={msg.LengthBytes} type={msg.ReadByte()}");
                        break;
                    case Lidgren.Network.NetIncomingMessageType.WarningMessage:
                    case Lidgren.Network.NetIncomingMessageType.ErrorMessage:
                    case Lidgren.Network.NetIncomingMessageType.DebugMessage:
                        Console.WriteLine($"[probe] {msg.MessageType}: {msg.ReadString()}");
                        break;
                }
                client.Recycle(msg);
            }
            Thread.Sleep(50);
        }
        Console.WriteLine($"[probe] final: conn={conn?.Status} lastStatus={lastStatus}");
        client.Shutdown("bye");
        return 0;
    }
}
