using System;
using System.ServiceModel;
using System.ServiceModel.Description;
using MKX.Lobby.Server;
using MKX.Lobby.Contracts;
using MKX.Lobby.Business; // make sure Server.Host references the Business project

class Program
{
    static void Main()
    {
        // Base address used by both tiers (data + business) with different relative paths.
        var baseAddr = new Uri("net.tcp://0.0.0.0:9090/MKXLobby");

        // Shared NetTcp binding for both services (large quotas; no transport security for local demo).
        var binding = new NetTcpBinding(SecurityMode.None)
        {
            MaxReceivedMessageSize = 20_000_000,
            MaxBufferSize = 20_000_000,
            MaxBufferPoolSize = 20_000_000,
            TransferMode = TransferMode.Buffered,
            OpenTimeout = TimeSpan.FromSeconds(30),
            CloseTimeout = TimeSpan.FromSeconds(30),
            SendTimeout = TimeSpan.FromMinutes(2),
            ReceiveTimeout = TimeSpan.FromMinutes(10)
        };
        binding.ReaderQuotas.MaxArrayLength = 20_000_000;
        binding.ReaderQuotas.MaxStringContentLength = 20_000_000;

        // ===== DATA tier host =====
        // Hosts the core LobbyService (state + events) at /Service.
        var dataHost = new ServiceHost(typeof(LobbyService), baseAddr);
        dataHost.AddServiceEndpoint(typeof(ILobbyService), binding, "Service");

        // Ensure sensible throttling so many clients can connect concurrently.
        var dataThrottle = dataHost.Description.Behaviors.Find<ServiceThrottlingBehavior>();
        if (dataThrottle == null)
        {
            dataThrottle = new ServiceThrottlingBehavior
            {
                MaxConcurrentCalls = 100,
                MaxConcurrentSessions = 100,
                MaxConcurrentInstances = 100
            };
            dataHost.Description.Behaviors.Add(dataThrottle);
        }

        // Expose MEX (metadata exchange) once on the data tier so clients can generate proxies.
        var dataMeta = dataHost.Description.Behaviors.Find<ServiceMetadataBehavior>();
        if (dataMeta == null)
        {
            dataMeta = new ServiceMetadataBehavior();
            dataHost.Description.Behaviors.Add(dataMeta);
        }
        dataHost.AddServiceEndpoint(
            ServiceMetadataBehavior.MexContractName,
            MetadataExchangeBindings.CreateMexTcpBinding(),
            "mex"
        );

        // ===== BUSINESS tier host =====
        // Hosts the BusinessServer (pass-through/adapter) at /Business.
        var bizHost = new ServiceHost(typeof(BusinessServer), baseAddr);
        bizHost.AddServiceEndpoint(typeof(ILobbyService), binding, "Business");

        // Throttling for the business tier as well.
        var bizThrottle = bizHost.Description.Behaviors.Find<ServiceThrottlingBehavior>();
        if (bizThrottle == null)
        {
            bizThrottle = new ServiceThrottlingBehavior
            {
                MaxConcurrentCalls = 100,
                MaxConcurrentSessions = 100,
                MaxConcurrentInstances = 100
            };
            bizHost.Description.Behaviors.Add(bizThrottle);
        }

        //  DO NOT add a mex endpoint here (would collide with the one above)

        try
        {
            // Open in this order so MEX is up before clients try to connect to business.
            dataHost.Open();
            bizHost.Open();

            // Console banner with the exact endpoints exposed.
            Console.WriteLine("Data tier    : net.tcp://0.0.0.0:9090/MKXLobby/Service");
            Console.WriteLine("Business tier: net.tcp://0.0.0.0:9090/MKXLobby/Business");
            Console.WriteLine("MEX          : net.tcp://0.0.0.0:9090/MKXLobby/mex");
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();

            // Graceful shutdown.
            bizHost.Close();
            dataHost.Close();
        }
        catch (Exception ex)
        {
            // Fatal host error: print details and abort both hosts.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FATAL: " + ex.Message);
            Console.WriteLine(ex);
            Console.ResetColor();
            try { bizHost.Abort(); } catch { }
            try { dataHost.Abort(); } catch { }
        }
    }
}
