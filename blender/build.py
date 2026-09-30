"""Validate and build the Sable Link Blender extension into blender/dist/.

Usage: python build.py
Blender is taken from the SABLE_BLENDER environment variable, else
C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe, else the newest Blender under Program Files.
"""

import glob
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "sable_link")
DIST = os.path.join(HERE, "dist")
FOUNDATION = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Blender Foundation")


def _version_key(path):
    match = re.search(r"Blender (\d+)\.(\d+)", path)
    return (int(match.group(1)), int(match.group(2))) if match else (0, 0)


def find_blender():
    env = os.environ.get("SABLE_BLENDER")
    if env:
        if os.path.isfile(env):
            return env
        sys.exit("SABLE_BLENDER is set but does not exist: " + env)
    preferred = os.path.join(FOUNDATION, "Blender 5.2", "blender.exe")
    if os.path.isfile(preferred):
        return preferred
    found = sorted(glob.glob(os.path.join(FOUNDATION, "Blender *", "blender.exe")), key=_version_key)
    if found:
        return found[-1]
    sys.exit("Blender not found; set SABLE_BLENDER to blender.exe")


def run(blender, *args):
    cmd = [blender, "--command", "extension", *args]
    print(">", " ".join('"{}"'.format(a) if " " in a else a for a in cmd), flush=True)
    result = subprocess.run(cmd)
    if result.returncode != 0:
        sys.exit("failed with exit code {}".format(result.returncode))


def main():
    blender = find_blender()
    os.makedirs(DIST, exist_ok=True)
    run(blender, "validate", SOURCE)
    run(blender, "build", "--source-dir", SOURCE, "--output-dir", DIST)
    print("Built into", DIST)


if __name__ == "__main__":
    main()
