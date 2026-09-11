#!/usr/bin/env python3
"""Spike D measurement harness: agent context cost.

Measures how much context an agent must consume to answer a database question
under two conditions:

  A (raw)     the whole schema DDL plus the whole SQL corpus, which is what an
              agent reads when it has no semantic layer
  B (Strata)  the JSON answer from a targeted retrieval command

This measures CONTEXT SIZE only. It does NOT measure agent correctness, which
requires running agents under controlled conditions and is reported separately
as untested. Conflating the two would be exactly the overclaim the notebook's
§134 warns against.

Token counts are ESTIMATES. No tokenizer for the target model is available in
this environment, so the estimate uses a documented approximation and both raw
bytes (exact) and estimated tokens are reported.
"""

import json
import os
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CORPUS = REPO / "examples" / "corpus"
BIN = REPO / "src" / "Strata.Cli" / "bin" / "Debug" / "net8.0" / "strata"

# Approximation: ~4 characters per token for English/code mixed text. Stated
# rather than hidden; the byte counts below are exact and independent of it.
CHARS_PER_TOKEN = 4.0


def estimate_tokens(text: str) -> int:
    return int(round(len(text) / CHARS_PER_TOKEN))


def raw_schema_ddl(connection: str) -> str:
    """What an agent would read to learn the schema without Strata: pg_dump -s."""
    env = dict(os.environ)
    env["PGPASSWORD"] = "strata"
    result = subprocess.run(
        # Restricted to the `sales` schema so BOTH conditions cover exactly the
        # same objects. Dumping everything would give the raw condition objects
        # the corpus never mentions and that Strata's role cannot read, making
        # the comparison unfair in Strata's favour.
        ["pg_dump", "--schema-only", "--no-owner", "--no-privileges",
         "-n", "sales",
         "-h", "127.0.0.1", "-U", "strata", "strata_test"],
        capture_output=True, text=True, env=env, timeout=120,
    )
    if result.returncode != 0:
        raise SystemExit(f"pg_dump failed: {result.stderr.strip()}")
    return result.stdout


def raw_corpus() -> str:
    parts = []
    for path in sorted(CORPUS.rglob("*.sql")):
        parts.append(f"-- {path.relative_to(CORPUS)}\n{path.read_text()}")
    return "\n".join(parts)


def strata_answer(args: list[str]) -> str:
    env = dict(os.environ)
    result = subprocess.run(
        [str(BIN), *args, "--json"],
        capture_output=True, text=True, env=env, timeout=180,
    )
    if result.returncode != 0:
        raise SystemExit(f"strata failed: {result.stderr.strip()}")
    return result.stdout.strip()


# The task set. Each is a question a developer or agent actually asks, phrased
# as the Strata command that answers it.
TASKS = [
    ("What is the structure of sales.orders?",        ["inspect", "sales.orders"]),
    ("What relates to sales.customer?",               ["relationships", "sales.customer"]),
    ("How does sales.orders connect to sales.customer?", ["path", "sales.orders", "sales.customer"]),
    ("What reads sales.orders?",                      ["readers", "sales.orders"]),
    ("What writes sales.orders?",                     ["writers", "sales.orders"]),
    ("What is the structure of sales.invoice?",       ["inspect", "sales.invoice"]),
    ("What relates to sales.invoice?",                ["relationships", "sales.invoice"]),
]


def main() -> None:
    connection = os.environ.get("STRATA_PG")
    if not connection:
        raise SystemExit("STRATA_PG not set")

    ddl = raw_schema_ddl(connection)
    corpus = raw_corpus()
    raw_context = ddl + "\n" + corpus

    raw_bytes = len(raw_context.encode("utf-8"))
    raw_tokens = estimate_tokens(raw_context)

    rows = []
    for question, args in TASKS:
        answer = strata_answer(args)
        rows.append({
            "question": question,
            "command": " ".join(args),
            "strata_bytes": len(answer.encode("utf-8")),
            "strata_tokens": estimate_tokens(answer),
        })

    total_strata_bytes = sum(r["strata_bytes"] for r in rows)
    total_strata_tokens = sum(r["strata_tokens"] for r in rows)

    report = {
        "method": {
            "chars_per_token": CHARS_PER_TOKEN,
            "tokenizer": "none available in this environment; estimate only",
            "raw_condition": "pg_dump --schema-only + every .sql file in the corpus",
            "strata_condition": "the JSON answer from one targeted retrieval command",
            "measures": "context size only; NOT agent correctness",
        },
        "raw_condition": {
            "ddl_bytes": len(ddl.encode("utf-8")),
            "corpus_bytes": len(corpus.encode("utf-8")),
            "total_bytes": raw_bytes,
            "estimated_tokens": raw_tokens,
        },
        "tasks": rows,
        "totals": {
            "task_count": len(rows),
            "raw_tokens_per_task": raw_tokens,
            "raw_tokens_all_tasks": raw_tokens * len(rows),
            "strata_tokens_all_tasks": total_strata_tokens,
            "strata_bytes_all_tasks": total_strata_bytes,
            "reduction_ratio": round(raw_tokens * len(rows) / max(total_strata_tokens, 1), 1),
        },
    }

    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
