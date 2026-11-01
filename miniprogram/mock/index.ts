/**
 * mock/index.ts —— 本地契约级假后端
 *
 * 为什么要有它：`server/` 尚未落地，但 17 个页面必须能真机效果验收。
 * 本文件严格按 `docs/契约冻结表.md` 的 11 域 / action / 出入参返回，
 * 所以接真后端时只改 `config.USE_MOCK = false`，业务代码零改动。
 *
 * 数据落 storage（key 带版本号），改版本号即重新播种。
 *
 * ⚠️⚠️ 以下字段**代码在用、契约表 v1.4 未登记**（待批 4 升 v1.5 时补）。
 *     生产后端目前确实返回它们，所以线上没坏；但"严格照契约实现"的后来者会返回空，
 *     届时这几处页面会静默变空 —— 所以不要为了让 mock 与契约对齐就删掉它们（那会让开发环境也坏）。
 *     前端已有的降级兜底写在括号里，改契约前先看一遍：
 *   1. `medicine.list` 出参 `uid`      —— 药品按服药人分组用（pages/medicine/index.ts）。
 *                                        缺失时整份列表会归不到任何 patient 下 → 列表空。
 *   2. `medicine.list` 出参 `slots`    —— 药品列表/详情的"时间点·剂量"（同文件、medicine-detail）。
 *                                        已有兜底：空数组退化成只显示 `default_dose`。
 *   3. `medicine.list` 出参 `cycle_type` —— 药品表单/计划表单回填周期（medicine-form、plan-form）。
 *                                        已有兜底：`m.cycle_type || 'long_term'`。
 *   4. `medicine.list` 出参 `note`     —— 备注（表单回填）。
 *   5. `family.info` 出参 `invite_token` —— 家庭页"复制邀请码"、分享卡 ivt（family、weekly、checkin）。
 *                                        **且只对 admin 下发**（member/elder 为 null），mock 已同步；
 *                                        待补登记时要连"仅 admin"这条权限口径一起写进契约。
 *                                        已有兜底：空串时提示"邀请码还没加载出来"，分享路径不带 ivt。
 *   6. `checkin.submit` 允许 `taken → missed|skipped` —— 契约 §7 规定这条只能走 `checkin.correct`。
 *                                        这是 mock 比契约宽，前端实际走 correct，不会命中；
 *                                        保留是为了本地手动造数据方便，**不要据此认为线上允许**。
 *   （`notify.quota` 的 `mainSwitch` 已在契约 §二·8 登记，不算漏写。）
 */
import type {
  AppConfig,
  CheckinItem,
  DoseStatus,
  FamilyInfo,
  LoginResult,
  MedicineItem,
  WeekReport,
} from '../types/api';
import { CACHE_VERSION } from '../config/index';
import { addDays, dateCN, slotTs, timeToMinute, tsToTime, weekStartOf } from '../utils/time';

const KB = `mock_db_${CACHE_VERSION}`;
const ME = 'mock_openid_me';

interface MockPatient {
  uid: string;
  fid: string;
  name: string;
  alias: string;
  self_openid: string | null;
}
interface MockMedicine {
  mid: string;
  fid: string;
  uid: string;
  name: string;
  spec: string;
  default_dose: string;
  note: string;
  /** 与契约一致用布尔（后端 MedicineItemDto.Active 已收敛为 bool） */
  active: boolean;
}
interface MockPlan {
  planId: string;
  mid: string;
  uid: string;
  slots: { t: string; d: string; slot_key: string }[];
  cycle_type: string;
}
interface MockLog {
  dose_id: string;
  fid: string;
  pid: string;
  date: string;
  slot: number;
  due_ts: number;
  medicine_name: string;
  dose_text: string;
  status: DoseStatus;
  taken_at: number;
  is_late: number;
  is_corrected: number;
  snooze_count: number;
  skip_reason?: string;
}
interface MockUnlock {
  id: string;
  kind: 'med_slot' | 'week_report' | 'export';
  title: string;
  desc: string;
  at: number;
}
interface MockDB {
  v: string;
  openid: string;
  nickname: string;
  avatar: string;
  disclaimer_ack_at: number;
  privacy_ack_at: number;
  setting: { sound: boolean; quiet: boolean; reduceMotion: boolean };
  families: { fid: string; name: string; invite_token: string }[];
  members: { fid: string; openid: string; nick: string; role: 'admin' | 'member' | 'elder' }[];
  patients: MockPatient[];
  medicines: MockMedicine[];
  plans: MockPlan[];
  logs: MockLog[];
  unlocks: MockUnlock[];
  credits: number;
  quota: { CHECKIN: number; MISSED: number; WEEKLY: number };
  sealedDay: string;
}

