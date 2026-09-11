#!/usr/bin/env python3
"""Spike D, part 2: does targeted retrieval LOSE information?

Context reduction is worthless if the compact answer omits what is needed to
answer correctly. This is NG-010's inverse risk: retrieval that is small
because it is lossy, rather than small because it is targeted.

Method. A graded task set. Each task names a question, the Strata command that
should answer it, and the ground-truth FACTS a correct answer depends on. For
each condition we check whether every required fact is present in the context
that condition provides.

This is DETERMINISTIC and measures information SUFFICIENCY — whether a correct
answer is derivable at all. It is not agent correctness, which needs agents.
Sufficiency is a precondition for correctness: an agent cannot be right from a
context that lacks the facts.
"""

import json
import os
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CORPUS = REPO / "examples" / "corpus"
BIN = REPO / "src" / "Strata.Cli" / "bin" / "Debug" / "net8.0" / "strata"


def raw_context() -> str:
    env = dict(os.environ)
    env["PGPASSWORD"] = "strata"
    dump = subprocess.run(
        ["pg_dump", "--schema-only", "--no-owner", "--no-privileges", "-n", "sales",
         "-h", "127.0.0.1", "-U", "strata", "strata_test"],
        capture_output=True, text=True, env=env, timeout=120,
    )
    if dump.returncode != 0:
        raise SystemExit(f"pg_dump failed: {dump.stderr.strip()}")

    corpus = "\n".join(
        f"-- {p.relative_to(CORPUS)}\n{p.read_text()}" for p in sorted(CORPUS.rglob("*.sql"))
    )
    return dump.stdout + "\n" + corpus


def strata(args: list[str]) -> str:
    result = subprocess.run(
        [str(BIN), *args, "--json"],
        capture_output=True, text=True, env=dict(os.environ), timeout=180,
    )
    if result.returncode != 0:
        raise SystemExit(f"strata {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout.strip()


# Each fact is a regex that must appear in a context for the question to be
# answerable from it. Facts are ground truth about the fixture database.
TASKS = [
    {
        "question": "What columns does sales.orders have?",
        "command": ["inspect", "sales.orders"],
        "facts": {"order_id": r"order_id", "customer_id": r"customer_id",
                  "status": r"status", "total": r"total"},
    },
    {
        "question": "What type is sales.orders.total?",
        "command": ["inspect", "sales.orders"],
        "facts": {"numeric type": r"numeric"},
    },
    {
        "question": "Which table does sales.orders reference?",
        "command": ["inspect", "sales.orders"],
        "facts": {"references customer": r"sales\.customer"},
    },
    {
        "question": "What values are allowed in sales.orders.status?",
        "command": ["inspect", "sales.orders"],
        "facts": {"check constraint values": r"'open'|open.*closed"},
    },
    {
        "question": "Is sales.orders.customer_id indexed?",
        "command": ["inspect", "sales.orders"],
        "facts": {"index on customer_id": r"idx_orders_customer"},
    },
    {
        "question": "Is sales.customer.customer_number unique?",
        "command": ["inspect", "sales.customer"],
        "facts": {"unique constraint": r"UNIQUE|unique"},
    },
    {
        "question": "Which SQL files read sales.orders?",
        "command": ["readers", "sales.orders"],
        "facts": {"open-orders": r"open-orders", "customer-totals": r"customer-totals"},
    },
    {
        "question": "Is sales.invoice related to sales.customer?",
        "command": ["relationships", "sales.invoice"],
        "facts": {"invoice-customer link": r"customer_number"},
    },
    {
        "question": "Is the invoice-customer link enforced by the database?",
        "command": ["relationships", "sales.invoice"],
        "facts": {"enforcement status": r"enforcedByDatabase|FOREIGN KEY"},
    },
]


def check(context: str, facts: dict) -> dict:
    return {name: bool(re.search(pattern, context, re.IGNORECASE))
            for name, pattern in facts.items()}


def main() -> None:
    if not os.environ.get("STRATA_PG"):
        raise SystemExit("STRATA_PG not set")

    raw = raw_context()
    rows = []

    for task in TASKS:
        answer = strata(task["command"])
        raw_result = check(raw, task["facts"])
        strata_result = check(answer, task["facts"])

        rows.append({
            "question": task["question"],
            "command": " ".join(task["command"]),
            "raw": raw_result,
            "strata": strata_result,
            "raw_sufficient": all(raw_result.values()),
            "strata_sufficient": all(strata_result.values()),
            "missing_from_strata": [k for k, v in strata_result.items() if not v],
            "missing_from_raw": [k for k, v in raw_result.items() if not v],
        })

    print(json.dumps({
        "method": "deterministic information-sufficiency check; NOT agent correctness",
        "tasks": rows,
        "totals": {
            "task_count": len(rows),
            "raw_sufficient": sum(1 for r in rows if r["raw_sufficient"]),
            "strata_sufficient": sum(1 for r in rows if r["strata_sufficient"]),
        },
    }, indent=2))


if __name__ == "__main__":
    main()
