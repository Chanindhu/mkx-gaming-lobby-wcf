using MKX.Lobby.Contracts;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Windows;

namespace MKX.Lobby.Client.Duplex
{
    public partial class PrivateChatWindow : Window
    {
        private readonly DuplexClient _client;
        private readonly DuplexCallback _cb;
        private readonly string _me;

        private string _peer;

        // per-peer text logs
        private readonly Dictionary<string, ObservableCollection<string>> _logs =
            new Dictionary<string, ObservableCollection<string>>(StringComparer.OrdinalIgnoreCase);

        // PM files UI + inbox + de-dup
        private readonly ObservableCollection<string> _pmFileNames = new ObservableCollection<string>();
        private readonly List<SharedFile> _pmInbox = new List<SharedFile>();
        private readonly HashSet<string> _pmSeenFileKeys = new HashSet<string>(StringComparer.Ordinal);

        // de-dup for text PMs (covers callback + legacy Append() caller)
        private readonly HashSet<string> _pmSeenMsgKeys = new HashSet<string>(StringComparer.Ordinal);

        public ObservableCollection<string> PmFileNames => _pmFileNames;
        public string CurrentPeer => _peer ?? "";

        // --- Ctors ---

        // Opens a direct chat to a specific peer (legacy ctor without callback subscription).
        public PrivateChatWindow(DuplexClient client, string me, string peer)
        {
            InitializeComponent();
            DataContext = this;

            _client = client;
            _cb = null;
            _me = me ?? string.Empty;

            SwitchToPeer(peer);
        }

        // Opens the PM picker window and subscribes to duplex callback events.
        public PrivateChatWindow(DuplexClient client, DuplexCallback cb, string me)
        {
            InitializeComponent();
            DataContext = this;

            _client = client;
            _cb = cb;
            _me = me ?? string.Empty;

            Title = "Private Messages - Pick a player";

            SelectView.Visibility = Visibility.Visible;
            ChatView.Visibility = Visibility.Collapsed;

            RefreshUserList();

            if (_cb != null)
            {
                _cb.PrivateMessageReceived += Cb_PrivateMessageReceived;
                _cb.PrivateFileReceived += Cb_PrivateFileReceived;
            }
        }

        // Unsubscribes from duplex callback events when the window is closed.
        protected override void OnClosed(EventArgs e)
        {
            if (_cb != null)
            {
                _cb.PrivateMessageReceived -= Cb_PrivateMessageReceived;
                _cb.PrivateFileReceived -= Cb_PrivateFileReceived;
            }
            base.OnClosed(e);
        }

        // ========== Compatibility shim ==========

        // Appends a private message via the core path (legacy compatibility).
        public void Append(PrivateMessage pm) => AppendPrivateMessageCore(pm);

        // NEW: Public wrapper so RoomWindow can append a private file
        // Appends a private file to the current/related peer thread (public wrapper).
        public void AppendPrivateFile(SharedFile f)
        {
            if (f == null) return;
            var threadPeer = string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) ? f.To : f.From;
            if (string.IsNullOrWhiteSpace(threadPeer)) return;
            AppendPrivateFile(f, threadPeer); // reuse existing de-dup + UI logic
        }

        // ========== Duplex callback handlers ==========

        // Handles an incoming private message from the duplex callback.
        private void Cb_PrivateMessageReceived(PrivateMessage pm)
            => AppendPrivateMessageCore(pm);

