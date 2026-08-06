"""End-to-end smoke test for the Coder CLI .NET port.

Starts the mock LLM server, points the CLI at it, and asserts that the full
agent loop works: request shape -> tool dispatch -> file effects -> final
assistant message. Covers both wire dialects (OpenAI-compatible and Anthropic)
in both streaming and non-streaming modes.
"""

import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "bin", "Debug", "net11.0", "Coder.exe")
MOCK = os.path.join(ROOT, "tests", "mock_provider.py")

ALL_TOOLS = ("bash", "file_read", "file_write", "file_edit", "glob", "grep",
             "web_fetch", "web_search", "todo_write", "Task", "skill", "memory", "ask_user")

failures = []


def check(label, condition, detail=""):
    print(f"  [{'PASS' if condition else 'FAIL'}] {label}" +
          (f"  -- {detail}" if detail and not condition else ""))
    if not condition:
        failures.append(label)


def wait_for_server(port, path, timeout=15):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            req = urllib.request.Request(
                f"http://127.0.0.1:{port}{path}", data=b'{"messages":[],"tools":[]}',
                headers={"Content-Type": "application/json"})
            urllib.request.urlopen(req, timeout=2).read()
            return True
        except urllib.error.HTTPError:
            return True
        except Exception:
            time.sleep(0.4)
    return False


def scenario(name, port, stream, dialect):
    print(f"\n=== scenario: {name} ===")
    anthropic = dialect == "anthropic"
    probe_path = "/v1/messages" if anthropic else "/v1/chat/completions"
    base_url = f"http://127.0.0.1:{port}" if anthropic else f"http://127.0.0.1:{port}/v1"
    provider = "anthropic" if anthropic else "mockvendor"

    workdir = tempfile.mkdtemp(prefix="coder-e2e-")
    log_path = os.path.join(workdir, "requests.jsonl")
    with open(os.path.join(workdir, "package.json"), "w", encoding="utf-8") as fh:
        json.dump({"name": "e2e-fixture", "version": "0.0.1"}, fh)

    server = subprocess.Popen([sys.executable, MOCK, str(port), log_path],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try:
        if not wait_for_server(port, probe_path):
            check(f"{name}: mock server started", False)
            return

        proc = subprocess.run(
            [EXE, "--quiet", "--lang", "zh-CN",
             "--stream" if stream else "--no-stream",
             "--permission-mode", "bypassPermissions",
             "--provider", provider,
             "--base-url", base_url,
             "--api-key", "sk-test", "--model", "mock-model",
             "--cwd", workdir,
             "read package.json then write a file"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)

        out = (proc.stdout or "") + (proc.stderr or "")
        print("--- CLI output ---")
        print((out.strip() or "(empty)")[:900])
        print("--- assertions ---")

        check(f"{name}: exit code 0", proc.returncode == 0, f"got {proc.returncode}")
        check(f"{name}: final assistant text rendered", "mock_out.txt" in out, out[:160])
        check(f"{name}: text alongside tool_use rendered", "Writing the file now" in out, out[:200])

        wrote = os.path.join(workdir, "mock_out.txt")
        check(f"{name}: file_write executed", os.path.exists(wrote))
        if os.path.exists(wrote):
            body = open(wrote, encoding="utf-8").read().strip()
            check(f"{name}: written content correct", body == "written by agent loop", body)

        reqs = []
        if os.path.exists(log_path):
            with open(log_path, encoding="utf-8") as fh:
                reqs = [json.loads(l) for l in fh if l.strip()]

        check(f"{name}: full 3-turn tool loop ran", len(reqs) == 3, f"round-trips={len(reqs)}")
        if reqs:
            first = reqs[0]
            check(f"{name}: dialect = {dialect}", first["dialect"] == dialect, first["dialect"])
            check(f"{name}: stream flag = {stream}", first["stream"] is stream, str(first["stream"]))
            check(f"{name}: system prompt sent", first["has_system"], str(first["roles"]))
            check(f"{name}: tools advertised", first["n_tools"] >= 12, f"n_tools={first['n_tools']}")
            missing = [t for t in ALL_TOOLS if t not in first["tool_names"]]
            check(f"{name}: all 13 tools exposed", not missing, "missing: " + ", ".join(missing))
            check(f"{name}: model forwarded", first["model"] == "mock-model", str(first["model"]))
        if len(reqs) >= 2:
            check(f"{name}: tool results appended to history",
                  reqs[1]["n_messages"] > reqs[0]["n_messages"],
                  f"{reqs[0]['n_messages']} -> {reqs[1]['n_messages']}")
    finally:
        server.terminate()
        try:
            server.wait(timeout=5)
        except Exception:
            server.kill()


def main():
    if not os.path.exists(EXE):
        print(f"missing build output: {EXE}")
        return 1
    scenario("openai/non-stream", 8794, stream=False, dialect="openai")
    scenario("openai/stream", 8795, stream=True, dialect="openai")
    scenario("anthropic/non-stream", 8796, stream=False, dialect="anthropic")
    scenario("anthropic/stream", 8797, stream=True, dialect="anthropic")
    print("\n" + ("ALL PASSED" if not failures else f"{len(failures)} FAILED:\n  - " +
                  "\n  - ".join(failures)))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
