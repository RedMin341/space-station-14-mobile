#!/usr/bin/env python3
"""Keep the EXISTING SS14 Fobos 276 Android app aligned with live Fobos.

Only Fobos /info is authoritative. Never blindly follow Dead Space master.
Atomic commit updates the existing app branch, then workflow_dispatch starts
the normal release APK build with its permanent certificate checks.
"""
from __future__ import annotations

import base64
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

REPO = os.environ.get("GITHUB_REPOSITORY", "RedMin341/space-station-14-mobile")
BRANCH = "ss14-fobos-276"
SOURCE = "dead-space-server/dead-space-14"
ENGINE_SOURCE = "dead-space-server/RobustToolbox"
API = "https://api.github.com"
ADDRESS = "ss14://f1.deadspace14.net:1212"
INFO_URLS = [
    "http://f1.deadspace14.net:1212/info",
    "https://hub.spacestation14.com/api/servers/info?url=" + urllib.parse.quote(ADDRESS, safe=""),
    "https://hub.spacestation14.com/api/servers/info?url=" + urllib.parse.quote("ss14s://f1.deadspace14.net", safe=""),
]


class UnsafeUpdate(ValueError):
    pass


def request(url, method="GET", payload=None):
    headers = {"User-Agent": "SS14-Fobos-Autoupdater/1.0", "Accept": "application/vnd.github+json"}
    if url.startswith(API + "/"):
        token = os.environ.get("GITHUB_TOKEN")
        if not token:
            raise RuntimeError("GITHUB_TOKEN is required")
        headers.update({"Authorization": "Bearer " + token, "X-GitHub-Api-Version": "2022-11-28"})
    data = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload).encode()
    with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers, method=method), timeout=20) as response:
        raw = response.read(10000000)
    return json.loads(raw) if raw else None


def api(endpoint, method="GET", payload=None):
    return request(API + endpoint, method=method, payload=payload)


def get_file(repo, path, ref):
    result = api("/repos/{}/contents/{}?ref={}".format(repo, urllib.parse.quote(path, safe="/"), urllib.parse.quote(ref, safe="")))
    if result.get("encoding") != "base64" or not result.get("content"):
        raise UnsafeUpdate("GitHub file is not text: " + path)
    return base64.b64decode(result["content"]).decode("utf-8-sig")


def get_live_server():
    failures = []
    for url in INFO_URLS:
        try:
            result = request(url)
            info = result.get("build", {}) if isinstance(result, dict) else {}
            fork, content, engine = info.get("fork_id"), info.get("version"), info.get("engine_version")
            if fork != "dsfobos":
                raise UnsafeUpdate("Unexpected fork: " + repr(fork))
            if not isinstance(content, str) or not re.fullmatch("[a-fA-F0-9]{40}", content):
                raise UnsafeUpdate("No full commit SHA in live server /info")
            if not isinstance(engine, str) or not re.fullmatch(r"276\.\d+\.\d+", engine):
                raise UnsafeUpdate("Unexpected engine: " + repr(engine))
            print("Live Fobos build:", engine, content)
            return engine, content.lower()
        except (urllib.error.URLError, TimeoutError, KeyError, TypeError, ValueError) as exc:
            failures.append(str(exc))
    raise UnsafeUpdate("Live Fobos /info unavailable or invalid; no blind update. " + " | ".join(failures))


def get_source_engine(content):
    sub = api("/repos/{}/contents/RobustToolbox?ref={}".format(SOURCE, content))
    sha = sub.get("sha", "")
    if sub.get("type") != "submodule" or not re.fullmatch("[a-f0-9]{40}", sha):
        raise UnsafeUpdate("Fobos git commit has no valid RobustToolbox submodule")
    xml = get_file(ENGINE_SOURCE, "MSBuild/Robust.Engine.Version.props", sha)
    engine = ET.fromstring(xml).findtext("./PropertyGroup/Version")
    if not engine or not re.fullmatch(r"276\.\d+\.\d+", engine):
        raise UnsafeUpdate("Unsupported source engine: " + repr(engine))
    return sha, engine


def replace_once(text, pattern, replacement, path):
    new, count = re.subn(pattern, lambda _: replacement, text, count=1)
    if count != 1:
        raise UnsafeUpdate("Expected one match in " + path + ": " + pattern)
    return new


