using System;
using System.ServiceModel;
using MKX.Lobby.Contracts;

namespace MKX.Lobby.Client.Polling
{
    public class LobbyClient : IDisposable
    {
        public ILobbyService Channel { get; private set; }
        private DuplexChannelFactory<ILobbyService> _factory;

        //Business endpoint

        // Creates a net.tcp WCF channel to the Business service for the polling client.
        // Uses a dummy callback (NullCallback) because this client polls instead of receiving pushes.
        public LobbyClient(string addr = "net.tcp://127.0.0.1:9090/MKXLobby/Business")
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

            // Contract has a CallbackContract; we pass a dummy callback for polling.
            var ctx = new InstanceContext(new NullCallback());
            _factory = new DuplexChannelFactory<ILobbyService>(ctx, binding, new EndpointAddress(addr));
            Channel = _factory.CreateChannel();
        }

        // Disposes the channel factory by attempting a graceful close (best-effort; errors ignored).
        public void Dispose()
        {
            try { (_factory as ICommunicationObject)?.Close(); } catch { }
        }
    }
}
