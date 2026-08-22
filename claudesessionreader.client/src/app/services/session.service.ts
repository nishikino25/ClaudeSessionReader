import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { ClaudeProject, ClaudeSession, SessionMessage, SubAgentInfo } from '../models/claude.models';

@Injectable({ providedIn: 'root' })
export class SessionService {
  constructor(private http: HttpClient) {}

  discover() {
    return this.http.get<ClaudeProject[]>('/api/sessions/discover');
  }

  scan(path: string) {
    return this.http.post<ClaudeProject[]>('/api/sessions/scan', { path });
  }

  getSessions(projectPath: string) {
    return this.http.get<ClaudeSession[]>(
      `/api/sessions/projects/${encodeURIComponent(projectPath)}/sessions`
    );
  }

  getMessages(projectPath: string, sessionId: string) {
    return this.http.get<SessionMessage[]>(
      `/api/sessions/projects/${encodeURIComponent(projectPath)}/sessions/${sessionId}`
    );
  }

  getSubAgentMessages(projectPath: string, sessionId: string, agentId: string) {
    return this.http.get<SessionMessage[]>(
      `/api/sessions/projects/${encodeURIComponent(projectPath)}/sessions/${sessionId}/subagents/${agentId}`
    );
  }

  renameSession(projectPath: string, sessionId: string, title: string) {
    return this.http.put<void>(
      `/api/sessions/projects/${encodeURIComponent(projectPath)}/sessions/${sessionId}/title`,
      { title }
    );
  }

  deleteSession(projectPath: string, sessionId: string) {
    return this.http.delete<void>(
      `/api/sessions/projects/${encodeURIComponent(projectPath)}/sessions/${sessionId}`
    );
  }

  listBackupFiles() {
    return this.http.get<{ relativePath: string; size: number }[]>('/api/sessions/backup/files');
  }

  getBackupFile(relativePath: string) {
    return this.http.get(`/api/sessions/backup/file`, {
      params: { path: relativePath },
      responseType: 'blob',
    });
  }
}
