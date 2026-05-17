export interface ClaudeProject {
  folderName: string;
  fullPath: string;
  displayName: string;
  decodedPath: string;
  sessionCount: number;
  lastModified: string;
}

export interface SubAgentInfo {
  agentId: string;
  agentType: string;
  description: string;
  messageCount: number;
}

export interface ClaudeSession {
  id: string;
  title: string | null;
  startTime: string | null;
  messageCount: number;
  subAgents: SubAgentInfo[];
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
