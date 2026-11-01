/**
 * types/api.ts —— HTTP 契约类型（唯一真相源：docs/契约冻结表.md）
 * 铁律：字段名逐字照抄，不新增、不改名、不做二次映射。
 */

export type DoseStatus = 'pending' | 'taken' | 'missed' | 'skipped';
export type Mode = 'elder' | 'guardian';
export type Role = 'admin' | 'member' | 'elder';

export interface Envelope<T> {
  code: number;
  data: T;
  msg: string;
}

export interface DoseRow {
  dose_id: string;
  pid: string;
  slot: number;
  medicine_name: string;
  dose_text: string;
  due_time: string;
  due_ts: number;
  status: DoseStatus;
  is_late: boolean;
  taken_at: number;
  is_corrected: boolean;
  snooze_count: number;
  today_index: number;
  today_total: number;
  /** 前端本地态：已点但未同步（契约无此字段，不上送） */
  syncing?: boolean;
  /** 前端本地态：这一行属于哪个东八区自然日（契约 rows[] 无 date，落库时靠它防止跨天写错日期） */
  localDate?: string;
}

export interface ScheduleToday {
  rows: DoseRow[];
  recentView: { alias: string; at: number } | null;
  server_ts: number;
}

export interface Patient {
  uid: string;
  name: string;
  alias: string;
  avatar?: string;
  selfOpenid?: string | null;
}

export interface Member {
  openid: string;
  nick: string;
  role: Role;
}

export interface FamilyItem {
  fid: string;
  name: string;
  role: Role;
  invite_token?: string;
}

/** user.login 下发的家庭列表项（与 FamilyItem 同形，独立命名便于 store 引用） */
export interface FamilyListItemDto {
  fid: string;
  name: string;
  role: Role;
}

export interface FamilyInfo {
  fid: string;
  name: string;
  role: Role;
  mode: Mode;
  selfUid: string | null;
  selfUids: string[];
  members: Member[];
  patients: Patient[];
  /** 只对 admin 下发；member / elder 收到 null（reset_invite 是 admin 权限） */
  invite_token?: string | null;
}

export interface AppConfig {
  adOn: boolean;
  adSwitch: { interstitial: boolean; elderInterstitial: boolean };
  interstitialCool: number;
  interstitialDayMax: number;
  freeMedLimit: number;
  freePatientLimit: number;
  tmplIds: { CHECKIN: string; MISSED: string; WEEKLY: string };
  quietHours: { start: string; end: string };
  missedThresholdMin: number;
  snoozeDeadlineMin: number;
  notice: string;
}

export interface LoginResult {
  openid: string;
  user: {
    nickname: string;
    avatar: string;
    disclaimer_ack_at: number;
    privacy_ack_at: number;
    setting: { sound: boolean; quiet: boolean; reduceMotion: boolean };
  };
  families: FamilyItem[];
  entitlement: { memberLevel: string; credits: number };
  config: AppConfig;
}

export interface MedicineItem {
  mid: string;
  uid: string;
  name: string;
  spec: string;
  default_dose: string;
  note?: string;
  /** 与同表的 is_late / is_corrected 一致用布尔；medicine.list 已按 active 过滤，实际恒为 true */
  active: boolean;
  slots: { t: string; d: string; slot_key: string }[];
  cycle_type: string;
}

/**
 * medicine.suggest 的一项（药名联想 / 常用药 chips）。
 * `count` = 这个药名在本家庭被添加过几次：0 = 平台预设补位，≥1 = 自己用过的。
 */
export interface MedicineSuggestItem {
  name: string;
  spec?: string;
  default_dose?: string;
  count: number;
}

export interface CheckinItem {
  pid: string;
  date: string;
  slot: number;
  status: DoseStatus;
  skip_reason?: string;
}

export interface CheckinSubmitResult {
  applied: { pid: string; date: string; slot: number; status: DoseStatus; ts: number; changed: boolean }[];
  rejected: { pid: string; date: string; slot: number; code: number; msg: string }[];
  quota?: { CHECKIN: number; MISSED: number; WEEKLY: number };
  server_ts: number;
}

export interface WeekReport {
  rate: number;
  familyCount: number;
  manualCorrectCount: number;
  missList: { date: string; slot: number; pid: string; patient: string; medicine_name: string; dose_text: string }[];
  server_ts: number;
}

export interface UnlockRecord {
  id: string;
  kind: 'med_slot' | 'week_report' | 'export';
  title: string;
  desc: string;
  at: number;
}
