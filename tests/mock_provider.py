"""Minimal mock LLM server for Coder CLI smoke tests.

Speaks two dialects, chosen by request path:
  POST /v1/chat/completions -> OpenAI-compatible
  POST /v1/messages         -> Anthropic

Scripted conversation (per dialect, shared turn counter):
  Turn 1 -> ask the agent to call `file_read` on package.json
  Turn 2 -> ask the agent to call `file_write` (also emits a leading text block,
            which regression-tests content-block index alignment on Anthropic)
  Turn 3 -> return a final assistant message

Readiness probes (requests with no messages) do not advance the turn counter.

Usage: python mock_provider.py [port] [request-log-path]
"""

import json
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

STATE = {"turn": 0}

FINAL_TEXT = "Done: read package.json and wrote mock_out.txt."
PREAMBLE = "Writing the file now. "


# ── scripted turns (dialect-neutral) ────────────────────────────────────────
def script(turn):
    """Return (text, tool_calls) for a turn. text may be '' and precedes tools."""
    if turn == 1:
        return "", [("call_1", "file_read", {"path": "package.json"})]
    if turn == 2:
        return PREAMBLE, [("call_2", "file_write",
                           {"path": "mock_out.txt", "content": "written by agent loop\n"})]
    return FINAL_TEXT, []


# ── OpenAI dialect ──────────────────────────────────────────────────────────
def openai_body(text, calls):
    msg = {"role": "assistant", "content": text or None}
    if calls:
        msg["tool_calls"] = [{
            "id": cid, "type": "function",
            "function": {"name": name, "arguments": json.dumps(args)},
        } for cid, name, args in calls]
    return {
        "id": "chatcmpl-mock", "object": "chat.completion", "created": 0,
        "model": "mock-model",
        "choices": [{"index": 0, "message": msg,
                     "finish_reason": "tool_calls" if calls else "stop"}],
        "usage": {"prompt_tokens": 120, "completion_tokens": 20, "total_tokens": 140},
    }


def openai_sse(text, calls):
    """Split a completion into OpenAI-style streaming deltas."""
    frames = []

    def frame(delta, finish_reason=None):
        return {"id": "chatcmpl-mock", "object": "chat.completion.chunk", "created": 0,
                "model": "mock-model",
                "choices": [{"index": 0, "delta": delta, "finish_reason": finish_reason}]}

    if text:
        mid = max(1, len(text) // 2)
        frames.append(frame({"role": "assistant", "content": text[:mid]}))
        frames.append(frame({"content": text[mid:]}))

    for i, (cid, name, args) in enumerate(calls):
        # Header frame carries id + name; arguments then arrive in pieces.
        frames.append(frame({"tool_calls": [{
            "index": i, "id": cid, "type": "function",
            "function": {"name": name, "arguments": ""}}]}))
        raw = json.dumps(args)
        mid = max(1, len(raw) // 2)
        for piece in (raw[:mid], raw[mid:]):
            frames.append(frame({"tool_calls": [{"index": i, "function": {"arguments": piece}}]}))

    frames.append(frame({}, "tool_calls" if calls else "stop"))
    usage = frame({}, None)
    usage["choices"] = []
    usage["usage"] = {"prompt_tokens": 120, "completion_tokens": 20, "total_tokens": 140}
    frames.append(usage)
    return frames


# ── Anthropic dialect ───────────────────────────────────────────────────────
def anthropic_body(text, calls):
    content = []
    if text:
        content.append({"type": "text", "text": text})
    for cid, name, args in calls:
        content.append({"type": "tool_use", "id": cid, "name": name, "input": args})
    return {
        "id": "msg_mock", "type": "message", "role": "assistant", "model": "mock-model",
        "content": content,
        "stop_reason": "tool_use" if calls else "end_turn",
        "usage": {"input_tokens": 120, "output_tokens": 20},
    }


def anthropic_sse(text, calls):
    frames = [{"type": "message_start", "message": {
        "id": "msg_mock", "type": "message", "role": "assistant", "content": [],
        "model": "mock-model", "usage": {"input_tokens": 120, "output_tokens": 0}}}]

    index = 0
    if text:
        frames.append({"type": "content_block_start", "index": index,
                       "content_block": {"type": "text", "text": ""}})
        mid = max(1, len(text) // 2)
        for piece in (text[:mid], text[mid:]):
            frames.append({"type": "content_block_delta", "index": index,
                           "delta": {"type": "text_delta", "text": piece}})
        frames.append({"type": "content_block_stop", "index": index})
        index += 1

    for cid, name, args in calls:
        frames.append({"type": "content_block_start", "index": index,
                       "content_block": {"type": "tool_use", "id": cid, "name": name, "input": {}}})
        raw = json.dumps(args)
        mid = max(1, len(raw) // 2)
        for piece in (raw[:mid], raw[mid:]):
            frames.append({"type": "content_block_delta", "index": index,
                           "delta": {"type": "input_json_delta", "partial_json": piece}})
        frames.append({"type": "content_block_stop", "index": index})
        index += 1

    frames.append({"type": "message_delta",
                   "delta": {"stop_reason": "tool_use" if calls else "end_turn"},
                   "usage": {"output_tokens": 20}})
    frames.append({"type": "message_stop"})
    return frames


# ── server ──────────────────────────────────────────────────────────────────
def log_request(req, anthropic):
    tools = req.get("tools") or []
    names = sorted((t.get("name") or t.get("function", {}).get("name") or "") for t in tools)
    roles = [m.get("role") for m in req.get("messages", [])]
    record = {
        "dialect": "anthropic" if anthropic else "openai",
        "n_messages": len(req.get("messages", [])),
        "n_tools": len(tools),
        "tool_names": names,
        "model": req.get("model"),
        "stream": req.get("stream", False),
        "roles": roles,
        # Anthropic carries the system prompt top-level, OpenAI as a message.
        "has_system": bool(req.get("system")) or "system" in roles,
    }
    try:
        path = sys.argv[2] if len(sys.argv) > 2 else "requests.jsonl"
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record, ensure_ascii=False) + "\n")
    except Exception as exc:  # never let logging kill the handler
        print("log error:", exc, file=sys.stderr)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _send_json(self, body):
        payload = json.dumps(body).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _send_sse(self, frames):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        for f in frames:
            self.wfile.write(("data: " + json.dumps(f) + "\n\n").encode("utf-8"))
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length).decode("utf-8", "replace")
        try:
            req = json.loads(raw)
        except Exception:
            req = {}

        anthropic = self.path.rstrip("/").endswith("/messages")

        # Readiness probes carry no messages; answer without consuming a turn.
        if not req.get("messages"):
            self._send_json(anthropic_body("ok", []) if anthropic else openai_body("ok", []))
            return

        log_request(req, anthropic)
        STATE["turn"] += 1
        text, calls = script(STATE["turn"])

        if req.get("stream"):
            self._send_sse(anthropic_sse(text, calls) if anthropic else openai_sse(text, calls))
            return
        self._send_json(anthropic_body(text, calls) if anthropic else openai_body(text, calls))


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
