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
            .Select(f => new BackupFileEntry(
                Path.GetRelativePath(DefaultClaudePath, f).Replace('\\', '/'),
                new FileInfo(f).Length))
            .ToList();
        return entries;
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

        var sessions = new List<ClaudeSession>();
        foreach (var file in Directory.GetFiles(path, "*.jsonl"))
        {
            var session = ParseSessionMetadata(file, path);
            if (session != null) sessions.Add(session);
        }
        return sessions.OrderByDescending(s => s.StartTime ?? DateTime.MinValue).ToList();
    }

    [HttpGet("projects/{encodedPath}/sessions/{sessionId}")]
    public ActionResult<List<SessionMessage>> GetSession(string encodedPath, string sessionId)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        var file = Path.Combine(path, $"{sessionId}.jsonl");
        if (!System.IO.File.Exists(file))
            return NotFound();

        return ParseSessionMessages(file);
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

    private static ClaudeSession? ParseSessionMetadata(string filePath, string projectPath)
    {
        var sessionId = Path.GetFileNameWithoutExtension(filePath);
        string? title = null;
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
                    case "ai-title":
                        if (root.TryGetProperty("aiTitle", out var t))
                            title = t.GetString();
                        break;
                    case "user":
                        messageCount++;
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
                        break;
                }
            }
        }
        catch { /* skip malformed files */ }

        if (title == null && firstUserText != null)
        {
            title = firstUserText.Length > 60
                ? firstUserText[..60].TrimEnd() + "…"
                : firstUserText;
        }

        var subAgents = ScanSubAgents(projectPath, sessionId);
        return new ClaudeSession(sessionId, title, startTime, messageCount, subAgents);
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

                subAgents.Add(new SubAgentInfo(agentId, agentType, description, msgCount));
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
