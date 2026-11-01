"""
server/scripts/smoke_v2.py —— 补完的那批接口的端到端验证（走公开 API，不直接写库除少数造数场景）

覆盖：patient.update/archive、medicine.update/archive、plan.finish、
      family.update_role/leave/reset_invite、user.export/erase、report.batch、dose_views 回执。

跑法：python server/scripts/smoke_v2.py
前置：API 在 5080 跑着；docker 里 tigerrag-postgres-1 可访问（脚本用它造多成员/校验落库）。
注意：最后一步 user.erase 会注销本地开发用户，脚本跑完要重新跑 seed_demo.py 恢复演示数据。
"""
import json
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

BASE = "http://127.0.0.1:5080"
CN = timezone(timedelta(hours=8))
OP = urllib.request.build_opener(urllib.request.ProxyHandler({}))

FAILED = []


def call(method, path, body=None, token=None):
    """返回 (code, data, msg)。不抛异常 —— 负例要靠它读错误码。"""
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("content-type", "application/json")
    if token:
        req.add_header("authorization", "Bearer " + token)
    try:
        with OP.open(req, timeout=15) as r:
            env = json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            env = json.loads(raw)
        except Exception:
            return (e.code, {}, raw[:200])
        # HTTP 层错误（401 等）：返回状态码，业务 code 放 msg 里备查
        return (e.code, env, f"http{e.code}/code{env.get('code')}")
    return (env.get("code"), env.get("data"), env.get("msg"))


def ok(label, res):
    code, data, msg = res
    if code != 0:
        FAILED.append(f"{label}: code={code} msg={msg}")
        print(f"  ✗ {label} -> {code} {msg}")
        return None
    print(f"  ✓ {label}")
    return data


def sql(q):
    r = subprocess.run(
        ["docker", "exec", "tigerrag-postgres-1", "psql", "-U", "tigerrag", "-d", "medbox", "-tAc", q],
        capture_output=True, text=True,
    )
    return r.stdout.strip()


def expect(label, actual, wanted):
    if actual == wanted:
        print(f"  ✓ {label} = {actual}")
    else:
        FAILED.append(f"{label}: 期望 {wanted}，实际 {actual}")
        print(f"  ✗ {label}: 期望 {wanted}，实际 {actual}")


def section(t):
    print(f"\n── {t} ──")


