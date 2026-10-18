#!/bin/sh
# 为当前用户安装 Coder 本地 Agent 的 LaunchAgent；路径在安装时展开，不使用占位符。
set -eu

CODER_BIN="${CODER_BIN:-$HOME/.local/bin/coder}"
AGENT_CONFIG="${AGENT_CONFIG:-$HOME/.config/coder/agent.toml}"
LABEL="com.nexsteaduser.coder-agent"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

if [ ! -x "$CODER_BIN" ]; then
    echo "coder binary not executable: $CODER_BIN" >&2
    exit 1
fi
if [ ! -f "$AGENT_CONFIG" ]; then
    echo "agent config not found: $AGENT_CONFIG" >&2
    exit 1
fi

mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs/Coder"

cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$CODER_BIN</string>
        <string>agent</string>
        <string>run</string>
        <string>--config</string>
        <string>$AGENT_CONFIG</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>ThrottleInterval</key>
    <integer>10</integer>
    <key>ProcessType</key>
    <string>Background</string>
    <key>StandardOutPath</key>
    <string>$HOME/Library/Logs/Coder/agent.stdout.log</string>
    <key>StandardErrorPath</key>
    <string>$HOME/Library/Logs/Coder/agent.stderr.log</string>
</dict>
</plist>
EOF

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo "installed $LABEL"
