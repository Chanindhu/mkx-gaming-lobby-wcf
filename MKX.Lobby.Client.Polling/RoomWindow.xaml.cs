using Microsoft.Win32;
using MKX.Lobby.Contracts;
using System;
using System.ServiceModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents; // Hyperlink
using System.Windows.Threading;
using System.Windows.Data;      // IMultiValueConverter;

namespace MKX.Lobby.Client.Polling
{
    public partial class RoomWindow : Window
    {
        private readonly LobbyClient _client;
        private readonly string _me;
        private readonly string _room;

        private long _lastSeq = 0;
        private readonly DispatcherTimer _pollTimer;

        private readonly ObservableCollection<object> _chatItems = new ObservableCollection<object>();
        private readonly ObservableCollection<string> _users = new ObservableCollection<string>();
        private readonly ObservableCollection<string> _fileNames = new ObservableCollection<string>();
        private readonly List<SharedFile> _fileInbox = new List<SharedFile>();

        private PrivateChatWindow _pmWindow;
        private readonly Dictionary<string, ObservableCollection<string>> _pmLogs =
            new Dictionary<string, ObservableCollection<string>>(StringComparer.OrdinalIgnoreCase);

        // Prevents PM window auto-opening on initial historical snapshot
        private bool _initialSyncDone = false;

        public string Me => _me;
        public ObservableCollection<string> FileNames => _fileNames;

        // Initializes the room window: sets up bindings, starts the polling timer, and kicks off the first fetch.
        public RoomWindow(LobbyClient client, string me, string room)
        {
            InitializeComponent();

            // Fresh collections per room
            _chatItems.Clear();
            _fileNames.Clear();
            _users.Clear();

            _client = client;
            _me = me ?? string.Empty;
            _room = room ?? string.Empty;
            Title = "Room: " + _room;

            ChatList.ItemsSource = _chatItems;
            UsersList.ItemsSource = _users;

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += async (_, __) => await FetchUpdates();
            _pollTimer.Start();

            _ = FetchUpdates();
        }

