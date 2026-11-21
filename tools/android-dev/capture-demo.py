#!/usr/bin/env python3
"""Record explicit S25 displays and prepare one-eye clips for editing."""

import argparse
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
from tempfile import TemporaryDirectory
from uuid import uuid4


def command(arguments, *, read=False):
    return subprocess.run(arguments, check=True, text=True, capture_output=read).stdout


def display_listing(adb):
    return command([*adb, "shell", "dumpsys", "SurfaceFlinger", "--display-id"], read=True)


def probe(path):
    metadata = json.loads(command([
        "ffprobe", "-v", "error",
        "-show_entries", "format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate,channels,channel_layout",
        "-of", "json", str(path),
    ], read=True))
    video = [stream for stream in metadata.get("streams", []) if stream["codec_type"] == "video"]
    duration = float(metadata.get("format", {}).get("duration", "nan"))
    if len(video) != 1 or not 0 < duration < float("inf"):
        raise ValueError("Expected one video stream and a positive duration.")
    return video[0], metadata


def inspect(path, *, decode=False):
    video, metadata = probe(path)
    if decode:
        # screenrecord MP4s use variable frame timing. A nominal FPS time base
        # can collapse distinct source timestamps when checking the decode.
        command(["ffmpeg", "-nostdin", "-v", "error", "-xerror", "-i", str(path),
                 "-map", "0:v:0", "-map", "0:a?", "-fps_mode", "passthrough",
                 "-enc_time_base:v", "demux", "-f", "null", "-"])
    audio = [stream for stream in metadata["streams"] if stream["codec_type"] == "audio"]
    return {
        "file": str(path),
        "duration_seconds": float(metadata["format"]["duration"]),
        "video": video,
        "audio": audio,
    }


def output_path(filename):
    path = Path(filename).expanduser().resolve()
    if path.suffix.lower() != ".mp4":
        raise ValueError("Choose an output filename ending in .mp4.")
    if path.exists():
        raise FileExistsError(f"Output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def record(adb, display, seconds, size, output):
    connected = re.findall(r"^Display\s+(\d+)\b", display_listing(adb), re.MULTILINE)
    if display not in connected:
        raise ValueError("Physical display is unavailable. Run the displays command while the glasses are connected.")
    # Samsung's screenrecord writes its help to stderr.
    help_text = subprocess.run([*adb, "shell", "screenrecord", "--help"], check=True,
                               text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout
    if "--display-id" not in help_text:
        raise ValueError("This phone's screenrecord does not support physical display selection.")

    remote = f"/data/local/tmp/tenin-demo-{uuid4().hex}.mp4"
    print(f"Recording display {display} for {seconds}s. Device file: {remote}", flush=True)
    try:
        with TemporaryDirectory(prefix=".tenin-capture-", dir=output.parent) as directory:
            capture = Path(directory) / "capture.mp4"
            command([*adb, "shell", "screenrecord", "--display-id", display,
                     "--time-limit", str(seconds), "--size", size, remote])
            command([*adb, "pull", remote, str(capture)])
            metadata = inspect(capture, decode=True)
            video = metadata["video"]
            if f"{video['width']}x{video['height']}" != size:
                raise ValueError(f"Device did not record the requested dimensions ({size}).")
            os.link(capture, output)
    except (KeyboardInterrupt, OSError, ValueError, subprocess.CalledProcessError):
        recovery = shlex.join([*adb, "pull", remote, str(output)])
        print(f"If the capture finished on the phone, recover it with\n  {recovery}", file=sys.stderr)
        raise

    metadata["file"] = str(output)
    print(json.dumps(metadata, indent=2))
    command([*adb, "shell", "rm", remote])


def export_eye(source, output, eye):
    video, _ = probe(source)
    width, height = video["width"], video["height"]
    if width * 9 != 32 * height or width % 4 or height % 2:
        raise ValueError("Expected a side-by-side 32:9 render with two even-sized 16:9 views.")
    eye_width = width // 2
    x = 0 if eye == "left" else eye_width
    with TemporaryDirectory(prefix=".tenin-eye-", dir=output.parent) as directory:
        export = Path(directory) / "eye.mp4"
        command(["ffmpeg", "-nostdin", "-v", "error", "-xerror", "-i", str(source),
                 "-map", "0:v:0", "-vf", f"crop={eye_width}:{height}:{x}:0",
                 "-an", "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p",
                 "-fps_mode", "passthrough", "-enc_time_base:v", "demux",
                 "-movflags", "+faststart", str(export)])
        metadata = inspect(export, decode=True)
        os.link(export, output)
    metadata["file"] = str(output)
    print(json.dumps(metadata, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default=os.environ.get("ADB", "adb"))
    parser.add_argument("--serial", default=os.environ.get("ADB_SERIAL"), help="Wireless adb serial")
    subcommands = parser.add_subparsers(dest="action", required=True)
    subcommands.add_parser("displays", help="List current physical display IDs")
    capture = subcommands.add_parser("record", help="Capture a silent video from one physical display")
    capture.add_argument("display", help="Physical display ID from displays")
    capture.add_argument("output")
    capture.add_argument("--view", choices=("glasses", "phone"), required=True)
    capture.add_argument("--seconds", type=int, default=40)
    crop = subcommands.add_parser("export-eye", help="Export the selected half of a raw side-by-side render")
    crop.add_argument("source", type=Path)
    crop.add_argument("output")
    crop.add_argument("--eye", choices=("left", "right"), default="left")
    check = subcommands.add_parser("inspect", help="Report duration, picture dimensions and audio streams")
    check.add_argument("files", type=Path, nargs="+")
    check.add_argument("--decode", action="store_true", help="Decode all video and audio streams")

    args = parser.parse_args()
    adb = [args.adb] + (["-s", args.serial] if args.serial else [])
    try:
        if args.action == "displays":
            print(display_listing(adb), end="")
        elif args.action == "record":
            if not 1 <= args.seconds <= 180:
                parser.error("--seconds must be between 1 and 180")
            size = "2560x720" if args.view == "glasses" else "720x1560"
            record(adb, args.display, args.seconds, size, output_path(args.output))
        elif args.action == "export-eye":
            export_eye(args.source.resolve(), output_path(args.output), args.eye)
        else:
            print(json.dumps([inspect(path.resolve(), decode=args.decode) for path in args.files], indent=2))
    except subprocess.CalledProcessError as error:
        print(error.stderr.strip() if error.stderr else f"{error.cmd[0]} exited {error.returncode}.", file=sys.stderr)
        return 1
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())
