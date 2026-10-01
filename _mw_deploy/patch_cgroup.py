#!/usr/bin/env python3
"""Fix cgroup root detection: prefer cloud_judge.service (has Delegate=yes),
not cloud_judge_api.service."""
import sys

path = '/home/admin/eval-worker/src/cgroup_manager.cpp'
src = open(path, encoding='utf-8').read()

old = '''static std::string detect_cg_root() {
    const std::string delegate_path = "/sys/fs/cgroup/system.slice/cloud_judge_api.service";
    const std::string delegate_path_alt = "/sys/fs/cgroup/system.slice/cloud_judge.service";'''

new = '''static std::string detect_cg_root() {
    const std::string delegate_path = "/sys/fs/cgroup/system.slice/cloud_judge.service";
    const std::string delegate_path_alt = "/sys/fs/cgroup/system.slice/cloud_judge_api.service";'''

if 'cloud_judge.service";' in old and old in src:
    src = src.replace(old, new, 1)
    open(path, 'w', encoding='utf-8').write(src)
    print('PATCHED: swapped cgroup root priority')
elif 'delegate_path = "/sys/fs/cgroup/system.slice/cloud_judge.service"' in src:
    print('ALREADY FIXED')
else:
    print('MARKER NOT FOUND - manual fix needed')
    sys.exit(1)
