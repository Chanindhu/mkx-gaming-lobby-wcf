using System.Collections.Generic;
using MKX.Lobby.Contracts;

namespace MKX.Lobby.Client.Polling
{
    // No-op implementation used by the polling client.
    internal sealed class NullCallback : ILobbyCallback
    {
        public void OnRoomListChanged(List<LobbyRoomInfo> rooms) { }
        public void OnUserListChanged(string room, List<string> users) { }
        public void OnPublicMessage(ChatMessage msg) { }
        public void OnPrivateMessage(PrivateMessage pm) { }
        public void OnFileShared(SharedFile f) { }
        public void OnPrivateFileShared(SharedFile f) { } // required by updated interface
    }
}
