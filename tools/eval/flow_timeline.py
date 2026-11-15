#!/usr/bin/env python3
"""Rebuild a glasses lesson run from a phone log capture and the portal's turn log, and score it.

Usage:
  python3 tools/eval/flow_timeline.py DEVICE.log [--portal portal/agent/logs/YYYY-MM-DD.jsonl]
                                      [--from HH:MM:SS] [--to HH:MM:SS] [--utc-offset 7]

DEVICE.log comes from tools/eval/capture-device-log.sh. Times are the phone's local time; the
portal logs UTC, so --utc-offset is the phone's offset from UTC in hours (the S25 is on UTC+7).

What each line kind means:
  TOUCH    first touch on a window (Android logs only changes of window, not every tap)
  BUTTON   a phone-controller button handler that logged something (e.g. StartLive)
  XR       XR view paused/resumed, glasses display surface created (the native crash site)
  LISTEN   the speech recognizer started; HEARD = how that attempt ended
  PORTAL   a lesson turn on the portal (SAID = transcript sent by the glasses)
  QUIT     XREAL closed the app itself (glasses disconnect or runtime force-quit)
  POWER    the phone started/stopped supplying power to the glasses (USB OTG); BATTERY = phone battery
  DEATH / KILL / FATAL  process exits; the scorecard explains each one
"""
import argparse
import json
import re
from collections import Counter
from datetime import datetime, timedelta

APP = "com.immersethebay.app"
LINE = re.compile(r"^\d\d-\d\d (\d\d:\d\d:\d\d\.\d{3})\s+(\d+)\s+(\d+)\s+([VDIWEF])\s+([^:]*?)\s*: (.*)$")
# XREAL reports these as Failure, which it ignores (no popup, no quit).
NOISE = re.compile(r"FrameWait|OnErrorCallback Failure$")


def read_device(path, keep):
    app_pids, last_frame, last_button = set(), {}, {}
    for raw in open(path, errors="replace"):
        m = LINE.match(raw.rstrip("\n"))
        if not m:
            continue
        t, pid, _tid, level, tag, msg = m.groups()
        ours = pid in app_pids
        if tag == "am_proc_start" and APP in msg:
            app_pids.add(msg.split(",")[1])
            keep(t, "PROCESS", "app started: " + msg.split("{")[-1].rstrip("}]"))
        elif tag in ("am_proc_died", "am_crash", "am_anr") and APP in msg:
            keep(t, "DEATH", f"{tag} {msg}")
        elif tag == "am_kill" and APP in msg:
            keep(t, "KILL", msg)
        elif tag == "wm_destroy_activity" and APP in msg and "remove-by-pid" in msg:
            keep(t, "KILL", "remove task (Recents swipe closed " + msg.split(",")[3].split("/")[-1] + ")")
        elif tag == "input_interaction":
            window = re.search(r"\[\w+ ([^,\]]+)", msg)
            name = window.group(1) if window else msg[:60]
            if not re.match(r"NavigationBar|StatusBar|NotificationShade|Monitor", name):
                keep(t, "TOUCH", name)
        elif tag == "wm_create_activity" and APP in msg:
            keep(t, "ACTIVITY", "opened " + msg.split(",")[3].split("/")[-1])
        elif tag == "DisplayDeviceRepository" and "Air 2 Ultra" in msg and re.search(r"device (added|removed)", msg):
            keep(t, "GLASSES", "display " + re.search(r"device (added|removed)", msg).group(1))
        elif tag == "UsbPortManager" and "USB port changed" in msg:
            mode = re.search(r"currentMode=(\w+)", msg)
            keep(t, "USB", "port " + ("disconnected" if mode and mode.group(1) == "none" else "connected (" + (mode.group(1) if mode else "?") + ")"))
        elif tag == "UsbHostManager" and "DEVNAME=usb_otg" in msg and re.search(r"STATE=(SOURCE|SINK)", msg):
            on = "STATE=SOURCE" in msg
            keep(t, "POWER", "phone powering the glasses" if on else "phone STOPPED powering the glasses")
        elif tag == "battery_level":
            keep(t, "BATTERY", f"{msg.strip('[]').split(',')[0]}%")
        elif "show low battery warning" in msg:
            keep(t, "BATTERY", "low-battery warning " + re.search(r"level=\d+", msg).group(0))
        elif tag == "RecognitionClient" and msg.startswith("#startListening"):
            keep(t, "LISTEN", "recognizer started")
        elif tag == "RecognitionClient" and msg.startswith("#onResults"):
            keep(t, "HEARD", "text recognised" if "withSpeech: true" in msg else "empty result")
        elif tag == "RecognitionClient" and msg.startswith("#onError"):
            keep(t, "HEARD", "error " + msg.split("space ")[-1][:60])
        elif tag == "Unity" and ours:
            if NOISE.search(msg):
                continue
            if "OnApplicationPause:" in msg:
                keep(t, "XR", "paused" if "True" in msg else "resumed")
            elif "[NativeRendering] CreateSwapchainEx" in msg:
                keep(t, "XR", "glasses display surface created")
            elif "[XREALPlugin] ForceKill" in msg or "[XREALPlugin] Quit" in msg:
                keep(t, "QUIT", "XREAL closed the app: " + msg)
            elif re.search(r"\[(Learning|Visko|Speech|XREALErrorReceiver)\]|Exception", msg):
                keep(t, "UNITY", msg[:150])
            elif re.match(r"ImmerseTheBay\.\S+:(\w+)\(\)", msg):
                last_frame[pid] = re.match(r"ImmerseTheBay\.\S+:(\w+)\(\)", msg).group(1)
            elif msg.startswith("UnityEngine.Events.UnityEvent:Invoke") and last_frame.get(pid):
                handler = last_frame.pop(pid)
                # One tap can log several warnings, each with its own stack trace.
                if last_button.get(pid, ("", ""))[0] != handler or t[:8] != last_button[pid][1][:8]:
                    keep(t, "BUTTON", f"{handler} (tap on the phone controller)")
                last_button[pid] = (handler, t)
            elif msg == "":
                last_frame.pop(pid, None)
        elif ours and level in ("E", "F") and re.search(r"Fatal signal|SIGSEGV|SIGABRT", msg):
            keep(t, "FATAL", f"{tag}: {msg[:150]}")
        elif tag in ("DEBUG", "libc") and "Fatal signal" in msg and APP in msg:
            keep(t, "FATAL", f"{tag}: {msg[:150]}")


UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def read_portal(path, keep, offset, all_sessions):
    for raw in open(path):
        if not raw.strip():
            continue
        row = json.loads(raw)
        # Glasses (and web live) runs use the Reactor session UUID; practice-mode and simulator runs do not.
        if not all_sessions and not UUID.match(row.get("sessionId", "")):
            continue
        at = datetime.strptime(row["at"][:19], "%Y-%m-%dT%H:%M:%S") + offset
        resp = row.get("response") or {}
        speech = resp.get("speech") if isinstance(resp.get("speech"), dict) else {}
        score = (resp.get("feedback") or {}).get("score") if isinstance(resp.get("feedback"), dict) else None
        detail = f"turn {row['sessionId'][:8]} beat={row.get('beat')}"
        if row.get("transcript"):
            detail += f" SAID={row['transcript']!r} score={score}"
        if (row.get("context") or {}).get("gesture"):
            detail += f" gesture={row['context']['gesture']}"
        detail += f" advance={resp.get('advance')} partner_line={'yes' if speech.get('text') else 'no'}"
        keep(at.strftime("%H:%M:%S.") + row["at"][20:23], "PORTAL", detail)


def explain_exit(text):
    if "remove task" in text:
        return "swiped away in Recents"
    if "installPackageLI" in text:
        return "app reinstalled"
    if "am_crash" in text or "am_anr" in text:
        return "Java crash or ANR"
    return "no kill record: check for QUIT or FATAL just before"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("device_log")
    parser.add_argument("--portal", help="portal/agent/logs/YYYY-MM-DD.jsonl")
    parser.add_argument("--from", dest="start", default="00:00:00")
    parser.add_argument("--to", dest="end", default="99:99:99")
    parser.add_argument("--utc-offset", type=float, default=7, help="phone offset from UTC in hours")
    parser.add_argument("--all-sessions", action="store_true", help="include portal turns that are not from a video session")
    args = parser.parse_args()

    events = []
    def keep(t, kind, text):
        if args.start <= t[:8] <= args.end:
            events.append((t, kind, text))
    read_device(args.device_log, keep)
    if args.portal:
        read_portal(args.portal, keep, timedelta(hours=args.utc_offset), args.all_sessions)

    previous, count, first = None, 0, None
    def flush():
        if previous:
            print(f"{first}  {previous[0]:8} {previous[1]}" + (f"  (x{count})" if count > 1 else ""))
    for t, kind, text in sorted(events):
        if (kind, text) == previous:
            count += 1
            continue
        flush()
        previous, count, first = (kind, text), 1, t
    flush()

    kinds = Counter(kind for _, kind, _ in events)
    heard = [text for _, kind, text in events if kind == "HEARD" and not text.startswith("error")]
    said = [text for _, kind, text in events if kind == "PORTAL" and "SAID=" in text]
    print("\n== scorecard ==")
    print(f"recognizer starts {kinds['LISTEN']}: {heard.count('text recognised')} heard words, {heard.count('empty result')} heard nothing")
    print(f"spoken answers that reached the portal: {len(said)} of {kinds['PORTAL']} turns")
    print(f"phone-controller button handlers run: {kinds['BUTTON']}   XR pauses: {sum(1 for _, k, x in events if k == 'XR' and x == 'paused')}")
    print(f"glasses USB disconnects: {sum(1 for _, k, x in events if k == 'USB' and 'disconnected' in x)}   XREAL quits: {kinds['QUIT']}   native crashes: {kinds['FATAL']}")
    levels = [int(x.rstrip('%')) for _, k, x in events if k == "BATTERY" and x.endswith("%")]
    cuts = sum(1 for _, k, x in events if k == "POWER" and "STOPPED" in x)
    if levels:
        print(f"phone battery {max(levels)}% -> {min(levels)}%   power cuts to the glasses: {cuts}"
              + ("   (on 2026-11-15 the cuts began when the battery reached 15%: charge the phone and re-test)" if cuts and min(levels) <= 15 else ""))
    removed_at = None
    for t, kind, text in sorted(events):
        if kind == "KILL" and "Recents swipe" in text:
            removed_at = t
            continue
        if kind in ("DEATH", "KILL"):
            cause = explain_exit(text)
            if cause.startswith("no kill record") and removed_at and t[:7] == removed_at[:7]:
                cause = "swiped away in Recents"
            print(f"   {t}  process exit: {cause}")


if __name__ == "__main__":
    main()