export const CONFIG: AppConfig = {
  adOn: false, // 未开通流量主：全局关广告，AdSlot 折叠为 0 高（远端可开）
  adSwitch: { interstitial: false, elderInterstitial: false },
  interstitialCool: 30,
  interstitialDayMax: 3,
  freeMedLimit: 3,
  freePatientLimit: 1,
  tmplIds: { CHECKIN: '', MISSED: '', WEEKLY: '' },
  quietHours: { start: '22:00', end: '07:00' },
  missedThresholdMin: 60,
  snoozeDeadlineMin: 45,
  notice: '',
};

let db: MockDB | null = null;

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return Math.abs(h);
}

function seed(): MockDB {
  const today = dateCN();
  const fid = 'F1001';
  const me = ME;
  const base: MockDB = {
    v: CACHE_VERSION,
    openid: me,
    nickname: '周末',
    avatar: '',
    disclaimer_ack_at: 0,
    privacy_ack_at: 0,
    setting: { sound: true, quiet: true, reduceMotion: false },
    families: [{ fid, name: '我家', invite_token: 'IVT8888' }],
    members: [
      { fid, openid: me, nick: '我', role: 'admin' },
      { fid, openid: 'mock_sister', nick: '姐姐', role: 'member' },
      { fid, openid: 'mock_mom', nick: '妈妈', role: 'elder' },
    ],
    patients: [
      { uid: 'P001', fid, name: '爸爸', alias: '爸爸', self_openid: null },
      { uid: 'P002', fid, name: '妈妈', alias: '妈妈', self_openid: null },
    ],
    medicines: [
      { mid: 'M101', fid, uid: 'P001', name: '苯磺酸氨氯地平片', spec: '5mg×7片', default_dose: '1片', note: '早饭后', active: true },
      { mid: 'M102', fid, uid: 'P001', name: '二甲双胍缓释片', spec: '0.5g×30片', default_dose: '1片', note: '随餐', active: true },
      { mid: 'M103', fid, uid: 'P001', name: '阿司匹林肠溶片', spec: '100mg×30片', default_dose: '1片', note: '', active: true },
      { mid: 'M104', fid, uid: 'P002', name: '碳酸钙D3片', spec: '600mg×60片', default_dose: '2片', note: '', active: true },
      { mid: 'M105', fid, uid: 'P002', name: '左甲状腺素钠片', spec: '50μg×100片', default_dose: '半片', note: '空腹', active: true },
    ],
    plans: [
      { planId: 'PL101', mid: 'M101', uid: 'P001', slots: [{ t: '08:00', d: '1片', slot_key: 's0800' }], cycle_type: 'long_term' },
      { planId: 'PL102', mid: 'M102', uid: 'P001', slots: [{ t: '12:30', d: '1片', slot_key: 's1230' }], cycle_type: 'long_term' },
      { planId: 'PL103', mid: 'M103', uid: 'P001', slots: [{ t: '19:00', d: '1片', slot_key: 's1900' }], cycle_type: 'long_term' },
      { planId: 'PL104', mid: 'M104', uid: 'P002', slots: [{ t: '08:00', d: '2片', slot_key: 's0800' }, { t: '20:00', d: '2片', slot_key: 's2000' }], cycle_type: 'long_term' },
      { planId: 'PL105', mid: 'M105', uid: 'P002', slots: [{ t: '07:00', d: '半片', slot_key: 's0700' }], cycle_type: 'long_term' },
    ],
    logs: [],
    unlocks: [],
    // 5 种药 = 免费 3 个 + 已解锁 2 个名额（与"第 4 个起看视频"一致，避免首屏自相矛盾）
    credits: 2,
    quota: { CHECKIN: 4, MISSED: 2, WEEKLY: 1 },
    sealedDay: '',
  };
  return base;
}

