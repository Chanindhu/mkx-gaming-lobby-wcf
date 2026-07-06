using MKX.Lobby.Contracts;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Windows;

namespace MKX.Lobby.Client.Polling
{
    public partial class PrivateChatWindow : Window
    {
        private readonly LobbyClient _client;
        private readonly string _me;
        private readonly Func<List<string>> _usersProvider;
        private readonly Dictionary<string, ObservableCollection<string>> _logs;

        private string _peer = "";
        public string CurrentPeer => _peer;

        // PM files UI + inbox
        private readonly ObservableCollection<string> _pmFileNames = new ObservableCollection<string>();
        private readonly List<SharedFile> _pmInbox = new List<SharedFile>();
        public ObservableCollection<string> PmFileNames => _pmFileNames;

        // De-dup key set to avoid double entries (callback + snapshot, or repeated polls)
        private readonly HashSet<string> _pmSeenKeys = new HashSet<string>(StringComparer.Ordinal);

        // Initializes the PM window (polling client): binds data context, stores deps, shows picker UI,
        // populates user list and optionally opens a peer thread if provided.
        public PrivateChatWindow(
            LobbyClient client,
            string me,
            Func<List<string>> usersProvider,
            Dictionary<string, ObservableCollection<string>> logs,
            string openPeerIfAny = null)
        {
            InitializeComponent();
            DataContext = this; // ensure {Binding PmFileNames} resolves

            _client = client;
            _me = me ?? string.Empty;
            _usersProvider = usersProvider ?? (() => new List<string>());
            _logs = logs ?? new Dictionary<string, ObservableCollection<string>>();

            Title = "Private Messages - Pick a player";
            PeerHeader.Text = "Pick a player";
            SelectView.Visibility = Visibility.Visible;
            ChatView.Visibility = Visibility.Collapsed;
            BackBtn.Visibility = Visibility.Collapsed;

            RefreshUserList();

            if (!string.IsNullOrWhiteSpace(openPeerIfAny))
                SwitchToPeer(openPeerIfAny);
        }

        // Refreshes the selectable users list (excluding self) and binds it to the UI.
        private void RefreshUserList()
        {
            var users = _usersProvider.Invoke() ?? new List<string>();
            users = users.Where(u => !string.Equals(u, _me, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                         .ToList();
            UsersList.ItemsSource = users;
        }

        // Double-click on a user in the list starts a chat (delegates to StartChat_Click).
        private void UsersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => StartChat_Click(sender, e);

        // Starts a chat with the selected user from the picker view.
        private void StartChat_Click(object sender, RoutedEventArgs e)
        {
            var peer = UsersList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(peer) || string.Equals(peer, _me, StringComparison.OrdinalIgnoreCase))
                return;

            SwitchToPeer(peer);
        }

        // Returns from the chat thread view to the user picker and restores last selection.
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            var last = _peer;

            Title = "Private Messages - Pick a player";
            PeerHeader.Text = "Pick a player";
            SelectView.Visibility = Visibility.Visible;
            ChatView.Visibility = Visibility.Collapsed;
            BackBtn.Visibility = Visibility.Collapsed;

            RefreshUserList();

            if (!string.IsNullOrWhiteSpace(last))
            {
                var list = UsersList.ItemsSource as IEnumerable<string>;
                if (list != null)
                {
                    foreach (var u in list)
                    {
                        if (string.Equals(u, last, StringComparison.OrdinalIgnoreCase))
                        {
                            UsersList.SelectedItem = u;
                            break;
                        }
                    }
                }
            }
        }

        // Switches into a one-to-one conversation with the given peer and rebuilds the file list for that thread.
        public void SwitchToPeer(string peer)
        {
            _peer = peer ?? string.Empty;

            if (!_logs.TryGetValue(_peer, out var log))
            {
                log = new ObservableCollection<string>();
                _logs[_peer] = log;
            }
            List.ItemsSource = log;

            Title = "Private: " + _me + " <-> " + _peer;
            PeerHeader.Text = "Chat with " + _peer;

            SelectView.Visibility = Visibility.Collapsed;
            ChatView.Visibility = Visibility.Visible;
            BackBtn.Visibility = Visibility.Visible;

            // Show any previously received files for this thread
            RebuildPmFilesForCurrentPeer();
        }

        // Appends an incoming text message from a peer to that peer's log and refreshes the visible list if needed.
        public void AppendFromPeer(string peer, string text, DateTime atLocal)
        {
            if (string.IsNullOrWhiteSpace(peer)) return;

            if (!_logs.TryGetValue(peer, out var log))
            {
                log = new ObservableCollection<string>();
                _logs[peer] = log;
            }

            log.Add("[" + atLocal.ToShortTimeString() + "] " + peer + ": " + text);

            if (string.Equals(_peer, peer, StringComparison.OrdinalIgnoreCase))
                List.ItemsSource = log;
        }

        // Sends a private text message to the current peer (with local echo); shows FaultExceptions to the user.
        private async void Send_Click(object s, RoutedEventArgs e)
        {
            var text = (Input.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_peer)) return;

