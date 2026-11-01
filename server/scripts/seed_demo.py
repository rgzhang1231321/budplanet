"""
server/scripts/seed_demo.py —— 造一份可看的本地演示数据（走公开 API，不直接写库）
跑法：python server/scripts/seed_demo.py
前置：API 在 5080 跑着；库里没有同名家庭。
产出：我家 / 爸爸(2 种药) / 妈妈(1 种药)，今天 3 条剂量里 2 条已服、1 条待服（刚好卡在免费 3 个药品上限）。
"""
import json
import urllib.request
from datetime import datetime, timedelta, timezone

BASE = "http://127.0.0.1:5080"
CN = timezone(timedelta(hours=8))
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(method, path, body=None, token=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("content-type", "application/json")
    if token:
        req.add_header("authorization", "Bearer " + token)
    with OPENER.open(req, timeout=15) as r:
        env = json.loads(r.read().decode())
    if env.get("code") != 0:
        raise SystemExit(f"✗ {path} code={env['code']} msg={env['msg']}")
    return env["data"]


def main():
    today = datetime.now(CN).strftime("%Y-%m-%d")
    token = call("POST", "/api/user/login", {"code": "seed"})["token"]

    fid = call("POST", "/api/family/create", {"name": "我家"}, token)["fid"]
    dad = call("POST", "/api/patient/create", {"fid": fid, "name": "张建国", "alias": "爸爸"}, token)["uid"]
    mom = call("POST", "/api/patient/create", {"fid": fid, "name": "李秀兰", "alias": "妈妈"}, token)["uid"]

    def add_med(uid, name, spec, dose, times):
        mid = call("POST", "/api/medicine/create", {
            "fid": fid, "uid": uid, "name": name, "spec": spec, "default_dose": dose,
        }, token)["mid"]
        call("POST", "/api/plan/create", {
            "mid": mid,
            "slots": [{"t": t, "d": dose, "slot_key": "s" + str(int(t[:2]) * 60 + int(t[3:]))} for t in times],
            "cycle_type": "long_term",
            "startDate": today,
        }, token)
        return mid

    add_med(dad, "苯磺酸氨氯地平片", "5mg×28片", "1片", ["08:00"])
    add_med(dad, "盐酸二甲双胍片", "0.5g×20片", "1片", ["12:30"])
    add_med(mom, "碳酸钙D3片", "600mg×60片", "2片", ["08:00"])

    rows = call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token)["rows"]
    taken = [r for r in rows if r["due_time"] == "08:00"]
    call("POST", "/api/checkin/submit", {
        "items": [{"pid": r["pid"], "date": today, "slot": r["slot"], "status": "taken"} for r in taken],
    }, token)

    rows = call("GET", f"/api/schedule/today?fid={fid}&date={today}", None, token)["rows"]
    print(f"✓ 家庭 {fid}：{len(rows)} 条剂量")
    for r in rows:
        print(f"   {r['due_time']}  {r['medicine_name']} {r['dose_text']}  →  {r['status']}")


if __name__ == "__main__":
    main()
