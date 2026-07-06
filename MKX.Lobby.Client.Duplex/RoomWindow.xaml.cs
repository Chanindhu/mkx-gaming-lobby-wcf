using Microsoft.Win32;
using MKX.Lobby.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;       // IMultiValueConverter
using System.Windows.Documents;

namespace MKX.Lobby.Client.Duplex
{
    public partial class RoomWindow : Window
    {
        private readonly DuplexClient _client;
        private readonly DuplexCallback _cb;
        private readonly string _me;
        private readonly string _room;

        private readonly List<SharedFile> _fileInbox = new List<SharedFile>();
        private readonly ObservableCollection<string> _fileNames = new ObservableCollection<string>();
        public ObservableCollection<string> FileNames => _fileNames;

        // Combined chat stream (strings + file-link items)
        private readonly ObservableCollection<object> _chatItems = new ObservableCollection<object>();

        private PrivateChatWindow _pmWindow;

        // Initializes the room window, restores history for this room, and wires up duplex event handlers.
        public RoomWindow(DuplexClient client, DuplexCallback cb, string me, string room)
        {
            InitializeComponent();

            // Ensure fresh per-room view
            _chatItems.Clear();
            _fileNames.Clear();

            _client = client; _cb = cb; _me = me; _room = room;
            Title = "Room: " + _room;

            // restore this room's history (rejoin support)
            _cb.ResetForRoom(_room);

            ChatList.ItemsSource = _chatItems;   // string or FileLinkItem
            UsersList.ItemsSource = _cb.Users;
            FilesList.ItemsSource = FileNames;

            // Seed existing chat lines, mirror future ones
            if (_cb.Chat != null)
            {
                foreach (var line in _cb.Chat) _chatItems.Add(line);
                _cb.Chat.CollectionChanged += Chat_CollectionChanged;
            }

            // Subscribe to duplex pushes
            _cb.PrivateMessageReceived += OnPrivateMessage;
            _cb.FileReceived += OnRoomFileReceived;
            _cb.PrivateFileReceived += OnPrivatePmFileReceived;
        }

        // Unsubscribes from duplex events and chat collection changes when the window closes.
        protected override void OnClosed(EventArgs e)
        {
            if (_cb != null)
            {
                _cb.PrivateMessageReceived -= OnPrivateMessage;
                _cb.FileReceived -= OnRoomFileReceived;
                _cb.PrivateFileReceived -= OnPrivatePmFileReceived;
            }
            if (_cb.Chat != null) _cb.Chat.CollectionChanged -= Chat_CollectionChanged;
            base.OnClosed(e);
        }

        // Mirrors new items pushed into the shared Chat collection into this window's _chatItems.
        private void Chat_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Chat_CollectionChanged(sender, e));
                return;
            }
            if (e.NewItems != null)
            {
                foreach (var it in e.NewItems) _chatItems.Add(it);
            }
        }

        // ===== XAML Click handlers (required by your XAML) =====

        // Leaves the current room, returns to the lobby window, and clears any selected file highlight.
        private async void Leave_Click(object sender, RoutedEventArgs e)
        {
            ClearFileSelection();

            try { await Task.Run(() => _client.Channel.LeaveRoom(_me)); } catch { }
            var lobby = new LobbyWindow(_client, _cb, _me);
            lobby.Show();
            Close();
        }

        // Logs out from the server and shuts down the application (also clears file selection).
        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            ClearFileSelection();

            try { await Task.Run(() => _client.Channel.Logout(_me)); } catch { }
            Application.Current.Shutdown();
        }

        // Opens (or focuses) the Private Messages window; clears file selection first.
        private void Private_Click(object sender, RoutedEventArgs e)
        {
            ClearFileSelection();
            EnsurePmWindow();
        }

        // Saves the selected shared file from the FilesList to disk, then clears the selection highlight.
        private void SaveSelected_Click(object sender, RoutedEventArgs e)
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

            ClearFileSelection(); //remove yellow highlight after save/cancel
        }

        // Saves a file that was clicked via a hyperlink inside the chat list, then clears selection.
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

            ClearFileSelection();
        }

      /// <summary>
      /// /////////////////////////////////////////////////////////////////////////////////////////////
      /// </summary>

        // Creates the PrivateChatWindow once and keeps it around; shows/focuses it on demand.
        private void EnsurePmWindow()
        {
            if (_pmWindow == null)
            {
                _pmWindow = new PrivateChatWindow(_client, _cb, _me);
                _pmWindow.Owner = this;
                _pmWindow.Closing += (s, ev) =>
                {
                    ev.Cancel = true; // persist window
                    _pmWindow.Hide();
                };
            }

            if (!_pmWindow.IsVisible) _pmWindow.Show();
            if (_pmWindow.WindowState == WindowState.Minimized)
                _pmWindow.WindowState = WindowState.Normal;
            _pmWindow.Activate();
        }

        // Handles an incoming private text message by ensuring the PM window is visible and appending it.
        private void OnPrivateMessage(PrivateMessage pm)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnPrivateMessage(pm));
                return;
            }

            EnsurePmWindow();
            try { _pmWindow.SwitchToPeer(pm.From); } catch { }
            try { _pmWindow.Append(pm); } catch { }

            ClearFileSelection(); // any cross-UI action clears highlight
        }

        // Handles an incoming private file; auto-opens PM window for the first file and routes it to the correct peer.
        private void OnPrivatePmFileReceived(SharedFile f)
        {
            if (f == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnPrivatePmFileReceived(f));
                return;
            }

            var peer = string.Equals(f.From, _me, StringComparison.OrdinalIgnoreCase) ? f.To : f.From;
            if (string.IsNullOrWhiteSpace(peer)) return;

            if (_pmWindow != null && _pmWindow.IsVisible)
                return;

            EnsurePmWindow();
            try { _pmWindow.SwitchToPeer(peer); } catch { }
            try { _pmWindow.AppendPrivateFile(f); } catch { }

            ClearFileSelection();
        }

        // Handles a room-scoped file share pushed from the server; adds to inbox, file list, and chat as a clickable link.
        private void OnRoomFileReceived(SharedFile f)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnRoomFileReceived(f));
                return;
            }

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

        // Sends a public room message from the input box; clears the file selection and input after send.
        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            ClearFileSelection();

            var txt = (MessageBoxInput.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(txt)) return;
            MessageBoxInput.Clear();

            try { await Task.Run(() => _client.Channel.SendMessage(_me, txt)); }
            catch (FaultException fe) { MessageBox.Show(fe.Message); }
            catch (Exception ex) { MessageBox.Show("Failed to send: " + ex.Message); }
        }

        // Opens a file picker and shares the selected file to the room; clears current file selection first.
        private async void Share_Click(object sender, RoutedEventArgs e)
        {
            ClearFileSelection();

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

        // Helper: clears the selection in the FilesList to remove the yellow highlight state.
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

    // Chat list supports both strings and file-link items
    public class FileLinkItem
    {
        public string From { get; set; }
        public DateTime At { get; set; }
        public string FileName { get; set; }
        public byte[] Bytes { get; set; }
    }

    public sealed class IsMeConverter : IMultiValueConverter
    {
        // Returns true if the chat item's author equals the current user (case-insensitive).
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var itemUser = values != null && values.Length > 0 ? values[0] as string : null;
            var me = values != null && values.Length > 1 ? values[1] as string : null;
            if (string.IsNullOrEmpty(itemUser) || string.IsNullOrEmpty(me)) return false;
            return string.Equals(itemUser, me, StringComparison.OrdinalIgnoreCase);
        }
        // Not used: no conversion back from boolean to the two input strings.
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