function load(): MockDB {
  if (db) return db;
  try {
    const raw = wx.getStorageSync(KB);
    if (raw && raw.v === CACHE_VERSION) {
      db = raw as MockDB;
    }
  } catch (e) {
    db = null;
  }
  if (!db) db = seed();
  const d = db as MockDB;
  d.logs = d.logs.filter((l) => l.date >= addDays(dateCN(), -40));
  ensureDay(dateCN());
  save();
  return d;
}

function save() {
  try {
    wx.setStorageSync(KB, db);
  } catch (e) {
    /* 存储满时静默，不阻断打卡（铁律：辅助写入失败不回滚主链路） */
  }
}

/** 生成某天的剂量行（幂等）。已存在的行不动，避免覆盖用户打卡。未来日期不生成。 */
function ensureDay(date: string) {
  const d = db as MockDB;
  if (date > dateCN()) return; // 未来日期没有记录
  if (d.logs.some((l) => l.date === date)) return;
  const now = Date.now();
  const isToday = date === dateCN();
  const plans = d.plans;
  for (const p of plans) {
    const med = d.medicines.find((m) => m.mid === p.mid);
    if (!med) continue;
    for (const s of p.slots) {
      const slot = timeToMinute(s.t);
      const dueTs = slotTs(date, slot);
      const h = hash(`${date}_${p.uid}_${slot}`);
      let status: DoseStatus = 'pending';
      let takenAt = 0;
      let isCorrected = 0;

      if (isToday) {
        const passed = now - dueTs;
        if (passed > 60 * 60 * 1000) {
          // 已过点超 60 分钟：12:30 这条固定为漏服，其余为已服（保证首屏有 1 个漏服可演示）
          if (slot === 750) {
            status = 'missed';
          } else {
            status = 'taken';
            takenAt = dueTs + (h % 22) * 60 * 1000 + 40000;
          }
        } else if (passed > 0) {
          status = 'pending';
        } else {
          status = 'pending';
        }
      } else {
        // 历史日：85% 已服、10% 漏服、5% 跳过
        const r = h % 100;
        if (r < 85) {
          status = 'taken';
          takenAt = dueTs + (r % 40) * 60 * 1000 + 20000;
        } else if (r < 95) {
          status = 'missed';
        } else {
          status = 'skipped';
          isCorrected = r % 2;
        }
      }

      d.logs.push({
        dose_id: `${p.uid}_${date}_${slot}`,
        fid: 'F1001',
        pid: p.uid,
        date,
        slot,
        due_ts: dueTs,
        medicine_name: med.name,
        dose_text: s.d || med.default_dose,
        status,
        taken_at: takenAt,
        is_late: takenAt && takenAt - dueTs > 15 * 60 * 1000 ? 1 : 0,
        is_corrected: isCorrected,
        snooze_count: 0,
      });
    }
  }
}

function rowOf(l: MockLog, date: string) {
  const d = db as MockDB;
  const pidRows = d.logs.filter((x) => x.date === date && x.pid === l.pid).sort((a, b) => a.slot - b.slot);
  const idx = pidRows.findIndex((x) => x.dose_id === l.dose_id);
  return {
    dose_id: l.dose_id,
    pid: l.pid,
    slot: l.slot,
    medicine_name: l.medicine_name,
    dose_text: l.dose_text,
    due_time: `${`${Math.floor(l.slot / 60)}`.padStart(2, '0')}:${`${l.slot % 60}`.padStart(2, '0')}`,
    due_ts: l.due_ts,
    status: l.status,
    is_late: !!l.is_late,
    taken_at: l.taken_at || 0,
    is_corrected: !!l.is_corrected,
    snooze_count: l.snooze_count,
    today_index: idx + 1,
    today_total: pidRows.length,
  };
}

function modeOf(openid: string): 'elder' | 'guardian' {
  const d = db as MockDB;
  return d.patients.some((p) => p.self_openid === openid) ? 'elder' : 'guardian';
}

