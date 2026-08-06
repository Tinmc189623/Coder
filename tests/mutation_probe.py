"""Mutation probe — measures whether the test suite can actually detect regressions.

Deliberately injects a defect into the source, rebuilds, runs the suite, and
records whether the suite noticed. A mutation that survives is a blind spot:
the code could break that way in production and CI would stay green.

Usage:  python tests/mutation_probe.py [--only LABEL]
"""

import argparse
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
MAIN_PROJECT = os.path.join(ROOT, "Coder.csproj")
TEST_PROJECT = os.path.join(ROOT, "tests", "unit", "Coder.Tests.csproj")
TEST_EXE = os.path.join(ROOT, "tests", "unit", "bin", "Debug", "net11.0", "coder.tests.exe")
SUITE = os.path.join(ROOT, "tests", "run_smoke.py")

# (label, relative path, needle, replacement, what breaking this means for a user)
MUTATIONS = [
    ("perm-plan-allows-writes", "src/Permissions.cs",
     'return new PermissionDecision("deny", "plan mode");',
     'return new PermissionDecision("allow", "plan mode");',
     "Plan mode silently permits file writes and shell commands."),

    ("perm-acceptedits-covers-all", "src/Permissions.cs",
     '(inv.Name == "file_edit" || inv.Name == "file_write" || inv.Name == "bash")',
     'true',
     "acceptEdits auto-approves every tool, including web_fetch and Task."),

    ("edit-truncates-file", "src/Tools.cs",
     "var replaced = text.Replace(oldS, newS);",
     "var replaced = newS;",
     "file_edit discards the whole file and keeps only the replacement text."),

    ("edit-ignores-missing-target", "src/Tools.cs",
     'if (!text.Contains(oldS)) return Task.FromResult(Err(Id(input), "old_string not found in file"));',
     "",
     "file_edit reports success when old_string was never in the file."),

    ("wrap-drops-ansi-state", "src/UiChrome.cs",
     'if (active.Length > 0) sb.Append("\\u001b[0m");',
     "",
     "Colour codes leak across wrapped lines and corrupt every panel."),

    ("theme-colour-always-off", "src/Ui.cs",
     'if (Env("NO_COLOR").Length > 0) return ColorDepth.None;',
     "if (true) return ColorDepth.None;",
     "The entire theme is dead; output renders monochrome everywhere."),

    ("tokens-budget-zeroed", "src/Tokens.cs",
     "public static int Estimate(object? input)",
     "public static int Estimate(object? input) => 0;\n    private static int EstimateUnused(object? input)",
     "Context accounting reports zero; auto-compaction never fires."),

    ("sse-drops-tool-events", "src/Providers.cs",
     "onEvent(new StreamEventToolUseEnd(id, input));",
     "",
     "Streaming mode silently discards every tool call (the bug fixed earlier)."),
]


def run(cmd, timeout=180):
    return subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout)


def build():
    # Build both: the CLI (for the e2e smoke harness) and the unit-test project
    # (which compiles the same src/ and is the real regression guard).
    r1 = run([DOTNET, "build", MAIN_PROJECT, "-v", "q", "--nologo"])
    r2 = run([DOTNET, "build", TEST_PROJECT, "-v", "q", "--nologo"])
    ok = r1.returncode == 0 and r2.returncode == 0
    return ok, (r1.stdout or "") + (r1.stderr or "") + (r2.stdout or "") + (r2.stderr or "")


def unit_passes():
    if os.path.exists(TEST_EXE):
        r = run([TEST_EXE], timeout=180)
    else:
        r = run([DOTNET, "run", "--project", TEST_PROJECT, "--no-build"], timeout=180)
    return r.returncode == 0


def smoke_passes():
    r = run([sys.executable, SUITE], timeout=300)
    return r.returncode == 0


def suite_passes():
    # A mutation is "caught" if the unit tests notice it. We only fall back to
    # the happy-path e2e smoke when the unit suite is already green, so a mutant
    # that slips past the unit tests still has a chance to be caught by e2e.
    if not unit_passes():
        return False
    return smoke_passes()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only")
    args = ap.parse_args()

    targets = [m for m in MUTATIONS if not args.only or m[0] == args.only]

    ok, log = build()
    if not ok:
        print("baseline build failed, aborting:\n" + log[-800:])
        return 2
    if not suite_passes():
        print("baseline suite is already red, aborting")
        return 2
    print(f"baseline green -- probing {len(targets)} mutations\n")

    survived, killed, skipped = [], [], []

    for label, rel, needle, repl, impact in targets:
        path = os.path.join(ROOT, rel)
        with open(path, encoding="utf-8") as fh:
            original = fh.read()

        if needle not in original:
            print(f"[SKIP]     {label}  (anchor not found in {rel})")
            skipped.append(label)
            continue

        with open(path, "w", encoding="utf-8") as fh:
            fh.write(original.replace(needle, repl, 1))

        try:
            t0 = time.time()
            built, blog = build()
            if not built:
                print(f"[SKIP]     {label}  (mutant does not compile)")
                skipped.append(label)
                continue
            caught = not suite_passes()
            secs = time.time() - t0
            if caught:
                print(f"[KILLED]   {label}  ({secs:.0f}s)")
                killed.append(label)
            else:
                print(f"[SURVIVED] {label}  ({secs:.0f}s)")
                print(f"           -> {impact}")
                survived.append((label, impact))
        finally:
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(original)

    build()

    scored = len(survived) + len(killed)
    print("\n" + "=" * 62)
    print(f"mutation score: {len(killed)}/{scored} caught"
          + (f"   ({len(skipped)} skipped)" if skipped else ""))
    if survived:
        print(f"\n{len(survived)} defect(s) shipped green:")
        for label, impact in survived:
            print(f"  - {label}\n      {impact}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
