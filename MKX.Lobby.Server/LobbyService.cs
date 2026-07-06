using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using MKX.Lobby.Contracts;
using System.IO;
using System.Text;

namespace MKX.Lobby.Server
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
    public class LobbyService : ILobbyService
    {
        private readonly object _lock = new object();

        private readonly HashSet<string> _online =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // room name comparer is case-insensitive
        private readonly ConcurrentDictionary<string, HashSet<string>> _roomUsers =
            new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // username comparer is case-insensitive
        private readonly ConcurrentDictionary<string, string> _userRoom =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, ILobbyCallback> _callbacks =
            new ConcurrentDictionary<string, ILobbyCallback>(StringComparer.OrdinalIgnoreCase);

        // global event log for polling clients
        private long _seq = 0;
        private readonly SortedList<long, Action<ILobbyCallback>> _events =
            new SortedList<long, Action<ILobbyCallback>>();

        // Authenticates and registers a user session; ensures uniqueness; stores duplex callback channel if available.
        public bool Login(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            lock (_lock)
            {
                if (_online.Contains(username)) return false; // unique usernames
                _online.Add(username);
            }

            var cb = OperationContext.Current != null
                ? OperationContext.Current.GetCallbackChannel<ILobbyCallback>()
                : null;

            if (cb != null) _callbacks[username] = cb; // duplex gets callbacks; polling uses a dummy

            return true;
        }

        // Terminates a user session: leaves any room, removes from online set, drops callback registration.
        public void Logout(string username)
        {
            LeaveRoom(username);
            lock (_lock) { _online.Remove(username); }
            _callbacks.TryRemove(username, out _);
        }

        // Returns a sorted list of existing rooms with player counts.
        public List<LobbyRoomInfo> ListRooms()
        {
            return _roomUsers
                .Select(kv => new LobbyRoomInfo { Name = kv.Key, PlayerCount = kv.Value.Count })
                .OrderBy(r => r.Name)
                .ToList();
        }

        // >>> UPDATED: return false for duplicates/blank instead of throwing
        // Creates a new room if name is valid and not already present; broadcasts updated room list.
        public bool CreateRoom(string roomName)
        {
            roomName = (roomName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(roomName))
                return false;

            // TryAdd returns false if a room with this name already exists (case-insensitive)
            if (!_roomUsers.TryAdd(roomName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                return false;

            BroadcastRoomsChanged();
            return true;
        }
        // <<< UPDATED

        // >>> UPDATED: do NOT auto-create; require room to already exist
        // Moves the user into an existing room (no implicit create), updates user lists and room list.
        public bool JoinRoom(string username, string roomName)
        {
            username = (username ?? string.Empty).Trim();
            roomName = (roomName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(roomName))
                return false;

            // Require the room to already exist (no implicit creation here)
            if (!_roomUsers.ContainsKey(roomName))
                return false;

            // Move user from any previous room
            LeaveRoom(username);

            if (!_roomUsers.TryGetValue(roomName, out var set))
                return false; // room removed between the checks

            lock (set) set.Add(username);
            _userRoom[username] = roomName;

            PushUsers(roomName);
            BroadcastRoomsChanged();
            return true;
        }
        // <<< UPDATED

        // Removes the user from their current room (if any) and pushes updated user/room lists.
        public void LeaveRoom(string username)
        {
            if (_userRoom.TryRemove(username, out var room))
            {
                if (_roomUsers.TryGetValue(room, out var set))
                {
                    lock (set) set.Remove(username);
                }
                PushUsers(room);
                BroadcastRoomsChanged();
            }
        }

        // Broadcasts a public chat message to all users in the sender's room and logs an event for polling clients.
        public void SendMessage(string username, string text)
        {
            if (!_userRoom.TryGetValue(username, out var room)) return;

            var msg = new ChatMessage
            {
                From = username,
                Room = room,
                Text = text,
                At = DateTime.UtcNow
            };

            Multicast(cb => cb.OnPublicMessage(msg), room);
            EnqueueEvent(cb => cb.OnPublicMessage(msg));
        }

        // Validates and delivers a private message between two users in the same room; enqueues for polling clients.
        public void SendPrivateMessage(string from, string to, string text)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(text))
                throw new FaultException("Invalid private message.");

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                throw new FaultException("You can't send a private message to yourself.");

            string roomFrom, roomTo;

            lock (_lock)
            {
                if (!_online.Contains(from))
                    throw new FaultException("Sender is not online.");

                if (!_online.Contains(to))
                    throw new FaultException("Target user is not online.");
            }

            if (!_userRoom.TryGetValue(from, out roomFrom) || string.IsNullOrEmpty(roomFrom))
                throw new FaultException("Sender is not in a room.");

            if (!_userRoom.TryGetValue(to, out roomTo) || string.IsNullOrEmpty(roomTo))
                throw new FaultException("Target user is not in a room.");

            if (!string.Equals(roomFrom, roomTo, StringComparison.OrdinalIgnoreCase))
                throw new FaultException("Private messages are only allowed between users in the same room.");

            var pm = new PrivateMessage
            {
                From = from,
                To = to,
                Text = text,
                At = DateTime.UtcNow
            };

            if (_callbacks.TryGetValue(to, out var cb) && cb != null)
            {
                Try(() => cb.OnPrivateMessage(pm));
            }

            EnqueueEvent(c => c.OnPrivateMessage(pm));
        }

        // Validates and shares a file to the sender's room; emits a public "shared a file" message and a file event (push + polling).
        public void ShareFile(string username, SharedFile file)
        {
            if (!_userRoom.TryGetValue(username, out var room)) return;

            string reason;
            if (!IsAllowedSharedFile(file, out reason))
                throw new FaultException(reason);

            file.FileName = Path.GetFileName(file.FileName ?? string.Empty);
            file.Room = room;
            file.From = username;
            file.To = ""; // room-broadcast, not PM
            file.At = DateTime.UtcNow;

            var msg = new ChatMessage
            {
                From = username,
                Room = room,
                Text = $"shared a file: {file.FileName}",
                At = file.At
            };
            Multicast(cb => cb.OnPublicMessage(msg), room);
            EnqueueEvent(cb => cb.OnPublicMessage(msg));

            Multicast(cb => cb.OnFileShared(file), room);
            EnqueueEvent(cb => cb.OnFileShared(file));
        }

        // ===== Private file send (PM) =====
        // Validates and sends a file privately (both sender and recipient get immediate callbacks); enqueues for polling clients.
        public void SendPrivateFile(string from, string to, SharedFile file)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || file == null)
                throw new FaultException("Invalid private file request.");

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                throw new FaultException("You can't send a private file to yourself.");

            lock (_lock)
            {
                if (!_online.Contains(from)) throw new FaultException("Sender is not online.");
                if (!_online.Contains(to)) throw new FaultException("Target user is not online.");
            }

            if (!_userRoom.TryGetValue(from, out var roomFrom) || string.IsNullOrEmpty(roomFrom))
                throw new FaultException("Sender is not in a room.");
            if (!_userRoom.TryGetValue(to, out var roomTo) || string.IsNullOrEmpty(roomTo))
                throw new FaultException("Target user is not in a room.");
            if (!string.Equals(roomFrom, roomTo, StringComparison.OrdinalIgnoreCase))
                throw new FaultException("Private files are only allowed between users in the same room.");

            // sanitize + validate
            file.FileName = Path.GetFileName(file.FileName ?? string.Empty);
            file.Room = roomFrom;
            file.From = from;
            file.To = to;
            file.At = DateTime.UtcNow;

            string reason;
            if (!IsAllowedSharedFile(file, out reason))
                throw new FaultException(reason);

            // deliver to recipient + echo to sender immediately
            if (_callbacks.TryGetValue(to, out var cbTo) && cbTo != null) Try(() => cbTo.OnPrivateFileShared(file));
            if (_callbacks.TryGetValue(from, out var cbFrom) && cbFrom != null) Try(() => cbFrom.OnPrivateFileShared(file));

            // enqueue for polling clients
            EnqueueEvent(c => c.OnPrivateFileShared(file)); // <-- important
        }

        // Collects and returns all events that occurred after lastSeq for a specific user; also injects current room's user list.
        public LobbySnapshot GetUpdates(string username, long lastSeq)
        {
            var actions = new List<Action<ILobbyCallback>>();
            lock (_events)
            {
                foreach (var kv in _events)
                    if (kv.Key > lastSeq) actions.Add(kv.Value);
            }

            var snap = new LobbySnapshot { NextSeq = Interlocked.Read(ref _seq) };
            var collector = new Collector(snap, username);
            foreach (var a in actions) a(collector);

            if (_userRoom.TryGetValue(username, out var room))
                snap.Users = GetUsers(room);

            return snap;
        }

        private static readonly HashSet<string> _allowedExts = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".txt" },
            StringComparer.OrdinalIgnoreCase);

        private const int MaxFileBytes = 10 * 1024 * 1024; // 10 MB

        // Validates file metadata and content against allowed types/size; returns false with a user-facing error string.
        private static bool IsAllowedSharedFile(SharedFile file, out string error)
        {
            error = null;

            if (file == null) { error = "No file provided."; return false; }

            var name = Path.GetFileName(file.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name)) { error = "Filename required."; return false; }

            var ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext) || !_allowedExts.Contains(ext))
            {
                error = "Only .png, .jpg, .jpeg, .gif, or .txt files are allowed.";
                return false;
            }

            byte[] bytes;
            if (!TryGetBytes(file, out bytes) || bytes == null || bytes.Length == 0)
            {
                return true;
            }

            if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                int limit = Math.Min(bytes.Length, 8192);
                for (int i = 0; i < limit; i++)
                {
                    if (bytes[i] == 0) { error = "Text file appears to contain binary data."; return false; }
                }
                return true;
            }

            if (bytes.Length > MaxFileBytes)
            {
                error = $"File too large (max {MaxFileBytes / (1024 * 1024)} MB).";
                return false;
            }

            if (LooksLikePng(bytes)) return true;
            if (LooksLikeJpeg(bytes)) return true;
            if (LooksLikeGif(bytes)) return true;

            error = "File content does not match an allowed image format.";
            return false;
        }

        // Attempts to read a byte[] payload from several common property names on SharedFile-like objects.
        private static bool TryGetBytes(SharedFile file, out byte[] bytes)
        {
            bytes = null;
            var t = file.GetType();
            var names = new[] { "Bytes", "Data", "Content", "Payload", "Buffer" };
            for (int i = 0; i < names.Length; i++)
            {
                var p = t.GetProperty(names[i]);
                if (p != null && typeof(byte[]).IsAssignableFrom(p.PropertyType))
                {
                    bytes = (byte[])p.GetValue(file, null);
                    if (bytes != null) return true;
                }
            }
            return false;
        }

        // Signature check: PNG magic header.
        private static bool LooksLikePng(byte[] b)
        {
            if (b == null || b.Length < 8) return false;
            return b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
                   b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
        }

        // Signature check: JPEG SOI.
        private static bool LooksLikeJpeg(byte[] b)
        {
            if (b == null || b.Length < 2) return false;
            return b[0] == 0xFF && b[1] == 0xD8;
        }

        // Signature check: GIF headers (87a/89a).
        private static bool LooksLikeGif(byte[] b)
        {
            if (b == null || b.Length < 6) return false;
            var h = Encoding.ASCII.GetString(b, 0, 6);
            return h == "GIF87a" || h == "GIF89a";
        }

        // Adds an action to the ordered global event log with a new sequence id.
        private void EnqueueEvent(Action<ILobbyCallback> a)
        {
            var id = Interlocked.Increment(ref _seq);
            lock (_events) _events.Add(id, a);
        }

        // Pushes the current room list to all connected duplex clients.
        private void BroadcastRoomsChanged()
        {
            Multicast(cb => cb.OnRoomListChanged(ListRooms()), room: null);
        }

        // Pushes the user list for a specific room to that room's connected clients.
        private void PushUsers(string room)
        {
            Multicast(cb => cb.OnUserListChanged(room, GetUsers(room)), room);
        }

        // Returns the current user names in a given room.
        private List<string> GetUsers(string room)
        {
            return _roomUsers.TryGetValue(room, out var set) ? set.ToList() : new List<string>();
        }

        // Invokes a callback action for all duplex clients or for those within a specific room.
        private void Multicast(Action<ILobbyCallback> action, string room)
        {
            IEnumerable<string> targets = _callbacks.Keys;

            if (room != null)
            {
                if (_roomUsers.TryGetValue(room, out var set))
                    targets = set;
                else
                    targets = Array.Empty<string>();
            }

            foreach (var user in targets)
            {
                if (_callbacks.TryGetValue(user, out var cb))
                {
                    Try(() => action(cb));
                }
            }
        }

        // Best-effort wrapper that swallows exceptions from dead/invalid client channels.
        private static void Try(Action a)
        {
            try { a(); } catch { /* drop dead clients */ }
        }

        private class Collector : ILobbyCallback
        {
            private readonly LobbySnapshot _s;
            private readonly string _user;

            // Collector for polling snapshots; captures the requesting user for filtering PMs.
            public Collector(LobbySnapshot s, string user)
            {
                _s = s;
                _user = user;
            }

            // Adds a room-shared file to the snapshot.
            public void OnFileShared(SharedFile f) => _s.Files.Add(f);

            // Adds a private message to the snapshot when it involves the requesting user.
            public void OnPrivateMessage(PrivateMessage pm)
            {
                if (pm.To.Equals(_user, StringComparison.OrdinalIgnoreCase) ||
                    pm.From.Equals(_user, StringComparison.OrdinalIgnoreCase))
                {
                    _s.PrivateMessages.Add(pm);
                }
            }

            // Adds a public room message to the snapshot.
            public void OnPublicMessage(ChatMessage msg) => _s.Messages.Add(msg);

            // Ignored for snapshots (room list changes not needed here).
            public void OnRoomListChanged(List<LobbyRoomInfo> rooms) { }

            // Overwrites the snapshot's Users list with the latest for the room.
            public void OnUserListChanged(string room, List<string> users) => _s.Users = users;

            // Adds a private file to the snapshot when it involves the requesting user.
            public void OnPrivateFileShared(SharedFile f)
            {
                if (f == null) return;
                if (string.Equals(f.To, _user, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.From, _user, StringComparison.OrdinalIgnoreCase))
                {
                    _s.PrivateFiles.Add(f);
                }
            }
        }
    }
}
