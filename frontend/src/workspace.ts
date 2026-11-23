import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

type Status = 'submitted' | 'inReview' | 'informationRequested' | 'settled' | 'rejected';
type Category = 'motor' | 'property';

interface Account {
  id: string;
  name: string;
  role: 'claimant' | 'officer';
}

interface Claim {
  id: string;
  category: Category;
  incidentDate: string;
  description: string;
  reportedLossMinor: number;
  status: Status;
  assigneeId: string | null;
  version: number;
  updatedAt: string;
}

interface HistoryEntry {
  version: number;
  status: Status;
  actor: string;
  note: string | null;
  occurredAt: string;
}

interface Detail {
  claim: Claim;
  history: HistoryEntry[];
}

type Workload = Record<Status, number> & { outstandingReportedLossMinor: number; currency: string };

@Component({
  selector: 'claims-workspace',
  imports: [FormsModule, CurrencyPipe, DatePipe],
  templateUrl: './workspace.html',
})
export class Workspace {
  private readonly http = inject(HttpClient);
  readonly account = signal<Account | null>(null);
  readonly claims = signal<Claim[]>([]);
  readonly detail = signal<Detail | null>(null);
  readonly workload = signal<Workload | null>(null);
  readonly busy = signal(true);
  readonly error = signal('');
  readonly notice = signal('');
  readonly creating = signal(false);
  readonly filter = signal<Status | 'all' | 'mine'>('all');
  readonly today = new Date(Date.now() + 8 * 60 * 60 * 1000).toISOString().slice(0, 10);
  readonly labels: Record<Status, string> = {
    submitted: 'Submitted', inReview: 'In review', informationRequested: 'Information requested',
    settled: 'Settled', rejected: 'Rejected',
  };
  readonly statuses: Status[] = ['submitted', 'inReview', 'informationRequested', 'settled', 'rejected'];
  readonly visibleClaims = computed(() => {
    const filter = this.filter();
    if (filter === 'all') return this.claims();
    if (filter === 'mine') return this.claims().filter(claim => claim.assigneeId === this.account()?.id);
    return this.claims().filter(claim => claim.status === filter);
  });

  accountId = 'claimant-a';
  password = '';
  note = '';
  draft = this.emptyDraft();

  constructor() {
    void this.restoreSession();
  }

  async signIn(): Promise<void> {
    await this.run(async () => {
      this.account.set(await firstValueFrom(this.http.post<Account>('/api/session', {
        account: this.accountId, password: this.password,
      })));
      this.password = '';
      await this.loadList();
    });
  }

  async signOut(): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(this.http.delete('/api/session'));
      this.clearSession();
    });
  }

  async refresh(): Promise<void> {
    await this.run(async () => {
      await this.loadList();
      const id = this.detail()?.claim.id;
      if (id) this.detail.set(await firstValueFrom(this.http.get<Detail>(`/api/claims/${id}`)));
      this.notice.set('Claims updated.');
    });
  }

  async openClaim(id: string): Promise<void> {
    await this.run(async () => {
      this.detail.set(await firstValueFrom(this.http.get<Detail>(`/api/claims/${id}`)));
      this.creating.set(false);
      this.note = '';
    });
  }

  startClaim(): void {
    this.notice.set('');
    this.error.set('');
    this.creating.set(true);
  }

  async submitClaim(): Promise<void> {
    await this.run(async () => {
      const detail = await firstValueFrom(this.http.post<Detail>('/api/claims', this.draft));
      this.draft = this.emptyDraft();
      this.creating.set(false);
      await this.afterChange(detail, 'Claim submitted.');
    });
  }

  async change(action: 'assignment' | 'information-request' | 'information-response' | 'decision',
    status?: 'settled' | 'rejected'): Promise<void> {
    const claim = this.detail()?.claim;
    if (!claim) return;

    await this.run(async () => {
      const detail = await firstValueFrom(this.http.post<Detail>(`/api/claims/${claim.id}/${action}`, {
        expectedVersion: claim.version, note: this.note, status,
      }));
      this.note = '';
      await this.afterChange(detail, action === 'decision' ? 'Decision recorded.' : 'Claim updated.');
    });
  }

  reference(id: string): string {
    return `CL-${id.slice(0, 8).toUpperCase()}`;
  }

  private async restoreSession(): Promise<void> {
    this.busy.set(false);
    await this.run(async () => {
      this.account.set(await firstValueFrom(this.http.get<Account>('/api/session')));
      await this.loadList();
    }, true);
  }

  private async loadList(): Promise<void> {
    const isOfficer = this.account()?.role === 'officer';
    const [claims, workload] = await Promise.all([
      firstValueFrom(this.http.get<Claim[]>('/api/claims')),
      isOfficer ? firstValueFrom(this.http.get<Workload>('/api/workload')) : Promise.resolve(null),
    ]);
    this.claims.set(claims);
    this.workload.set(workload);
  }

  private async afterChange(detail: Detail, notice: string): Promise<void> {
    this.detail.set(detail);
    this.notice.set(notice);
    this.claims.update(claims => [detail.claim, ...claims.filter(claim => claim.id !== detail.claim.id)]);
    await this.loadList();
  }

  private async run(task: () => Promise<void>, restoring = false): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      await task();
    } catch (error) {
      if (error instanceof HttpErrorResponse) {
        if (error.status === 401 && restoring) return;
        if (error.status === 401 && this.account()) this.clearSession();
        const validation = error.error?.errors as Record<string, string[]> | undefined;
        this.error.set((validation && Object.values(validation).flat().join(' ')) || error.error?.detail ||
          (error.status === 0 ? 'The claims API is unavailable.' : 'The request failed. Try again.'));
      } else {
        this.error.set('The request failed. Try again.');
      }
    } finally {
      this.busy.set(false);
    }
  }

  private clearSession(): void {
    this.account.set(null);
    this.claims.set([]);
    this.detail.set(null);
    this.workload.set(null);
    this.creating.set(false);
    this.filter.set('all');
    this.note = '';
    this.draft = this.emptyDraft();
  }

  private emptyDraft(): { category: Category; incidentDate: string; description: string; reportedLoss: number | null } {
    return { category: 'motor', incidentDate: '', description: '', reportedLoss: null };
  }
}
