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
  invite_token: string;
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
  active: number;
  slots: { t: string; d: string; slot_key: string }[];
  cycle_type: string;
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
