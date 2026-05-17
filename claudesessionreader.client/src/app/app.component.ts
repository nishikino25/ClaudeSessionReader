import { Component, OnInit, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { ClaudeProject, ClaudeSession, SessionMessage, SubAgentInfo } from './models/claude.models';
import { SessionService } from './services/session.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent implements OnInit, AfterViewChecked {
  @ViewChild('messagesContainer') messagesContainer?: ElementRef;

  projects: ClaudeProject[] = [];
  sessions: ClaudeSession[] = [];
  messages: SessionMessage[] = [];

  selectedProject: ClaudeProject | null = null;
  selectedSession: ClaudeSession | null = null;
  selectedSubAgent: SubAgentInfo | null = null;

  manualPath = '';
  showPathHint = false;
  loadingProjects = false;
  loadingSessions = false;
  loadingMessages = false;
  error = '';

  expandedThinking = new Set<string>();
  expandedToolUse = new Set<string>();

  theme: 'dark' | 'light' = 'dark';
  fontSize: 'sm' | 'md' | 'lg' = 'md';

  private shouldScrollToBottom = false;

  constructor(private sessionService: SessionService) {}

  ngOnInit() {
    this.theme = (localStorage.getItem('csr-theme') as any) ?? 'dark';
    this.fontSize = (localStorage.getItem('csr-font') as any) ?? 'md';
    this.applyPrefs();
  }

  setTheme(t: 'dark' | 'light') {
    this.theme = t;
    localStorage.setItem('csr-theme', t);
    this.applyPrefs();
  }

  setFontSize(s: 'sm' | 'md' | 'lg') {
    this.fontSize = s;
    localStorage.setItem('csr-font', s);
    this.applyPrefs();
  }

  private applyPrefs() {
    const body = document.body;
    body.classList.toggle('theme-light', this.theme === 'light');
    body.classList.remove('size-sm', 'size-md', 'size-lg');
    body.classList.add(`size-${this.fontSize}`);
  }

  discoverDefault() {
    this.loadingProjects = true;
    this.error = '';
    this.sessionService.discover().subscribe({
      next: projects => {
        this.projects = projects;
        this.loadingProjects = false;
      },
      error: () => {
        this.loadingProjects = false;
        this.error = 'Failed to auto-discover Claude sessions folder.';
      }
    });
  }

  ngAfterViewChecked() {
    if (this.shouldScrollToBottom) {
      this.scrollToBottom();
      this.shouldScrollToBottom = false;
    }
  }

  scanManualPath() {
    const path = this.manualPath.trim();
    if (!path) return;
    this.loadingProjects = true;
    this.error = '';
    this.sessionService.scan(path).subscribe({
      next: projects => {
        this.projects = [...projects, ...this.projects];
        this.loadingProjects = false;
        this.manualPath = '';
      },
      error: (err) => {
        this.loadingProjects = false;
        this.error = err.error?.error ?? 'Path not found or access denied.';
      }
    });
  }

  selectProject(project: ClaudeProject) {
    this.selectedProject = project;
    this.selectedSession = null;
    this.messages = [];
    this.sessions = [];
    this.loadingSessions = true;
    this.error = '';
    this.sessionService.getSessions(project.fullPath).subscribe({
      next: sessions => {
        this.sessions = sessions;
        this.loadingSessions = false;
      },
      error: () => {
        this.loadingSessions = false;
        this.error = 'Failed to load sessions.';
      }
    });
  }

  selectSession(session: ClaudeSession) {
    this.selectedSession = session;
    this.selectedSubAgent = null;
    this.messages = [];
    this.loadingMessages = true;
    this.error = '';
    this.expandedThinking.clear();
    this.expandedToolUse.clear();
    this.sessionService.getMessages(this.selectedProject!.fullPath, session.id).subscribe({
      next: messages => {
        this.messages = messages;
        this.loadingMessages = false;
        this.shouldScrollToBottom = true;
      },
      error: () => {
        this.loadingMessages = false;
        this.error = 'Failed to load messages.';
      }
    });
  }

  selectSubAgent(subAgent: SubAgentInfo) {
    this.selectedSubAgent = subAgent;
    this.messages = [];
    this.loadingMessages = true;
    this.error = '';
    this.expandedThinking.clear();
    this.expandedToolUse.clear();
    this.sessionService.getSubAgentMessages(
      this.selectedProject!.fullPath,
      this.selectedSession!.id,
      subAgent.agentId
    ).subscribe({
      next: messages => {
        this.messages = messages;
        this.loadingMessages = false;
        this.shouldScrollToBottom = true;
      },
      error: () => {
        this.loadingMessages = false;
        this.error = 'Failed to load subagent messages.';
      }
    });
  }

  toggleThinking(key: string) {
    if (this.expandedThinking.has(key)) this.expandedThinking.delete(key);
    else this.expandedThinking.add(key);
  }

  toggleToolUse(key: string) {
    if (this.expandedToolUse.has(key)) this.expandedToolUse.delete(key);
    else this.expandedToolUse.add(key);
  }

  isThinkingExpanded(key: string) { return this.expandedThinking.has(key); }
  isToolUseExpanded(key: string) { return this.expandedToolUse.has(key); }

  formatDate(dateStr: string | null) {
    if (!dateStr) return '';
    return new Date(dateStr).toLocaleString();
  }

  formatShortDate(dateStr: string | null) {
    if (!dateStr) return '';
    const d = new Date(dateStr);
    return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  private scrollToBottom() {
    try {
      const el = this.messagesContainer?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    } catch {}
  }
}