function familyInfo(): FamilyInfo {
  const d = db as MockDB;
  const f = d.families[0];
  const mine = d.patients.filter((p) => p.self_openid === d.openid).map((p) => p.uid);
  const myRole = (d.members.find((m) => m.openid === d.openid) || { role: 'admin' }).role;
  return {
    fid: f.fid,
    name: f.name,
    role: myRole as any,
    mode: modeOf(d.openid),
    selfUid: mine[0] || null,
    selfUids: mine,
    members: d.members
      .filter((m) => m.fid === f.fid)
      .map((m) => ({ openid: m.openid, nick: m.nick, role: m.role as any })),
    patients: d.patients
      .filter((p) => p.fid === f.fid)
      .map((p) => ({ uid: p.uid, name: p.name, alias: p.alias, selfOpenid: p.self_openid })),
    // 与真后端同步：invite_token 只给 admin（reset_invite 是 admin 权限，
    // 把码下发给 member/elder 等于把"拉人进家"的能力给了没有该权限的人）
    invite_token: myRole === 'admin' ? f.invite_token : '',
  };
}

function loginResult(): LoginResult {
  const d = db as MockDB;
  return {
    openid: d.openid,
    user: {
      nickname: d.nickname,
      avatar: d.avatar,
      disclaimer_ack_at: d.disclaimer_ack_at,
      privacy_ack_at: d.privacy_ack_at,
      setting: d.setting,
    },
    families: d.families.map((f) => ({
      fid: f.fid,
      name: f.name,
      role: (d.members.find((m) => m.openid === d.openid && m.fid === f.fid) || { role: 'admin' }).role as any,
    })),
    entitlement: { memberLevel: 'free', credits: d.credits },
    config: CONFIG,
  };
}

function weekReport(weekStart: string): WeekReport {
  const d = db as MockDB;
  const days = Array.from({ length: 7 }, (_, i) => addDays(weekStart, i));
  days.forEach((x) => ensureDay(x));
  const rows = d.logs.filter((l) => days.indexOf(l.date) >= 0);
  const planned = rows.length || 1;
  const manual = rows.filter((l) => l.is_corrected).length;
  // 完成率口径：只算"系统判定 + 自主打卡"，人工修改不计入分子分母
  const auto = rows.filter((l) => l.status === 'taken' && !l.is_corrected).length;
  const base = rows.filter((l) => !l.is_corrected).length || 1;
  const missList = rows
    .filter((l) => l.status === 'missed')
    .sort((a, b) => (a.date + a.slot < b.date + b.slot ? -1 : 1))
    .map((l) => ({
      date: l.date,
      slot: l.slot,
      pid: l.pid,
      patient: (d.patients.find((p) => p.uid === l.pid) || { alias: '' }).alias,
      medicine_name: l.medicine_name,
      dose_text: l.dose_text,
    }));
  return {
    rate: Math.round((auto / base) * 100),
    familyCount: rows.filter((l) => l.status === 'taken').length,
    manualCorrectCount: manual,
    missList,
    server_ts: Date.now(),
  };
}

type Payload = Record<string, any>;

