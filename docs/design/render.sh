#!/bin/bash
# render.sh <screen> <out.png> : 1536x864 CSS px at 125% = 1920x1080, like the target laptop
cd "$(dirname "$0")"
"${CHROME:-chrome-headless-shell}" --no-sandbox --disable-gpu --hide-scrollbars \
  --force-device-scale-factor=1.25 --window-size=1536,864 \
  --virtual-time-budget=1500 --screenshot="$2" "file://$PWD/mock.html#$1" 2>/dev/null
