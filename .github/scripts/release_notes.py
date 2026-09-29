#!/usr/bin/env python3
"""前回リリースからのコミットを元に、リリースノートの下書きを作る。

Run from anywhere:  python .github/scripts/release_notes.py <version> [--prev <tag>] [--out <file>]

- Conventional Commits の type で振り分ける（feat → 新機能、fix / perf → バグ修正・改善）。
- それ以外（refactor / docs / chore など）は HTML コメントに入れるので、公開しても表示されない。
- 作者がリポジトリのオーナー以外なら "Thanks @xxx" の行を付ける（gh が使えるときだけ）。
- 結果は .github/release-notes-template.md の {{VERSION}} / {{CHANGES}} に流し込む。
"""

import argparse
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / ".github" / "release-notes-template.md"

CONVENTIONAL = re.compile(r"^(?P<type>[a-z]+)(?:\((?P<scope>[^)]*)\))?!?:\s*(?P<subject>.+)$")

# (見出し, 対象の type)
SECTIONS = [
    ("新機能 / New Features", {"feat"}),
    ("バグ修正・改善 / Bug Fixes & Improvements", {"fix", "perf"}),
]


def run(*args):
    return subprocess.run(
        args, cwd=ROOT, check=True, capture_output=True, text=True, encoding="utf-8"
    ).stdout


def try_run(*args):
    try:
        return run(*args).strip()
    except (FileNotFoundError, subprocess.CalledProcessError):
        return ""


def tag_exists(tag):
    return bool(try_run("git", "rev-parse", "--verify", "--quiet", f"{tag}^{{commit}}"))


def previous_tag(repo):
    # 公開済みの最新リリース（UpdateChecker が見ているもの）を基準にする。
    tag = try_run("gh", "api", f"repos/{repo}/releases/latest", "--jq", ".tag_name") if repo else ""
    if tag and tag_exists(tag):
        return tag
    return try_run("git", "describe", "--tags", "--abbrev=0", "HEAD^")


def author_login(repo, sha):
    if not repo:
        return ""
    return try_run("gh", "api", f"repos/{repo}/commits/{sha}", "--jq", '.author.login // ""')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("version")
    parser.add_argument("--prev", help="比較元のタグ（省略時は公開済みの最新リリース）")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--out", help="出力先（省略時は標準出力）")
    args = parser.parse_args()

    prev = args.prev or previous_tag(args.repo)
    if not prev or not tag_exists(prev):
        print(f"比較元のタグが見つかりません: {prev!r}", file=sys.stderr)
        sys.exit(1)

    owner = args.repo.split("/")[0] if args.repo else ""
    log = run("git", "log", "--no-merges", "--reverse", "--format=%H%x1f%s", f"{prev}..HEAD")

    visible = {title: [] for title, _ in SECTIONS}
    hidden = []
    for line in log.splitlines():
        sha, subject = line.split("\x1f", 1)
        m = CONVENTIONAL.match(subject)
        ctype, scope = (m["type"], m["scope"]) if m else ("", None)
        if ctype == "chore" and scope == "release":
            continue

        section = next((title for title, types in SECTIONS if ctype in types), None)
        if section is None:
            # --> が含まれるとコメントが途中で閉じてしまうため崩しておく
            hidden.append(f"- {subject.replace('-->', '-- >')} ({sha[:7]})")
            continue

        item = f"- **{m['subject']}**  \n  <!-- {subject} ({sha[:7]}) -->"
        login = author_login(args.repo, sha)
        if login and login != owner and not login.endswith("[bot]"):
            item += f"\n  (Thanks [@{login}](https://github.com/{login}) for the contribution!)"
        visible[section].append(item)

    blocks = [
        f"### {title}\n\n" + "\n\n".join(items) for title, items in visible.items() if items
    ]
    if hidden:
        blocks.append(
            "<!--\n"
            "内部向けの変更（このコメントは公開されません。載せたいものは上に移してください）\n"
            + "\n".join(hidden)
            + "\n-->"
        )
    if args.repo:
        blocks.append(f"<!-- 差分: https://github.com/{args.repo}/compare/{prev}...{args.version} -->")

    notes = (
        TEMPLATE.read_text(encoding="utf-8")
        .replace("{{VERSION}}", args.version)
        .replace("{{CHANGES}}", "\n\n".join(blocks))
    )

    if args.out:
        pathlib.Path(args.out).write_text(notes, encoding="utf-8")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stdout.write(notes)


if __name__ == "__main__":
    main()
