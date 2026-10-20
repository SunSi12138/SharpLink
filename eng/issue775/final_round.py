#!/usr/bin/env python3
"""Final #775 build-only diagnosis and append-only evidence archive. No performance runs."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile

REPO = "SunSi12138/SharpLink"
BASE = "6cfb09e7dd6b68695f92d28f726340bc8eb39e1d"
EXPERIMENT = "5487a21f020e16c2b036b439b8d7efd896267422"
CANDIDATE = "8fc84d486c242d67fe05d4cf9962661d8351c779"
TREE = "1bd11d1d56ef951428440ee5cede21af94d1c80e"
SDK = "10.0.113"
PROJECT = "test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
DLLDIR = "test/SharpLink.Benchmarks/bin/Release/net10.0"
SNAPSHOT_HEAD = "377ee2ac1d7d4c80f57ffda1bdd39437e9950879"


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def command(argv, root, directory, env, timeout=360):
    directory.mkdir(parents=True, exist_ok=True)
    record = {"argv": list(map(str, argv)), "cwd": str(root), "exitCode": None}
    save(directory / "command.json", record)
    try:
        with (directory / "output.log").open("w") as log:
            run = subprocess.run(argv, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        record["exitCode"] = run.returncode
    except Exception as error:
        record["error"] = str(error)
    save(directory / "command.json", record)
    return record["exitCode"] == 0


def compare_metadata(left, right):
    a = {x["file"]: x for x in left}
    b = {x["file"]: x for x in right}
    if a.keys() != b.keys():
        raise ValueError("Assembly set changed")
    result = {}
    for name in a:
        x, y = a[name], b[name]
        def methods(v, key):
            pairs = [(m["name"], m[key]) for m in v["methods"]]
            if len(dict(pairs)) != len(pairs):
                raise ValueError("Duplicate logical method name")
            return dict(pairs)
        mx, my = methods(x, "normalizedBodySha256"), methods(y, "normalizedBodySha256")
        changed = [k for k in sorted(mx.keys() | my.keys()) if mx.get(k) != my.get(k)]
        rawx, rawy = methods(x, "rawIlSha256"), methods(y, "rawIlSha256")
        result[name] = {"binaryEqual": x["sha256"] == y["sha256"], "mvidEqual": x["mvid"] == y["mvid"],
            "rawIlByMethodEqual": rawx == rawy, "normalizedBodiesEqual": not changed,
            "normalizedBodyChanges": changed, "typeLayoutsEqual": x["types"] == y["types"],
            "attributeRowsEqual": x["attributes"] == y["attributes"], "debugEqual": x["debug"] == y["debug"],
            "unresolvedTokens": [x["unresolvedTokens"], y["unresolvedTokens"]],
            "scope": "Not a claim of whole-program behavioral equivalence or causal performance attribution."}
    return result


def build(args):
    driver, out = args.driver.resolve(), args.output.resolve()
    if out.exists() and any(out.iterdir()):
        raise ValueError("Refuse overwriting evidence")
    out.mkdir(parents=True)
    if os.environ.get("GITHUB_RUN_ATTEMPT") != "1":
        raise ValueError("No retry selection in this final diagnostic")
    work = Path(os.environ["RUNNER_TEMP"]) / ("issue775-final-build-" + str(args.host))
    work.mkdir(exist_ok=False)
    env = os.environ.copy()
    for key in list(env):
        if any(s in key.upper() for s in ("TOKEN", "SECRET", "PASSWORD")):
            del env[key]
    env |= {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
            "DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    if subprocess.check_output(["dotnet", "--version"], cwd=driver, text=True).strip() != SDK:
        raise ValueError("Pinned SDK unavailable; no fallback")
    roots = {name: work / name for name in ("old", "candidate", "experiment")}
    for name, root in roots.items():
        subprocess.run(["git", "worktree", "add", "--detach", str(root), EXPERIMENT if name == "experiment" else BASE], cwd=driver, check=True)
    sys.path.insert(0, str(roots["experiment"] / "eng/issue775"))
    bridge = importlib.import_module("bridge")
    identity = bridge.apply_candidate(roots["candidate"], roots["experiment"], out)
    if identity["localCandidateSha"] != CANDIDATE or identity["tree"] != TREE:
        raise ValueError("Fixed candidate identity mismatch")
    helper = work / "metadata-reader"
    helper.mkdir()
    (helper / "MetadataReader.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>\n')
    shutil.copyfile(driver / "eng/issue775/FinalMetadata.cs", helper / "Program.cs")
    if not command(["dotnet", "build", "-c", "Release", "-v", "minimal"], helper, out / "reader-build", env):
        raise RuntimeError("Metadata reader build failed; raw logs retained")
    manifest = {"driverSha": git(driver, "rev-parse", "HEAD"), "snapshotHead": SNAPSHOT_HEAD,
        "host": args.host, "sdk": SDK, "candidate": identity, "runId": os.environ.get("GITHUB_RUN_ID"),
        "bootId": Path("/proc/sys/kernel/random/boot_id").read_text().strip(),
        "runner": os.environ.get("RUNNER_NAME"), "image": os.environ.get("ImageVersion"),
        "plan": "Two hosts; each arm two clean same-path ordinary builds, then one clean generated-source export build. No timing, no source optimization, no deterministic-flag intervention.",
        "environment": {k: env[k] for k in ("SharpLinkStaticBindingPrototype", "DOTNET_PROCESSOR_COUNT", "DOTNET_TieredCompilation", "DOTNET_TieredPGO")},
        "runs": [], "stage2Authorized": False}
    command(["dotnet", "--info"], driver, out / "dotnet", env)
    command(["lscpu"], driver, out / "cpu", env)
    failures = []
    for arm in ("old", "candidate"):
        root = roots[arm]
        sha = BASE if arm == "old" else CANDIDATE
        aenv = env | {"SHARPLINK_BENCHMARK_SHA": sha}
        arm_records = []
        for repetition in (1, 2, 3):
            directory = out / arm / str(repetition)
            directory.mkdir(parents=True)
            if git(root, "rev-parse", "HEAD") != sha:
                raise ValueError("Source changed")
            command(["dotnet", "build-server", "shutdown"], root, directory / "shutdown", aenv, 90)
            subprocess.run(["git", "clean", "-ffdx"], cwd=root, check=True, stdout=subprocess.DEVNULL)
            args_build = ["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal",
                          "-bl:" + str(directory / "build.binlog") + ";ProjectImports=None"]
            if repetition == 3:
                args_build.append("-p:EmitCompilerGeneratedFiles=true")
            record = {"arm": arm, "repetition": repetition, "sha": sha,
                      "sourceTree": git(root, "rev-parse", "HEAD^{tree}"), "cwd": str(root),
                      "generatedSourceExport": repetition == 3, "assemblies": {}}
            success = command(args_build, root, directory / "build", aenv)
            record["buildPassed"] = success
            if success:
                binary = directory / "binaries"
                binary.mkdir()
                for path in sorted((root / DLLDIR).iterdir()):
                    if path.is_file() and path.suffix in (".dll", ".pdb", ".json"):
                        shutil.copyfile(path, binary / path.name)
                        record["assemblies"][path.name] = digest(path)
                generated = {}
                for top in (root / "src", root / "test/SharpLink.Benchmarks"):
                    for path in top.rglob("*"):
                        if path.is_file() and "obj" in path.relative_to(root).parts and (path.suffix in (".cs", ".props", ".targets", ".editorconfig") or path.name == "project.assets.json"):
                            rel = str(path.relative_to(root))
                            dest = directory / "build-inputs" / rel
                            dest.parent.mkdir(parents=True, exist_ok=True)
                            shutil.copyfile(path, dest)
                            generated[rel] = digest(path)
                save(directory / "generated-input-hashes.json", generated)
                for project in ("SharpLink.Client", "SharpLink.Server"):
                    props = ["dotnet", "msbuild", "src/" + project + "/" + project + ".csproj", "-p:Configuration=Release",
                        "-getProperty:Deterministic,ContinuousIntegrationBuild,PathMap,SourceRevisionId,DefineConstants,TargetFramework,LangVersion,DebugType,Optimize,DeterministicSourcePaths,AssemblyName,InformationalVersion",
                        "-getItem:Compile,Analyzer"]
                    if repetition == 3:
                        props.append("-p:EmitCompilerGeneratedFiles=true")
                    if not command(props, root, directory / (project + "-evaluated-inputs"), aenv):
                        failures.append(f"{arm}/{repetition}: evaluated inputs")
                meta = directory / "metadata.json"
                if command(["dotnet", str(helper / "bin/Release/net10.0/MetadataReader.dll"), str(binary), str(meta)], root, directory / "inspect", aenv):
                    arm_records.append(json.loads(meta.read_text()))
                else:
                    failures.append(f"{arm}/{repetition}: metadata inspection")
                    arm_records.append(None)
                if repetition == 1 and not command(["dotnet", DLLDIR + "/SharpLink.Benchmarks.dll", "--static-binding-smoke"], root, directory / "smoke", aenv, 120):
                    failures.append(arm + ": smoke")
            else:
                failures.append(f"{arm}/{repetition}: build")
                arm_records.append(None)
            manifest["runs"].append(record)
            save(out / "manifest.json", manifest)
        for index in (1, 2):
            if arm_records[0] is not None and arm_records[index] is not None:
                save(out / arm / f"compare-1-{index+1}.json", compare_metadata(arm_records[0], arm_records[index]))
    save(out / "execution-failures.json", failures)
    save(out / "completed.json", {"host": args.host, "executionPassed": not failures, "buildCount": 6,
        "newPerformanceSamples": 0, "stage2Authorized": False,
        "limits": "New diagnostic bytes cannot retroactively identify older hash-only bytes. Export build differs by EmitCompilerGeneratedFiles and is not silently pooled with normal builds."})
    if failures:
        raise RuntimeError(str(failures))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def archive(args):
    # This path executes only in the authorized Actions archive job. No source writes,
    # deletes, issue closure, PR merges or performance executions are performed.
    driver, out = args.driver.resolve(), args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    token = os.environ["GH_TOKEN"]
    api_root = "https://api.github.com/repos/" + REPO
    headers = {"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json", "User-Agent": "issue775-final-archive", "X-GitHub-Api-Version": "2022-11-28"}
    def api(path, data=None, method=None):
        url = path if path.startswith("https://") else api_root + path
        if not url.startswith(api_root):
            raise ValueError("Unexpected API destination")
        request = urllib.request.Request(url, headers=headers, method=method or ("POST" if data is not None else "GET"),
            data=None if data is None else json.dumps(data).encode())
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.load(response)
    def pages(path, key=None):
        result = []
        for page in range(1, 101):
            value = api(path + ("&" if "?" in path else "?") + f"per_page=100&page={page}")
            items = value if key is None else value[key]
            result.extend(items)
            if len(items) < 100:
                return result
        raise ValueError("Pagination cap reached; refuse incomplete inventory")
    history = out / "index"
    history.mkdir()
    issue = api("/issues/775")
    pr = api("/pulls/862")
    comments = pages("/issues/862/comments")
    issue_comments = pages("/issues/775/comments")
    save(history / "issue775-before.json", issue)
    save(history / "pr862-before.json", pr)
    save(history / "pr862-all-comments.json", comments)
    save(history / "issue775-all-comments-before.json", issue_comments)
    for title, values in (("pr862-all-comments", comments), ("issue775-all-comments-before", issue_comments)):
        (history / (title + ".md")).write_text("\n\n---\n\n".join("## " + c["html_url"] + "\n\n" + c["body"] for c in values))
    runs = pages("/actions/runs?branch=prototype%2F775-static-session-binding", "workflow_runs")
    runs = [r for r in runs if "775" in r.get("path", "") or "775" in r.get("name", "")]
    save(history / "all-study-workflow-runs.json", runs)
    # Also retain artifacts from these workflows if a historical name used no prefix.
    run_ids = {r["id"] for r in runs}
    artifacts = pages("/actions/artifacts", "artifacts")
    selected = [a for a in artifacts if (a.get("workflow_run") or {}).get("id") in run_ids
                or a["name"].lower().startswith(("issue775", "issue-775"))]
    selected.sort(key=lambda a: a["id"])
    save(history / "artifact-inventory-before.json", selected)
    run_id = os.environ["GITHUB_RUN_ID"]
    tag = "issue-775-research-20261021-" + run_id
    release = api("/releases", {"tag_name": tag, "target_commitish": os.environ["GITHUB_SHA"],
        "name": "Issue #775 research evidence archive — NOT a product release",
        "body": "Research data only. No production package, performance acceptance, merge, or phase-2 authorization. Final index is being populated; missing artifacts will be recorded explicitly.",
        "draft": True, "prerelease": True, "make_latest": "false"})
    manifest = {"schema": 1, "repository": REPO, "issue": 775, "driverSha": os.environ["GITHUB_SHA"],
        "priorHead": SNAPSHOT_HEAD, "archiveRun": run_id, "releaseId": release["id"], "tag": tag,
        "coverage": "All API-visible artifacts from the issue-775 workflow branch or issue775 name prefixes, including failed runs. Deleted artifacts cannot be reconstructed by listing; historical references are retained.",
        "performanceAccepted": False, "stage2Authorized": False, "artifacts": [], "failures": []}
    def upload(path, name=None):
        name = name or path.name
        url = "https://uploads.github.com/repos/" + REPO + f"/releases/{release['id']}/assets?name=" + urllib.parse.quote(name)
        request = urllib.request.Request(url, headers=headers | {"Content-Type": "application/octet-stream"}, data=path.read_bytes(), method="POST")
        with urllib.request.urlopen(request, timeout=240) as response:
            asset = json.load(response)
        if asset["size"] != path.stat().st_size:
            raise ValueError("Uploaded asset size mismatch")
        if asset.get("digest") and asset["digest"] != "sha256:" + digest(path):
            raise ValueError("Uploaded asset digest mismatch")
        return {"id": asset["id"], "name": asset["name"], "url": asset["browser_download_url"], "size": asset["size"], "sha256": digest(path)}
    scratch = out / "download"
    scratch.mkdir()
    for a in selected:
        row = {k: a.get(k) for k in ("id", "name", "size_in_bytes", "digest", "expired", "created_at", "expires_at", "workflow_run")}
        if a["expired"]:
            row["status"] = "expired-not-recovered"
            manifest["failures"].append(f"{a['id']}: expired")
        else:
            path = scratch / (str(a["id"]) + ".zip")
            try:
                req = urllib.request.Request(a["archive_download_url"], headers=headers)
                try:
                    response = urllib.request.build_opener(NoRedirect()).open(req, timeout=120)
                except urllib.error.HTTPError as redirect:
                    if redirect.code not in (301, 302, 303, 307, 308):
                        raise
                    location = redirect.headers["Location"]
                    if not location.startswith("https://"):
                        raise ValueError("Insecure artifact redirect")
                    # Never forward GitHub credentials to the signed storage URL.
                    response = urllib.request.urlopen(location, timeout=180)
                with response, path.open("wb") as target:
                    shutil.copyfileobj(response, target)
                row["sha256"] = digest(path)
                if a.get("digest") and a["digest"] != "sha256:" + row["sha256"]:
                    raise ValueError("Downloaded artifact digest mismatch")
                with zipfile.ZipFile(path) as z:
                    summaries = []
                    for item in z.infolist():
                        parts = Path(item.filename).parts
                        if item.is_dir() or ".." in parts or Path(item.filename).is_absolute():
                            continue
                        name = Path(item.filename).name.lower()
                        keep = name in ("summary.json", "summary.md", "manifest.json", "provenance.json", "candidate-identity.json", "completed.json", "execution-failures.json", "confirmation-plan.json", "confirmation-failures.json", "diagnostic-notes.json", "smoke.log") or name.startswith("compare-1-")
                        if keep and item.file_size < 5000000:
                            dest = history / "summaries" / str(a["id"]) / item.filename
                            dest.parent.mkdir(parents=True, exist_ok=True)
                            dest.write_bytes(z.read(item))
                            summaries.append(str(dest.relative_to(history)))
                    row["summaryFiles"] = summaries
                safe = re.sub(r"[^a-zA-Z0-9._-]", "_", a["name"])[:130]
                row["asset"] = upload(path, str(a["id"]) + "--" + safe + ".zip")
                row["status"] = "archived-and-hash-checked"
            except Exception as error:
                row["status"] = "archive-failed"
                row["error"] = str(error)
                manifest["failures"].append(f"{a['id']}: {error}")
            finally:
                path.unlink(missing_ok=True)
        manifest["artifacts"].append(row)
        save(out / "archive-progress.json", manifest)
        print(row["id"], row["status"], flush=True)
    shutil.copytree(driver / "eng/issue775", history / "current-reproduction-scripts")
    with (history / "final-driver-source.tar.gz").open("wb") as stream:
        p = subprocess.Popen(["git", "archive", "--format=tar.gz", "HEAD"], cwd=driver, stdout=stream)
        if p.wait() != 0:
            manifest["failures"].append("Driver source archive failed")
    visible = {a["id"] for a in selected}
    mentioned = set()
    for c in comments + issue_comments:
        for m in re.finditer(r"artifacts/(\d+)|artifact\s*[`* ]*(\d{9,})", c["body"], re.I):
            mentioned.add(int(m.group(1) or m.group(2)))
        mentioned.update(int(v) for v in re.findall(r"\b12\d{9}\b", c["body"]))
    manifest["historicallyMentionedArtifactIdsNotInVisibleInventory"] = sorted(mentioned - visible)
    save(history / "archive-manifest.json", manifest)
    lines = ["# #775 全研究原始产物目录", "", "这是研究数据归档，不是产品发布或性能验收。失败、负结果和旧评论均保留。", "",
             "| 原artifact ID | 原名称 | 保存状态 | 不随Actions到期的副本 | ZIP SHA256 |", "|---|---|---|---|---|"]
    for row in manifest["artifacts"]:
        asset = row.get("asset")
        lines.append(f"| {row['id']} | {row['name']} | {row['status']} | " + (f"[ZIP]({asset['url']})" if asset else "未恢复；见manifest错误") + f" | `{row.get('sha256', 'unknown')}` |")
    lines += ["", "历史被提及但本次API清单中不存在的ID：" + repr(manifest["historicallyMentionedArtifactIdsNotInVisibleInventory"]),
              "这些缺口不能写成已归档。完整原始评论快照保留其上下文；目录重复产物不应重复计入独立样本。"]
    (history / "INDEX.md").write_text("\n".join(lines) + "\n")
    bundle = out / "issue775-all-research-index.zip"
    with zipfile.ZipFile(bundle, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(history.rglob("*")):
            if path.is_file():
                z.write(path, path.relative_to(history))
    index_asset = upload(bundle)
    manifest_asset = upload(history / "archive-manifest.json")
    index_md_asset = upload(history / "INDEX.md")
    count = sum(r["status"] == "archived-and-hash-checked" for r in manifest["artifacts"])
    text = (f"# 最终轮：历次全部研究数据归档索引（2026-10-21 JST）\n\n"
        f"本次枚举到 **{len(selected)} 个研究产物，已原样保存并校验 {count} 个**；失败/过期 {len(selected)-count} 个。"
        "不删除旧失败、不改写研究结论，不关闭issue或合并PR。\n\n"
        f"[完整目录（每个ZIP的ID、状态、SHA256和保存链接）]({index_md_asset['url']}) · "
        f"[机器可读完整清单]({manifest_asset['url']}) · [全部summary、原始评论快照、源码及复现脚本索引包]({index_asset['url']})\n\n"
        "原始ZIP逐个保留，包含逐次延迟、BDN实际测量、分配样本、命令/环境、成功及失败诊断、源码归档。"
        "索引包还保存PR #862和本issue全部此前评论原文，防止只有PR摘要或聊天附件。"
        "这是明确标注的研究数据预发布归档，不含产品包、不代表发布新版本；不修改任何生产源码。\n\n"
        f"历史评论中被提及而本次API不可见的artifact ID：`{sorted(mentioned-visible)}`。"
        "目录记录不可恢复项；不能将链接存在或哈希存在当作已保存原始字节。\n\n"
        "最后构建诊断产物也列在目录；其运行成功仅说明采集完成，不证明历史性能回退的原因。"
        "本轮不新增性能样本，不为第二阶段放行。")
    final_release = api(f"/releases/{release['id']}", {"draft": False, "prerelease": True, "make_latest": "false",
        "body": text + "\n\n总体研究台账：https://github.com/" + REPO + "/issues/775#issuecomment-6295616045"}, "PATCH")
    text += "\n\n[研究原始数据归档入口](" + final_release["html_url"] + ")"
    posted = api("/issues/775/comments", {"body": text})
    save(out / "published.json", {"issueComment": posted["html_url"], "release": final_release["html_url"],
        "archived": count, "selected": len(selected), "index": index_asset, "manifest": manifest_asset, "failures": manifest["failures"]})
    if manifest["failures"]:
        raise RuntimeError("Archive incomplete; issue index records all failures")


def self_test():
    import unittest
    class Tests(unittest.TestCase):
        def test_equal(self):
            a = {"file":"a.dll", "sha256":"a", "mvid":"a", "methods":[{"name":"m", "normalizedBodySha256":"x", "rawIlSha256":"y"}], "types":[], "attributes":[], "debug":[], "unresolvedTokens":0}
            self.assertTrue(compare_metadata([a],[a])["a.dll"]["binaryEqual"])
        def test_metadata_only_not_same_binary(self):
            a = {"file":"a.dll", "sha256":"a", "mvid":"a", "methods":[], "types":[], "attributes":[], "debug":[], "unresolvedTokens":0}
            result=compare_metadata([a],[a|{"sha256":"b"}])["a.dll"]
            self.assertFalse(result["binaryEqual"])
            self.assertTrue(result["normalizedBodiesEqual"])
        def test_changed_body(self):
            a = {"file":"a.dll", "sha256":"a", "mvid":"a", "methods":[{"name":"m","normalizedBodySha256":"x","rawIlSha256":"y"}], "types":[], "attributes":[], "debug":[], "unresolvedTokens":0}
            b=a|{"methods":[{"name":"m","normalizedBodySha256":"z","rawIlSha256":"q"}]}
            self.assertEqual(compare_metadata([a],[b])["a.dll"]["normalizedBodyChanges"],["m"])
        def test_assembly_set(self):
            with self.assertRaises(ValueError): compare_metadata([], [{"file":"x"}])
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(Tests))
    if not result.wasSuccessful(): raise SystemExit(1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("build", "archive", "self-test"))
    parser.add_argument("--driver", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path)
    parser.add_argument("--host", type=int, choices=(1,2))
    args = parser.parse_args()
    if args.mode == "self-test": self_test()
    elif args.mode == "build": build(args)
    else: archive(args)