function handleSync(domain: string, action: string, p: Payload): { code: number; data: any; msg: string } {
  const d = load();
  const ok = (data: any) => ({ code: 0, data, msg: 'ok' });
  const err = (code: number, msg: string) => ({ code, data: {}, msg });
  const today = dateCN();

  switch (`${domain}.${action}`) {
    /* ── user ── */
    case 'user.login':
      return ok(loginResult());
    case 'user.agree_privacy':
      d.privacy_ack_at = Date.now();
      save();
      return ok({ ok: true, acknowledgedAt: d.privacy_ack_at });
    case 'user.agree_disclaimer':
      d.disclaimer_ack_at = Date.now();
      save();
      return ok({ ok: true, acknowledgedAt: d.disclaimer_ack_at });
    case 'user.export':
      return ok({ fileUrl: 'https://example.com/export/mock.json', expireAt: Date.now() + 86400000 });
    case 'user.erase':
      if (!p.confirm) return err(1005, '请确认');
      db = seed();
      save();
      return ok({ ok: true, erasedAt: Date.now() });

    /* ── family ── */
    case 'family.list':
      return ok({ families: d.families });
    case 'family.info':
      return ok({ ...familyInfo(), server_ts: Date.now() });
    case 'family.join': {
      const f = d.families.find((x) => x.invite_token === p.ivt) || d.families[0];
      if (!d.members.some((m) => m.openid === d.openid)) {
        d.members.push({
          fid: f.fid,
          openid: d.openid,
          nick: d.nickname,
          role: p.role === 'patient' ? 'elder' : 'member',
        });
      }
      if (p.role === 'patient') {
        const target = d.patients.find((x) => x.uid === p.uid) || d.patients[0];
        target.self_openid = d.openid;
      }
      save();
      const info = familyInfo();
      return ok({ fid: f.fid, role: info.role, mode: info.mode, selfUid: info.selfUid, selfUids: info.selfUids, noop: false });
    }
    case 'family.create': {
      const fid = `F${Date.now() % 100000}`;
      d.families.push({ fid, name: p.name || '我家', invite_token: `IVT${fid.slice(-4)}` });
      d.members.push({ fid, openid: d.openid, nick: d.nickname, role: 'admin' });
      save();
      return ok({ fid, role: 'admin' });
    }
    case 'family.update_role': {
      const m = d.members.find((x) => x.openid === p.openid);
      if (!m) return err(1005, '成员不存在');
      m.role = p.role;
      save();
      return ok({ ok: true });
    }
    case 'family.reset_invite': {
      const f = d.families[0];
      f.invite_token = `IVT${Math.floor(Math.random() * 90000 + 10000)}`;
      save();
      return ok({ ivt: f.invite_token });
    }

    /* ── patient ── */
    case 'patient.list':
      return ok({ patients: d.patients });
    case 'patient.create': {
      if (d.patients.length >= CONFIG.freePatientLimit + 3) return err(1004, '超出限额');
      const uid = `P${Date.now() % 100000}`;
      d.patients.push({ uid, fid: 'F1001', name: p.name, alias: p.alias || p.name, self_openid: null });
      save();
      return ok({ uid });
    }
    case 'patient.update': {
      const x = d.patients.find((i) => i.uid === p.uid);
      if (!x) return err(1005, '服药人不存在');
      x.name = p.name;
      x.alias = p.alias || p.name;
      save();
      return ok({ uid: x.uid });
    }
    case 'patient.archive': {
      const i = d.patients.findIndex((x) => x.uid === p.uid);
      if (i >= 0) d.patients.splice(i, 1);
      d.medicines = d.medicines.filter((m) => m.uid !== p.uid);
      d.plans = d.plans.filter((pl) => pl.uid !== p.uid);
      save();
      return ok({ ok: true });
    }

    /* ── medicine ── */
    case 'medicine.common':
      return ok({
        items: [
          { name: '降压药', default_dose: '1片' },
          { name: '二甲双胍', default_dose: '1片' },
          { name: '阿司匹林', default_dose: '1片' },
          { name: '钙片', default_dose: '2片' },
          { name: '他汀', default_dose: '1片' },
          { name: '降糖药', default_dose: '1片' },
        ],
      });
    case 'medicine.list': {
      const items: MedicineItem[] = d.medicines
        .filter((m) => (!p.uid || m.uid === p.uid) && m.active)
        .map((m) => {
          const plan = d.plans.find((pl) => pl.mid === m.mid);
          return {
            mid: m.mid,
            uid: m.uid,
            name: m.name,
            spec: m.spec,
            default_dose: m.default_dose,
            note: m.note,
            active: m.active,
            slots: plan ? plan.slots : [],
            cycle_type: plan ? plan.cycle_type : 'long_term',
          };
        });
      return ok({ items, server_ts: Date.now() });
    }
    case 'medicine.create': {
      const owned = d.medicines.filter((m) => m.active).length;
      if (owned >= CONFIG.freeMedLimit + d.credits) {
        return err(1004, '免费药品名额已用完');
      }
      const mid = `M${Date.now() % 100000}`;
      d.medicines.push({
        mid,
        fid: 'F1001',
        uid: p.uid,
        name: p.name,
        spec: p.spec || '',
        default_dose: p.default_dose || '1片',
        note: p.note || '',
        active: true,
      });
      save();
      return ok({ mid });
    }
    case 'medicine.update': {
      const m = d.medicines.find((x) => x.mid === p.mid);
      if (!m) return err(1005, '药品不存在');
      if (p.name !== undefined) m.name = p.name;
      if (p.spec !== undefined) m.spec = p.spec;
      if (p.default_dose !== undefined) m.default_dose = p.default_dose;
      if (p.note !== undefined) m.note = p.note;
      save();
      return ok({ mid: m.mid });
    }
    case 'medicine.archive': {
      const m = d.medicines.find((x) => x.mid === p.mid);
      if (m) m.active = false;
      d.plans = d.plans.filter((pl) => pl.mid !== p.mid);
      save();
      return ok({ ok: true });
    }

    /* ── plan ── */
    case 'plan.create':
    case 'plan.update': {
      const mid = p.mid;
      const uid = (d.medicines.find((m) => m.mid === mid) || { uid: p.uid }).uid;
      const slots = (p.slots || []).map((s: any) => ({
        t: s.t,
        d: s.d || (d.medicines.find((m) => m.mid === mid) || { default_dose: '1片' }).default_dose,
        slot_key: s.slot_key || `s${timeToMinute(s.t)}`,
      }));
      const exist = d.plans.find((pl) => pl.mid === mid);
      if (exist) {
        exist.slots = slots;
        exist.cycle_type = p.cycle_type || 'long_term';
      } else {
        d.plans.push({ planId: `PL${Date.now() % 100000}`, mid, uid, slots, cycle_type: p.cycle_type || 'long_term' });
      }
      // 改计划后整树重拉：未到点的 pending 旧行删除（契约 §6）
      d.logs = d.logs.filter((l) => !(l.date === today && l.status === 'pending' && l.slot > 0 && l.medicine_name === (d.medicines.find((m) => m.mid === mid) || { name: '' }).name));
      const plan = d.plans.find((pl) => pl.mid === mid)!;
      return ok({ planId: plan.planId, nextFireAt: Date.now() + 3600000 });
    }
    case 'plan.finish': {
      const plan = d.plans.find((pl) => pl.planId === p.planId);
      if (plan) d.plans = d.plans.filter((x) => x.planId !== plan.planId);
      save();
      return ok({ ok: true });
    }

    /* ── schedule ── */
    case 'schedule.today': {
      const date = p.date || today;
      ensureDay(date);
      save();
      const info = familyInfo();
      let rows = d.logs.filter((l) => l.date === date);
      // 服务端按 selfUids 过滤（老人端收不到别人的药，不是下发后前端隐藏）
      if (info.mode === 'elder' && info.selfUids.length) {
        rows = rows.filter((l) => info.selfUids.indexOf(l.pid) >= 0);
      }
      const list = rows.sort((a, b) => a.slot - b.slot).map((l) => rowOf(l, date));
      return ok({
        rows: list,
        recentView: info.mode === 'elder' ? { alias: '女儿', at: Date.now() - 19 * 3600 * 1000 } : null,
        server_ts: Date.now(),
      });
    }
    case 'schedule.week': {
      const ws = p.weekStart || weekStartOf(today);
      return ok(weekReport(ws));
    }

    /* ── checkin ── */
    case 'checkin.submit': {
      const items: CheckinItem[] = p.items || [];
      if (items.length > 8) return err(1005, '单批最多 8 条');
      const applied: any[] = [];
      const rejected: any[] = [];
      for (const it of items) {
        ensureDay(it.date);
        const log = d.logs.find((l) => l.date === it.date && l.pid === it.pid && l.slot === it.slot);
        if (!log) {
          rejected.push({ pid: it.pid, date: it.date, slot: it.slot, code: 1005, msg: '剂量不存在' });
          continue;
        }
        // 允许迁移：pending→taken|skipped|missed；missed(未人工改判)→taken；taken→missed|skipped 仅经 correct
        const from = log.status;
        const to = it.status;
        let changed = false;
        if (from === to) {
          changed = false;
        } else if (from === 'pending' && (to === 'taken' || to === 'skipped' || to === 'missed')) {
          changed = true;
        } else if (from === 'missed' && !log.is_corrected && to === 'taken') {
          changed = true; // 系统误判可救回
        } else if (from === 'taken' && (to === 'missed' || to === 'skipped')) {
          changed = true;
        } else {
          rejected.push({ pid: it.pid, date: it.date, slot: it.slot, code: 1005, msg: '状态不允许' });
          continue;
        }
        if (changed) {
          log.status = to;
          if (to === 'taken') {
            log.taken_at = Date.now();
            log.is_late = log.taken_at - log.due_ts > 15 * 60 * 1000 ? 1 : 0;
          } else {
            log.taken_at = 0;
            log.skip_reason = it.skip_reason;
          }
        }
        applied.push({ pid: it.pid, date: it.date, slot: it.slot, status: to, ts: Date.now(), changed });
      }
      if (applied.length) d.quota.CHECKIN = Math.min(10, d.quota.CHECKIN + 1); // 打卡回填额度
      save();
      return ok({ applied, rejected, quota: d.quota, server_ts: Date.now() });
    }
    case 'checkin.snooze': {
      const log = d.logs.find((l) => l.dose_id === p.dose_id);
      if (!log) return err(1005, '剂量不存在');
      if (log.snooze_count >= 2) return err(1005, '已延后 2 次');
      const now = Date.now();
      if (now < slotTs(log.date, log.slot) || now > slotTs(log.date, log.slot) + CONFIG.snoozeDeadlineMin * 60000) {
        return err(1005, '快到这个点最后时间了');
      }
      log.due_ts += 15 * 60000;
      log.snooze_count += 1;
      save();
      return ok({ ok: true, due_ts: log.due_ts, snooze_count: log.snooze_count });
    }
    case 'checkin.correct': {
      const log = d.logs.find((l) => l.dose_id === p.dose_id);
      if (!log) return err(1005, '剂量不存在');
      if (log.is_corrected) return err(1005, '已人工改判，不可再改');
      const to = p.to_status;
      if (!(log.status === 'taken' && (to === 'missed' || to === 'skipped')) && !(log.status === 'missed' && to === 'taken')) {
        return err(1005, '状态不允许');
      }
      log.status = to;
      log.is_corrected = 1;
      log.taken_at = to === 'taken' ? Date.now() : 0;
      save();
      return ok({ ok: true, is_corrected: true });
    }

    /* ── notify ── */
    case 'notify.ack':
      d.quota.CHECKIN = Math.min(10, d.quota.CHECKIN + (p.accepted && p.accepted.length ? 1 : 0));
      save();
      return ok({ quota: d.quota });
    case 'notify.quota':
      return ok({ quota: d.quota, mainSwitch: true, server_ts: Date.now() });

    /* ── ad ── */
    case 'ad.unlock': {
      const product = p.product_id as string;
      if (product === 'med_slot') d.credits += 1;
      d.unlocks.unshift({
        id: `U${Date.now()}`,
        kind: product === 'week_report' ? 'week_report' : 'med_slot',
        title: product === 'week_report' ? '周报详细漏服分析' : '额外药品名额',
        desc: product === 'week_report' ? '含每次漏服的具体时间与趋势' : '永久有效，按名额累加',
        at: Date.now(),
      });
      save();
      return ok({ granted: true, credits: d.credits, expireAt: 0 });
    }

    /* ── report ── */
    case 'report.batch':
      return ok({ ok: true });

    /* ── config ── */
    case 'config.get':
      return ok({ config: CONFIG, server_ts: Date.now() });

    default:
      return err(1005, `未知接口 ${domain}.${action}`);
  }
}

export function mockCall(domain: string, action: string, data: Payload = {}) {
  return new Promise<{ code: number; data: any; msg: string }>((resolve) => {
    const delay = 60 + Math.floor(Math.random() * 120);
    setTimeout(() => {
      try {
        resolve(handleSync(domain, action, data));
      } catch (e) {
        resolve({ code: 1009, data: {}, msg: 'mock error' });
      }
    }, delay);
  });
}

/** 调试用：清空本地假数据 */
export function mockReset() {
  db = seed();
  save();
}

export function mockUnlocks() {
  const d = load();
  return d.unlocks;
}

/** 调试用：直接发名额（广告位未开通时也能验收解锁后的链路） */
export function mockGrant(n = 1) {
  const d = load();
  d.credits += n;
  save();
  return d.credits;
}

/** mock 内部读取：给页面补充展示用的附加信息（不参与契约） */
export function mockPeek() {
  const d = load();
  return {
    credits: d.credits,
    quota: d.quota,
    unlocks: d.unlocks,
    members: d.members.filter((m) => m.fid === 'F1001'),
    takenAt: (doseId: string) => {
      const l = d.logs.find((x) => x.dose_id === doseId);
      return l ? tsToTime(l.taken_at) : '';
    },
  };
}
