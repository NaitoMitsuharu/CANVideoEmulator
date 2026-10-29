#!/usr/bin/env bash
set -euo pipefail

interface="${1:-can0}"
if [[ "$(id -u)" -ne 0 ]]; then
  echo "run as root: sudo $0 $interface" >&2
  exit 2
fi

modprobe peak_usb
modprobe can
modprobe can_raw

ip link set "$interface" down
ip link set "$interface" type can bitrate 500000 restart-ms 0
ip link set "$interface" up
ip -details -statistics link show dev "$interface"
