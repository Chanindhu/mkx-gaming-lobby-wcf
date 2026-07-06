using System.Threading.Tasks;
using System.Windows;

namespace MKX.Lobby.Client.Duplex
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            var name = UsernameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Enter a username.");
                return;
            }

            // Create the callback bound to UI Dispatcher and open a duplex channel
            var cb = new DuplexCallback(a => Dispatcher.Invoke(a));
            var client = new DuplexClient(cb);

            bool ok = await Task.Run(() => client.Channel.Login(name));
            if (!ok)
            {
                MessageBox.Show("Username taken. Try another.");
                return;
            }

            new LobbyWindow(client, cb, name).Show();
            Close();
        }
    }
}
