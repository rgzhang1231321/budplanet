#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
scripts/gh-milestones.py —— 按 scripts/milestones.json 同步 GitHub Milestones

为什么是脚本而不是手点：里程碑是汇总口径的锚点（见 docs/MILESTONES.md §四），
手点必然出现「标题写成 M6 真机验收」「漏建一个」这类漂移，脚本按标题幂等，
重复跑不会创建重复项，改完 JSON 再跑一次即可对齐。

用法：
    export GH_TOKEN=ghp_xxx          # classic: repo；fine-grained: Issues 读写
    python scripts/gh-milestones.py --dry-run   # 只看要建什么
    python scripts/gh-milestones.py             # 真正创建
"""
import json
import os
import sys
import urllib.request
import urllib.error

REPO = os.environ.get("REPO", "rgzhang1231321/budplanet")
API = "https://api.github.com"
HERE = os.path.dirname(os.path.abspath(__file__))


def request(method, path, token, payload=None):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(API + path, data=data, method=method)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", "budplanet-milestones")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req, timeout=20) as r:
        body = r.read().decode("utf-8")
    return json.loads(body) if body.strip() else {}


def list_milestones(token):
    titles, page = set(), 1
    while True:
        chunk = request("GET", f"/repos/{REPO}/milestones?state=all&per_page=100&page={page}", token)
        if not chunk:
            break
        for m in chunk:
            titles.add(m["title"])
        if len(chunk) < 100:
            break
        page += 1
    return titles


def main():
    dry = "--dry-run" in sys.argv[1:]
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not dry and not token:
        print("缺少 GH_TOKEN / GITHUB_TOKEN（classic 需 repo 权限；fine-grained 需 Issues 读写）")
        return 1

    with open(os.path.join(HERE, "milestones.json"), encoding="utf-8") as f:
        milestones = json.load(f)

    existing = set() if dry else list_milestones(token)
    created = skipped = 0

    for m in milestones:
        title = m["title"]
        if title in existing:
            print(f"  跳过（已存在） {title}")
            skipped += 1
            continue
        body = {k: m[k] for k in ("title", "state", "due_on", "description") if k in m}
        if dry:
            print(f"  [dry-run] 将创建 {title}  state={body['state']}  due={body['due_on']}")
            created += 1
            continue
        try:
            res = request("POST", f"/repos/{REPO}/milestones", token, body)
            print(f"  已创建 #{res.get('number')} {title}")
            created += 1
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:300]
            print(f"  创建失败 {title}: HTTP {e.code} {detail}")
            return 2

    print(f"\n仓库 {REPO}：新建 {created} 个，跳过 {skipped} 个")
    return 0


if __name__ == "__main__":
    sys.exit(main())
