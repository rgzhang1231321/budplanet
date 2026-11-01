"""
server/scripts/smoke.py —— 端到端冒烟（不依赖任何三方包）
跑法：python server/scripts/smoke.py
前置：API 已在 5080 启动；medbox 已应用 V001–V003。

覆盖：login → family.create → patient.create → medicine.create → plan.create
      → schedule.today → checkin.submit → 复读 → schedule.week → checkin.correct
      + 两个负例（snapshot 超批上限 / snooze 超出可点窗口）
"""
import json
import sys
import urllib.request
from datetime import datetime, timedelta, timezone

BASE = "http://127.0.0.1:5080"
CN = timezone(timedelta(hours=8))
# 绕过系统代理：本机请求不该走 HTTP_PROXY（否则 502）
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(method, path, body=None, token=None):
    url = BASE + path
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("content-type", "application/json")
    if token:
        req.add_header("authorization", "Bearer " + token)
    with OPENER.open(req, timeout=15) as r:
        return json.loads(r.read().decode())


def ok(env, name):
    if env.get("code") != 0:
        print(f"  ✗ {name}: code={env.get('code')} msg={env.get('msg')}")
        return None
    print(f"  ✓ {name}")
    return env["data"]


def main():
    today = datetime.now(CN).strftime("%Y-%m-%d")
    print(f"目标：{BASE}  今天(东八区)={today}")

    print("[1] user.login")
    login = ok(call("POST", "/api/user/login", {"code": "smoke"}), "login")
    if not login:
        return 1
    token = login["token"]
    if login["user"]["disclaimer_ack_at"] == 0:
        ok(call("POST", "/api/user/agree_disclaimer", {}, token), "agree_disclaimer")

    print("[2] family")
    fam = ok(call("POST", "/api/family/create", {"name": "冒烟之家"}, token), "family.create")
    fid = fam["fid"]
    info = ok(call("GET", f"/api/family/info?fid={fid}", None, token), "family.info")
    print(f"      role={info['role']} mode={info['mode']} selfUids={info['selfUids']}")
    ivt = info["invite_token"]

    print("[3] patient")
    p = ok(call("POST", "/api/patient/create", {"fid": fid, "name": "张建国", "alias": "爸爸"}, token), "patient.create")
    uid = p["uid"]

    print("[4] medicine + plan")
    mid = ok(call("POST", "/api/medicine/create", {
        "fid": fid, "uid": uid, "name": "苯磺酸氨氯地平片", "spec": "5mg×28片", "default_dose": "1片"
    }, token), "medicine.create")["mid"]
    plan = ok(call("POST", "/api/plan/create", {
        "mid": mid,
        "slots": [{"t": "08:00", "d": "1片", "slot_key": "s480"}],
        "cycle_type": "long_term",
        "startDate": today,
    }, token), "plan.create")
    print(f"      planId={plan['planId']}")

    print("[5] schedule.today（首次会按需展开剂量行）")
    today_res = ok(call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token), "schedule.today")
    rows = today_res["rows"]
    print(f"      rows={len(rows)} server_ts={today_res['server_ts']}")
    if not rows:
        print("  ✗ 没有展开出剂量行")
        return 1
    row = rows[0]
    print(f"      dose_id={row['dose_id']} slot={row['slot']} dose_text={row['dose_text']} status={row['status']}")

    print("[6] checkin.submit（pid/date/slot 三字段，禁止从 dose_id 解析）")
    sub = ok(call("POST", "/api/checkin/submit", {
        "items": [{"pid": row["pid"], "date": today, "slot": row["slot"], "status": "taken"}]
    }, token), "checkin.submit")
    print(f"      applied={sub['applied']} rejected={sub['rejected']}")

    print("[7] 幂等复投（changed 必须为 false）")
    sub2 = ok(call("POST", "/api/checkin/submit", {
        "items": [{"pid": row["pid"], "date": today, "slot": row["slot"], "status": "taken"}]
    }, token), "checkin.submit(again)")
    print(f"      applied={sub2['applied']}")

    print("[8] schedule.week")
    week = ok(call("GET", f"/api/schedule/week?fid={fid}", None, token), "schedule.week")
    print(f"      rate={week['rate']}% familyCount={week['familyCount']} manual={week['manualCorrectCount']} miss={len(week['missList'])}")

    print("[9] checkin.correct（taken → missed，不可逆，留痕）")
    cor = ok(call("POST", "/api/checkin/correct", {
        "dose_id": row["dose_id"], "to_status": "missed", "reason": "老人没吃"
    }, token), "checkin.correct")
    print(f"      is_corrected={cor.get('is_corrected')}")

    print("[10] 负例")
    again = call("POST", "/api/checkin/correct", {"dose_id": row["dose_id"], "to_status": "taken"}, token)
    print(f"      再次改判 → code={again['code']} msg={again['msg']}（期望 1005）")
    too_many = call("POST", "/api/checkin/submit", {
        "items": [{"pid": row["pid"], "date": today, "slot": 480, "status": "taken"}] * 9
    }, token)
    print(f"      9 条超上限 → code={too_many['code']} msg={too_many['msg']}（期望 1005）")
    snooze = call("POST", "/api/checkin/snooze", {"dose_id": row["dose_id"]}, token)
    print(f"      08:00 的 dose 现在 snooze → code={snooze['code']} msg={snooze['msg']}（期望 1005，超出 45 分钟窗口）")
    noauth = call("GET", f"/api/schedule/today?fid={fid}&date={today}")
    print(f"      无 token 读 today → code={noauth['code']}（期望 1001）")

    print("[11] 老人端数据范围（服务端按 selfUids 过滤，不是下发后前端隐藏）")
    joined = ok(call("POST", "/api/family/join", {"fid": fid, "ivt": ivt, "role": "patient", "uid": uid}, token), "family.join(patient)")
    print(f"      role={joined['role']} mode={joined['mode']} selfUid={joined['selfUid']}")
    elder_today = ok(call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token), "schedule.today(elder)")
    foreign = [r for r in elder_today["rows"] if r["pid"] != uid]
    print(f"      rows={len(elder_today['rows'])} 其它人的行={len(foreign)}（期望 0）")

    print("\n完成：全链路跑通。fid=%s uid=%s mid=%s" % (fid, uid, mid))
    return 0


if __name__ == "__main__":
    sys.exit(main())
