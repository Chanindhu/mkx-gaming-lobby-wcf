using MKX.Lobby.Contracts;
using System;
using System.ComponentModel;
using System.Linq;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MKX.Lobby.Client.Duplex
{
    public partial class LobbyWindow : Window
    {
        private readonly DuplexClient _client;
        private readonly DuplexCallback _cb;
        private readonly string _me;

        private ICollectionView _roomsView;
        private bool _isLoadingRooms = false;
        private readonly DispatcherTimer _autoRefreshTimer;

        // When user starts interacting with the list, pause auto-refresh briefly
        private DateTime _suppressRefreshUntilUtc = DateTime.MinValue;

        public LobbyWindow(DuplexClient client, DuplexCallback cb, string me)
        {
            InitializeComponent();
            _client = client;
            _cb = cb;
            _me = me;

            UserLabel.Text = "Logged in as: " + _me;

            // Bind rooms to a filtered view
            _roomsView = CollectionViewSource.GetDefaultView(_cb.Rooms);
            _roomsView.Filter = RoomFilter;
            RoomsList.ItemsSource = _roomsView;

            // Suppress refreshes for a short window while the user is clicking in the list
            RoomsList.PreviewMouseDown += (_, __) => _suppressRefreshUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
            RoomsList.PreviewKeyDown += (_, __) => _suppressRefreshUntilUtc = DateTime.UtcNow.AddSeconds(1.0);

            // Initial load (in case app starts after rooms already exist)
            _ = RefreshRooms();

            // Also listen to server push (Duplex)
            // DuplexCallback.OnRoomListChanged updates _cb.Rooms; the view will sync automatically.

            // Lightweight periodic refresh to cover edge cases (but don't fight with the user)
            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoRefreshTimer.Tick += async (_, __) =>
            {
                // Skip while user is interacting or very recently interacted
                if (RoomsList.IsKeyboardFocusWithin || RoomsList.IsMouseOver) return;
                if (DateTime.UtcNow < _suppressRefreshUntilUtc) return;

                await RefreshRooms();
            };
            _autoRefreshTimer.Start();
        }

        private async Task RefreshRooms()
        {
            if (_isLoadingRooms) return;
            _isLoadingRooms = true;

            // Remember current selection by room name (stable key)
            var selectedName =
                (RoomsList.SelectedItem as LobbyRoomInfo)?.Name ??
                (RoomsList.SelectedValue as string);

            try
            {
                var list = await Task.Run(() => _client.Channel.ListRooms());

                // Replace contents to avoid stale entries
                _cb.Rooms.Clear();
                foreach (var r in list.OrderBy(x => x.Name))
                    _cb.Rooms.Add(r);

                _roomsView?.Refresh();
            }
            catch (FaultException fe)
            {
                System.Diagnostics.Debug.WriteLine("ListRooms fault: " + fe.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ListRooms error: " + ex.Message);
            }
            finally
            {
                _isLoadingRooms = false;
            }

            // Restore selection if the same room still exists
            if (!string.IsNullOrWhiteSpace(selectedName) && RoomsList.SelectedIndex == -1)
            {
                var match = _cb.Rooms.FirstOrDefault(r =>
                    string.Equals(r.Name, selectedName, StringComparison.OrdinalIgnoreCase));
                if (match != null) RoomsList.SelectedItem = match;
            }
        }

        private bool RoomFilter(object obj)
        {
            var item = obj as LobbyRoomInfo;
            if (item == null) return false;

            var q = (SearchBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q)) return true;

            return item.Name != null &&
                   item.Name.ToLowerInvariant().Contains(q.ToLowerInvariant());
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _roomsView?.Refresh();
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            var name = (NewRoomBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Room name is required.");
                return;
            }

            // Avoid accidentally filtering the room you just created
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = string.Empty;

            try
            {
                var ok = await Task.Run(() => _client.Channel.CreateRoom(name));
                if (!ok)
                {
                    MessageBox.Show($"Room \"{name}\" already exists.");
                    return;
                }

                // Briefly suppress refresh jitter while we update the list and user clicks
                _suppressRefreshUntilUtc = DateTime.UtcNow.AddSeconds(1.5);

                await RefreshRooms();
                // Optionally pre-select it for a smooth Join click
                RoomsList.SelectedItem = _cb.Rooms.FirstOrDefault(r =>
                    string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch (FaultException fe)
            {
                MessageBox.Show(fe.Message);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Create failed: " + ex.Message);
            }
        }

        private async void Join_Click(object sender, RoutedEventArgs e)
        {
            // RoomsList contains LobbyRoomInfo items
            var selected = RoomsList.SelectedItem as LobbyRoomInfo;
            var room = selected?.Name ?? (RoomsList.SelectedValue as string);
            if (string.IsNullOrWhiteSpace(room))
            {
                MessageBox.Show("Please select a room.");
                return;
            }

            bool ok = await Task.Run(() => _client.Channel.JoinRoom(_me, room));
            if (!ok)
            {
                MessageBox.Show("Join failed.");
                return;
            }

            _autoRefreshTimer?.Stop();

            // IMPORTANT: reset callback to this room (restores history if rejoining same room)
            _cb.ResetForRoom(room);

            new RoomWindow(_client, _cb, _me, room).Show();
            Close();
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try { await Task.Run(() => _client.Channel.Logout(_me)); } catch { }
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _autoRefreshTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
