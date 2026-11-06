using System.Text.Json;
using ClaudeSessionReader.Server.Models;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeSessionReader.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private static readonly string DefaultClaudePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    [HttpGet("discover")]
    public ActionResult<List<ClaudeProject>> Discover()
    {
        return ScanForProjects(DefaultClaudePath);
    }

    [HttpPost("scan")]
    public ActionResult<List<ClaudeProject>> Scan([FromBody] ScanRequest request)
    {
        if (!Directory.Exists(request.Path))
            return BadRequest(new { error = $"Directory not found: {request.Path}" });
        return ScanForProjects(request.Path);
    }

    [HttpGet("backup/files")]
    public ActionResult<List<BackupFileEntry>> ListBackupFiles()
    {
        if (!Directory.Exists(DefaultClaudePath))
            return new List<BackupFileEntry>();

        var entries = Directory.GetFiles(DefaultClaudePath, "*", SearchOption.AllDirectories)
            .Where(f => !IsOsMetadataFile(f))
            .Select(f => new BackupFileEntry(
                Path.GetRelativePath(DefaultClaudePath, f).Replace('\\', '/'),
                new FileInfo(f).Length))
            .ToList();
        return entries;
    }

    private static bool IsOsMetadataFile(string path)
    {
        // desktop.ini and thumbs.db are Windows folder-customization files, not Claude data,
        // and the browser's File System Access API refuses to create files with these names.
        var name = Path.GetFileName(path);
        return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("backup/file")]
    public IActionResult GetBackupFile([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Path is required." });

        var basePath = Path.GetFullPath(DefaultClaudePath);
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));
        if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Invalid path." });

        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        return PhysicalFile(fullPath, "application/octet-stream");
    }

    [HttpGet("projects/{encodedPath}/sessions")]
    public ActionResult<List<ClaudeSession>> GetSessions(string encodedPath)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        if (!Directory.Exists(path))
            return NotFound();

        var parsed = new Dictionary<string, (ClaudeSession Session, HashSet<string> Uuids)>();
        foreach (var file in Directory.GetFiles(path, "*.jsonl"))
        {
            var (session, uuids) = ParseSessionMetadata(file, path);
            parsed[session.Id] = (session, uuids);
        }

        // When a session runs out of context, Claude Code continues it in a new session file that
        // copies much of the earlier history, and writes a "continued-in" line into the old file.
        // Collapse each chain into its latest session so it shows up once, with the earlier
        // segments listed (oldest first) so their messages can be merged in when viewing.
        var continuedFrom = parsed.Values
            .Where(p => p.Session.ContinuedInSessionId is { } next && parsed.ContainsKey(next))
            .GroupBy(p => p.Session.ContinuedInSessionId!)
            .ToDictionary(g => g.Key, g => g.First().Session.Id);

        var sessions = new List<ClaudeSession>();
        foreach (var (latest, latestUuids) in parsed.Values)
        {
            if (latest.ContinuedInSessionId is { } next && parsed.ContainsKey(next))
                continue;

            var previous = new List<string>();
            var cursor = latest.Id;
            while (continuedFrom.TryGetValue(cursor, out var prev) && prev != latest.Id && !previous.Contains(prev))
            {
                previous.Insert(0, prev);
                cursor = prev;
            }

            if (previous.Count == 0)
            {
                sessions.Add(latest with { PreviousSessionIds = previous });
                continue;
            }

            var chain = previous.Select(id => parsed[id]).Append((Session: latest, Uuids: latestUuids)).ToList();
            var allUuids = new HashSet<string>(chain.SelectMany(c => c.Uuids));
            sessions.Add(latest with
            {
                StartTime = chain.Select(c => c.Session.StartTime).FirstOrDefault(t => t != null),
                MessageCount = allUuids.Count,
                SubAgents = chain.SelectMany(c => c.Session.SubAgents).ToList(),
                PreviousSessionIds = previous
            });
        }

        return sessions.OrderByDescending(s => s.StartTime ?? DateTime.MinValue).ToList();
    }

    [HttpGet("projects/{encodedPath}/sessions/{sessionId}")]
    public ActionResult<List<SessionMessage>> GetSession(string encodedPath, string sessionId, [FromQuery] string? previous)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        var file = Path.Combine(path, $"{sessionId}.jsonl");
        if (!System.IO.File.Exists(file))
            return NotFound();

        // previous: comma-separated earlier segments of a continued session, oldest first.
        // Continuations re-copy earlier messages, so de-duplicate by uuid while concatenating.
        var segmentIds = (previous ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidSessionId)
            .Append(sessionId);

        var seen = new HashSet<string>();
        var messages = new List<SessionMessage>();
        foreach (var id in segmentIds)
        {
            var segmentFile = Path.Combine(path, $"{id}.jsonl");
            if (!System.IO.File.Exists(segmentFile)) continue;
            messages.AddRange(ParseSessionMessages(segmentFile).Where(m => seen.Add(m.Uuid)));
        }
        return messages.OrderBy(m => m.Timestamp).ToList();
    }

    [HttpPut("projects/{encodedPath}/sessions/{sessionId}/title")]
    public IActionResult RenameSession(string encodedPath, string sessionId, [FromBody] RenameSessionRequest request)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        if (!IsValidSessionId(sessionId))
            return BadRequest(new { error = "Invalid session id." });

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            return BadRequest(new { error = "Title is required." });

        var file = Path.Combine(path, $"{sessionId}.jsonl");
        if (!System.IO.File.Exists(file))
            return NotFound();

        try
        {
            AppendCustomTitle(file, sessionId, title);
        }
        catch (IOException)
        {
            return StatusCode(500, new { error = "Failed to update the session file." });
        }

        return NoContent();
    }

    [HttpDelete("projects/{encodedPath}/sessions/{sessionId}")]
    public IActionResult DeleteSession(string encodedPath, string sessionId)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        if (!IsValidSessionId(sessionId))
            return BadRequest(new { error = "Invalid session id." });

        var file = Path.Combine(path, $"{sessionId}.jsonl");
        if (!System.IO.File.Exists(file))
            return NotFound();

        try
        {
            System.IO.File.Delete(file);

            var sessionDataDir = Path.Combine(path, sessionId);
            if (Directory.Exists(sessionDataDir))
                Directory.Delete(sessionDataDir, recursive: true);
        }
        catch (IOException)
        {
            return StatusCode(500, new { error = "Failed to delete the session file." });
        }

        return NoContent();
    }

    [HttpGet("projects/{encodedPath}/sessions/{sessionId}/subagents/{agentId}")]
    public ActionResult<List<SessionMessage>> GetSubAgentMessages(string encodedPath, string sessionId, string agentId)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        var file = Path.Combine(path, sessionId, "subagents", $"agent-{agentId}.jsonl");
        if (!System.IO.File.Exists(file))
            return NotFound();

        return ParseSessionMessages(file);
    }

    private List<ClaudeProject> ScanForProjects(string basePath)
    {
        if (!Directory.Exists(basePath))
            return [];

        return Directory.GetDirectories(basePath)
            .Select(dir =>
            {
                var folderName = Path.GetFileName(dir);
                var sessionCount = Directory.GetFiles(dir, "*.jsonl").Length;
                var (displayName, decodedPath) = DecodeFolderName(folderName);
                return new ClaudeProject(
                    folderName,
                    dir,
                    displayName,
                    decodedPath,
                    sessionCount,
                    Directory.GetLastWriteTime(dir));
            })
            .OrderByDescending(p => p.LastModified)
            .ToList();
    }

    private static (string DisplayName, string DecodedPath) DecodeFolderName(string folderName)
    {
        // Folder names encode paths: C:\Users\foo\Desktop\MyProject → C--Users-foo-Desktop-MyProject
        // ":\" → "--", "\" → "-"
        var decoded = folderName;

        // Replace first -- with :/ (drive separator)
        var driveSepIdx = decoded.IndexOf("--", StringComparison.Ordinal);
        if (driveSepIdx >= 0)
            decoded = decoded[..driveSepIdx] + ":/" + decoded[(driveSepIdx + 2)..];

        // Replace remaining - with /
        decoded = decoded[..Math.Min(3, decoded.Length)] + decoded[Math.Min(3, decoded.Length)..].Replace("-", "/");

        // DisplayName: last path segment
        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var displayName = segments.Length > 0 ? segments[^1] : folderName;

        return (displayName, decoded);
    }

    private static bool IsValidSessionId(string sessionId) =>
        !string.IsNullOrEmpty(sessionId)
        && sessionId.All(c => char.IsLetterOrDigit(c) || c == '-');

    private static void AppendCustomTitle(string filePath, string sessionId, string newTitle)
    {
        // Claude Code records a rename by appending a "custom-title" line, and keeps appending
        // "ai-title" lines afterwards; custom-title takes precedence when reading (see
        // ParseSessionMetadata). Appending the same way keeps the rename visible to Claude Code's
        // own /resume list and avoids rewriting a file Claude Code may still be writing to.
        var line = JsonSerializer.Serialize(new { type = "custom-title", customTitle = newTitle, sessionId });
        var needsNewline = false;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                needsNewline = fs.ReadByte() != '\n';
            }
        }
        System.IO.File.AppendAllText(filePath, (needsNewline ? "\n" : "") + line + "\n", new System.Text.UTF8Encoding(false));
    }

    private static (ClaudeSession Session, HashSet<string> Uuids) ParseSessionMetadata(string filePath, string projectPath)
    {
        var uuids = new HashSet<string>();
        var sessionId = Path.GetFileNameWithoutExtension(filePath);
        string? aiTitle = null;
        string? customTitle = null;
        string? continuedIn = null;
        string? firstUserText = null;
        DateTime? startTime = null;
        int messageCount = 0;

        try
        {
            foreach (var line in System.IO.File.ReadLines(filePath, System.Text.Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                switch (type)
                {
                    // Claude Code keeps appending ai-title lines even after a rename, so a
                    // custom-title must win regardless of line order; within each kind the last wins.
                    case "ai-title":
                        if (root.TryGetProperty("aiTitle", out var t))
                            aiTitle = t.GetString();
                        break;
                    case "custom-title":
                        if (root.TryGetProperty("customTitle", out var ct))
                            customTitle = ct.GetString();
                        break;
                    case "continued-in":
                        if (root.TryGetProperty("continuedInSessionId", out var ci))
                            continuedIn = ci.GetString();
                        break;
                    case "user":
                        messageCount++;
                        if (root.TryGetProperty("uuid", out var uu) && uu.GetString() is { } userUuid)
                            uuids.Add(userUuid);
                        if (startTime == null && root.TryGetProperty("timestamp", out var ts))
                        {
                            if (DateTime.TryParse(ts.GetString(), out var dt))
                                startTime = dt;
                        }
                        if (firstUserText == null && root.TryGetProperty("message", out var msg))
                            firstUserText = ExtractFirstText(msg);
                        break;
                    case "assistant":
                        messageCount++;
                        if (root.TryGetProperty("uuid", out var au) && au.GetString() is { } assistantUuid)
                            uuids.Add(assistantUuid);
                        break;
                }
            }
        }
        catch { /* skip malformed files */ }

        var title = customTitle ?? aiTitle;
        if (title == null && firstUserText != null)
        {
            title = firstUserText.Length > 60
                ? firstUserText[..60].TrimEnd() + "…"
                : firstUserText;
        }

        var subAgents = ScanSubAgents(projectPath, sessionId);
        return (new ClaudeSession(sessionId, title, aiTitle, startTime, messageCount, subAgents, [], continuedIn), uuids);
    }

    private static List<SubAgentInfo> ScanSubAgents(string projectPath, string sessionId)
    {
        var subAgents = new List<SubAgentInfo>();
        var subagentsDir = Path.Combine(projectPath, sessionId, "subagents");
        if (!Directory.Exists(subagentsDir)) return subAgents;

        foreach (var metaFile in Directory.GetFiles(subagentsDir, "*.meta.json"))
        {
            try
            {
                var json = System.IO.File.ReadAllText(metaFile, System.Text.Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var agentType = root.TryGetProperty("agentType", out var at) ? at.GetString() ?? "unknown" : "unknown";
                var description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                // strip double extension: agent-<id>.meta.json → agent-<id>
                var fileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metaFile));
                var agentId = fileName.StartsWith("agent-") ? fileName["agent-".Length..] : fileName;

                var jsonlFile = Path.Combine(subagentsDir, $"agent-{agentId}.jsonl");
                var msgCount = CountMessages(jsonlFile);

                subAgents.Add(new SubAgentInfo(sessionId, agentId, agentType, description, msgCount));
            }
            catch { /* skip malformed meta */ }
        }

        return subAgents;
    }

    private static int CountMessages(string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return 0;
        int count = 0;
        try
        {
            foreach (var line in System.IO.File.ReadLines(filePath, System.Text.Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) continue;
                var type = t.GetString();
                if (type == "user" || type == "assistant") count++;
            }
        }
        catch { }
        return count;
    }

    private static string? ExtractFirstText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                    && item.TryGetProperty("text", out var t))
                {
                    var text = t.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
        }

        return null;
    }

    private static List<SessionMessage> ParseSessionMessages(string filePath)
    {
        var messages = new List<SessionMessage>();

        try
        {
            foreach (var line in System.IO.File.ReadLines(filePath, System.Text.Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString();

                if (type != "user" && type != "assistant") continue;
                if (!root.TryGetProperty("message", out var msgProp)) continue;

                var uuid = root.TryGetProperty("uuid", out var u) ? u.GetString()! : Guid.NewGuid().ToString();
                var parentUuid = root.TryGetProperty("parentUuid", out var pu) && pu.ValueKind != JsonValueKind.Null
                    ? pu.GetString() : null;
                var timestamp = root.TryGetProperty("timestamp", out var ts) && DateTime.TryParse(ts.GetString(), out var dt)
                    ? dt : DateTime.MinValue;

                if (!msgProp.TryGetProperty("role", out var roleProp)) continue;
                var role = roleProp.GetString()!;
                var content = ParseContent(msgProp);

                if (content.Count == 0) continue;
                messages.Add(new SessionMessage(uuid, parentUuid, role, content, timestamp));
            }
        }
        catch { /* skip malformed files */ }

        return messages;
    }

    private static List<ContentBlock> ParseContent(JsonElement message)
    {
        var blocks = new List<ContentBlock>();
        if (!message.TryGetProperty("content", out var content)) return blocks;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add(new ContentBlock("text", text, null, null, null, null));
            return blocks;
        }

        if (content.ValueKind != JsonValueKind.Array) return blocks;

        foreach (var item in content.EnumerateArray())
        {
            var blockType = item.TryGetProperty("type", out var bt) ? bt.GetString() : "text";
            switch (blockType)
            {
                case "text":
                    var text = item.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrEmpty(text))
                        blocks.Add(new ContentBlock("text", text, null, null, null, null));
                    break;
                case "thinking":
                    var thinking = item.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                    if (!string.IsNullOrEmpty(thinking))
                        blocks.Add(new ContentBlock("thinking", null, thinking, null, null, null));
                    break;
                case "tool_use":
                    var toolId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
                    var toolName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? inputJson = null;
                    if (item.TryGetProperty("input", out var inp))
                        inputJson = inp.GetRawText();
                    blocks.Add(new ContentBlock("tool_use", null, null, toolId, toolName, inputJson));
                    break;
                case "tool_result":
                    string? resultText = null;
                    if (item.TryGetProperty("content", out var rc))
                    {
                        if (rc.ValueKind == JsonValueKind.String)
                            resultText = rc.GetString();
                        else if (rc.ValueKind == JsonValueKind.Array)
                        {
                            var parts = new List<string>();
                            foreach (var ri in rc.EnumerateArray())
                                if (ri.TryGetProperty("text", out var rt))
                                    parts.Add(rt.GetString() ?? "");
                            resultText = string.Join("\n", parts);
                        }
                    }
                    if (!string.IsNullOrEmpty(resultText))
                        blocks.Add(new ContentBlock("tool_result", resultText, null, null, null, null));
                    break;
            }
        }

        return blocks;
    }
}
