# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local web app for browsing Claude Code conversation histories. Claude Code stores sessions as `.jsonl` files under `~/.claude/projects`; the backend parses those files and the frontend renders them in a three-panel UI (Projects → Sessions → Conversation).

## Commands

```bash
# Install frontend deps (first time only)
cd claudesessionreader.client && npm install && cd ..

# Run (SPA proxy launches the Angular dev server automatically)
cd ClaudeSessionReader.Server && dotnet run

# Frontend only
cd claudesessionreader.client
npm start          # ng serve with HTTPS dev cert
npm run build       # ng build
npm run watch       # ng build --watch --configuration development
npm test            # ng test (Karma/Jasmine) — runs full suite, browser-launching
```

There is no dedicated lint script. TypeScript type errors can be checked directly with `npx tsc --noEmit -p tsconfig.app.json` from `claudesessionreader.client/`.

Alternatively, open `ClaudeSessionReader.slnx` in Visual Studio and press F5 — it starts backend + Angular dev server together via SPA Proxy.

## Architecture

**Backend** (`ClaudeSessionReader.Server/`, ASP.NET Core / .NET 10): a single `SessionsController` does all the work — no service layer, no DI beyond the controller itself. It reads directly from the filesystem on every request; there is no caching or database.

- `DefaultClaudePath` is hardcoded to `~/.claude/projects`. `/api/sessions/scan` accepts an arbitrary alternate path for one-off browsing.
- Project folder names encode the original working directory path (Claude Code's own encoding: `C:\Users\foo\Desktop\MyProject` → `C--Users-foo-Desktop-MyProject`). `DecodeFolderName` reverses this to recover a display path — this is fragile string manipulation tied to Claude Code's current encoding scheme, not a documented format.
- Session `.jsonl` files are newline-delimited JSON; each line is a `user`/`assistant` message, or metadata like `ai-title`. `ParseSessionMessages`/`ParseContent` flatten Claude's message-content blocks (`text`, `thinking`, `tool_use`, `tool_result`) into a uniform `ContentBlock` list the frontend can render generically.
- Sub-agent (Task tool) transcripts live at `<project>/<sessionId>/subagents/agent-<id>.jsonl` with a sibling `.meta.json` for agent type/description — a separate endpoint (`GetSubAgentMessages`) serves these independently of the parent session's messages.
- The backup endpoints (`/api/sessions/backup/files`, `/api/sessions/backup/file`) exist to support the frontend's local folder-picker export (see below); `GetBackupFile` guards against path traversal by resolving and prefix-checking against the base path.
- JSON responses use ASP.NET's default camelCase policy — frontend model field names (`fullPath`, `sessionCount`, etc.) match the C# record property names automatically; don't add custom `JsonPropertyName` attributes expecting snake/Pascal case.

**Frontend** (`claudesessionreader.client/`, Angular 18, NgModule-based — not standalone components): almost the entire UI lives in one `AppComponent` (state, HTTP calls, and view logic together) rather than being split into child components. `SessionService` is a thin HTTP wrapper with no client-side caching; every project/session/sub-agent selection re-fetches from the backend. Theme and font-size preferences persist to `localStorage` and are applied by toggling CSS classes on `document.body` (`applyPrefs()`), not via Angular's style bindings.

### Backup / local export feature

`runBackup()` in `app.component.ts` uses the browser's native File System Access API (`showDirectoryPicker`, `getFileHandle`/`getDirectoryHandle` with `create: true`) to copy the entire `~/.claude/projects` tree into a folder the user picks — Chrome/Edge only. Two things to keep in mind when touching this path:

- `SessionsController.ListBackupFiles` filters out `desktop.ini` and `thumbs.db`. These are Windows folder-customization files that can end up under `~/.claude/projects` (e.g. via OneDrive sync), and the File System Access API hard-rejects those filenames with `NotAllowedError: Name is not allowed` regardless of destination path — they must stay excluded rather than surfacing as a mysterious failure.
- `runBackup()` catches copy failures per-file rather than letting one bad file abort the whole backup, and reports a summary of any files that failed at the end.
