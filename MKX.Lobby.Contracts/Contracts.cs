using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.ServiceModel;

namespace MKX.Lobby.Contracts
{
    [DataContract]
    public class LobbyRoomInfo
    {
        [DataMember] public string Name { get; set; } = "";
        [DataMember] public int PlayerCount { get; set; }
    }

    [DataContract]
    public class ChatMessage
    {
        [DataMember] public string From { get; set; } = "";
        [DataMember] public string Room { get; set; } = "";
        [DataMember] public string Text { get; set; } = "";
        [DataMember] public DateTime At { get; set; } = DateTime.UtcNow;
    }

    [DataContract]
    public class PrivateMessage
    {
        [DataMember] public string From { get; set; } = "";
        [DataMember] public string To { get; set; } = "";
        [DataMember] public string Text { get; set; } = "";
        [DataMember] public DateTime At { get; set; } = DateTime.UtcNow;
    }

    [DataContract]
    public class SharedFile
    {
        [DataMember] public string FileName { get; set; } = "";
        [DataMember] public string ContentType { get; set; } = "application/octet-stream";
        [DataMember] public byte[] Bytes { get; set; } = new byte[0];
        [DataMember] public string Room { get; set; } = "";
        [DataMember] public string From { get; set; } = "";
        [DataMember] public string To { get; set; } = "";   // PM targeting
        [DataMember] public DateTime At { get; set; } = DateTime.UtcNow;
    }

    [DataContract]
    public class LobbySnapshot
    {
        [DataMember] public long NextSeq { get; set; }
        [DataMember] public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        [DataMember] public List<PrivateMessage> PrivateMessages { get; set; } = new List<PrivateMessage>();
        [DataMember] public List<SharedFile> PrivateFiles { get; set; } = new List<SharedFile>(); // <-- PM files
        [DataMember] public List<string> Users { get; set; } = new List<string>();
        [DataMember] public List<SharedFile> Files { get; set; } = new List<SharedFile>();
    }

    // Duplex callback contract (server -> client)
    public interface ILobbyCallback
    {
        // Pushes the full room list to the client (e.g., after create/delete).
        [OperationContract(IsOneWay = true)] void OnRoomListChanged(List<LobbyRoomInfo> rooms);

        // Pushes the active user list for a room (e.g., after join/leave).
        [OperationContract(IsOneWay = true)] void OnUserListChanged(string room, List<string> users);

        // Delivers a public room message to the client.
        [OperationContract(IsOneWay = true)] void OnPublicMessage(ChatMessage msg);

        // Delivers a private message to the client.
        [OperationContract(IsOneWay = true)] void OnPrivateMessage(PrivateMessage pm);

        // Delivers a room-scoped shared file to the client.
        [OperationContract(IsOneWay = true)] void OnFileShared(SharedFile f);

        // Delivers a private file (one-to-one) to the client.
        [OperationContract(IsOneWay = true)] void OnPrivateFileShared(SharedFile f); // PM files (also used by polling via snapshot)
    }

    [ServiceContract(CallbackContract = typeof(ILobbyCallback))]
    public interface ILobbyService
    {
        // Attempts to reserve a username (returns false if already taken).
        [OperationContract] bool Login(string username);

        // Releases a username and any associated session state.
        [OperationContract] void Logout(string username);

        // Retrieves the current list of rooms (with player counts).
        [OperationContract] List<LobbyRoomInfo> ListRooms();

        // Creates a new room (returns false if name exists or invalid).
        [OperationContract] bool CreateRoom(string roomName);

        // Adds a user to a room (returns false on failure).
        [OperationContract] bool JoinRoom(string username, string roomName);

        // Removes a user from their current room.
        [OperationContract] void LeaveRoom(string username);

        // Sends a public message to the user's current room.
        [OperationContract] void SendMessage(string username, string text);

        // Sends a private message from one user to another.
        [OperationContract] void SendPrivateMessage(string from, string to, string text);

        // Shares a file to the user's current room.
        [OperationContract] void ShareFile(string username, SharedFile file);

        // Sends a file privately from one user to another.
        [OperationContract] void SendPrivateFile(string from, string to, SharedFile file);

        // Returns new events since the given sequence for a polling client.
        [OperationContract] LobbySnapshot GetUpdates(string username, long lastSeq);
    }
}
