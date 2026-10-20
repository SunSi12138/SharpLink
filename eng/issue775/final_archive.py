#!/usr/bin/env python3
"""Finish the requested archive via per-run API enumeration. No build/performance execution."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from final_round import digest, save, NoRedirect

REPO = "SunSi12138/SharpLink"
API = "https://api.github.com/repos/" + REPO
FIRST_FINAL_RUN = 39457744667
LEDGER = "https://github.com/" + REPO + "/issues/775#issuecomment-6295616045"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    index = out / "index"
    index.mkdir()
    headers = {"Authorization": "Bearer " + os.environ["GH_TOKEN"], "Accept": "application/vnd.github+json",
               "User-Agent": "issue775-final-evidence-only", "X-GitHub-Api-Version": "2022-11-28"}
    retries, failures = [], []
    def api(path, data=None, method=None):
        url = API + path
        verb = method or ("POST" if data is not None else "GET")
        for attempt in range(3):
            request = urllib.request.Request(url, headers=headers, method=verb,
                data=None if data is None else json.dumps(data).encode())
            try:
                with urllib.request.urlopen(request, timeout=120) as response:
                    return json.load(response)
            except urllib.error.HTTPError as error:
                if verb != "GET" or error.code not in (429, 500, 502, 503, 504) or attempt == 2:
                    raise
                retries.append({"path": path, "status": error.code, "attempt": attempt+1})
                save(index / "read-retries.json", retries)
                time.sleep(2 ** attempt)
        raise RuntimeError("Unreachable retry exit")
    def pages(path, key=None):
        result = []
        for page in range(1, 101):
            value = api(path + ("&" if "?" in path else "?") + f"per_page=100&page={page}")
            items = value if key is None else value[key]
            result.extend(items)
            if len(items) < 100:
                return result
        raise RuntimeError("Pagination limit; refusing silent truncation")
    all_comments = []
    for issue_number in (775, 862):
        issue = api(f"/issues/{issue_number}")
        comments = pages(f"/issues/{issue_number}/comments")
        save(index / f"issue-{issue_number}.json", issue)
        save(index / f"issue-{issue_number}-all-comments.json", comments)
        (index / f"issue-{issue_number}-all-comments.md").write_text(
            "\n\n---\n\n".join("## " + c["html_url"] + "\n\n" + c["body"] for c in comments))
        all_comments.extend(comments)
    save(index / "pr-862.json", api("/pulls/862"))
    runs = pages("/actions/runs?branch=prototype%2F775-static-session-binding", "workflow_runs")
    runs = {r["id"]: r for r in runs if "775" in r.get("path", "") or "775" in r.get("name", "")}
    mentioned_runs, mentioned_artifacts = {FIRST_FINAL_RUN}, set()
    for c in all_comments:
        mentioned_runs.update(map(int, re.findall(r"/actions/runs/(\d+)", c["body"])))
        mentioned_artifacts.update(map(int, re.findall(r"\b12\d{9}\b", c["body"])))
    for run_id in sorted(mentioned_runs - runs.keys()):
        try:
            runs[run_id] = api(f"/actions/runs/{run_id}")
        except Exception as error:
            failures.append({"stage":"run-metadata", "id":run_id, "error":str(error)})
    save(index / "all-study-runs.json", list(runs.values()))
    artifacts = {}
    for run_id in sorted(runs):
        try:
            for artifact in pages(f"/actions/runs/{run_id}/artifacts", "artifacts"):
                artifact["source_run_id"] = run_id
                artifact.setdefault("workflow_run", {"id":run_id, "head_sha":runs[run_id]["head_sha"]})
                artifacts[artifact["id"]] = artifact
        except Exception as error:
            failures.append({"stage":"run-artifact-list", "id":run_id, "error":str(error)})
        print("inventory", run_id, len(artifacts), flush=True)
    missing = []
    for artifact_id in sorted(mentioned_artifacts - artifacts.keys()):
        try:
            a = api(f"/actions/artifacts/{artifact_id}")
            artifacts[a["id"]] = a
        except Exception as error:
            missing.append({"id":artifact_id, "error":str(error)})
    save(index / "artifact-inventory.json", list(artifacts.values()))
    save(index / "historical-artifact-gaps.json", missing)
    archive_run = os.environ["GITHUB_RUN_ID"]
    release = api("/releases", {"tag_name":"issue-775-research-20261021-" + archive_run,
        "target_commitish":os.environ["GITHUB_SHA"], "name":"Issue #775 research evidence archive — NOT a product release",
        "body":"Research data only; archive in progress. No product package or performance approval.",
        "draft":True, "prerelease":True, "make_latest":"false"})
    state = {"schema":1, "repository":REPO, "driverSha":os.environ["GITHUB_SHA"],
        "archiveRun":archive_run, "releaseId":release["id"], "performanceAccepted":False, "stage2Authorized":False,
        "coverage":"All per-run artifacts from the research branch plus artifact/run IDs referenced in issue775 and PR862 comments; no repository-wide artifact list.",
        "originalArchiveFailure":{"run":FIRST_FINAL_RUN, "reason":"Repository-wide artifact-list GET returned HTTP500; no release had been created. Only archive recovery is executed."},
        "historicalGaps":missing, "readRetries":retries, "failures":failures, "artifacts":[]}
    save(out / "progress.json", state)
    def upload(path, name=None):
        url = f"https://uploads.github.com/repos/{REPO}/releases/{release['id']}/assets?name=" + urllib.parse.quote(name or path.name)
        request = urllib.request.Request(url, headers=headers | {"Content-Type":"application/octet-stream"}, method="POST", data=path.read_bytes())
        with urllib.request.urlopen(request, timeout=300) as response:
            asset = json.load(response)
        sha = digest(path)
        if asset["size"] != path.stat().st_size or (asset.get("digest") and asset["digest"] != "sha256:"+sha):
            raise ValueError("Release asset size/digest mismatch")
        return {"id":asset["id"], "name":asset["name"], "url":asset["browser_download_url"], "size":asset["size"], "sha256":sha}
    for artifact_id, a in sorted(artifacts.items()):
        row = {k:a.get(k) for k in ("id","name","size_in_bytes","digest","expired","created_at","expires_at","workflow_run","source_run_id")}
        path = out / f"{artifact_id}.zip"
        try:
            if a["expired"]:
                raise RuntimeError("Expired artifact; original bytes not recovered")
            request = urllib.request.Request(a["archive_download_url"], headers=headers)
            try:
                response = urllib.request.build_opener(NoRedirect()).open(request, timeout=120)
            except urllib.error.HTTPError as redirect:
                if redirect.code not in (301,302,303,307,308):
                    raise
                location = redirect.headers["Location"]
                if not location.startswith("https://"):
                    raise ValueError("Insecure redirect")
                # No GitHub credential is forwarded to signed storage.
                response = urllib.request.urlopen(location, timeout=240)
            with response, path.open("wb") as stream:
                shutil.copyfileobj(response, stream)
            row["sha256"] = digest(path)
            if a.get("digest") and a["digest"] != "sha256:" + row["sha256"]:
                raise ValueError("Original artifact digest mismatch")
            summaries = []
            with zipfile.ZipFile(path) as z:
                for item in z.infolist():
                    p = Path(item.filename)
                    if item.is_dir() or p.is_absolute() or ".." in p.parts:
                        continue
                    keep = p.name.lower() in ("summary.json","summary.md","manifest.json","provenance.json","candidate-identity.json","completed.json","execution-failures.json","confirmation-plan.json","confirmation-failures.json","diagnostic-notes.json","smoke.log","published.json") or p.name.startswith("compare-1-")
                    if keep and item.file_size < 5000000:
                        dest = index / "summaries" / str(artifact_id) / p
                        dest.parent.mkdir(parents=True, exist_ok=True)
                        dest.write_bytes(z.read(item))
                        summaries.append(str(dest.relative_to(index)))
            row["summaryFiles"] = summaries
            safe = re.sub(r"[^A-Za-z0-9._-]", "_", a["name"])[:130]
            row["asset"] = upload(path, str(artifact_id) + "--" + safe + ".zip")
            row["status"] = "archived-and-hash-checked"
        except Exception as error:
            row["status"] = "not-recovered"
            row["error"] = str(error)
            failures.append({"stage":"artifact-archive", "id":artifact_id, "error":str(error)})
        finally:
            path.unlink(missing_ok=True)
        state["artifacts"].append(row)
        save(out / "progress.json", state)
        print("archive", artifact_id, row["status"], flush=True)
    shutil.copytree(Path("eng/issue775"), index / "reproduction-scripts", ignore=shutil.ignore_patterns("__pycache__"))
    with (index / "final-source.tar.gz").open("wb") as stream:
        subprocess.run(["git","archive","--format=tar.gz","HEAD"], stdout=stream, check=True)
    save(index / "archive-manifest.json", state)
    lines = ["# #775 全研究原始数据目录", "", "研究数据归档，不是产品版本、合并或性能验收。每个原始ZIP包含完整样本，不只是下列摘要。", "", "[全部阶段及正负结果台账]("+LEDGER+")", "", "| 原artifact ID | 原名称 | 状态 | 原始ZIP副本 | SHA256 |", "|---|---|---|---|---|"]
    for row in state["artifacts"]:
        asset = row.get("asset")
        link = "[ZIP]("+asset["url"]+")" if asset else "未恢复，见manifest"
        lines.append(f"|{row['id']}|{row['name']}|{row['status']}|{link}|`{row.get('sha256','unknown')}`|")
    lines += ["", "历史引用不可恢复项："+json.dumps(missing, ensure_ascii=False), "", "不将重复artifact当独立样本。完整评论原文、每轮summary、身份和失败清单另见索引包。"]
    (index / "INDEX.md").write_text("\n".join(lines)+"\n")
    bundle = out / "issue775-all-research-index.zip"
    with zipfile.ZipFile(bundle,"w",zipfile.ZIP_DEFLATED) as z:
        for p in sorted(index.rglob("*")):
            if p.is_file(): z.write(p,p.relative_to(index))
    assets = {"index":upload(bundle), "manifest":upload(index/"archive-manifest.json"), "catalogue":upload(index/"INDEX.md")}
    count = sum(r["status"]=="archived-and-hash-checked" for r in state["artifacts"])
    size = sum(r.get("asset",{}).get("size",0) for r in state["artifacts"])
    text = ("# 最终轮全部研究数据归档完成情况（2026-10-21 JST）\n\n"
        f"枚举到 **{len(artifacts)} 个原始研究产物，已保存并逐个校验 {count} 个，共 {size:,} 字节**；未恢复 {len(artifacts)-count} 个。"
        f"历史引用但不可获取项 {len(missing)} 个，其他错误 {len(failures)} 项，均在清单中明列。\n\n"
        f"[全部阶段数值台账]({LEDGER}) · [每个原始ZIP的完整目录]({assets['catalogue']['url']}) · "
        f"[机器可读SHA256/源码/状态清单]({assets['manifest']['url']}) · [全部summary、评论原文、源代码和复现脚本索引包]({assets['index']['url']})\n\n"
        "逐次RPC延迟、原BDN测量、分配样本、源码/补丁、反汇编、跟踪、命令及失败输出均在各原始ZIP，未只保存有利数据或聊天摘要。"
        "此前issue/PR全部评论另有完整快照。标记为研究预发布且不设为latest，不是产品版本，不含产品发布或性能通过决定。\n\n"
        "最后构建诊断保留两台VM的实际DLL/PDB、生成源码、评估输入及binlog；同路径重复的三个DLL差异仅72字节标识/调试信息，方法IL未变，详见归档内FINAL-FINDINGS.md。"
        "这不能反向确认全部历史DLL，也未解释性能波动；本轮没有新性能样本。\n\n"
        "首归档运行39457744667因仓库级列表HTTP500失败已保留；本次仅改为逐运行归档，没有重跑成功诊断或性能。"
        "**研究至此收尾；仍未证明恢复原始基线，不集成未验收候选、不合并#862、不关闭#775、不放行Replace/Receipt/Streaming。**")
    release = api(f"/releases/{release['id']}", {"draft":False,"prerelease":True,"make_latest":"false","body":text}, "PATCH")
    text += "\n\n[研究原始产物归档入口]("+release["html_url"]+")"
    posted = api("/issues/775/comments", {"body":text})
    save(out/"published.json", {"issueComment":posted["html_url"],"release":release["html_url"],"artifacts":count,
        "total":len(artifacts),"totalBytes":size,"historicalGaps":missing,"failures":failures,"assets":assets})
    if failures or missing:
        raise RuntimeError("Archive gaps recorded explicitly; no silent success")


if __name__ == "__main__":
    main()
