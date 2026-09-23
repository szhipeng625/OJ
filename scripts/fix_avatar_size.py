# -*- coding: utf-8 -*-
# One-off: shrink the oversized avatar to 256x256 JPEG and re-upload via /api/profile.
# Root cause of slow startup/login: whoami & login return the full base64 avatar every time.
import base64
import io
import json
import sys
import urllib.request

from PIL import Image

MW = "http://47.253.41.10:8899"
SESSION = r"d:\OJ\client\bin\Debug\net8.0-windows\ojdata\session.txt"

tok = open(SESSION, encoding="utf-8").read().strip()
if not tok:
    print("no token")
    sys.exit(1)

# 1. read whoami response (prefer local full copy already downloaded; avoids flaky re-fetch)
import os
whoami_file = os.path.join(os.environ.get("TEMP", "."), "whoami_resp.txt")
if os.path.exists(whoami_file):
    raw = open(whoami_file, encoding="utf-8").read()
else:
    req = urllib.request.Request(MW + "/api/whoami", headers={"Authorization": "Bearer " + tok})
    raw = urllib.request.urlopen(req, timeout=180).read().decode("utf-8")
j = json.loads(raw)
if not j.get("ok"):
    print("whoami failed:", j)
    sys.exit(1)
nick = j.get("nickname", "")
avatar = j.get("avatar", "")
print("original avatar chars:", len(avatar))

# 2. resize + compress
b64 = avatar.split(",", 1)[1] if "," in avatar else avatar
img = Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGB")
w, h = img.size
s = min(w, h)
img = img.crop(((w - s) // 2, (h - s) // 2, (w + s) // 2, (h + s) // 2))
img = img.resize((256, 256), Image.ANTIALIAS)
buf = io.BytesIO()
img.save(buf, "JPEG", quality=80)
new_avatar = "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()
print("new avatar chars:", len(new_avatar))

# 3. upload
body = json.dumps({"nickname": nick, "avatar": new_avatar}).encode("utf-8")
req2 = urllib.request.Request(
    MW + "/api/profile",
    data=body,
    method="POST",
    headers={"Content-Type": "application/json", "Authorization": "Bearer " + tok},
)
resp = urllib.request.urlopen(req2, timeout=180)
print("profile response:", resp.read().decode("utf-8"))