            Input.Clear();

            try
            {
                await System.Threading.Tasks.Task.Run(() => _client.Channel.SendPrivateMessage(_me, _peer, text));

                // Local echo for TEXT ONLY (files have no local echo)
                if (!_logs.TryGetValue(_peer, out var log))
                {
                    log = new ObservableCollection<string>();
                    _logs[_peer] = log;
                }
                log.Add("[" + DateTime.Now.ToShortTimeString() + "] " + _me + ": " + text);
                List.ItemsSource = log;
            }
            catch (FaultException fe) { MessageBox.Show(fe.Message); }
            catch (Exception ex) { MessageBox.Show("Failed to send PM: " + ex.Message); }
        }

        // ===== Private files =====

        // Builds a stable de-duplication key for a private file (sender|receiver|name|utcTicks|length).
        private static string KeyFor(SharedFile f)
        {
            // Stable de-dup key across callback/snapshot, regardless of local time zone
            var ticks = (f.At.Kind == DateTimeKind.Utc ? f.At : DateTime.SpecifyKind(f.At, DateTimeKind.Utc)).Ticks;
            var len = f.Bytes != null ? f.Bytes.Length : 0;
            return $"{f.From}|{f.To}|{f.FileName}|{ticks}|{len}";
        }

        // Checks whether a file pertains to the current open thread (peer <-> me).
        private bool BelongsHere(SharedFile f)
        {
            if (f == null || string.IsNullOrWhiteSpace(_peer)) return false;

            return (string.Equals(f.From, _peer, StringComparison.OrdinalIgnoreCase) && string.Equals(f.To, _me, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) && string.Equals(f.To, _peer, StringComparison.OrdinalIgnoreCase));
        }

        // Appends a received private file to the current thread (with de-dup), updates inbox and UI list, and logs a line.
        public void AppendPrivateFile(SharedFile f)
        {
            if (f == null || !BelongsHere(f)) return;

            // De-dup guard
            var key = KeyFor(f);
            if (!_pmSeenKeys.Add(key)) return;

            _pmInbox.Add(f);
            _pmFileNames.Add(f.FileName);

            var who = string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) ? "You" : f.From;
            var whenLocal = f.At.Kind == DateTimeKind.Local ? f.At : DateTime.SpecifyKind(f.At, DateTimeKind.Utc).ToLocalTime();

            if (!_logs.TryGetValue(_peer, out var log))
            {
                log = new ObservableCollection<string>();
                _logs[_peer] = log;
                List.ItemsSource = log;
            }
            log.Add($"[{whenLocal:HH:mm}] {who} shared a file: {f.FileName}");
        }

        // Opens a file picker, validates size/type, reads bytes and sends a private file to the current peer.
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

            // Client-side guards (match server)
            const int MaxFileBytesClient = 10 * 1024 * 1024; // 10 MB
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".png", ".jpg", ".jpeg", ".gif", ".txt" };

            var ext = System.IO.Path.GetExtension(name) ?? "";
            if (!allowed.Contains(ext)) { MessageBox.Show("Only .png, .jpg, .jpeg, .gif, or .txt files are allowed."); return; }

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception ex) { MessageBox.Show("Could not access the file: " + ex.Message); return; }

            if (info.Length > MaxFileBytesClient)
            {
                MessageBox.Show($"File too large (max {MaxFileBytesClient / (1024 * 1024)} MB).");
                return;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { MessageBox.Show("Could not read the file: " + ex.Message); return; }

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
                // IMPORTANT: no local echo here - AppendPrivateFile() will add a single line when the event arrives
            }
            catch (FaultException fe) { MessageBox.Show(fe.Message, "Share failed"); }
            catch (Exception ex) { MessageBox.Show("Failed to send file: " + ex.Message, "Share failed"); }
        }

        // Saves the selected file from the PM files list to disk (with basic validation).
        private void SaveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedName = PmFilesList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedName)) { MessageBox.Show("Select a file in the list first."); return; }

            var file = _pmInbox.Find(f => string.Equals(f.FileName, selectedName, StringComparison.OrdinalIgnoreCase) && BelongsHere(f));
            if (file == null) { MessageBox.Show("File not found in inbox."); return; }

            var sfd = new SaveFileDialog { FileName = file.FileName, DefaultExt = System.IO.Path.GetExtension(file.FileName) };
            if (sfd.ShowDialog(this) == true)
            {
                try { File.WriteAllBytes(sfd.FileName, file.Bytes); }
                catch (Exception ex) { MessageBox.Show("Failed to save file: " + ex.Message); }
            }
        }

        // Rebuilds the visible PM file name list so it only shows files relevant to the current peer thread.
        private void RebuildPmFilesForCurrentPeer()
        {
            _pmFileNames.Clear();
            foreach (var f in _pmInbox)
            {
                if (BelongsHere(f))
                    _pmFileNames.Add(f.FileName);
            }
        }
    }
}
