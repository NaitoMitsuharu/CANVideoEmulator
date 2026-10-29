#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <interface> <can-id|e:can-id> <hex-data|->" >&2
  echo "Example: $0 can0 123 1122334455667788" >&2
}

if [[ $# -ne 3 ]]; then
  usage
  exit 2
fi

interface="$1"
can_id="$2"
data="${3^^}"

if ! command -v cansend >/dev/null 2>&1; then
  echo "cansend was not found; install it with: sudo apt install can-utils" >&2
  exit 2
fi

if [[ ! "$interface" =~ ^[[:alnum:]_.:-]+$ ]]; then
  echo "Invalid interface name: $interface" >&2
  exit 2
fi

extended=0
case "$can_id" in
  e:*|E:*)
    extended=1
    can_id="${can_id:2}"
    ;;
  0x*|0X*)
    can_id="${can_id:2}"
    ;;
esac

if [[ ! "$can_id" =~ ^[[:xdigit:]]{1,8}$ ]]; then
  echo "CAN ID must be 1-8 hexadecimal digits, optionally prefixed with e: or 0x." >&2
  exit 2
fi

id_value=$((16#$can_id))
if (( extended )); then
  if (( id_value > 0x1fffffff )); then
    echo "Extended CAN ID exceeds 0x1FFFFFFF." >&2
    exit 2
  fi
  printf -v can_id '%08X' "$id_value"
else
  if (( id_value > 0x7ff )); then
    if (( id_value > 0x1fffffff )); then
      echo "CAN ID exceeds 0x1FFFFFFF." >&2
      exit 2
    fi
    extended=1
    printf -v can_id '%08X' "$id_value"
  else
    printf -v can_id '%03X' "$id_value"
  fi
fi

if [[ "$data" == "-" ]]; then
  data=""
elif [[ ! "$data" =~ ^([[:xdigit:]]{2}){0,8}$ ]]; then
  echo "Data must be an even number of hexadecimal digits (0-8 bytes), or '-'." >&2
  exit 2
fi

if ! ip link show dev "$interface" >/dev/null 2>&1; then
  echo "SocketCAN interface not found: $interface" >&2
  exit 3
fi

frame="${can_id}#${data}"
echo "Sending once on $interface: $frame"
exec cansend "$interface" "$frame"
