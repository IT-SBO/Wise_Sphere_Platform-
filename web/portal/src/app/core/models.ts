// API contracts of the portal backend (src/WiseSphere.Portal). Keep in sync with PortalModels.cs.

export interface EntityInfo { id: number; name: string; clientCode: string; }
export interface Me { name: string; login: string; isStaff: boolean; entities: EntityInfo[]; }

export interface TicketRow {
  id: number; title: string; status: number; priority: number; client: string; entity: string;
  openedAt: string | null; dueAt: string | null; updatedAt: string | null; source: string;
}
export interface TimelineItem { kind: 'followup' | 'task' | 'solution'; at: string | null; author: string; content: string; isPrivate: boolean; }
export interface TicketDetail { ticket: TicketRow; content: string; timeline: TimelineItem[]; glpiUrl: string; }

export interface ClientHealth { client: string; name: string; openP1: number; openP2: number; open: number; breached: number; atRisk: number; status: 'red' | 'amber' | 'green'; }
export interface Dashboard {
  open: number; openP1: number; openP2: number; atRisk: number; breached: number; storms: number;
  clients: ClientHealth[]; urgent: TicketRow[]; recent: TicketRow[];
}
export interface Problem { eventId: string; name: string; severity: number; host: string; client: string; since: string; acknowledged: boolean; }

/** GLPI ticket status codes */
export const STATUS: Record<number, string> = { 1: 'status.new', 2: 'status.assigned', 3: 'status.planned', 4: 'status.waiting', 5: 'status.solved', 6: 'status.closed' };
/** GLPI priority 6/5 = P1 ... */
export function priorityLabel(p: number): string { return p >= 5 ? 'P1' : p === 4 ? 'P2' : p === 3 ? 'P3' : 'P4'; }
