# MKX Gaming Lobby - WCF/WPF

A desktop online gaming lobby prototype built with **C#**, **.NET Framework 4.8**, **Windows Communication Foundation (WCF)**, and **Windows Presentation Foundation (WPF)**.

The system demonstrates a lobby server, a polling-based WPF client, and a duplex WCF client that receives real-time server-pushed updates.

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

## Project Structure

```text
MKX.Lobby.sln
MKX.Lobby.Contracts/         WCF contracts and data transfer models
MKX.Lobby.Server/            Core lobby service and in-memory room state
MKX.Lobby.Business/          Business/proxy service layer
MKX.Lobby.Server.Host/       Console host for the WCF services
MKX.Lobby.Client.Polling/    WPF client using background polling
MKX.Lobby.Client.Duplex/     WPF client using duplex WCF callbacks
docs/                        Architecture and run notes
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