        // Polls the server for updates since _lastSeq and appends messages/files/users.
        // Also gates PM auto-open behavior until the first historical snapshot is processed.
        private async Task FetchUpdates()
        {
            try
            {
                var snap = await Task.Run(() => _client.Channel.GetUpdates(_me, _lastSeq));
                if (snap == null) return;

                _lastSeq = snap.NextSeq;

                if (snap.Messages != null)
                {
                    foreach (var m in snap.Messages.Where(m => string.Equals(m.Room, _room, StringComparison.OrdinalIgnoreCase)))
                    {
                        _chatItems.Add($"[{m.At.ToLocalTime():HH:mm}] {m.From}: {m.Text}");
                    }
                }

                if (snap.Files != null)
                {
                    foreach (var f in snap.Files.Where(f => string.Equals(f.Room, _room, StringComparison.OrdinalIgnoreCase)))
                    {
                        _fileInbox.Add(f);
                        _fileNames.Add(f.FileName);
                        _chatItems.Add(new FileLinkItem
                        {
                            From = f.From,
                            At = f.At.ToLocalTime(),
                            FileName = f.FileName,
                            Bytes = f.Bytes
                        });
                    }
                }

                if (snap.PrivateMessages != null)
                {
                    foreach (var pm in snap.PrivateMessages)
                    {
                        // Only auto-open the PM window for *new* messages (after initial sync)
                        if (_initialSyncDone && !string.Equals(pm.From, _me, StringComparison.OrdinalIgnoreCase))
                        {
                            HandleIncomingPrivateMessage(pm.From, pm.Text, pm.At);
                        }
                    }
                }

                // Handle incoming PRIVATE FILES (auto-open) after initial snapshot
                if (snap.PrivateFiles != null)
                {
                    foreach (var pf in snap.PrivateFiles)
                    {
                        if (!_initialSyncDone) continue;

                        var peer = string.Equals(pf.From, _me, StringComparison.OrdinalIgnoreCase) ? pf.To : pf.From;
                        if (string.IsNullOrWhiteSpace(peer)) continue;

                        EnsurePmWindow(peer);
                        try { _pmWindow.AppendPrivateFile(pf); } catch { }
                    }
                }

                if (snap.Users != null && snap.Users.Count > 0)
                {
                    _users.Clear();
                    foreach (var u in snap.Users.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        _users.Add(u);
                }
            }
            catch
            {
                // ignore transient blips
            }
            finally
            {
                if (!_initialSyncDone) _initialSyncDone = true;
            }
        }

        // Routes an incoming private text message to the PM window; opens/focuses it as needed.
        private void HandleIncomingPrivateMessage(string from, string text, DateTime atUtc)
        {
            var atLocal = atUtc.ToLocalTime();

            if (_pmWindow != null && _pmWindow.IsVisible &&
                string.Equals(_pmWindow.CurrentPeer, from, StringComparison.OrdinalIgnoreCase))
            {
                _pmWindow.AppendFromPeer(from, text, atLocal);
                return;
            }

            EnsurePmWindow(from);
            _pmWindow.AppendFromPeer(from, text, atLocal);
        }

        // Ensures the PM window exists and is visible; optionally switches to a given initial peer.
        private void EnsurePmWindow(string initialPeer = null)
        {
            if (_pmWindow == null || !_pmWindow.IsVisible)
            {
                _pmWindow = new PrivateChatWindow(
                    _client,
                    _me,
                    new Func<List<string>>(() => _users.ToList()),
                    _pmLogs,
                    null);

                _pmWindow.Owner = this;
                _pmWindow.Show();
            }

            if (_pmWindow.WindowState == WindowState.Minimized)
                _pmWindow.WindowState = WindowState.Normal;
            _pmWindow.Activate();

            if (!string.IsNullOrEmpty(initialPeer))
            {
                try { _pmWindow.SwitchToPeer(initialPeer); }
                catch
                {
                    var mi = _pmWindow.GetType().GetMethod("SwitchToPeer");
                    if (mi != null) { try { mi.Invoke(_pmWindow, new object[] { initialPeer }); } catch { } }
                }
            }
        }

        // Sends a public message to the room and clears the input (and file selection highlight).
        private async void Send_Click(object s, RoutedEventArgs e)
        {
            ClearFileSelection(); // clear yellow highlight when chatting

            var txt = (MessageBoxInput.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(txt)) return;

            MessageBoxInput.Clear();
            try
            {
                await Task.Run(() => _client.Channel.SendMessage(_me, txt));
            }
            catch (FaultException fe) { MessageBox.Show(fe.Message); }
            catch (Exception ex) { MessageBox.Show("Failed to send: " + ex.Message); }
        }

        // Opens a file picker and shares the selected file to the current room.
        private async void Share_Click(object s, RoutedEventArgs e)
        {
            ClearFileSelection(); // interacting elsewhere clears selection

            var dlg = new OpenFileDialog
            {
                Filter = "Images/Text|*.png;*.jpg;*.jpeg;*.gif;*.txt",
                Title = "Share a file to the room"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var bytes = File.ReadAllBytes(dlg.FileName);
                var f = new SharedFile
                {
                    Room = _room,
                    From = _me,
                    FileName = System.IO.Path.GetFileName(dlg.FileName),
                    ContentType = "application/octet-stream",
                    Bytes = bytes,
                    At = DateTime.UtcNow
                };
                await Task.Run(() => _client.Channel.ShareFile(_me, f));
            }
            catch (FaultException fe) { MessageBox.Show(fe.Message); }
            catch (Exception ex) { MessageBox.Show("Failed to share: " + ex.Message); }
        }

        // Handles a file hyperlink click inside the chat list; prompts to save the file locally.
        private void ChatFileLink_Click(object sender, RoutedEventArgs e)
        {
            var link = sender as Hyperlink;
            var item = (link != null) ? link.Tag as FileLinkItem : null;
            if (item == null) return;

            var dlg = new SaveFileDialog { FileName = item.FileName };
            if (dlg.ShowDialog(this) == true)
            {
                try { File.WriteAllBytes(dlg.FileName, item.Bytes ?? Array.Empty<byte>()); }
                catch (Exception ex) { MessageBox.Show("Save failed: " + ex.Message); }
            }

            ClearFileSelection(); // ensure side list selection is cleared too
        }

        // Leaves the room (server call), returns to the lobby window, and stops polling.
        private async void Leave_Click(object s, RoutedEventArgs e)
        {
            ClearFileSelection();

            try { await Task.Run(() => _client.Channel.LeaveRoom(_me)); } catch { }
            _pollTimer?.Stop();
            var lobby = new LobbyWindow(_client, _me);
            lobby.Show();
            Close();
        }

        // Logs out from the server and shuts down the application.
        private async void Logout_Click(object s, RoutedEventArgs e)
        {
            ClearFileSelection();

            try { await Task.Run(() => _client.Channel.Logout(_me)); } catch { }
            Application.Current.Shutdown();
        }

        // Opens (or focuses) the PM window from the room toolbar button.
        private void Private_Click(object s, RoutedEventArgs e)
        {
            ClearFileSelection();
            EnsurePmWindow();
        }

        // Saves the currently selected file from the side list, then clears the selection highlight.
        private void SaveSelected_Click(object s, RoutedEventArgs e)
        {
            var idx = FilesList.SelectedIndex;
            if (idx < 0 || idx >= _fileInbox.Count) { ClearFileSelection(); return; }

            var f = _fileInbox[idx];

            var dlg = new SaveFileDialog { FileName = f?.FileName ?? "file" };
            if (dlg.ShowDialog(this) == true)
            {
                try { File.WriteAllBytes(dlg.FileName, f?.Bytes ?? Array.Empty<byte>()); }
                catch (Exception ex) { MessageBox.Show("Save failed: " + ex.Message); }
            }

            ClearFileSelection(); //  always remove yellow highlight after save/cancel
        }

        // Clears the selection in the FilesList to remove the yellow highlight state.
        private void ClearFileSelection()
        {
            try
            {
                if (FilesList.SelectedIndex != -1)
                    FilesList.SelectedIndex = -1;
            }
            catch { }
        }
    }
}

// ======= TOP-LEVEL TYPES FOR XAML (not nested) =======

namespace MKX.Lobby.Client.Polling
{
    public class FileLinkItem
    {
        public string From { get; set; }
        public DateTime At { get; set; }
        public string FileName { get; set; }
        public byte[] Bytes { get; set; }
    }

    public sealed class IsMeConverter : IMultiValueConverter
    {
        // Returns true when the chat item's author equals the current user (case-insensitive).
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var itemUser = values != null && values.Length > 0 ? values[0] as string : null;
            var me = values != null && values.Length > 1 ? values[1] as string : null;
            if (string.IsNullOrEmpty(itemUser) || string.IsNullOrEmpty(me)) return false;
            return string.Equals(itemUser, me, StringComparison.OrdinalIgnoreCase);
        }

        // Not supported: this converter is one-way only (no multi-value convert-back).
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
