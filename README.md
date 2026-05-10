# Claude Session Reader

A local web application for browsing Claude Code conversation histories.
Claude Code stores sessions as `.jsonl` files under `~/.claude/projects` — this tool parses and presents them in a readable three-panel UI.

## Features

- **Auto-discover** the default `~/.claude/projects` directory, or paste any custom path
- **Three-panel layout**: Projects → Sessions → Conversation
- Renders all content block types:
  - `text` — with Markdown formatting
  - `thinking` — collapsible extended thinking blocks
  - `tool_use` — collapsible tool call details
  - `tool_result` — expandable tool output
- Dark / Light theme toggle (persisted in localStorage)
- Font size control: S / M / L (persisted in localStorage)

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core (.NET 10) |
| Frontend | Angular 18 |
| Markdown | marked |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (LTS recommended)

## Getting Started

### Visual Studio

Open `ClaudeSessionReader.slnx` and press **F5**. Visual Studio will start both the backend and the Angular dev server automatically via SPA Proxy.

### CLI

```bash
# Install frontend dependencies (first time only)
cd claudesessionreader.client
npm install
cd ..

# Run the backend (SPA Proxy will launch the frontend automatically)
cd ClaudeSessionReader.Server
dotnet run
```

The app will be available at `https://localhost:7xxx` (see `launchSettings.json` for the exact port).

## API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/sessions/discover` | Scan the default `~/.claude/projects` directory |
| POST | `/api/sessions/scan` | Scan a custom directory `{ "path": "..." }` |
| GET | `/api/sessions/projects/{encodedPath}/sessions` | List sessions in a project |
| GET | `/api/sessions/projects/{encodedPath}/sessions/{sessionId}` | Get messages for a session |

## Project Structure

```
ClaudeSessionReader/
├── ClaudeSessionReader.Server/   # ASP.NET Core backend
│   ├── Controllers/
│   │   └── SessionsController.cs
│   ├── Models/
│   │   └── ClaudeModels.cs
│   └── Program.cs
└── claudesessionreader.client/   # Angular frontend
    └── src/
        └── app/
            ├── app.component.*
            ├── models/
            ├── pipes/
            └── services/
```
