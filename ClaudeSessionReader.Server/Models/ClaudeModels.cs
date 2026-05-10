namespace ClaudeSessionReader.Server.Models;

public record ClaudeProject(
    string FolderName,
    string FullPath,
    string DisplayName,
    string DecodedPath,
    int SessionCount,
    DateTime LastModified);

public record ClaudeSession(
    string Id,
    string? Title,
    DateTime? StartTime,
    int MessageCount);

public record ContentBlock(
    string Type,
    string? Text,
    string? Thinking,
    string? ToolUseId,
    string? ToolName,
    string? Input);

public record SessionMessage(
    string Uuid,
    string? ParentUuid,
    string Role,
    List<ContentBlock> Content,
    DateTime Timestamp);

public record ScanRequest(string Path);
