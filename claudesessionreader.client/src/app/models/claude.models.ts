export interface ClaudeProject {
  folderName: string;
  fullPath: string;
  displayName: string;
  decodedPath: string;
  sessionCount: number;
  lastModified: string;
}

export interface SubAgentInfo {
  sessionId: string;
  agentId: string;
  agentType: string;
  description: string;
  messageCount: number;
}

export interface ClaudeSession {
  id: string;
  title: string | null;
  aiTitle: string | null;
  startTime: string | null;
  messageCount: number;
  subAgents: SubAgentInfo[];
  /** Earlier segments (oldest first) of a session Claude Code continued into this one. */
  previousSessionIds: string[];
}

export interface ContentBlock {
  type: 'text' | 'thinking' | 'tool_use' | 'tool_result';
  text: string | null;
  thinking: string | null;
  toolUseId: string | null;
  toolName: string | null;
  input: string | null;
}

export interface SessionMessage {
  uuid: string;
  parentUuid: string | null;
  role: 'user' | 'assistant';
  content: ContentBlock[];
  timestamp: string;
}