        // Handles an incoming private file from the duplex callback.
        private void Cb_PrivateFileReceived(SharedFile f)
        {
            if (f == null) return;
            var other = string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) ? f.To : f.From;
            if (string.IsNullOrWhiteSpace(other)) return;
            AppendPrivateFile(f, other);
        }

        // Appends a private text message with de-duplication and per-peer logging.
        private void AppendPrivateMessageCore(PrivateMessage pm)
        {
            if (pm == null) return;

            var key = $"{pm.From}|{pm.To}|{pm.Text}|{ToUtc(pm.At).Ticks}";
            if (!_pmSeenMsgKeys.Add(key)) return; // already shown

            var atLocal = ToLocal(pm.At);
            var other = string.Equals(pm.From, _me, StringComparison.OrdinalIgnoreCase) ? pm.To : pm.From;
            if (string.IsNullOrWhiteSpace(other)) return;

            EnsureLog(other).Add($"[{atLocal:HH:mm}] {pm.From}: {pm.Text}");

            if (string.Equals(_peer, other, StringComparison.OrdinalIgnoreCase))
                List.ItemsSource = _logs[other];
        }

        // --- UI wiring for picker ---

        // Rebuilds and binds the users list for the PM picker view (excludes self).
        private void RefreshUserList()
        {
            try
            {
                var users = (_cb?.Users ?? new ObservableCollection<string>())
                            .Where(u => !string.Equals(u, _me, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                UsersList.ItemsSource = users;
            }
            catch { UsersList.ItemsSource = new List<string>(); }
        }

        // Starts a chat with the double-clicked user in the list.
        private void UsersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => StartChat_Click(sender, e);

        // Starts a chat with the selected user from the picker.
        private void StartChat_Click(object sender, RoutedEventArgs e)
        {
            var peer = UsersList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(peer) || string.Equals(peer, _me, StringComparison.OrdinalIgnoreCase))
                return;

            SwitchToPeer(peer);
        }

        // Returns from chat view to the PM picker, remembering the last selection.
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            var last = _peer;

            Title = "Private Messages - Pick a player";
            SelectView.Visibility = Visibility.Visible;
            ChatView.Visibility = Visibility.Collapsed;

            RefreshUserList();

            if (!string.IsNullOrWhiteSpace(last))
                UsersList.SelectedItem = (UsersList.ItemsSource as IEnumerable<string>)?
                    .FirstOrDefault(u => string.Equals(u, last, StringComparison.OrdinalIgnoreCase));
        }

        // --- Chat mode ---

        // Switches the UI into a one-to-one chat with the specified peer.
        public void SwitchToPeer(string peer)
        {
            _peer = peer ?? string.Empty;

            List.ItemsSource = EnsureLog(_peer);

            Title = "Private: " + _me + " <-> " + _peer;
            PeerHeader.Text = "Chat with " + _peer;

            SelectView.Visibility = Visibility.Collapsed;
            ChatView.Visibility = Visibility.Visible;

            RebuildPmFilesForCurrentPeer();
        }

        // Ensures a text log collection exists for a peer and returns it.
        private ObservableCollection<string> EnsureLog(string who)
        {
            if (!_logs.TryGetValue(who, out var log))
            {
                log = new ObservableCollection<string>();
                _logs[who] = log;
            }
            return log;
        }

        // Sends a private text message to the current peer and locally echoes it.
        private async void Send_Click(object s, RoutedEventArgs e)
        {
            var text = (Input.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_peer)) return;

            Input.Clear();

            try
            {
                await System.Threading.Tasks.Task.Run(() => _client.Channel.SendPrivateMessage(_me, _peer, text));
                // local echo only for TEXT
                EnsureLog(_peer).Add("[" + DateTime.Now.ToShortTimeString() + "] " + _me + ": " + text);
            }
            catch (FaultException fe) { MessageBox.Show(fe.Message); }
            catch (Exception ex) { MessageBox.Show("Failed to send PM: " + ex.Message); }
        }

        // ---- Private Files ----

        // Produces a stable de-duplication key for a private file entry.
        private static string FileKey(SharedFile f)
        {
            var ticks = ToUtc(f.At).Ticks;
            var len = f.Bytes != null ? f.Bytes.Length : 0;
            return $"{f.From}|{f.To}|{f.FileName}|{ticks}|{len}";
        }

        // Determines whether a file belongs to the current peer thread (either direction).
        private bool BelongsHere(SharedFile f, string threadPeer)
        {
            return (string.Equals(f.From, threadPeer, StringComparison.OrdinalIgnoreCase) && string.Equals(f.To, _me, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) && string.Equals(f.To, threadPeer, StringComparison.OrdinalIgnoreCase));
        }

        // Appends a private file to the thread (de-dup + add to inbox and visible list if current).
        private void AppendPrivateFile(SharedFile f, string threadPeer)
        {
            if (f == null || !BelongsHere(f, threadPeer)) return;

            var key = FileKey(f);
            if (!_pmSeenFileKeys.Add(key)) return; // already added

            _pmInbox.Add(f);
            if (string.Equals(_peer, threadPeer, StringComparison.OrdinalIgnoreCase))
                _pmFileNames.Add(f.FileName); // only show in current thread

            var who = string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) ? "You" : f.From;
            var whenLocal = ToLocal(f.At);
            EnsureLog(threadPeer).Add($"[{whenLocal:HH:mm}] {who} shared a file: {f.FileName}");
        }

        // Rebuilds the visible list of PM file names for the active peer thread.
        private void RebuildPmFilesForCurrentPeer()
        {
            _pmFileNames.Clear();
            foreach (var f in _pmInbox)
                if (BelongsHere(f, _peer)) _pmFileNames.Add(f.FileName);
        }

        // Opens a file picker and sends the selected file privately to the current peer.
        private async void SharePmFile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_peer)) return;

            var dlg = new OpenFileDialog
            {
                Title = "Select a file to send privately",
                CheckFileExists = true,
                Multiselect = false,
                Filter = "Images/Text|*.png;*.jpg;*.jpeg;*.gif;*.txt|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            var path = dlg.FileName;
            var name = System.IO.Path.GetFileName(path);

            const int MaxFileBytesClient = 10 * 1024 * 1024;
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".png", ".jpg", ".jpeg", ".gif", ".txt" };
            var ext = System.IO.Path.GetExtension(name) ?? "";
            if (!allowed.Contains(ext)) { MessageBox.Show("Only .png, .jpg, .jpeg, .gif, or .txt files are allowed."); return; }

            FileInfo info;
            try { info = new FileInfo(path); } catch (Exception ex) { MessageBox.Show("Could not access the file: " + ex.Message); return; }
            if (info.Length > MaxFileBytesClient) { MessageBox.Show($"File too large (max {MaxFileBytesClient / (1024 * 1024)} MB)."); return; }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch (Exception ex) { MessageBox.Show("Could not read the file: " + ex.Message); return; }

            var sf = new SharedFile
            {
                FileName = name,
                Bytes = bytes,
                From = _me,
                To = _peer,
                At = DateTime.UtcNow
            };

            try
            {
                await System.Threading.Tasks.Task.Run(() => _client.Channel.SendPrivateFile(_me, _peer, sf));
                // NO local echo here (prevents double line). Callback will append once via de-dup.
            }
            catch (FaultException fe) { MessageBox.Show(fe.Message, "Share failed"); }
            catch (Exception ex) { MessageBox.Show("Failed to send file: " + ex.Message, "Share failed"); }
        }

        // Saves the currently selected private file from the list to disk.
        private void SaveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedName = PmFilesList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedName)) { MessageBox.Show("Select a file in the list first."); return; }

            var file = _pmInbox.Find(f =>
                string.Equals(f.FileName, selectedName, StringComparison.OrdinalIgnoreCase) &&
                BelongsHere(f, _peer));
            if (file == null) { MessageBox.Show("File not found in inbox."); return; }

            var sfd = new SaveFileDialog { FileName = file.FileName, DefaultExt = System.IO.Path.GetExtension(file.FileName) };
            if (sfd.ShowDialog(this) == true)
            {
                try { File.WriteAllBytes(sfd.FileName, file.Bytes); }
                catch (Exception ex) { MessageBox.Show("Failed to save file: " + ex.Message); }
            }
        }

        // --- helpers ---

        // Converts a timestamp to local time (tolerates unspecified kinds).
        private static DateTime ToLocal(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) return dt;
            if (dt.Kind == DateTimeKind.Utc) return dt.ToLocalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        }

        // Ensures a UTC timestamp (tolerates unspecified kinds).
        private static DateTime ToUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
    }
}