def main():
    today = datetime.now(CN).strftime("%Y-%m-%d")
    login = ok("user.login", call("POST", "/api/user/login", {"code": "smoke2"}))
    token = login["token"]
    openid = login["openid"]
    print(f"     openid={openid}")

    fid = ok("family.create", call("POST", "/api/family/create", {"name": "Smoke2Home"}, token))["fid"]
    uid = ok("patient.create", call("POST", "/api/patient/create",
                                    {"fid": fid, "name": "Smoke Dad", "alias": "Dad"}, token))["uid"]

    mid_a = ok("medicine.create A(08:00)", call("POST", "/api/medicine/create",
                                                {"fid": fid, "uid": uid, "name": "SmokeMedA", "default_dose": "1"}, token))["mid"]
    mid_b = ok("medicine.create B(22:00)", call("POST", "/api/medicine/create",
                                                {"fid": fid, "uid": uid, "name": "SmokeMedB", "default_dose": "1"}, token))["mid"]

    plan_a = ok("plan.create A", call("POST", "/api/plan/create",
                                      {"mid": mid_a, "slots": [{"t": "08:00", "d": "1"}], "cycle_type": "long_term",
                                       "startDate": today}, token))["planId"]
    plan_b = ok("plan.create B", call("POST", "/api/plan/create",
                                      {"mid": mid_b, "slots": [{"t": "22:00", "d": "1"}], "cycle_type": "long_term",
                                       "startDate": today}, token))["planId"]

    section("patient.update / medicine.update（快照同步）")
    ok("patient.update", call("POST", "/api/patient/update",
                              {"fid": fid, "uid": uid, "name": "Smoke Dad", "alias": "Father"}, token))
    expect("patient.alias 落库", sql(f"select alias from patients where patient_id='{uid}'"), "Father")

    # 先 materialize 今天两行，再改名 —— 22:00 未到点应同步快照，08:00 已过点不动
    today_rows = ok("schedule.today", call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token))
    expect("今日行数", len(today_rows["rows"]), 2)

    ok("medicine.update B -> SmokeMedB2", call("POST", "/api/medicine/update",
                                               {"fid": fid, "uid": uid, "mid": mid_b, "name": "SmokeMedB2",
                                                "spec": "10mg", "default_dose": "1"}, token))
    expect("22:00 行快照已同步",
           sql(f"select med_name_snapshot from dose_logs where medicine_id='{mid_b}' and due_date='{today}'"),
           "SmokeMedB2")
    expect("08:00 行快照保持旧名（历史不可被改名抹掉）",
           sql(f"select med_name_snapshot from dose_logs where medicine_id='{mid_a}' and due_date='{today}'"),
           "SmokeMedA")

    section("plan.finish（幂等 + 删未到点 pending）")
    ok("plan.finish B", call("POST", "/api/plan/finish", {"planId": plan_b}, token))
    ok("plan.finish B 再调一次（幂等）", call("POST", "/api/plan/finish", {"planId": plan_b}, token))
    expect("22:00 未到点行已删", sql(f"select count(*) from dose_logs where plan_id='{plan_b}'"), "0")
    expect("plan 状态", sql(f"select status from plans where plan_id='{plan_b}'"), "finished")

    section("medicine.archive")
    ok("medicine.archive A", call("POST", "/api/medicine/archive", {"fid": fid, "mid": mid_a}, token))
    ok("medicine.archive A 再调（幂等）", call("POST", "/api/medicine/archive", {"fid": fid, "mid": mid_a}, token))
    left = ok("medicine.list", call("GET", f"/api/medicine/list?fid={fid}", None, token))
    expect("归档 A 后只剩 B", [x["name"] for x in left["items"]], ["SmokeMedB2"])

    section("patient.archive")
    ok("patient.archive", call("POST", "/api/patient/archive", {"fid": fid, "uid": uid}, token))
    plist = ok("patient.list", call("GET", f"/api/patient/list?fid={fid}", None, token))
    expect("归档后患者列表为空", len(plist["patients"]), 0)

    section("report.batch（隐私闸门 + 落 stats_daily）")
    ok("report.batch", call("POST", "/api/report/batch", {"events": [
        {"name": "checkin_success", "ts": 1, "params": {"n": 2, "batch": 1, "dose_text": "1片",
                                                        "medicine_name": "阿司匹林", "from": "invite"}},
        {"name": "checkin_success", "ts": 2, "params": {"n": 1}},
        {"name": "ad_show", "ts": 3, "params": {"unit": "interstitial", "mode": "elder"}},
        {"name": "BAD NAME", "ts": 4, "params": {"n": 1}},
    ]}, token))
    metrics = sql(f"select metric_key||'|'||metric_value from stats_daily where stat_date='{today}' order by metric_key")
    print("     stats_daily:")
    for line in metrics.splitlines():
        print("       " + line)
    expect("有 evt_checkin_success", "evt_checkin_success" in metrics, True)
    expect("非法事件名未落库", "BAD" in metrics, False)
    expect("中文药名未落库", "阿司匹林" in metrics, False)
    expect("中文剂量未落库", "1片" in metrics, False)

    section("dose_views 已读回执")
    # guardian 调 today 会写一条 view
    call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token)
    expect("guardian 已写 dose_views", sql(f"select count(*) from dose_views where family_id='{fid}'"), "1")

    # 造一条"昨天、别人、非 elder"的回执，再把本人绑成患者 -> mode=elder -> recentView 有值
    y_start = sql(f"select (extract(epoch from (date '{today}' - interval '1 day') at time zone 'Asia/Shanghai'))::bigint * 1000")
    sql(f"delete from dose_views where family_id='{fid}'")
    sql("insert into dose_views (view_id, family_id, viewer_openid, viewer_role, viewer_alias, viewed_at) "
        f"values ('{fid}_y', '{fid}', 'other_openid', 'member', 'Daughter', {int(y_start) + 3600000})")
    call("POST", "/api/patient/create", {"fid": fid, "name": "Smoke Dad", "alias": "Father"}, token)
    self_uid = sql(f"select patient_id from patients where family_id='{fid}' and status='active' limit 1")
    sql(f"update patients set self_openid='{openid}' where patient_id='{self_uid}'")

    elder_today = ok("schedule.today（elder）", call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token))
    rv = elder_today.get("recentView")
    print(f"     recentView = {rv}")
    expect("recentView.alias", (rv or {}).get("alias"), "Daughter")
    expect("elder 不写回执", sql(f"select count(*) from dose_views where family_id='{fid}' and viewer_role='elder'"), "0")

    section("family.reset_invite / update_role")
    old_ivt = ok("family.info", call("GET", f"/api/family/info?fid={fid}", None, token))["invite_token"]
    new_ivt = ok("family.reset_invite", call("POST", "/api/family/reset_invite", {"fid": fid}, token))["ivt"]
    expect("邀请码已更换", new_ivt != old_ivt, True)

    sql("insert into family_members (family_id, openid, role, is_patient, notify_enabled, joined_at) "
        f"values ('{fid}', 'mock_member', 'member', false, true, 0) on conflict do nothing")
    ok("family.update_role -> admin", call("POST", "/api/family/update_role",
                                           {"fid": fid, "openid": "mock_member", "role": "admin"}, token))
    expect("mock_member 角色", sql(f"select role from family_members where family_id='{fid}' and openid='mock_member'"), "admin")

    code = call("POST", "/api/family/update_role", {"fid": fid, "openid": "mock_member", "role": "owner"}, token)[0]
    expect("非法角色返回 1005", code, 1005)

    # 只剩自己时不能退出
    sql(f"delete from family_members where family_id='{fid}' and openid='mock_member'")
    code = call("POST", "/api/family/leave", {"fid": fid}, token)[0]
    expect("只剩自己时 leave 被拒 1005", code, 1005)

    sql("insert into family_members (family_id, openid, role, is_patient, notify_enabled, joined_at) "
        f"values ('{fid}', 'mock_member', 'member', false, true, 0) on conflict do nothing")
    ok("family.leave", call("POST", "/api/family/leave", {"fid": fid}, token))
    expect("成员关系已删", sql(f"select count(*) from family_members where family_id='{fid}' and openid='{openid}'"), "0")
    expect("管理权已移交", sql(f"select role from family_members where family_id='{fid}' and openid='mock_member'"), "admin")
    expect("自绑患者已解绑", sql(f"select coalesce(self_openid,'NULL') from patients where patient_id='{self_uid}'"), "NULL")

    section("user.export")
    exp = ok("user.export", call("POST", "/api/user/export", {}, token))
    print(f"     fileUrl = {exp['fileUrl'][:90]}...")
    expect("expireAt 约为 24h 后", abs(exp["expireAt"] - (datetime.now(CN).timestamp() * 1000 + 86400000)) < 120000, True)

    with OP.open(exp["fileUrl"], timeout=15) as r:
        body = json.loads(r.read().decode())
    expect("导出含 account", "account" in body, True)
    expect("导出含 dose_records", "dose_records" in body, True)
    expect("导出给我的操作计数", "checkins_i_performed" in body.get("operation_summary", {}), True)

    bad = call("GET", "/api/user/export/download?token=forged.token.value", None)
    expect("伪造 token 被拒（401）", bad[0], 401)

    section("user.erase")
    code = call("POST", "/api/user/erase", {"confirm": False}, token)[0]
    expect("未确认时 1005", code, 1005)
    ok("user.erase(confirm:true)", call("POST", "/api/user/erase", {"confirm": True}, token))
    expect("身份已清空", sql(f"select coalesce(nick_name,'NULL') from users where openid='{openid}'"), "NULL")
    expect("已退出所有家庭", sql(f"select count(*) from family_members where openid='{openid}'"), "0")
    expect("订阅额度清零", sql(f"select coalesce(sum(quota),0) from subscriptions where openid='{openid}'"), "0")

    relogin = ok("重新登录", call("POST", "/api/user/login", {"code": "smoke2"}))
    expect("注销后家庭列表为空", len(relogin["families"]), 0)

    print("\n" + "=" * 60)
    if FAILED:
        print(f"失败 {len(FAILED)} 项：")
        for f in FAILED:
            print("  - " + f)
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
