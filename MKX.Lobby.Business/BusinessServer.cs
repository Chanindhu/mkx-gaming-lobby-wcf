using System;
using System.ServiceModel;
using System.Collections.Generic;
using MKX.Lobby.Contracts;

namespace MKX.Lobby.Business
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.PerSession, ConcurrencyMode = ConcurrencyMode.Reentrant)]
    public sealed class BusinessServer : ILobbyService, ILobbyCallback, IDisposable
    {
        private readonly ILobbyCallback _clientCb;
        private readonly ILobbyService _data;
        private readonly DuplexChannelFactory<ILobbyService> _fac;
        private const string DataServiceAddr = "net.tcp://127.0.0.1:9090/MKXLobby/Service";


        /// Creates a new business server session:
        /// - Captures the caller's duplex callback channel.
        /// - Builds a NetTcpBinding with generous quotas for chat/file payloads.
        /// - Opens a duplex channel to the underlying data service and proxies calls to it.
      
        public BusinessServer()
        {
            _clientCb = OperationContext.Current?.GetCallbackChannel<ILobbyCallback>();

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

            var ctx = new InstanceContext(this);
            _fac = new DuplexChannelFactory<ILobbyService>(ctx, binding, new EndpointAddress(DataServiceAddr));
            _data = _fac.CreateChannel();
        }
        // Forwards

       
        /// Forwards a login attempt to the data service; returns false if the username is already in use
        public bool Login(string username) => _data.Login(username);

        /// Forwards a logout request to the data service for the specified user.
        public void Logout(string username) => _data.Logout(username);

        
        /// Retrieves the current list of rooms from the data service.
        public List<LobbyRoomInfo> ListRooms() => _data.ListRooms();

     
        /// Requests the data service to create a room; returns false if the room already exists.
        public bool CreateRoom(string roomName) => _data.CreateRoom(roomName);

        
        /// Asks the data service to join the given user to the specified room; returns success status.
        public bool JoinRoom(string username, string roomName) => _data.JoinRoom(username, roomName);

 
        /// Requests the data service to remove the user from their current room.
        public void LeaveRoom(string username) => _data.LeaveRoom(username);

       
        /// Sends a public room message via the data service on behalf of the user.
        public void SendMessage(string username, string text) => _data.SendMessage(username, text);

        
        /// Sends a private message between two users via the data service.
        public void SendPrivateMessage(string from, string to, string text) => _data.SendPrivateMessage(from, to, text);

        
        /// Shares a file to the user's current room via the data service.
        public void ShareFile(string username, SharedFile file) => _data.ShareFile(username, file);

     
        /// Sends a file privately between two users via the data service.
        public void SendPrivateFile(string from, string to, SharedFile file) => _data.SendPrivateFile(from, to, file);

       
        /// Polling API: returns a snapshot of new events since the supplied sequence number.
        public LobbySnapshot GetUpdates(string username, long lastSeq) => _data.GetUpdates(username, lastSeq);

        // Callback relays

     
        /// Relay from data service: pushes the updated room list to the original client callback (best-effort).
        public void OnRoomListChanged(List<LobbyRoomInfo> rooms) { try { _clientCb?.OnRoomListChanged(rooms); } catch { } }

       
        /// Relay from data service: pushes the updated user list for a room to the original client callback (best-effort).
        public void OnUserListChanged(string room, List<string> users) { try { _clientCb?.OnUserListChanged(room, users); } catch { } }

        
        /// Relay from data service: forwards a public chat message to the original client callback (best-effort).
        public void OnPublicMessage(ChatMessage msg) { try { _clientCb?.OnPublicMessage(msg); } catch { } }

        
        /// Relay from data service: forwards a private message to the original client callback (best-effort).
        public void OnPrivateMessage(PrivateMessage pm) { try { _clientCb?.OnPrivateMessage(pm); } catch { } }

        /// Relay from data service: forwards a room file share to the original client callback (best-effort).
        public void OnFileShared(SharedFile f) { try { _clientCb?.OnFileShared(f); } catch { } }

       
        /// Relay from data service: forwards a private file share to the original client callback (best-effort).
        public void OnPrivateFileShared(SharedFile f) { try { _clientCb?.OnPrivateFileShared(f); } catch { } }

   
        /// Disposes the duplex channel factory gracefully; aborts on failure.
        public void Dispose()
        {
            try { (_fac as ICommunicationObject)?.Close(); } catch { try { (_fac as ICommunicationObject)?.Abort(); } catch { } }
        }
    }
}
