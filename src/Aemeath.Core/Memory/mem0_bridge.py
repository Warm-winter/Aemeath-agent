#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Aemeath <-> Mem0 长驻桥接进程（JSON-RPC over stdio）。

协议：每行一个 JSON 请求：{"id": "<str>", "op": "<op>", "args": {...}}
返回一行 JSON 响应：{"id": "<str>", "ok": true/false, "result": ... / "error": "..."}

受支持的操作（与 C# 侧 Mem0Client 对齐）：
  - "ping"          -> {"ready": true}
  - "health"        -> {"mem0_importable": bool, "error": str|None}
  - "add"           -> args: {messages, user_id, run_id?, agent_id?, infer?}  -> Mem0.add 结果
  - "search"        -> args: {query, user_id, run_id?, agent_id?, top_k?}     -> Mem0.search 结果
  - "get_all"       -> args: {user_id, run_id?, agent_id?, top_k?}            -> Mem0.get_all 结果
  - "delete"        -> args: {memory_id}                                      -> Mem0.delete 结果
  - "delete_all"    -> args: {user_id?, run_id?, agent_id?}                   -> Mem0.delete_all 结果
  - "shutdown"      -> 优雅退出

配置通过环境变量传入（由 C# 侧启动子进程时设置）：
  AEMEATH_MEM0_DIR            Mem0 数据目录（向量库 + history.db），必填
  AEMEATH_MEM0_LLM_MODEL      LLM 模型名（OpenAI 兼容），必填
  AEMEATH_MEM0_LLM_BASE_URL   LLM OpenAI 兼容 endpoint，必填
  AEMEATH_MEM0_LLM_API_KEY    LLM api key，必填
  AEMEATH_MEM0_EMBED_MODEL    embedding 模型名
  AEMEATH_MEM0_EMBED_BASE_URL embedding OpenAI 兼容 endpoint（缺省回退到 LLM 的）
  AEMEATH_MEM0_EMBED_API_KEY  embedding api key（缺省回退到 LLM 的）
  AEMEATH_MEM0_EMBED_DIMS     embedding 维度（默认 1536，需与 vector_store 一致）
  AEMEATH_MEM0_VECTOR_PROVIDER 向量库 provider（默认 qdrant）
  AEMEATH_MEM0_VECTOR_PATH    向量库本地路径（默认 <MEM0_DIR>/qdrant）

