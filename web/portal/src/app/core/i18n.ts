import { DOCUMENT } from '@angular/common';
import { Injectable, Pipe, PipeTransform, inject, signal } from '@angular/core';

/** English / Arabic UI (NFR-LNG-01). Arabic switches the page to right-to-left. Add keys to both dictionaries. */
const EN: Record<string, string> = {
  'app.tagline': 'Managed network & security services',
  'nav.dashboard': 'Dashboard', 'nav.tickets': 'Tickets', 'nav.monitoring': 'Monitoring', 'nav.newTicket': 'New request',
  'auth.title': 'Sign in', 'auth.username': 'Username', 'auth.password': 'Password', 'auth.submit': 'Sign in',
  'auth.invalid': 'Wrong username or password.', 'auth.locked': 'Too many attempts. Wait a minute and try again.', 'auth.logout': 'Sign out',
  'auth.hint': 'Use your WiseSphere support account.',
  'dash.open': 'Open tickets', 'dash.p1': 'P1 open', 'dash.p2': 'P2 open', 'dash.atRisk': 'SLA at risk', 'dash.breached': 'SLA breached',
  'dash.storms': 'Alert storms', 'dash.health': 'Client health', 'dash.urgent': 'Nearest to SLA breach', 'dash.recent': 'Recently updated',
  'dash.client': 'Client', 'dash.status': 'Status', 'dash.none': 'Nothing here.',
  'health.red': 'Critical', 'health.amber': 'Attention', 'health.green': 'Healthy',
  'tickets.title': 'Tickets', 'tickets.open': 'Open', 'tickets.solved': 'Solved', 'tickets.all': 'All', 'tickets.id': '#',
  'tickets.subject': 'Subject', 'tickets.priority': 'Priority', 'tickets.status': 'Status', 'tickets.client': 'Client',
  'tickets.due': 'Resolve by', 'tickets.updated': 'Updated', 'tickets.more': 'Load more', 'tickets.source': 'Source',
  'ticket.description': 'Description', 'ticket.history': 'History', 'ticket.reply': 'Add a reply', 'ticket.send': 'Send',
  'ticket.openGlpi': 'Open in GLPI', 'ticket.private': 'internal', 'ticket.followup': 'Reply', 'ticket.task': 'Task', 'ticket.solution': 'Solution',
  'ticket.back': 'Back to tickets', 'ticket.notFound': 'Ticket not found or not visible to you.',
  'new.title': 'New request', 'new.client': 'Client', 'new.subject': 'Subject', 'new.description': 'What is happening?',
  'new.urgency': 'Urgency', 'new.submit': 'Submit', 'new.created': 'Request created',
  'urgency.2': 'Low', 'urgency.3': 'Medium', 'urgency.4': 'High', 'urgency.5': 'Very high',
  'mon.title': 'Monitoring – current problems', 'mon.host': 'Device', 'mon.problem': 'Problem', 'mon.severity': 'Severity',
  'mon.since': 'Since', 'mon.ack': 'Acknowledged', 'mon.disabled': 'Monitoring view is not configured.', 'mon.none': 'No active problems.',
  'sev.0': 'Not classified', 'sev.1': 'Information', 'sev.2': 'Warning', 'sev.3': 'Average', 'sev.4': 'High', 'sev.5': 'Disaster',
  'status.new': 'New', 'status.assigned': 'In progress', 'status.planned': 'Planned', 'status.waiting': 'Waiting', 'status.solved': 'Solved', 'status.closed': 'Closed',
  'common.loading': 'Loading…', 'common.error': 'Something went wrong. Try again.', 'common.refresh': 'Refresh', 'common.yes': 'Yes', 'common.no': 'No',
  'lang.switch': 'العربية',
};
const AR: Record<string, string> = {
  'app.tagline': 'خدمات مُدارة للشبكات والأمن',
  'nav.dashboard': 'لوحة المتابعة', 'nav.tickets': 'التذاكر', 'nav.monitoring': 'المراقبة', 'nav.newTicket': 'طلب جديد',
  'auth.title': 'تسجيل الدخول', 'auth.username': 'اسم المستخدم', 'auth.password': 'كلمة المرور', 'auth.submit': 'دخول',
  'auth.invalid': 'اسم المستخدم أو كلمة المرور غير صحيحة.', 'auth.locked': 'محاولات كثيرة. انتظر دقيقة ثم حاول مجدداً.', 'auth.logout': 'تسجيل الخروج',
  'auth.hint': 'استخدم حساب الدعم الخاص بك في WiseSphere.',
  'dash.open': 'التذاكر المفتوحة', 'dash.p1': 'أولوية 1 مفتوحة', 'dash.p2': 'أولوية 2 مفتوحة', 'dash.atRisk': 'قريبة من تجاوز الاتفاقية', 'dash.breached': 'تجاوزت الاتفاقية',
  'dash.storms': 'موجات إنذار', 'dash.health': 'حالة العملاء', 'dash.urgent': 'الأقرب لتجاوز اتفاقية الخدمة', 'dash.recent': 'آخر التحديثات',
  'dash.client': 'العميل', 'dash.status': 'الحالة', 'dash.none': 'لا يوجد شيء هنا.',
  'health.red': 'حرجة', 'health.amber': 'تحتاج انتباه', 'health.green': 'سليمة',
  'tickets.title': 'التذاكر', 'tickets.open': 'مفتوحة', 'tickets.solved': 'محلولة', 'tickets.all': 'الكل', 'tickets.id': '#',
  'tickets.subject': 'الموضوع', 'tickets.priority': 'الأولوية', 'tickets.status': 'الحالة', 'tickets.client': 'العميل',
  'tickets.due': 'موعد الحل', 'tickets.updated': 'آخر تحديث', 'tickets.more': 'عرض المزيد', 'tickets.source': 'المصدر',
  'ticket.description': 'الوصف', 'ticket.history': 'السجل', 'ticket.reply': 'إضافة رد', 'ticket.send': 'إرسال',
  'ticket.openGlpi': 'فتح في GLPI', 'ticket.private': 'داخلي', 'ticket.followup': 'رد', 'ticket.task': 'مهمة', 'ticket.solution': 'الحل',
  'ticket.back': 'العودة إلى التذاكر', 'ticket.notFound': 'التذكرة غير موجودة أو غير متاحة لك.',
  'new.title': 'طلب جديد', 'new.client': 'العميل', 'new.subject': 'الموضوع', 'new.description': 'ما المشكلة؟',
  'new.urgency': 'درجة الاستعجال', 'new.submit': 'إرسال', 'new.created': 'تم إنشاء الطلب',
  'urgency.2': 'منخفضة', 'urgency.3': 'متوسطة', 'urgency.4': 'عالية', 'urgency.5': 'عالية جداً',
  'mon.title': 'المراقبة – المشاكل الحالية', 'mon.host': 'الجهاز', 'mon.problem': 'المشكلة', 'mon.severity': 'الخطورة',
  'mon.since': 'منذ', 'mon.ack': 'تمت المعالجة', 'mon.disabled': 'عرض المراقبة غير مُفعّل.', 'mon.none': 'لا توجد مشاكل حالياً.',
  'sev.0': 'غير مصنفة', 'sev.1': 'معلومة', 'sev.2': 'تحذير', 'sev.3': 'متوسطة', 'sev.4': 'عالية', 'sev.5': 'كارثية',
  'status.new': 'جديدة', 'status.assigned': 'قيد المعالجة', 'status.planned': 'مجدولة', 'status.waiting': 'بانتظار', 'status.solved': 'محلولة', 'status.closed': 'مغلقة',
  'common.loading': 'جارٍ التحميل…', 'common.error': 'حدث خطأ. حاول مرة أخرى.', 'common.refresh': 'تحديث', 'common.yes': 'نعم', 'common.no': 'لا',
  'lang.switch': 'English',
};

