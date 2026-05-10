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

    [HttpGet("projects/{encodedPath}/sessions")]
    public ActionResult<List<ClaudeSession>> GetSessions(string encodedPath)
    {
        var path = Uri.UnescapeDataString(encodedPath);
        if (!Directory.Exists(path))
            return NotFound();

        var sessions = new List<ClaudeSession>();
        foreach (var file in Directory.GetFiles(path, "*.jsonl")
                     .OrderByDescending(System.IO.File.GetLastWriteTime))
        {
            var session = ParseSessionMetadata(file);
            if (session != null) sessions.Add(session);
        }
        return sessions;
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

    private static ClaudeSession? ParseSessionMetadata(string filePath)
    {
        var sessionId = Path.GetFileNameWithoutExtension(filePath);
        string? title = null;
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
                        break;
                    case "assistant":
                        messageCount++;
                        break;
                }
            }
        }
        catch { /* skip malformed files */ }

        return new ClaudeSession(sessionId, title, startTime, messageCount);
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
