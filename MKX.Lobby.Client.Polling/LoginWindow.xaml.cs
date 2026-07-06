using System.Threading.Tasks;
using System.Windows;

namespace MKX.Lobby.Client.Polling
{
    public partial class LoginWindow : Window
    {
        // Initializes the login window and loads its XAML-defined components.
        public LoginWindow()
        {
            InitializeComponent();
        }

        // Handles the "Login" button click:
        // 1) Validates the username input (non-empty/whitespace).
        // 2) Creates a LobbyClient and calls the server's Login on a background thread.
        // 3) If the name is taken, shows a message; otherwise opens the LobbyWindow and closes this window.
        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            var name = UsernameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Enter a username.");
                return;
            }

            var client = new LobbyClient();
            bool ok = await Task.Run(() => client.Channel.Login(name));
            if (!ok)
            {
                MessageBox.Show("Username taken. Try another.");
                return;
            }

            new LobbyWindow(client, name).Show();
            Close();
        }
    }
}