def plan(files, live_engine, live_content, source_engine_sha):
    bp = "Content.Android/FobosBuild.cs"
    pp = "scripts/prepare-fobos.py"
    wp = ".github/workflows/android-release.yml"
    cp = "Content.Android/Content.Android.csproj"
    old_build = files[bp]
    old_engine = re.search(r'public const string Engine = "([^"]+)";', old_build)
    old_content = re.search(r'public const string ContentRevision = "([0-9a-f]{40})";', old_build)
    if not old_engine or not old_content:
        raise UnsafeUpdate("Missing FobosBuild engine/revision markers")
    if old_engine.group(1) == live_engine and old_content.group(1) == live_content:
        return {}

    build = replace_once(old_build, r'public const string Engine = "[^"]+";', 'public const string Engine = "{}";'.format(live_engine), bp)
    build = replace_once(build, r'public const string ContentRevision = "[0-9a-f]{40}";', 'public const string ContentRevision = "{}";'.format(live_content), bp)

    prep = files[pp]
    prep = replace_once(prep, r'(?m)^CONTENT = "[0-9a-f]{40}"$', 'CONTENT = "{}"'.format(live_content), pp)
    prep = replace_once(prep, r'(?m)^ENGINE = "[0-9a-f]{40}"$', 'ENGINE = "{}"'.format(source_engine_sha), pp)
    prep = replace_once(prep, r'(?m)^print\(f"Fobos content \{CONTENT\}; engine [^\n]*\)$', 'print(f"Fobos content {{CONTENT}}; engine {} ({{ENGINE}})")'.format(live_engine), pp)

    wf = replace_once(files[wp], r'(?m)^\s+ref: [0-9a-f]{40}$', '          ref: ' + live_content, wp)

    project = files[cp]
    code = re.search(r"<ApplicationVersion>(\d+)</ApplicationVersion>", project)
    version = re.search(r"<ApplicationDisplayVersion>(\d+)\.(\d+)\.(\d+)</ApplicationDisplayVersion>", project)
    if not code or not version:
        raise UnsafeUpdate("Missing APK version markers")
    next_code = int(code.group(1)) + 1
    a, b, c = map(int, version.groups())
    if next_code > 2000000000 or c > 2000000000:
        raise UnsafeUpdate("Android version overflow")
    project = replace_once(project, r"<ApplicationVersion>\d+</ApplicationVersion>", "<ApplicationVersion>{}</ApplicationVersion>".format(next_code), cp)
    project = replace_once(project, r"<ApplicationDisplayVersion>\d+\.\d+\.\d+</ApplicationDisplayVersion>",
                           "<ApplicationDisplayVersion>{}.{}.{}</ApplicationDisplayVersion>".format(a, b, c + 1), cp)
    updated = {bp: build, pp: prep, wp: wf, cp: project}
    return {path: value for path, value in updated.items() if files[path] != value}


def commit_once(changes, engine, content):
    head = api("/repos/{}/branches/{}".format(REPO, BRANCH))["commit"]["sha"]
    base_tree = api("/repos/{}/git/commits/{}".format(REPO, head))["tree"]["sha"]
    tree = api("/repos/{}/git/trees".format(REPO), "POST", {
        "base_tree": base_tree,
        "tree": [{"path": p, "mode": "100644", "type": "blob", "content": value} for p, value in changes.items()],
    })["sha"]
    commit = api("/repos/{}/git/commits".format(REPO), "POST", {
        "message": "Auto-sync live Fobos {}/{}".format(engine, content[:12]),
        "tree": tree,
        "parents": [head],
    })["sha"]
    api("/repos/{}/git/refs/heads/{}".format(REPO, BRANCH), "PATCH", {"sha": commit, "force": False})
    return commit


def main():
    try:
        engine, content = get_live_server()
        engine_sha, source_version = get_source_engine(content)
        if engine != source_version:
            raise UnsafeUpdate("Live server engine {} != source engine {}".format(engine, source_version))
        paths = ["Content.Android/FobosBuild.cs", "scripts/prepare-fobos.py",
                 ".github/workflows/android-release.yml", "Content.Android/Content.Android.csproj"]
        files = {p: get_file(REPO, p, BRANCH) for p in paths}
        changes = plan(files, engine, content, engine_sha)
        if not changes:
            print("No update needed. Fobos 276 Android matches live server.")
            return 0
        print("Atomic update:", ", ".join(changes))
        new_sha = commit_once(changes, engine, content)
        print("Fobos 276 updated to", new_sha)
        # GITHUB_TOKEN pushes do not trigger workflows; workflow_dispatch DOES.
        api("/repos/{}/actions/workflows/android-release.yml/dispatches".format(REPO),
            "POST", {"ref": BRANCH, "inputs": {"release_tag": ""}})
        print("Started the signed APK workflow with the existing Fobos certificate.")
        return 0
    except UnsafeUpdate as exc:
        print("::warning::Skipping Fobos auto-update safely: {}".format(exc))
        return 0
    except Exception as exc:
        print("::error::Fobos automation failed: {}".format(exc))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
