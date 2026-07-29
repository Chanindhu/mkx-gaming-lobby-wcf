# MKX Gaming Lobby - WCF/WPF

A desktop online gaming lobby prototype built with **C#**, **.NET Framework 4.8**, **Windows Communication Foundation (WCF)**, and **Windows Presentation Foundation (WPF)**.

The system demonstrates a lobby server, a polling-based WPF client, and a duplex WCF client that receives real-time server-pushed updates.

## Reviewer Quick Scan

- **What it demonstrates:** distributed .NET desktop architecture, WCF service contracts, polling clients, duplex callbacks, chat, rooms, and file-sharing workflows.
- **Best files to inspect first:** [`MKX.Lobby.Contracts/Contracts.cs`](MKX.Lobby.Contracts/Contracts.cs), [`MKX.Lobby.Server/LobbyService.cs`](MKX.Lobby.Server/LobbyService.cs), and [`MKX.Lobby.Client.Duplex/DuplexCallback.cs`](MKX.Lobby.Client.Duplex/DuplexCallback.cs).
- **How to verify it:** open the solution in Visual Studio, start the server host, then run multiple client projects to test room, chat, private message, and file-transfer flows.

## Features

- Unique username login and logout
- Create, list, join, and leave lobby rooms
- Room-based public chat
- Private one-to-one messaging with a separate chat window
- Shared image/text file transfer inside a room
- Private file sharing between players
- Polling client using timed background update retrieval
- Duplex client using WCF callback channels over `netTcpBinding`
- Multi-project Visual Studio solution separating contracts, server, business proxy, host, and clients

## Proof and Review Evidence

| Evidence | Where to inspect it | What it proves |
|---|---|---|
| WCF service contract | [`MKX.Lobby.Contracts/Contracts.cs`](MKX.Lobby.Contracts/Contracts.cs) | The lobby, callback, DTO, room, message, and file-sharing contracts are explicit. |
| Lobby service implementation | [`MKX.Lobby.Server/LobbyService.cs`](MKX.Lobby.Server/LobbyService.cs) | The server-side room, chat, private messaging, and file-transfer behavior is implemented. |
| Server host | [`MKX.Lobby.Server.Host/Program.cs`](MKX.Lobby.Server.Host/Program.cs) | The WCF endpoints are hosted as a runnable console service. |
| Polling client | [`MKX.Lobby.Client.Polling/LobbyWindow.xaml.cs`](MKX.Lobby.Client.Polling/LobbyWindow.xaml.cs) | One client path demonstrates timed refresh/polling behavior. |
| Duplex callback client | [`MKX.Lobby.Client.Duplex/DuplexCallback.cs`](MKX.Lobby.Client.Duplex/DuplexCallback.cs) | The second client path demonstrates server-pushed updates through callbacks. |
| Architecture notes | [`docs/architecture-notes.md`](docs/architecture-notes.md) | The solution structure and communication responsibilities are documented. |
| Build/run notes | [`docs/build-and-run.md`](docs/build-and-run.md) | A Windows reviewer can reproduce the project locally. |

## Project Structure

```text
mkx-gaming-lobby-wcf/
|-- MKX.Lobby.sln
|-- MKX.Lobby.Contracts/                      # WCF contracts and shared DTO models
|-- MKX.Lobby.Server/                         # Core lobby service and in-memory room state
|-- MKX.Lobby.Business/                       # Business/proxy service layer
|-- MKX.Lobby.Server.Host/                    # Console host for WCF services
|-- MKX.Lobby.Client.Polling/                 # WPF client using background polling
|-- MKX.Lobby.Client.Duplex/                  # WPF client using duplex WCF callbacks
|-- docs/
|   |-- architecture-notes.md
|   `-- build-and-run.md
|-- .gitignore
`-- README.md
```

## Tech Stack

- C#
- .NET Framework 4.8
- WPF
- WCF
- `netTcpBinding`
- Visual Studio

## Requirements

- Windows 10/11
- Visual Studio 2022
- .NET Framework 4.8 Developer Pack
- `.NET desktop development` workload installed in Visual Studio

This is a Windows/.NET Framework project. It is not intended to run directly on macOS without a Windows VM or Windows machine.

## How to Run

1. Open `MKX.Lobby.sln` in Visual Studio.
2. Build the full solution.
3. Start the server host first:

```text
MKX.Lobby.Server.Host
```

The host exposes:

```text
Data tier     : net.tcp://0.0.0.0:9090/MKXLobby/Service
Business tier : net.tcp://0.0.0.0:9090/MKXLobby/Business
MEX           : net.tcp://0.0.0.0:9090/MKXLobby/mex
```

4. Run one or more client projects:

```text
MKX.Lobby.Client.Polling
MKX.Lobby.Client.Duplex
```

5. Log in with different usernames, create/join rooms, send public/private messages, and share files.

## Notes

- The server uses in-memory state, so rooms/messages reset when the host closes.
- The app is designed for local demonstration using `127.0.0.1` / port `9090`.
- If clients cannot connect, check that the server host is running and that Windows Firewall allows local TCP traffic on port `9090`.

## Portfolio Context

This project was originally developed as an academic distributed computing assignment and has been cleaned for portfolio presentation by removing submission artifacts, generated build output, and IDE cache files.
