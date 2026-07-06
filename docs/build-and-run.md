# Build and Run Notes

## Recommended Environment

- Windows 10/11
- Visual Studio 2022
- .NET Framework 4.8 Developer Pack

## Build

Open the solution:

```text
MKX.Lobby.sln
```

Then choose:

```text
Build -> Build Solution
```

## Run Order

1. Start `MKX.Lobby.Server.Host`.
2. Start one or more clients:
   - `MKX.Lobby.Client.Polling`
   - `MKX.Lobby.Client.Duplex`
3. Use unique usernames for each client instance.
4. Create or join a room.
5. Test public chat, private messages, file sharing, and logout.

## Troubleshooting

### Client cannot connect

Check that the server host is running and listening on port `9090`.

### Duplicate username rejected

This is expected. Each logged-in player must have a unique username.

### Shared files do not appear

Confirm that the sender and receiver are inside the same lobby room for room file sharing. Private files require a selected private recipient.

### Running on macOS

This is a WPF/WCF .NET Framework application, so use a Windows machine or Windows virtual machine.
