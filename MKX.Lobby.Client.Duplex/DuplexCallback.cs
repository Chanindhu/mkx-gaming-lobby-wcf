using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MKX.Lobby.Contracts;

namespace MKX.Lobby.Client.Duplex
{
    // Bridge for server-pushed events -> safe UI updates
    public class DuplexCallback : ILobbyCallback
    {
        private readonly Action<Action> _ui;

        // Track current room to scope messages/files
        private string _currentRoom = null;

        // Per-room public-chat history (restore on rejoin)
        private readonly Dictionary<string, List<string>> _roomChatHistory =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Bind directly to WPF controls (RoomWindow mirrors Chat into its own _chatItems)
        public ObservableCollection<string> Chat { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Users { get; } = new ObservableCollection<string>();
        public ObservableCollection<LobbyRoomInfo> Rooms { get; } = new ObservableCollection<LobbyRoomInfo>();

        // Events consumed by windows
        public event Action<PrivateMessage> PrivateMessageReceived; // PM text
        public event Action<SharedFile> FileReceived;               // room files
        public event Action<SharedFile> PrivateFileReceived;        // PM files

        public DuplexCallback(Action<Action> ui) { _ui = ui; }

        // Call after successful JoinRoom
        public void ResetForRoom(string room)
        {
            _ui(() =>
            {
                _currentRoom = room;
                Chat.Clear();

                if (!_roomChatHistory.TryGetValue(room, out var list))
                {
                    list = new List<string>();
                    _roomChatHistory[room] = list;
                }
                foreach (var line in list) Chat.Add(line);
            });
        }

        // ---- Public room chat (kept in Chat + history) ----
        public void OnPublicMessage(ChatMessage msg)
        {
            if (msg == null) return;
            var local = ToLocal(msg.At);
            var line = $"[{local:HH:mm}] {msg.From}: {msg.Text}";

            _ui(() =>
            {
                if (!_roomChatHistory.TryGetValue(msg.Room ?? "", out var list))
                {
                    list = new List<string>();
                    _roomChatHistory[msg.Room ?? ""] = list;
                }
                list.Add(line);

                if (!string.IsNullOrEmpty(_currentRoom) &&
                    !string.Equals(msg.Room, _currentRoom, StringComparison.OrdinalIgnoreCase))
                    return;

                Chat.Add(line);
            });
        }

        // ---- Private messages (DO NOT add to Chat) ----
        public void OnPrivateMessage(PrivateMessage pm)
        {
            if (pm == null) return;
            _ui(() => PrivateMessageReceived?.Invoke(pm));
        }

        // ---- Room file shares (DO NOT add to Chat; emit event only) ----
        public void OnFileShared(SharedFile f)
        {
            if (f == null) return;

            _ui(() =>
            {
                // Only deliver to the active room window
                if (!string.IsNullOrEmpty(_currentRoom) &&
                    !string.Equals(f.Room, _currentRoom, StringComparison.OrdinalIgnoreCase))
                    return;

                FileReceived?.Invoke(f);
            });
        }

        // PM file shares (forward only)
        public void OnPrivateFileShared(SharedFile f)
        {
            if (f == null) return;
            f.At = ToLocal(f.At);
            _ui(() => PrivateFileReceived?.Invoke(f));
        }

        /// Replaces the current users collection with the provided list (sorted),
        public void OnUserListChanged(string room, List<string> users)
        {
            _ui(() =>
            {
                Users.Clear();
                users.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var u in users) Users.Add(u);
            });
        }

        /// Replaces the current room list collection with the provided, sorted list.
        /// Triggered when rooms are created or removed.
        public void OnRoomListChanged(List<LobbyRoomInfo> rooms)
        {
            _ui(() =>
            {
                Rooms.Clear();
                rooms.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var r in rooms) Rooms.Add(r);
            });
        }
        /// Converts a DateTime of unknown/UTC kind into local time in a safe, consistent way.
        private static DateTime ToLocal(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) return dt;
            if (dt.Kind == DateTimeKind.Utc) return dt.ToLocalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        }
    }
}
