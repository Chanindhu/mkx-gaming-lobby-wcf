# Architecture Notes

## Overview

The application is structured as a Visual Studio solution with separate projects for service contracts, server logic, a business/proxy layer, a console service host, and two WPF clients.

```text
WPF Client -> WCF Business Endpoint -> WCF Data/Service Endpoint
```

## Main Components

### MKX.Lobby.Contracts

Defines the WCF service contract, callback contract, and data transfer objects:

- `ILobbyService`
- `ILobbyCallback`
- `LobbyRoomInfo`
- `ChatMessage`
- `PrivateMessage`
- `SharedFile`
- `LobbySnapshot`

### MKX.Lobby.Server

Contains the core lobby state and server-side operations:

- Unique username tracking
- Room creation and membership
- Public room messaging
- Private messaging
- File sharing
- Event history for polling clients
- Callback dispatch for duplex clients

### MKX.Lobby.Business

Acts as a service-facing proxy layer that forwards requests to the data service while relaying duplex callback events back to clients.

### MKX.Lobby.Server.Host

Console application that hosts the WCF service endpoints using `netTcpBinding`.

### MKX.Lobby.Client.Polling

WPF client that periodically calls `GetUpdates()` on a background timer to retrieve messages, files, player lists, and room updates.

### MKX.Lobby.Client.Duplex

WPF client that registers a callback object so the server can push room, message, and file events directly to the client.

## Communication Model

The solution includes two update models:

1. **Polling model** - the client periodically asks the server for changes.
2. **Duplex model** - the server pushes updates to clients through WCF callback channels.

## Data Storage

The project uses in-memory collections for demonstration purposes. No database is required.
