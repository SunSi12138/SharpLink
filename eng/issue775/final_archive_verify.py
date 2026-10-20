#!/usr/bin/env python3
"""Verify published archive and canonicalize draft-era asset URLs; no scientific reruns."""
import hashlib
import io
import json
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from final_round import digest, save, NoRedirect

REPO = "SunSi12138/SharpLink"
API = "https://api.github.com/repos/"+REPO
TAG = "issue-775-research-20261021-39460041206"
LEDGER_ID = 6295616045


def main():
    out = Path("final-archive-verified")
    out.mkdir(exist_ok=False)
    headers = {"Authorization":"Bearer "+os.environ["GH_TOKEN"],"Accept":"application/vnd.github+json","User-Agent":"issue775-final-link-verification"}
    def api(path,data=None,method=None):
        req=urllib.request.Request(API+path,headers=headers,method=method or ("POST" if data is not None else "GET"),data=None if data is None else json.dumps(data).encode())
        with urllib.request.urlopen(req,timeout=120) as r:return json.load(r)
    def pages(path):
        result=[]
        for page in range(1,101):
            items=api(path+("&" if "?" in path else "?")+f"per_page=100&page={page}")
            result.extend(items)
            if len(items)<100:return result
        raise RuntimeError("Truncated pagination")
    release=api("/releases/tags/"+TAG)
    if release["draft"] or not release["prerelease"] or release["tag_name"]!=TAG:
        raise ValueError("Expected published research prerelease only")
    assets=pages(f"/releases/{release['id']}/assets")
    names={a["name"]:a for a in assets}
    byid={a["id"]:a for a in assets}
    def read_asset(asset):
        req=urllib.request.Request(asset["url"],headers=headers|{"Accept":"application/octet-stream"})
        try:r=urllib.request.build_opener(NoRedirect()).open(req,timeout=120)
        except urllib.error.HTTPError as redirect:
            if redirect.code not in (301,302,303,307,308):raise
            url=redirect.headers["Location"]
            if not url.startswith("https://"):raise ValueError("Insecure redirect")
            r=urllib.request.urlopen(url,timeout=120)
        with r:data=r.read()
        sha=hashlib.sha256(data).hexdigest()
        if len(data)!=asset["size"] or (asset.get("digest") and asset["digest"]!="sha256:"+sha):
            raise ValueError("Readback digest mismatch")
        return data
    original=names["issue775-all-research-index.zip"]
    original_bytes=read_asset(original)
    old=zipfile.ZipFile(io.BytesIO(original_bytes))
    manifest=json.loads(old.read("archive-manifest.json"))
    replacements={}
    verified=[]
    def canonical(a):
        return "https://github.com/"+REPO+"/releases/download/"+TAG+"/"+urllib.parse.quote(a["name"])
    for row in manifest["artifacts"]:
        if row["status"]!="archived-and-hash-checked":continue
        saved=row["asset"]
        actual=byid.get(saved["id"])
        if not actual or actual["size"]!=saved["size"] or actual["name"]!=saved["name"]:
            raise ValueError("Missing or substituted archived asset")
        if actual.get("digest")!="sha256:"+row["sha256"] or saved["sha256"]!=row["sha256"]:
            raise ValueError("Published raw ZIP digest mismatch")
        url=canonical(actual)
        replacements[saved["url"]]=url
        saved["url"]=url
        verified.append({"artifactId":row["id"],"releaseAssetId":actual["id"],"size":actual["size"],"sha256":row["sha256"],"url":url})
    for name in ("issue775-all-research-index.zip","archive-manifest.json","INDEX.md"):
        a=names[name]
        replacements[a["browser_download_url"]]=canonical(a)
        # Draft release links were issued before GitHub assigned the public tag.
        for row in manifest["artifacts"]:
            former=row.get("asset",{}).get("url","")
            if "/untagged-" in former:
                raise ValueError("Uncorrected draft link")
    manifest["publishedUrlVerification"]={"releaseId":release["id"],"tag":TAG,"verifiedRawAssets":len(verified),
        "method":"Read published asset metadata and require exact original ZIP SHA256/size/identity; canonicalize draft-era URLs without changing raw ZIP bytes.",
        "rawDataChanged":False,"verificationDriverSha":os.environ["GITHUB_SHA"]}
    fixed_manifest=json.dumps(manifest,ensure_ascii=False,indent=2)+"\n"
    index_text=old.read("INDEX.md").decode()
    for before,after in replacements.items():index_text=index_text.replace(before,after)
    if "/releases/download/untagged-" in index_text:
        raise ValueError("Remaining draft asset URL in directory")
    save(out/"published-asset-verification.json",{"release":release["html_url"],"assets":verified,"originalIndexSha256":hashlib.sha256(original_bytes).hexdigest(),"rawDataChanged":False})
    (out/"archive-manifest.json").write_text(fixed_manifest)
    (out/"INDEX.md").write_text(index_text)
    bundle=out/"issue775-all-research-index.zip"
    with zipfile.ZipFile(bundle,"w",zipfile.ZIP_DEFLATED) as z:
        for info in old.infolist():
            if info.is_dir():continue
            data=old.read(info)
            if info.filename=="archive-manifest.json":data=fixed_manifest.encode()
            elif info.filename=="INDEX.md":data=index_text.encode()
            z.writestr(info.filename,data)
        z.write(out/"published-asset-verification.json","published-asset-verification.json")
    def upload(path):
        url=f"https://uploads.github.com/repos/{REPO}/releases/{release['id']}/assets?name="+urllib.parse.quote(path.name)
        req=urllib.request.Request(url,headers=headers|{"Content-Type":"application/octet-stream"},data=path.read_bytes(),method="POST")
        with urllib.request.urlopen(req,timeout=240) as r:a=json.load(r)
        if a["size"]!=path.stat().st_size or a.get("digest")!="sha256:"+digest(path):raise ValueError("Corrected index upload mismatch")
        return a
    # Retain the original indexes as explicit pre-publication snapshots, never delete raw evidence.
    for name in ("issue775-all-research-index.zip","archive-manifest.json","INDEX.md"):
        a=names[name]
        api(f"/releases/assets/{a['id']}",{"name":"pre-publication--"+name,"label":"Original draft-URL index; use canonical index without this prefix"},"PATCH")
    uploaded={p.name:upload(p) for p in (bundle,out/"archive-manifest.json",out/"INDEX.md",out/"published-asset-verification.json")}
    old_prefixes={u.split('/releases/download/')[1].split('/')[0] for u in replacements if '/releases/download/untagged-' in u}
    def fix_text(text):
        for prefix in old_prefixes:text=text.replace("/releases/download/"+prefix+"/","/releases/download/"+TAG+"/")
        return text
    for c in pages("/issues/775/comments"):
        if c["body"].startswith("# 最终轮全部研究数据归档完成情况"):
            body=fix_text(c["body"])
            body+="\n\n发布后目录复核：全部已保存原始ZIP的release资产ID、大小和SHA256重新核对；已将草稿时期untagged链接规范化为最终tag链接。原始ZIP未改动，旧索引保留为pre-publication副本。"
            api(f"/issues/comments/{c['id']}",{"body":body},"PATCH")
    api(f"/releases/{release['id']}",{"body":fix_text(release["body"]),"make_latest":"false"},"PATCH")
    ledger=api(f"/issues/comments/{LEDGER_ID}")
    body=ledger["body"]
    if "## 5. 最终轮归档状态" not in body:raise ValueError("Unexpected ledger text; do not overwrite")
    count=len(verified)
    total=sum(v["size"] for v in verified)
    body=body.split("## 5. 最终轮归档状态")[0]+("## 5. 最终轮归档已收尾\n\n"
        f"已保存并校验 **{count} 份原始ZIP，共 {total:,} 字节**。"
        f"历史不可获取项：{len(manifest.get('historicalGaps',[]))}；其他记录错误：{len(manifest.get('failures',[]))}。"
        "每项实际状态见清单，不将研究摘要替代原始数据。\n\n"
        f"[原始产物研究归档]({release['html_url']}) · [完整ZIP目录]({canonical(uploaded['INDEX.md'])}) · "
        f"[SHA256/源码/状态清单]({canonical(uploaded['archive-manifest.json'])}) · "
        f"[全部summary、历史评论、源码及复现脚本]({canonical(uploaded['issue775-all-research-index.zip'])})\n\n"
        "最后构建诊断完成12次构建，44组普通同路径DLL对照中41组字节一致、3组仅72字节MVID/时间标记/调试信息变化；所检方法IL未变。"
        "不据此追溯证明全部历史DLL或性能因果；本轮新增性能样本为零。详见归档内FINAL-FINDINGS.md。\n\n"
        "首次归档HTTP500失败快照保留；仅归档修正使用逐运行API，并核对发布后链接，未重新跑科学样本。"
        "研究按要求在本轮结束。仍未证明恢复原始基线，保留正确性修复，不集成未验收候选，不合并#862、不关闭#775、不授权第二阶段。")
    body=body.replace("本评论末尾将在本轮执行结束后补入实际结果与归档定位，不预先声称成功。","本评论末尾已补入实际诊断结果、完整原始产物目录与发布后校验入口。")
    api(f"/issues/comments/{LEDGER_ID}",{"body":body},"PATCH")
    issue=api("/issues/775")
    prefix=("> **2026-10-21 最终研究归档**：[全阶段数值、失败记录和最终结论](https://github.com/"+REPO+f"/issues/775#issuecomment-{LEDGER_ID})；"
        f"[全部原始数据及SHA256目录]({release['html_url']})。静态原型尚未证明恢复基线，研究收尾不等于实现验收；#862继续Draft。原设计正文保留如下。\n\n")
    if not issue["body"].startswith("> **2026-10-21 最终研究归档**"):
        api("/issues/775",{"body":prefix+issue["body"]},"PATCH")
    save(out/"verified.json",{"release":release["html_url"],"verifiedRawAssets":count,"totalBytes":total,
        "ledger":"https://github.com/"+REPO+f"/issues/775#issuecomment-{LEDGER_ID}",
        "newIndexes":{k:{"id":a["id"],"url":canonical(a),"sha256":a['digest']} for k,a in uploaded.items()},
        "rawEvidenceModified":False,"newScientificRuns":0,"stage2Authorized":False})
    print(json.dumps(json.loads((out/"verified.json").read_text()),ensure_ascii=False,indent=2))


if __name__=="__main__":main()