本文件由 Aemeath 在启动时从内嵌资源覆盖写出，请勿手动编辑。
"""
from __future__ import annotations

import json
import os
import sys
import threading
import traceback

# Windows 控制台 UTF-8：避免中文记忆读写乱码
if sys.platform.startswith("win"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

_LOCK = threading.Lock()
_MEMORY = None  # 延迟初始化
_INIT_ERROR = None


def _build_config() -> dict:
    data_dir = os.environ.get("AEMEATH_MEM0_DIR") or _default_data_dir()
    llm_model = os.environ.get("AEMEATH_MEM0_LLM_MODEL", "gpt-4o-mini")
    llm_base = os.environ.get("AEMEATH_MEM0_LLM_BASE_URL") or "https://api.openai.com/v1"
    llm_key = os.environ.get("AEMEATH_MEM0_LLM_API_KEY", "")

    embed_model = os.environ.get("AEMEATH_MEM0_EMBED_MODEL", "text-embedding-3-small")
    embed_base = os.environ.get("AEMEATH_MEM0_EMBED_BASE_URL") or llm_base
    embed_key = os.environ.get("AEMEATH_MEM0_EMBED_API_KEY") or llm_key
    embed_dims = int(os.environ.get("AEMEATH_MEM0_EMBED_DIMS", "1536"))

    vector_provider = os.environ.get("AEMEATH_MEM0_VECTOR_PROVIDER", "qdrant")
    vector_path = os.environ.get("AEMEATH_MEM0_VECTOR_PATH") or os.path.join(data_dir, "qdrant")

    os.makedirs(data_dir, exist_ok=True)
    os.makedirs(vector_path, exist_ok=True)

    return {
        "vector_store": {
            "provider": vector_provider,
            "config": {
                "collection_name": "aemeath",
                "embedding_model_dims": embed_dims,
                "path": vector_path,
            },
        },
        "llm": {
            "provider": "openai",
            "config": {
                "model": llm_model,
                "openai_base_url": llm_base,
                "api_key": llm_key,
            },
        },
        "embedder": {
            "provider": "openai",
            "config": {
                "model": embed_model,
                "openai_base_url": embed_base,
                "api_key": embed_key,
                "embedding_dims": embed_dims,
            },
        },
        # history.db 放到数据目录，避免污染用户主目录
        "history_db_path": os.path.join(data_dir, "history.db"),
    }


def _default_data_dir() -> str:
    base = os.environ.get("APPDATA")
    if base:
        return os.path.join(base, "Aemeath", "mem0")
    return os.path.join(os.path.expanduser("~"), ".aemeath", "mem0")


def _ensure_memory():
    """延迟导入并构造 Memory 单例。失败则记录错误，后续请求统一报错。"""
    global _MEMORY, _INIT_ERROR
    if _MEMORY is not None:
        return _MEMORY
    with _LOCK:
        if _MEMORY is not None:
            return _MEMORY
        try:
            # 延迟导入：这样 health 检查可以在 mem0ai 未安装时优雅返回
            from mem0 import Memory  # type: ignore

            _MEMORY = Memory.from_config(_build_config())
            _INIT_ERROR = None
            return _MEMORY
        except Exception as ex:  # noqa: BLE001
            _INIT_ERROR = f"{type(ex).__name__}: {ex}"
            raise


def _health() -> dict:
    """只检测 mem0ai 是否可导入，不构造 Memory（避免未配置时崩溃）。"""
    try:
        import importlib

        importlib.import_module("mem0")  # noqa: F401
        return {"mem0_importable": True, "error": None}
    except Exception as ex:  # noqa: BLE001
        return {"mem0_importable": False, "error": f"{type(ex).__name__}: {ex}"}


def _normalize_messages(messages):
    """把字符串/单条 dict 统一成 list[dict]，兼容 Mem0.add 的 messages 入参。"""
    if messages is None:
        return []
    if isinstance(messages, str):
        return [{"role": "user", "content": messages}]
    if isinstance(messages, dict):
        return [messages]
    if isinstance(messages, list):
        return messages
    return []


def _filters(args: dict) -> dict:
    """Mem0 search/get_all 要求 user_id/agent_id/run_id 放进 filters。"""
    filters = {}
    for key in ("user_id", "agent_id", "run_id"):
        val = args.get(key)
        if val:
            filters[key] = val
    return filters


def _op_add(m, args):
    messages = _normalize_messages(args.get("messages"))
    if not messages:
        raise ValueError("add 需要 messages 参数")
    kwargs = {"infer": bool(args.get("infer", True))}
    for key in ("user_id", "agent_id", "run_id"):
        val = args.get(key)
        if val:
            kwargs[key] = val
    if "metadata" in args and args["metadata"]:
        kwargs["metadata"] = args["metadata"]
    return m.add(messages, **kwargs)


def _op_search(m, args):
    query = args.get("query")
    if not query:
        raise ValueError("search 需要 query 参数")
    filters = _filters(args)
    if not filters:
        raise ValueError("search 至少需要 user_id / agent_id / run_id 之一")
    kwargs = {"filters": filters}
    if "top_k" in args:
        kwargs["top_k"] = int(args["top_k"])
    if "threshold" in args:
        kwargs["threshold"] = float(args["threshold"])
    return m.search(query, **kwargs)


def _op_get_all(m, args):
    filters = _filters(args)
    if not filters:
        raise ValueError("get_all 至少需要 user_id / agent_id / run_id 之一")
    kwargs = {"filters": filters}
    if "top_k" in args:
        kwargs["top_k"] = int(args["top_k"])
    return m.get_all(**kwargs)


def _op_delete(m, args):
    memory_id = args.get("memory_id")
    if not memory_id:
        raise ValueError("delete 需要 memory_id 参数")
    return m.delete(memory_id)


def _op_delete_all(m, args):
    kwargs = {}
    for key in ("user_id", "agent_id", "run_id"):
        val = args.get(key)
        if val:
            kwargs[key] = val
    return m.delete_all(**kwargs)


_OPS = {
    "add": _op_add,
    "search": _op_search,
    "get_all": _op_get_all,
    "delete": _op_delete,
    "delete_all": _op_delete_all,
}


def _handle_request(req: dict) -> dict:
    req_id = req.get("id")
    op = req.get("op")
    try:
        if op == "ping":
            return {"id": req_id, "ok": True, "result": {"ready": True}}
        if op == "health":
            return {"id": req_id, "ok": True, "result": _health()}
        if op == "shutdown":
            # 由主循环检测并退出
            return {"id": req_id, "ok": True, "result": {"shutting_down": True}}

        if op not in _OPS:
            return {"id": req_id, "ok": False, "error": f"未知操作: {op}"}

        try:
            m = _ensure_memory()
        except Exception:
            return {
                "id": req_id,
                "ok": False,
                "error": f"Mem0 未就绪：{_INIT_ERROR or '初始化失败'}。请在设置中安装 mem0 依赖。",
            }

        result = _OPS[op](m, req.get("args") or {})
        return {"id": req_id, "ok": True, "result": _to_jsonable(result)}
    except Exception as ex:  # noqa: BLE001
        return {
            "id": req_id,
            "ok": False,
            "error": f"{type(ex).__name__}: {ex}",
            "trace": traceback.format_exc(limit=3),
        }


def _to_jsonable(obj):
    """把 pydantic / datetime / set 等转成纯 JSON 友好结构。"""
    try:
        return json.loads(json.dumps(obj, default=_default_serializer, ensure_ascii=False))
    except Exception:
        return str(obj)


def _default_serializer(o):
    if hasattr(o, "model_dump"):
        return o.model_dump()
    if hasattr(o, "__dict__"):
        return {k: v for k, v in o.__dict__.items() if not k.startswith("_")}
    if hasattr(o, "isoformat"):
        return o.isoformat()
    return str(o)


def _emit(obj: dict) -> None:
    line = json.dumps(obj, ensure_ascii=False, default=_default_serializer)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def main() -> int:
    # 启动握手：立刻回一个 ready 信号，让 C# 侧确认进程已拉起。
    _emit({"id": "__hello__", "ok": True, "result": {"bridge": "aemeath-mem0", "version": 1}})
    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            req = json.loads(raw)
        except Exception as ex:  # noqa: BLE001
            _emit({"id": None, "ok": False, "error": f"JSON 解析失败: {ex}"})
            continue
        if req.get("op") == "shutdown":
            _emit({"id": req.get("id"), "ok": True, "result": {"shutting_down": True}})
            break
        _emit(_handle_request(req))
    return 0


if __name__ == "__main__":
    sys.exit(main())