@Injectable({ providedIn: 'root' })
export class I18n {
  private doc = inject(DOCUMENT);
  readonly lang = signal<'en' | 'ar'>(this.saved());

  constructor() { this.apply(); }

  t(key: string): string { return (this.lang() === 'ar' ? AR : EN)[key] ?? EN[key] ?? key; }
  toggle() { this.lang.set(this.lang() === 'en' ? 'ar' : 'en'); this.apply(); }

  /** Dates in the user's language, Kuwait time by default. */
  date(iso: string | null): string {
    if (!iso) return '–';
    return new Intl.DateTimeFormat(this.lang() === 'ar' ? 'ar-KW' : 'en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));
  }

  private apply() {
    const html = this.doc.documentElement;
    html.lang = this.lang();
    html.dir = this.lang() === 'ar' ? 'rtl' : 'ltr';
    try { localStorage.setItem('ws.lang', this.lang()); } catch { /* storage blocked: keep in memory */ }
  }
  private saved(): 'en' | 'ar' {
    try { return localStorage.getItem('ws.lang') === 'ar' ? 'ar' : 'en'; } catch { return 'en'; }
  }
}

/** {{ 'key' | t }} - impure so it re-renders when the language changes. */
@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
  private i18n = inject(I18n);
  transform(key: string): string { return this.i18n.t(key); }
}
