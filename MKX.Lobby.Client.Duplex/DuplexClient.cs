using System;
using System.ServiceModel;
using MKX.Lobby.Contracts;

namespace MKX.Lobby.Client.Duplex
{
    public class DuplexClient : IDisposable
    {
        public ILobbyService Channel { get; private set; }
        private DuplexChannelFactory<ILobbyService> _factory;

        // Business endpoint

        // Creates a duplex net.tcp channel to the Business service, registers the given
        // callback instance to receive server push events, and exposes the proxy via Channel.
        public DuplexClient(DuplexCallback cb, string addr = "net.tcp://127.0.0.1:9090/MKXLobby/Business")
        {
            var binding = new NetTcpBinding(SecurityMode.None)
            {
                MaxReceivedMessageSize = 20_000_000,  // 20 MB
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

            var ctx = new InstanceContext(cb); // register callback instance
            _factory = new DuplexChannelFactory<ILobbyService>(ctx, binding, new EndpointAddress(addr));
            Channel = _factory.CreateChannel();
        }

        // Attempts to gracefully close the duplex channel factory (best-effort; ignores errors).
        public void Dispose()
        {
            try { (_factory as ICommunicationObject)?.Close(); } catch { }
        }
    }
}
