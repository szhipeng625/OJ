#!/usr/bin/env python3
"""Patch cloud_judge_api.py: add non-SSE polling endpoint + result TTL cleanup."""
import sys

path = '/home/admin/eval-worker/cloud_judge_api.py'
src = open(path, encoding='utf-8').read()

if '/api/cloud_judge_result' in src:
    print('ALREADY PATCHED')
    sys.exit(0)

# 1) add timestamp dict next to _results
old1 = '_results = {}\n_results_lock = threading.Lock()'
new1 = '_results = {}\n_results_ts = {}\n_results_lock = threading.Lock()'
assert old1 in src, 'marker1 missing'
src = src.replace(old1, new1, 1)

# 2) record timestamp when a result arrives
old2 = '                        with _results_lock:\n                            _results[tid] = r'
new2 = ('                        with _results_lock:\n'
        '                            _results[tid] = r\n'
        '                            _results_ts[tid] = time.time()')
assert old2 in src, 'marker2 missing'
src = src.replace(old2, new2, 1)

# 3) insert polling endpoint before do_POST
old3 = '    def do_POST(self):'
endpoint = ('        elif parsed.path == "/api/cloud_judge_result":\n'
            '            task_id = qs.get("task_id", [""])[0]\n'
            '            if not task_id:\n'
            '                self._send_json(400, {"error": "task_id required"})\n'
            '                return\n'
            '            now = time.time()\n'
            '            with _results_lock:\n'
            '                evt = _results.get(task_id)\n'
            '                stale = [k for k, t in _results_ts.items() if now - t > 600]\n'
            '                for k in stale:\n'
            '                    _results.pop(k, None)\n'
            '                    _results_ts.pop(k, None)\n'
            '            if evt is None:\n'
            '                self._send_json(200, {"task_id": task_id, "status": "pending", "phase": "queued"})\n'
            '            else:\n'
            '                self._send_json(200, evt)\n'
            '\n'
            '    def do_POST(self):')
assert old3 in src, 'marker3 missing'
src = src.replace(old3, endpoint, 1)

open(path, 'w', encoding='utf-8').write(src)
print('PATCHED OK')
