#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Aemeath <-> UFO 桥接脚本：把 UFO 的 SessionFactory/SessionPool 包装成
「单次任务执行 + 打印 JSON 结果」的命令行入口，供 C#（Aemeath.ComputerControl.UfoRunner）
通过子进程调用。

用法：
    python ufo_runner.py "<自然语言任务>" [--task-name <name>] [--config-dir <UFO config 目录>]

工作流：
    1. 设置 UFO 用到的配置目录（UFO_CONFIG_DIR / 改写 config 路径）
    2. 调用 UFO 的 SessionFactory 创建 normal 模式 session
    3. SessionPool.run_all() 跑完整个 ReAct 循环
    4. 打印一行 JSON：{"success": bool, "complete": "yes"/"no", "message": str}

设计要点（来自 UFO 源码分析）：
    - 必须 -r 传 request，否则 UFO 会进交互式 stdin 阻塞（子进程无 TTY 会死锁）
    - 关掉 UFO 的逐步骤 SAFE_GUARD：子进程里 Confirm.ask 会卡死；
      确认改由 Aemeath 侧的任务级前置确认卡片负责
    - UFO 不是 PyPI 包，本脚本假定已经 git clone UFO 到某目录并 pip install -r requirements.txt，
      调用方通过 PYTHONPATH 或 UFO 安装目录来 import ufo

本文件由 Aemeath 从内嵌资源释放到 %AppData%\Aemeath\tools\ufo-bridge\。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import traceback


def _patch_ufo_safeguard_off():
    """尽力把 UFO 的逐步骤确认关掉，避免子进程在 Confirm.ask 上死锁。

    UFO 的 sensitive_step_asker 在 ufo/module/interactor.py 里。
    确认责任已移交给 Aemeath 侧的任务级前置确认卡片。
    """
    try:
        from ufo.module import interactor  # type: ignore

        def _no_ask(*_args, **_kwargs):
            return True  # 始终放行，由 Aemeath 侧已做任务级确认

        interactor.sensitive_step_asker = _no_ask
    except Exception:
        # 找不到模块或打补丁失败时忽略：UFO 默认 SAFE_GUARD 仍可能生效，
        # 但 Aemeath 侧会设 SAFE_GUARD=False（通过 system.yaml）。
        pass


def run(request: str, task_name: str) -> dict:
    _patch_ufo_safeguard_off()

    try:
        from ufo.module.session_pool import SessionFactory, SessionPool  # type: ignore
    except Exception as ex:
        return {
            "success": False,
            "complete": "no",
            "message": f"无法导入 UFO（请确认已 git clone UFO 并 pip install -r requirements.txt）：{ex}",
        }

    try:
        sessions = SessionFactory().create_session(
            task=task_name,
            mode="normal",
            plan="",
            request=request,
        )
        pool = SessionPool(sessions)
        import asyncio

        asyncio.run(pool.run_all())

        session = sessions[0] if sessions else None
        complete = "no"
        if session is not None:
            results = getattr(session, "results", {}) or {}
            complete = results.get("complete", "no")

        return {
            "success": complete == "yes",
            "complete": complete,
            "message": f"任务{'完成' if complete == 'yes' else '未完成'}（task={task_name}）。",
        }
    except Exception as ex:  # noqa: BLE001
        return {
            "success": False,
            "complete": "no",
            "message": f"UFO 执行异常：{type(ex).__name__}: {ex}",
            "trace": traceback.format_exc(limit=4),
        }


def main() -> int:
    parser = argparse.ArgumentParser(description="Aemeath UFO runner")
    parser.add_argument("request", help="自然语言任务")
    parser.add_argument("--task-name", default=None, help="任务名（同时是日志目录）")
    parser.add_argument("--config-dir", default=None, help="UFO config 目录（含 system.yaml/agents.yaml）")
    args = parser.parse_args()

    if args.config_dir:
        # UFO 用 UFO_CONFIG_DIR 环境变量定位配置（若该版本支持）
        os.environ.setdefault("UFO_CONFIG_DIR", args.config_dir)

    task_name = args.task_name or f"aemeath_{os.getpid()}"
    result = run(args.request, task_name)
    sys.stdout.write(json.dumps(result, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    return 0 if result.get("success") else 1


if __name__ == "__main__":
    sys.exit(main())
