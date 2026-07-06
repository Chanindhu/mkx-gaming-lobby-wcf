using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.ServiceModel;                  // keep this
using MKX.Lobby.Contracts;

namespace MKX.Lobby.Client.Polling
{
    public partial class LobbyWindow : Window
    {
        private readonly LobbyClient _client;
        private readonly string _me;

        private readonly ObservableCollection<LobbyRoomInfo> _rooms =
            new ObservableCollection<LobbyRoomInfo>();
        private ICollectionView _roomsView;

        private readonly DispatcherTimer _autoRefreshTimer;
        private bool _isLoadingRooms = false;

        // Initializes the lobby UI, binds the rooms list with a filter, performs an initial fetch,
        // and starts a lightweight auto-refresh timer (skips while the list has focus/hover).
        public LobbyWindow(LobbyClient client, string me)
        {
            InitializeComponent();
            _client = client;
            _me = me;

            UserLabel.Text = "Logged in as: " + _me;

            _roomsView = CollectionViewSource.GetDefaultView(_rooms);
            _roomsView.Filter = RoomFilter;
            RoomsList.ItemsSource = _roomsView;

            _ = LoadRooms(force: true);

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoRefreshTimer.Tick += async (_, __) =>
            {
                if (RoomsList.IsKeyboardFocusWithin || RoomsList.IsMouseOver)
                    return;
                await LoadRooms();
            };
            _autoRefreshTimer.Start();
        }

        // Predicate for ICollectionView: returns true if a room matches the search text (case-insensitive).
        private bool RoomFilter(object obj)
        {
            var item = obj as LobbyRoomInfo;
            if (item == null) return false;

            var q = (SearchBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q)) return true;

            return item.Name != null &&
                   item.Name.ToLowerInvariant().Contains(q.ToLowerInvariant());
        }

        // Triggers re-evaluation of the rooms filter whenever the search box text changes.
        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _roomsView?.Refresh();
        }

        // Fetches the latest rooms from the server and applies a delta update to the bound collection.
        // Uses a reentrancy guard; optionally forces a reload even if already loading.
        private async Task LoadRooms(bool force = false)
        {
            if (_isLoadingRooms && !force) return;

            string selectedName = RoomsList.SelectedValue as string;

            try
            {
                _isLoadingRooms = true;

                var fresh = await Task.Run(() => _client.Channel.ListRooms())
                           ?? new List<LobbyRoomInfo>();

                ApplyRoomsDelta(fresh);

                _roomsView.Refresh();
            }
            catch
            {
            }
            finally
            {
                _isLoadingRooms = false;
            }

            if (!string.IsNullOrWhiteSpace(selectedName) &&
                _rooms.Any(r => string.Equals(r.Name, selectedName, StringComparison.OrdinalIgnoreCase)))
            {
                RoomsList.SelectedValue = selectedName;
            }
        }

        // Updates the local rooms collection to match the fresh list:
        // removes missing rooms, updates player counts, and inserts new rooms in sorted order.
        private void ApplyRoomsDelta(IEnumerable<LobbyRoomInfo> freshList)
        {
            var fresh = (freshList ?? Enumerable.Empty<LobbyRoomInfo>())
                        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

            for (int i = _rooms.Count - 1; i >= 0; i--)
            {
                var cur = _rooms[i];
                var newer = fresh.FirstOrDefault(r => string.Equals(r.Name, cur.Name, StringComparison.OrdinalIgnoreCase));
                if (newer == null)
                {
                    _rooms.RemoveAt(i);
                }
                else if (cur.PlayerCount != newer.PlayerCount)
                {
                    _rooms[i] = new LobbyRoomInfo { Name = newer.Name, PlayerCount = newer.PlayerCount };
                }
            }

            foreach (var r in fresh)
            {
                if (!_rooms.Any(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    int idx = 0;
                    while (idx < _rooms.Count &&
                           string.Compare(_rooms[idx].Name, r.Name, StringComparison.OrdinalIgnoreCase) < 0)
                        idx++;
                    _rooms.Insert(idx, new LobbyRoomInfo { Name = r.Name, PlayerCount = r.PlayerCount });
                }
            }
        }

        // Creates a new room on the server after validating the name; refreshes the list on success.
        private async void Create_Click(object s, RoutedEventArgs e)
        {
            var name = (NewRoomBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Room name is required.");
                return;
            }

            // avoid hiding the new room accidentally
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = string.Empty;

            try
            {
                // server now returns false for duplicates / blanks
                var ok = await Task.Run(() => _client.Channel.CreateRoom(name));
                if (!ok)
                {
                    MessageBox.Show($"Room \"{name}\" already exists.");
                    return;
                }

                await LoadRooms(force: true);
                RoomsList.SelectedIndex = -1;
            }
            catch (FaultException fe)
            {
                // in case server throws in future
                MessageBox.Show(fe.Message);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Create failed: " + ex.Message);
            }
        }

        // Joins the selected (or typed) room; stops auto-refresh and opens the room window on success.
        private async void Join_Click(object s, RoutedEventArgs e)
        {
            var selected = RoomsList.SelectedItem as LobbyRoomInfo;
            var room = selected != null ? selected.Name : (NewRoomBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(room)) return;

            bool ok = await Task.Run(() => _client.Channel.JoinRoom(_me, room));
            if (!ok)
            {
                MessageBox.Show("Join failed.");
                return;
            }

            _autoRefreshTimer?.Stop();

            var wnd = new RoomWindow(_client, _me, room);
            wnd.Show();
            Close();
        }

        // Logs out the current user and closes the application.
        private async void Logout_Click(object s, RoutedEventArgs e)
        {
            try { await Task.Run(() => _client.Channel.Logout(_me)); } catch { }
            Application.Current.Shutdown();
        }

        // Stops the auto-refresh timer when the window is closed.
        protected override void OnClosed(EventArgs e)
        {
            _autoRefreshTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
